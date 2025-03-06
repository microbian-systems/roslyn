﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.ExternalAccess.Copilot.GenerateImplementation
{
    internal sealed class ImplementationDetailsWrapper
    {
        public required string Message { get; init; }

        public required SyntaxNode? ReplacementNode { get; init; }
    }
}
