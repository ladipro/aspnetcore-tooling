// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.Workspaces;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.LanguageServices.Razor
{
    [System.Composition.Shared]
    [Export(typeof(ForegroundDispatcher))]
    internal class VisualStudioForegroundDispatcher : DefaultForegroundDispatcher
    {
        [ImportingConstructor]
        [Obsolete("This exported object must be obtained through the MEF export provider.", error: true)]
        public VisualStudioForegroundDispatcher(JoinableTaskContext context) : base(context.Factory)
        {
        }
    }
}
