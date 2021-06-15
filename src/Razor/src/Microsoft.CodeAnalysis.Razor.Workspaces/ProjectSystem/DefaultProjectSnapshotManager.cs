﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Razor.Workspaces;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem
{
    // The implementation of project snapshot manager abstracts the host's underlying project system (HostProject), 
    // to provide a immutable view of the underlying project systems.
    //
    // The HostProject support all of the configuration that the Razor SDK exposes via the project system
    // (language version, extensions, named configuration).
    //
    // The implementation will create a ProjectSnapshot for each HostProject.
    internal class DefaultProjectSnapshotManager : ProjectSnapshotManagerBase
    {
        public override event EventHandler<ProjectChangeEventArgs> Changed;

        private readonly ErrorReporter _errorReporter;
        private readonly ForegroundDispatcher _foregroundDispatcher;
        private readonly ProjectSnapshotChangeTrigger[] _triggers;

        // Each entry holds a ProjectState and an optional ProjectSnapshot. ProjectSnapshots are
        // created lazily.
        private readonly Dictionary<string, Entry> _projects;
        private readonly HashSet<string> _openDocuments;

        // We have a queue for changes because if one change results in another change aka, add -> open we want to make sure the "add" finishes running first before "open" is notified.
        private readonly Queue<ProjectChangeEventArgs> _notificationWork;

        public DefaultProjectSnapshotManager(
            ForegroundDispatcher foregroundDispatcher,
            ErrorReporter errorReporter,
            IEnumerable<ProjectSnapshotChangeTrigger> triggers,
            Workspace workspace)
        {
            if (foregroundDispatcher == null)
            {
                throw new ArgumentNullException(nameof(foregroundDispatcher));
            }

            if (errorReporter == null)
            {
                throw new ArgumentNullException(nameof(errorReporter));
            }

            if (triggers == null)
            {
                throw new ArgumentNullException(nameof(triggers));
            }

            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            _foregroundDispatcher = foregroundDispatcher;
            _errorReporter = errorReporter;
            _triggers = triggers.OrderByDescending(trigger => trigger.InitializePriority).ToArray();
            Workspace = workspace;

            _projects = new Dictionary<string, Entry>(FilePathComparer.Instance);
            _openDocuments = new HashSet<string>(FilePathComparer.Instance);
            _notificationWork = new Queue<ProjectChangeEventArgs>();

            for (var i = 0; i < _triggers.Length; i++)
            {
                _triggers[i].Initialize(this);
            }
        }

        public override IReadOnlyList<ProjectSnapshot> Projects
        {
            get
            {
                _foregroundDispatcher.AssertForegroundThread();

                var i = 0;
                var projects = new ProjectSnapshot[_projects.Count];
                foreach (var entry in _projects)
                {
                    projects[i++] = entry.Value.GetSnapshot();
                }

                return projects;
            }
        }

        public override IReadOnlyCollection<string> OpenDocuments
        {
            get
            {
                _foregroundDispatcher.AssertForegroundThread();

                return _openDocuments;
            }
        }

        public override Workspace Workspace { get; }

        public override ProjectSnapshot GetLoadedProject(string filePath)
        {
            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            _foregroundDispatcher.AssertForegroundThread();

            if (_projects.TryGetValue(filePath, out var entry))
            {
                return entry.GetSnapshot();
            }

            return null;
        }

        public override ProjectSnapshot GetOrCreateProject(string filePath)
        {
            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            _foregroundDispatcher.AssertForegroundThread();

            return GetLoadedProject(filePath) ?? new EphemeralProjectSnapshot(Workspace.Services, filePath);
        }

        public override bool IsDocumentOpen(string documentFilePath)
        {
            if (documentFilePath == null)
            {
                throw new ArgumentNullException(nameof(documentFilePath));
            }

            _foregroundDispatcher.AssertForegroundThread();

            return _openDocuments.Contains(documentFilePath);
        }

        public override void DocumentAdded(HostProject hostProject, HostDocument document, TextLoader textLoader)
        {
            if (hostProject == null)
            {
                throw new ArgumentNullException(nameof(hostProject));
            }

            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            _foregroundDispatcher.AssertForegroundThread();

            if (_projects.TryGetValue(hostProject.FilePath, out var entry))
            {
                var loader = textLoader == null
                    ? DocumentState.EmptyLoader
                    : (() => textLoader.LoadTextAndVersionAsync(Workspace, null, CancellationToken.None));
                var state = entry.State.WithAddedHostDocument(document, loader);

                // Document updates can no-op.
                if (!object.ReferenceEquals(state, entry.State))
                {
                    var oldSnapshot = entry.GetSnapshot();
                    entry = new Entry(state);
                    _projects[hostProject.FilePath] = entry;
                    NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, entry.GetSnapshot(), document.FilePath, ProjectChangeKind.DocumentAdded));
                }
            }
        }

        public override void DocumentRemoved(HostProject hostProject, HostDocument document)
        {
            if (hostProject == null)
            {
                throw new ArgumentNullException(nameof(hostProject));
            }

            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            _foregroundDispatcher.AssertForegroundThread();
            if (_projects.TryGetValue(hostProject.FilePath, out var entry))
            {
                var state = entry.State.WithRemovedHostDocument(document);

                // Document updates can no-op.
                if (!object.ReferenceEquals(state, entry.State))
                {
                    var oldSnapshot = entry.GetSnapshot();
                    entry = new Entry(state);
                    _projects[hostProject.FilePath] = entry;
                    NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, entry.GetSnapshot(), document.FilePath, ProjectChangeKind.DocumentRemoved));
                }
            }
        }

        public override void DocumentOpened(string projectFilePath, string documentFilePath, SourceText sourceText)
        {
            if (projectFilePath == null)
            {
                throw new ArgumentNullException(nameof(projectFilePath));
            }

            if (documentFilePath == null)
            {
                throw new ArgumentNullException(nameof(documentFilePath));
            }

            if (sourceText == null)
            {
                throw new ArgumentNullException(nameof(sourceText));
            }

            _foregroundDispatcher.AssertForegroundThread();
            if (_projects.TryGetValue(projectFilePath, out var entry) &&
                entry.State.Documents.TryGetValue(documentFilePath, out var older))
            {
                ProjectState state;

                var currentText = sourceText;
                if (older.TryGetText(out var olderText) &&
                    older.TryGetTextVersion(out var olderVersion))
                {
                    var version = currentText.ContentEquals(olderText) ? olderVersion : olderVersion.GetNewerVersion();
                    state = entry.State.WithChangedHostDocument(older.HostDocument, currentText, version);
                }
                else
                {
                    state = entry.State.WithChangedHostDocument(older.HostDocument, async () =>
                    {
                        olderText = await older.GetTextAsync().ConfigureAwait(false);
                        olderVersion = await older.GetTextVersionAsync().ConfigureAwait(false);

                        var version = currentText.ContentEquals(olderText) ? olderVersion : olderVersion.GetNewerVersion();
                        return TextAndVersion.Create(currentText, version, documentFilePath);
                    });
                }

                _openDocuments.Add(documentFilePath);

                // Document updates can no-op.
                if (!object.ReferenceEquals(state, entry.State))
                {
                    var oldSnapshot = entry.GetSnapshot();
                    entry = new Entry(state);
                    _projects[projectFilePath] = entry;
                    NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, entry.GetSnapshot(), documentFilePath, ProjectChangeKind.DocumentChanged));
                }
            }
        }

        public override void DocumentClosed(string projectFilePath, string documentFilePath, TextLoader textLoader)
        {
            if (projectFilePath == null)
            {
                throw new ArgumentNullException(nameof(projectFilePath));
            }

            if (documentFilePath == null)
            {
                throw new ArgumentNullException(nameof(documentFilePath));
            }

            if (textLoader == null)
            {
                throw new ArgumentNullException(nameof(textLoader));
            }

            _foregroundDispatcher.AssertForegroundThread();
            if (_projects.TryGetValue(projectFilePath, out var entry) &&
                entry.State.Documents.TryGetValue(documentFilePath, out var older))
            {
                var state = entry.State.WithChangedHostDocument(
                    older.HostDocument,
                    async () => await textLoader.LoadTextAndVersionAsync(Workspace, default, default));

                _openDocuments.Remove(documentFilePath);

                // Document updates can no-op.
                if (!object.ReferenceEquals(state, entry.State))
                {
                    var oldSnapshot = entry.GetSnapshot();
                    entry = new Entry(state);
                    _projects[projectFilePath] = entry;
                    NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, entry.GetSnapshot(), documentFilePath, ProjectChangeKind.DocumentChanged));
                }
            }
        }

        public override void DocumentChanged(string projectFilePath, string documentFilePath, SourceText sourceText)
        {
            if (projectFilePath == null)
            {
                throw new ArgumentNullException(nameof(projectFilePath));
            }

            if (documentFilePath == null)
            {
                throw new ArgumentNullException(nameof(documentFilePath));
            }

            if (sourceText == null)
            {
                throw new ArgumentNullException(nameof(sourceText));
            }

            _foregroundDispatcher.AssertForegroundThread();
            if (_projects.TryGetValue(projectFilePath, out var entry) &&
                entry.State.Documents.TryGetValue(documentFilePath, out var older))
            {
                ProjectState state;

                var currentText = sourceText;
                if (older.TryGetText(out var olderText) &&
                    older.TryGetTextVersion(out var olderVersion))
                {
                    var version = currentText.ContentEquals(olderText) ? olderVersion : olderVersion.GetNewerVersion();
                    state = entry.State.WithChangedHostDocument(older.HostDocument, currentText, version);
                }
                else
                {
                    state = entry.State.WithChangedHostDocument(older.HostDocument, async () =>
                    {
                        olderText = await older.GetTextAsync().ConfigureAwait(false);
                        olderVersion = await older.GetTextVersionAsync().ConfigureAwait(false);

                        var version = currentText.ContentEquals(olderText) ? olderVersion : olderVersion.GetNewerVersion();
                        return TextAndVersion.Create(currentText, version, documentFilePath);
                    });
                }

                // Document updates can no-op.
                if (!object.ReferenceEquals(state, entry.State))
                {
                    var oldSnapshot = entry.GetSnapshot();
                    entry = new Entry(state);
                    _projects[projectFilePath] = entry;
                    NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, entry.GetSnapshot(), documentFilePath, ProjectChangeKind.DocumentChanged));
                }
            }
        }

        public override void DocumentChanged(string projectFilePath, string documentFilePath, TextLoader textLoader)
        {
            if (projectFilePath == null)
            {
                throw new ArgumentNullException(nameof(projectFilePath));
            }

            if (documentFilePath == null)
            {
                throw new ArgumentNullException(nameof(documentFilePath));
            }

            if (textLoader == null)
            {
                throw new ArgumentNullException(nameof(textLoader));
            }

            _foregroundDispatcher.AssertForegroundThread();
            if (_projects.TryGetValue(projectFilePath, out var entry) &&
                entry.State.Documents.TryGetValue(documentFilePath, out var older))
            {
                var state = entry.State.WithChangedHostDocument(
                    older.HostDocument,
                    async () => await textLoader.LoadTextAndVersionAsync(Workspace, default, default));

                // Document updates can no-op.
                if (!ReferenceEquals(state, entry.State))
                {
                    var oldSnapshot = entry.GetSnapshot();
                    entry = new Entry(state);
                    _projects[projectFilePath] = entry;
                    NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, entry.GetSnapshot(), documentFilePath, ProjectChangeKind.DocumentChanged));
                }
            }
        }

        public override void ProjectAdded(HostProject hostProject)
        {
            if (hostProject == null)
            {
                throw new ArgumentNullException(nameof(hostProject));
            }

            _foregroundDispatcher.AssertForegroundThread();

            // We don't expect to see a HostProject initialized multiple times for the same path. Just ignore it.
            if (_projects.ContainsKey(hostProject.FilePath))
            {
                return;
            }

            var state = ProjectState.Create(Workspace.Services, hostProject);
            var entry = new Entry(state);
            _projects[hostProject.FilePath] = entry;

            // We need to notify listeners about every project add.
            NotifyListeners(new ProjectChangeEventArgs(null, entry.GetSnapshot(), ProjectChangeKind.ProjectAdded));
        }

        public override void ProjectConfigurationChanged(HostProject hostProject)
        {
            if (hostProject == null)
            {
                throw new ArgumentNullException(nameof(hostProject));
            }

            _foregroundDispatcher.AssertForegroundThread();

            if (_projects.TryGetValue(hostProject.FilePath, out var entry))
            {
                var state = entry.State.WithHostProject(hostProject);

                // HostProject updates can no-op.
                if (!object.ReferenceEquals(state, entry.State))
                {
                    var oldSnapshot = entry.GetSnapshot();
                    entry = new Entry(state);
                    _projects[hostProject.FilePath] = entry;
                    NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, entry.GetSnapshot(), ProjectChangeKind.ProjectChanged));
                }
            }
        }

        public override void ProjectWorkspaceStateChanged(string projectFilePath, ProjectWorkspaceState projectWorkspaceState)
        {
            if (projectFilePath == null)
            {
                throw new ArgumentNullException(nameof(projectFilePath));
            }

            if (projectWorkspaceState == null)
            {
                throw new ArgumentNullException(nameof(projectWorkspaceState));
            }

            _foregroundDispatcher.AssertForegroundThread();

            if (_projects.TryGetValue(projectFilePath, out var entry))
            {
                var state = entry.State.WithProjectWorkspaceState(projectWorkspaceState);

                // HostProject updates can no-op.
                if (!object.ReferenceEquals(state, entry.State))
                {
                    var oldSnapshot = entry.GetSnapshot();
                    entry = new Entry(state);
                    _projects[projectFilePath] = entry;
                    NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, entry.GetSnapshot(), ProjectChangeKind.ProjectChanged));
                }
            }
        }

        public override void ProjectRemoved(HostProject hostProject)
        {
            if (hostProject == null)
            {
                throw new ArgumentNullException(nameof(hostProject));
            }

            _foregroundDispatcher.AssertForegroundThread();

            if (_projects.TryGetValue(hostProject.FilePath, out var entry))
            {
                // We need to notify listeners about every project removal.
                var oldSnapshot = entry.GetSnapshot();
                _projects.Remove(hostProject.FilePath);
                NotifyListeners(new ProjectChangeEventArgs(oldSnapshot, null, ProjectChangeKind.ProjectRemoved));
            }
        }

        public override void ReportError(Exception exception)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            _errorReporter.ReportError(exception);
        }

        public override void ReportError(Exception exception, ProjectSnapshot project)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            _errorReporter.ReportError(exception, project);
        }

        public override void ReportError(Exception exception, HostProject hostProject)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            var snapshot = hostProject?.FilePath == null ? null : GetLoadedProject(hostProject.FilePath);
            _errorReporter.ReportError(exception, snapshot);
        }

        // virtual so it can be overridden in tests
#pragma warning disable VSTHRD100 // Avoid async void methods
        protected async virtual void NotifyListeners(ProjectChangeEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            try
            {
                if (_foregroundDispatcher is DefaultForegroundDispatcher d)
                {
                    await Task.Factory.StartNew(() =>
                    {
                        _notificationWork.Enqueue(e);

                        if (_notificationWork.Count == 1)
                        {
                            // Only one notification, go ahead and start notifying. In the situation where Count > 1 it means an event was triggered as a response to another event.
                            // To ensure order we wont immediately re-invoke Changed here, we'll wait for the stack to unwind to notify others. This process still happens synchronously
                            // it just ensures that events happen in the correct order. For instance lets take the situation where a document is added to a project. That document will be
                            // added and then opened. However, if the result of "adding" causes an "open" to triger we want to ensure that "add" finishes prior to "open" being notified.


                            // Start unwinding the notification queue
                            do
                            {
                                // Don't dequeue yet, we want the notification to sit in the queue until we've finished notifying to ensure other calls to NotifyListeners know there's a currently running event loop.
                                var args = _notificationWork.Peek();
                                Changed?.Invoke(this, args);

                                _notificationWork.Dequeue();
                            }
                            while (_notificationWork.Count > 0);
                        }
                    }, CancellationToken.None, TaskCreationOptions.None, d.UIScheduler);
                }
            }
            catch
            {
            }
        }

        private class Entry
        {
            public ProjectSnapshot SnapshotUnsafe;
            public readonly ProjectState State;

            public Entry(ProjectState state)
            {
                State = state;
            }

            public ProjectSnapshot GetSnapshot()
            {
                return SnapshotUnsafe ??= new DefaultProjectSnapshot(State);
            }
        }
    }
}
