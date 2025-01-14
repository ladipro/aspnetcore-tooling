﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Razor.LanguageServer.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;

namespace Microsoft.AspNetCore.Razor.LanguageServer
{
    internal class DefaultAdhocWorkspaceFactory : AdhocWorkspaceFactory
    {
        private readonly HostWorkspaceServicesProvider _hostWorkspaceServicesProvider;

        public DefaultAdhocWorkspaceFactory(HostWorkspaceServicesProvider hostWorkspaceServicesProvider)
        {
            if (hostWorkspaceServicesProvider is null)
            {
                throw new ArgumentNullException(nameof(hostWorkspaceServicesProvider));
            }

            _hostWorkspaceServicesProvider = hostWorkspaceServicesProvider;
        }

        public override AdhocWorkspace Create() => Create(Enumerable.Empty<IWorkspaceService>());

        public override AdhocWorkspace Create(IEnumerable<IWorkspaceService> workspaceServices)
        {
            if (workspaceServices is null)
            {
                throw new ArgumentNullException(nameof(workspaceServices));
            }

            var fallbackServices = _hostWorkspaceServicesProvider.GetServices();
            var services = AdhocServices.Create(
                workspaceServices,
                razorLanguageServices: Enumerable.Empty<ILanguageService>(),
                fallbackServices);
            var workspace = new AdhocWorkspace(services);
            return workspace;
        }
    }
}
