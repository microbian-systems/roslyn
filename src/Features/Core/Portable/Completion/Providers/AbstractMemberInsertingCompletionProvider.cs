﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeCleanup;
using Microsoft.CodeAnalysis.CodeGeneration;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Completion.Providers;

internal abstract partial class AbstractMemberInsertingCompletionProvider : LSPCompletionProvider
{
    private readonly SyntaxAnnotation _annotation = new();
    private readonly SyntaxAnnotation _replaceStartAnnotation = new();
    private readonly SyntaxAnnotation _replaceEndAnnotation = new();

    protected abstract SyntaxToken GetToken(CompletionItem completionItem, SyntaxTree tree, CancellationToken cancellationToken);

    protected abstract Task<ISymbol> GenerateMemberAsync(ISymbol member, INamedTypeSymbol containingType, Document document, CompletionItem item, CancellationToken cancellationToken);
    protected abstract int GetTargetCaretPosition(SyntaxNode caretTarget);
    protected abstract SyntaxNode GetSyntax(SyntaxToken commonSyntaxToken);

    public AbstractMemberInsertingCompletionProvider()
    {
    }

    public override async Task<CompletionChange> GetChangeAsync(Document document, CompletionItem item, char? commitKey = null, CancellationToken cancellationToken = default)
    {
        var newDocument = await DetermineNewDocumentAsync(document, item, cancellationToken).ConfigureAwait(false);
        var newText = await newDocument.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
        var newRoot = await newDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        int? newPosition = null;

        // Attempt to find the inserted node and move the caret appropriately
        if (newRoot != null)
        {
            var caretTarget = newRoot.GetAnnotatedNodes(_annotation).FirstOrDefault();
            if (caretTarget != null)
            {
                var targetPosition = GetTargetCaretPosition(caretTarget);

                // Something weird happened and we failed to get a valid position.
                // Bail on moving the caret.
                if (targetPosition > 0 && targetPosition <= newText.Length)
                {
                    newPosition = targetPosition;
                }
            }
        }

        var changes = await newDocument.GetTextChangesAsync(document, cancellationToken).ConfigureAwait(false);
        var changesArray = changes.ToImmutableArray();
        var change = Utilities.Collapse(newText, changesArray);

        return CompletionChange.Create(change, changesArray, newPosition, includesCommitCharacter: true);
    }

    private async Task<Document> DetermineNewDocumentAsync(
        Document document,
        CompletionItem completionItem,
        CancellationToken cancellationToken)
    {
        var text = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);

        // The span we're going to replace
        var line = text.Lines[MemberInsertionCompletionItem.GetLine(completionItem)];

        // Annotate the line we care about so we can find it after adding usings
        // We annotate the line in order to handle adding the generated code before our annotated token in the same line
        var tree = await document.GetRequiredSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var treeRoot = tree.GetRoot(cancellationToken);

        // DevDiv 958235: 
        //
        // void goo()
        // {
        // }
        // override $$
        //
        // If our text edit includes the trailing trivia of the close brace of goo(),
        // that token will be reconstructed. The ensuing tree diff will then count
        // the { } as replaced even though we didn't want it to. If the user
        // has collapsed the outline for goo, that means we'll edit the outlined 
        // region and weird stuff will happen. Therefore, we'll start with the first
        // token on the line in order to leave the token and its trivia alone.
        var lineStart = line.GetFirstNonWhitespacePosition();
        Contract.ThrowIfNull(lineStart);
        var endToken = GetToken(completionItem, tree, cancellationToken);
        var annotatedRoot = treeRoot
            .ReplaceToken(endToken, endToken.WithAdditionalAnnotations(_replaceEndAnnotation));

        var startToken = annotatedRoot.FindTokenOnRightOfPosition(lineStart.Value);
        annotatedRoot = annotatedRoot
            .ReplaceToken(startToken, startToken.WithAdditionalAnnotations(_replaceStartAnnotation));

        // Make sure the new document is frozen before we try to get the semantic model. This is to 
        // avoid trigger source generator, which is expensive and not needed for calculating the change.
        document = document.WithSyntaxRoot(annotatedRoot).WithFrozenPartialSemantics(cancellationToken);

        var memberContainingDocument = await GenerateMemberAndUsingsAsync(document, completionItem, line, cancellationToken).ConfigureAwait(false);
        if (memberContainingDocument == null)
        {
            // Generating the new document failed because we somehow couldn't resolve
            // the underlying symbol's SymbolKey. At this point, we won't be able to 
            // make any changes, so just return the document we started with.
            return document;
        }

        var memberContainingDocumentCleanupOptions = await document.GetCodeCleanupOptionsAsync(cancellationToken).ConfigureAwait(false);

        // We get the insertion root and immediately create a document with the replaced text removed
        // before calculating the insertion text, to ensure syntactical correctness
        // An example problematic case is explicit interface implementations of static operator methods
        // which could cause many errors, causing the resulting text to be unformatted in various bad ways
        var memberContainingRoot = await memberContainingDocument.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var destinationSpan = ComputeDestinationSpan(memberContainingRoot);

        document = await RemoveDestinationNodeAsync(memberContainingDocument, destinationSpan, cancellationToken).ConfigureAwait(false);
        var formattingOptions = await document.GetSyntaxFormattingOptionsAsync(cancellationToken).ConfigureAwait(false);
        document = await Simplifier.ReduceAsync(document, _annotation, memberContainingDocumentCleanupOptions.SimplifierOptions, cancellationToken).ConfigureAwait(false);
        return await Formatter.FormatAsync(document, _annotation, formattingOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Document?> GenerateMemberAndUsingsAsync(
        Document document,
        CompletionItem completionItem,
        TextLine line,
        CancellationToken cancellationToken)
    {
        var codeGenService = document.GetRequiredLanguageService<ICodeGenerationService>();

        // Resolve member and type in our new, forked, solution
        var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        var containingType = semanticModel.GetEnclosingSymbol<INamedTypeSymbol>(line.Start, cancellationToken);
        Contract.ThrowIfNull(containingType);

        var symbols = await SymbolCompletionItem.GetSymbolsAsync(completionItem, document, cancellationToken).ConfigureAwait(false);
        var overriddenMember = symbols.FirstOrDefault();

        if (overriddenMember == null)
        {
            // Unfortunately, SymbolKey resolution failed. Bail.
            return null;
        }

        // CodeGenerationOptions containing before and after
        var context = new CodeGenerationSolutionContext(
            document.Project.Solution,
            new CodeGenerationContext(
                contextLocation: semanticModel.SyntaxTree.GetLocation(TextSpan.FromBounds(line.Start, line.Start))));

        var generatedMember = await GenerateMemberAsync(overriddenMember, containingType, document, completionItem, cancellationToken).ConfigureAwait(false);
        generatedMember = _annotation.AddAnnotationToSymbol(generatedMember);

        Document? memberContainingDocument = null;
        if (generatedMember.Kind == SymbolKind.Method)
        {
            memberContainingDocument = await codeGenService.AddMethodAsync(context, containingType, (IMethodSymbol)generatedMember, cancellationToken).ConfigureAwait(false);
        }
        else if (generatedMember.Kind == SymbolKind.Property)
        {
            memberContainingDocument = await codeGenService.AddPropertyAsync(context, containingType, (IPropertySymbol)generatedMember, cancellationToken).ConfigureAwait(false);
        }
        else if (generatedMember.Kind == SymbolKind.Event)
        {
            memberContainingDocument = await codeGenService.AddEventAsync(context, containingType, (IEventSymbol)generatedMember, cancellationToken).ConfigureAwait(false);
        }

        Contract.ThrowIfNull(memberContainingDocument);
        return memberContainingDocument!;
    }

    private TextSpan ComputeDestinationSpan(SyntaxNode insertionRoot)
    {
        var startToken = insertionRoot.GetAnnotatedTokens(_replaceStartAnnotation).FirstOrNull();
        Contract.ThrowIfNull(startToken);
        var endToken = insertionRoot.GetAnnotatedTokens(_replaceEndAnnotation).FirstOrNull();
        Contract.ThrowIfNull(endToken);

        var text = insertionRoot.GetText();
        var line = text.Lines.GetLineFromPosition(endToken.Value.Span.End);

        return TextSpan.FromBounds(startToken.Value.SpanStart, line.End);
    }

    private static async Task<Document> RemoveDestinationNodeAsync(
        Document memberContainingDocument, TextSpan destinationSpan, CancellationToken cancellationToken)
    {
        var root = await memberContainingDocument.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var destinationNode = root.FindNode(destinationSpan);
        var newRoot = root.RemoveNode(destinationNode, SyntaxRemoveOptions.KeepNoTrivia)!;
        return memberContainingDocument.WithSyntaxRoot(newRoot);
    }

    private static readonly ImmutableArray<CharacterSetModificationRule> s_commitRules = [CharacterSetModificationRule.Create(CharacterSetModificationKind.Replace, '(')];

    private static readonly ImmutableArray<CharacterSetModificationRule> s_filterRules = [CharacterSetModificationRule.Create(CharacterSetModificationKind.Remove, '(')];

    private static readonly CompletionItemRules s_defaultRules =
        CompletionItemRules.Create(
            commitCharacterRules: s_commitRules,
            filterCharacterRules: s_filterRules,
            enterKeyRule: EnterKeyRule.Never);

    protected static CompletionItemRules GetRules()
        => s_defaultRules;

    internal override Task<CompletionDescription> GetDescriptionWorkerAsync(Document document, CompletionItem item, CompletionOptions options, SymbolDescriptionOptions displayOptions, CancellationToken cancellationToken)
        => MemberInsertionCompletionItem.GetDescriptionAsync(item, document, displayOptions, cancellationToken);
}
