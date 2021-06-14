// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.Workspaces;

namespace Microsoft.VisualStudio.LanguageServices.Razor
{
    [System.Composition.Shared]
    [Export(typeof(ForegroundDispatcher))]
    internal class VisualStudioForegroundDispatcher : ForegroundDispatcher
    {
        private readonly ForegroundDispatcher _foregroundDispatcher;

        [ImportingConstructor]
        public VisualStudioForegroundDispatcher()
        {
            _foregroundDispatcher = new DefaultForegroundDispatcher();
            ForegroundScheduler = _foregroundDispatcher.ForegroundScheduler;
        }

        public override TaskScheduler BackgroundScheduler { get; } = TaskScheduler.Default;

        public override TaskScheduler ForegroundScheduler { get; }

        public override bool IsForegroundThread => _foregroundDispatcher.IsForegroundThread;
    }
}
