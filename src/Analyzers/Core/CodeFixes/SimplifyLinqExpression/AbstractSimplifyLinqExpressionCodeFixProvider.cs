﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.SimplifyLinqExpression;

internal abstract class AbstractSimplifyLinqExpressionCodeFixProvider<TInvocationExpressionSyntax, TSimpleNameSyntax, TExpressionSyntax> : SyntaxEditorBasedCodeFixProvider
    where TExpressionSyntax : SyntaxNode
    where TInvocationExpressionSyntax : TExpressionSyntax
    where TSimpleNameSyntax : TExpressionSyntax
{
    public sealed override ImmutableArray<string> FixableDiagnosticIds
       => [IDEDiagnosticIds.SimplifyLinqExpressionDiagnosticId];

    public sealed override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        RegisterCodeFix(context, AnalyzersResources.Simplify_LINQ_expression, nameof(AnalyzersResources.Simplify_LINQ_expression));
        return Task.CompletedTask;
    }

    protected override Task FixAllAsync(
        Document document,
        ImmutableArray<Diagnostic> diagnostics,
        SyntaxEditor editor,
        CancellationToken cancellationToken)
    {
        var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
        var root = editor.OriginalRoot;

        foreach (var diagnostic in diagnostics.OrderByDescending(diagnostics => diagnostics.Location.SourceSpan.Start))
        {
            var invocation = (TInvocationExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            editor.ReplaceNode(invocation, (current, generator) =>
            {
                var invocation = (TInvocationExpressionSyntax)current;

                var memberAccess = syntaxFacts.GetExpressionOfInvocationExpression(current);
                var name = (TSimpleNameSyntax)syntaxFacts.GetNameOfMemberAccessExpression(memberAccess);
                var whereExpression = (TInvocationExpressionSyntax)syntaxFacts.GetExpressionOfMemberAccessExpression(memberAccess)!;
                var arguments = syntaxFacts.GetArgumentsOfInvocationExpression(whereExpression);
                var expression = (TExpressionSyntax)syntaxFacts.GetExpressionOfMemberAccessExpression(syntaxFacts.GetExpressionOfInvocationExpression(whereExpression))!;

                return generator.InvocationExpression(
                    generator.MemberAccessExpression(expression, name),
                    arguments).WithTriviaFrom(current);
            });
        }

        return Task.CompletedTask;
    }
}
