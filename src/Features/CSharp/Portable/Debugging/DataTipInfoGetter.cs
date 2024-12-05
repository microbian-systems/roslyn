﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Debugging;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.Debugging;

internal sealed class DataTipInfoGetter : AbstractDataTipInfoGetter<ExpressionSyntax>
{
    public static async Task<DebugDataTipInfo> GetInfoAsync(
        Document document, int position, bool includeKind, CancellationToken cancellationToken)
    {
        try
        {
            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var token = root.FindToken(position);

            if (token.Parent is not ExpressionSyntax expression)
            {
                return token.IsKind(SyntaxKind.IdentifierToken)
                    ? new DebugDataTipInfo(token.Span, Text: null)
                    : default;
            }

            if (expression is LiteralExpressionSyntax)
            {
                // If the user hovers over a literal, give them a DataTip for the type of the literal they're hovering
                // over. Partial semantics should always be sufficient because the (unconverted) type of a literal can
                // always easily be determined.
                document = document.WithFrozenPartialSemantics(cancellationToken);
                var semanticModel = await document.GetRequiredNullableDisabledSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var type = semanticModel.GetTypeInfo(expression, cancellationToken).Type;
                return type == null
                    ? default
                    : new DebugDataTipInfo(expression.Span, type.ToNameDisplayString());
            }
            else
            {
                var (kind, semanticModel) = await ComputeKindAsync(document, expression, includeKind, cancellationToken).ConfigureAwait(false);

                if (expression.IsRightSideOfDotOrArrow())
                {
                    // NOTE: There may not be an ExpressionSyntax corresponding to the range we want.
                    // For example, for input a?.$$B?.C, we want span [|a?.B|]?.C.
                    var currrent = expression.GetRootConditionalAccessExpression() ?? expression;

                    var span = currrent == expression
                        ? expression.GetRequiredParent().Span
                        : TextSpan.FromBounds(currrent.SpanStart, expression.Span.End);

                    return new DebugDataTipInfo(span, Text: null, kind);
                }

                // NOTE(cyrusn): This behavior is to mimic what we did in Dev10, I'm not sure if it's
                // necessary or not.
                if (expression is InvocationExpressionSyntax invocation)
                    expression = invocation.Expression;

                string? text = null;
                if (expression is TypeSyntax typeSyntax && typeSyntax.IsVar)
                {
                    // If the user is hovering over 'var', then pass back the full type name that 'var'
                    // binds to.
                    semanticModel ??= await document.GetRequiredNullableDisabledSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    var type = semanticModel.GetTypeInfo(typeSyntax, cancellationToken).Type;
                    if (type != null)
                        text = type.ToNameDisplayString();
                }

                return new DebugDataTipInfo(expression.Span, text, kind);
            }
        }
        catch (Exception e) when (FatalError.ReportAndCatchUnlessCanceled(e, cancellationToken))
        {
            return default;
        }
    }
}
