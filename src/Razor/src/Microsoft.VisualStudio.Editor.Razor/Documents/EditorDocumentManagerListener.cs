﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;

namespace Microsoft.VisualStudio.Editor.Razor.Documents
{
    // Hooks up the document manager to project snapshot events. The project snapshot manager
    // tracks the existance of projects/files and the the document manager watches for changes.
    //
    // This class forwards notifications in both directions.
    [Export(typeof(ProjectSnapshotChangeTrigger))]
    internal class EditorDocumentManagerListener : ProjectSnapshotChangeTrigger
    {
        private readonly EventHandler _onChangedOnDisk;
        private readonly EventHandler _onChangedInEditor;
        private readonly EventHandler _onOpened;
        private readonly EventHandler _onClosed;

        private EditorDocumentManager _documentManager;
        private ProjectSnapshotManagerBase _projectManager;

        [ImportingConstructor]
        [Obsolete("This exported object must be obtained through the MEF export provider.", error: true)]
        public EditorDocumentManagerListener()
        {
            _onChangedOnDisk = Document_ChangedOnDisk;
            _onChangedInEditor = Document_ChangedInEditor;
            _onOpened = Document_Opened;
            _onClosed = Document_Closed;
        }

        // For testing purposes only.
#pragma warning disable RS0034 // Exported parts should be marked with 'ImportingConstructorAttribute'
        internal EditorDocumentManagerListener(
            EditorDocumentManager documentManager,
            EventHandler onChangedOnDisk,
            EventHandler onChangedInEditor,
            EventHandler onOpened,
            EventHandler onClosed)
        {
            _documentManager = documentManager;
            _onChangedOnDisk = onChangedOnDisk;
            _onChangedInEditor = onChangedInEditor;
            _onOpened = onOpened;
            _onClosed = onClosed;
        }
#pragma warning restore RS0034 // Exported parts should be marked with 'ImportingConstructorAttribute'

        // InitializePriority controls when a snapshot change trigger gets initialized. By specifying 100 we're saying we're pretty important and should get initialized before
        // other triggers with lesser priority so we can attach to Changed sooner. We happen to be so important because we control the open/close state of documents. If other triggers
        // depend on a document being open/closed (some do) then we need to ensure we can mark open/closed prior to them running.
        public override int InitializePriority => 100;

        public override void Initialize(ProjectSnapshotManagerBase projectManager)
        {
            if (projectManager == null)
            {
                throw new ArgumentNullException(nameof(projectManager));
            }

            _projectManager = projectManager;
            _documentManager = projectManager.Workspace.Services.GetRequiredService<EditorDocumentManager>();

            _projectManager.Changed += ProjectManager_Changed;
        }

        // Internal for testing.
        internal void ProjectManager_Changed(object sender, ProjectChangeEventArgs e)
        {
            try
            {
                switch (e.Kind)
                {
                    case ProjectChangeKind.DocumentAdded:
                        {
                            var key = new DocumentKey(e.ProjectFilePath, e.DocumentFilePath);
                            var document = _documentManager.GetOrCreateDocument(key, _onChangedOnDisk, _onChangedInEditor, _onOpened, _onClosed);
                            if (document.IsOpenInEditor)
                            {
                                _onOpened(document, EventArgs.Empty);
                            }

                            break;
                        }

                    case ProjectChangeKind.DocumentRemoved:
                        {
                            // This class 'owns' the document entry so it's safe for us to dispose it.
                            if (_documentManager.TryGetDocument(new DocumentKey(e.ProjectFilePath, e.DocumentFilePath), out var document))
                            {
                                document.Dispose();
                            }
                            break;
                        }
                }
            }
            catch
            {
                Debug.Fail("ProjectManager_Changed");
            }
        }

        private void Document_ChangedOnDisk(object sender, EventArgs e)
        {
            var document = (EditorDocument)sender;
            _projectManager.DocumentChanged(document.ProjectFilePath, document.DocumentFilePath, document.TextLoader);
        }

        private void Document_ChangedInEditor(object sender, EventArgs e)
        {
            var document = (EditorDocument)sender;
            _projectManager.DocumentChanged(document.ProjectFilePath, document.DocumentFilePath, document.EditorTextContainer.CurrentText);
        }

        private void Document_Opened(object sender, EventArgs e)
        {
            var document = (EditorDocument)sender;
            _projectManager.DocumentOpened(document.ProjectFilePath, document.DocumentFilePath, document.EditorTextContainer.CurrentText);
        }

        private void Document_Closed(object sender, EventArgs e)
        {
            var document = (EditorDocument)sender;
            _projectManager.DocumentClosed(document.ProjectFilePath, document.DocumentFilePath, document.TextLoader);
        }
    }
}
