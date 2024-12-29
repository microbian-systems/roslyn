﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.ExtractMethod;

internal abstract partial class AbstractExtractMethodService<
    TValidator,
    TExtractor,
    TSelectionResult,
    TStatementSyntax,
    TExecutableStatementSyntax,
    TExpressionSyntax>
{
    public abstract partial class SelectionValidator(
        SemanticDocument document,
        TextSpan textSpan)
    {
        protected readonly SemanticDocument SemanticDocument = document;
        protected readonly TextSpan OriginalSpan = textSpan;

        public bool ContainsValidSelection => !OriginalSpan.IsEmpty;

        protected abstract SelectionInfo GetInitialSelectionInfo(CancellationToken cancellationToken);
        protected abstract Task<TSelectionResult> CreateSelectionResultAsync(SelectionInfo selectionInfo, CancellationToken cancellationToken);

        protected abstract bool AreStatementsInSameContainer(TStatementSyntax statement1, TStatementSyntax statement2);

        public async Task<(TSelectionResult?, OperationStatus)> GetValidSelectionAsync(CancellationToken cancellationToken)
        {
            if (!this.ContainsValidSelection)
                return (null, OperationStatus.FailedWithUnknownReason);

            var selectionInfo = GetInitialSelectionInfo(cancellationToken);
            if (selectionInfo.Status.Failed)
                return (null, selectionInfo.Status);

            if (!selectionInfo.SelectionInExpression &&
                GetStatementRangeContainedInSpan(SemanticDocument.Root, selectionInfo.FinalSpan, cancellationToken) == null)
            {
                return (null, selectionInfo.Status.With(succeeded: false, FeaturesResources.Cannot_determine_valid_range_of_statements_to_extract));
            }

            var selectionResult = await CreateSelectionResultAsync(selectionInfo, cancellationToken).ConfigureAwait(false);

            var status = selectionInfo.Status.With(
                selectionResult.AnalyzeControlFlow(cancellationToken));

            return (selectionResult, status);
        }

        protected static (TStatementSyntax firstStatement, TStatementSyntax lastStatement)? GetStatementRangeContainedInSpan(
            SyntaxNode root, TextSpan textSpan, CancellationToken cancellationToken)
        {
            // use top-down approach to find largest statement range contained in the given span
            // this method is a bit more expensive than bottom-up approach, but way more simpler than the other approach.
            var token1 = root.FindToken(textSpan.Start);
            var token2 = root.FindTokenFromEnd(textSpan.End);

            var commonRoot = token1.GetCommonRoot(token2).GetAncestorOrThis<TStatementSyntax>() ?? root;

            TStatementSyntax? firstStatement = null;
            TStatementSyntax? lastStatement = null;

            foreach (var statement in commonRoot.DescendantNodesAndSelf().OfType<TStatementSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (firstStatement == null && statement.SpanStart >= textSpan.Start)
                    firstStatement = statement;

                if (firstStatement != null && statement.Span.End <= textSpan.End && statement.Parent == firstStatement.Parent)
                    lastStatement = statement;
            }

            if (firstStatement == null || lastStatement == null)
                return null;

            return (firstStatement, lastStatement);
        }

        protected (TStatementSyntax firstStatement, TStatementSyntax lastStatement)? GetStatementRangeContainingSpan(
            SyntaxNode root,
            TextSpan textSpan,
            CancellationToken cancellationToken)
        {
            // use top-down approach to find smallest statement range that contains given span.
            // this approach is more expansive than bottom-up approach I used before but way simpler and easy to understand
            var token1 = root.FindToken(textSpan.Start);
            var token2 = root.FindTokenFromEnd(textSpan.End);

            var commonRoot = token1.GetCommonRoot(token2).GetAncestorOrThis<TStatementSyntax>() ?? root;

            TStatementSyntax? firstStatement = null;
            TStatementSyntax? lastStatement = null;

            var spine = new List<TStatementSyntax>();

            foreach (var statement in commonRoot.DescendantNodesAndSelf().OfType<TStatementSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // quick skip check.
                // - not containing at all
                if (statement.Span.End < textSpan.Start)
                    continue;

                // quick exit check
                // - passed candidate statements
                if (textSpan.End < statement.SpanStart)
                    break;

                if (statement.SpanStart <= textSpan.Start)
                {
                    // keep track spine
                    spine.Add(statement);
                }

                if (textSpan.End <= statement.Span.End && spine.Any(s => AreStatementsInSameContainer(s, statement)))
                {
                    // malformed code or selection can make spine to have more than an elements
                    firstStatement = spine.First(s => AreStatementsInSameContainer(s, statement));
                    lastStatement = statement;

                    spine.Clear();
                }
            }

            if (firstStatement == null || lastStatement == null)
                return null;

            return (firstStatement, lastStatement);
        }
    }
}
