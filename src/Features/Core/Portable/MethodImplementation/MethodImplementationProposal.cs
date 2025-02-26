﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.MethodImplementation;

internal sealed record MethodImplementationProposal
{
    public required string MethodName { get; init; }
    public string? MethodBody { get; init; }
    public string? ExpressionBody { get; init; }
    public required string ReturnType { get; init; }
    public required string ContainingType { get; init; }
    public required string Accessibility { get; init; }
    public required ImmutableArray<string> Modifiers { get; init; }
    public required ImmutableArray<ParameterContext> Parameters { get; init; }
    public required ImmutableArray<ReferenceContext> TopReferences { get; init; }
    public required int ReferenceCount { get; init; }
    public required string PreviousTokenText { get; init; }
    public required string NextTokenText { get; init; }
    public required string LanguageVersion { get; init; }
    public bool IsExpressionBody => ExpressionBody != null;
}
