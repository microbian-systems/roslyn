﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer.ExternalAccess.Copilot;

internal abstract class AbstractCopilotLspServiceRequestHandler<TRequest, TResponse> : ILspServiceRequestHandler<TRequest, TResponse>
{
    public abstract Task<TResponse> HandleRequestAsync(TRequest request, RequestContext context, CancellationToken cancellationToken);

    bool IMethodHandler.MutatesSolutionState => false;
    bool ISolutionRequiredHandler.RequiresLSPSolution => true;

    Task<TResponse> IRequestHandler<TRequest, TResponse, LspRequestContext>.HandleRequestAsync(TRequest request, LspRequestContext context, CancellationToken cancellationToken)
        => HandleRequestAsync(request, new RequestContext(context), cancellationToken);
}
