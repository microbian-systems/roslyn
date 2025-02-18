﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Text;

namespace Microsoft.CodeAnalysis.DocumentationComments
{
    internal class DocumentationCommentSuggestion(CopilotGenerateDocumentationCommentProvider providerInstance, ProposalBase proposal,
        SuggestionManagerBase suggestionManager, VisualStudio.Threading.IAsyncDisposable? intellicodeLineCompletionsDisposable) : SuggestionBase
    {
        public ProposalBase Proposal { get; } = proposal;

        public SuggestionManagerBase SuggestionManager { get; } = suggestionManager;

        public VisualStudio.Threading.IAsyncDisposable? IntellicodeLineCompletionsDisposable { get; set; } = intellicodeLineCompletionsDisposable;

        public override TipStyle TipStyle => TipStyle.AlwaysShowTip;

        public override EditDisplayStyle EditStyle => EditDisplayStyle.GrayText;

        public override bool HasMultipleSuggestions => false;

        public override event PropertyChangedEventHandler? PropertyChanged;

        private SuggestionSessionBase? _suggestionSession;

        public override async Task OnAcceptedAsync(SuggestionSessionBase session, ProposalBase originalProposal, ProposalBase currentProposal, ReasonForAccept reason, CancellationToken cancel)
        {
            var threadingContext = providerInstance.ThreadingContext;

            await threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancel);
            await DisposeAsync().ConfigureAwait(false);
        }

        public override Task OnChangeProposalAsync(SuggestionSessionBase session, ProposalBase originalProposal, ProposalBase currentProposal, bool forward, CancellationToken cancel)
        {
            return Task.CompletedTask;
        }

        public override async Task OnDismissedAsync(SuggestionSessionBase session, ProposalBase? originalProposal, ProposalBase? currentProposal, ReasonForDismiss reason, CancellationToken cancel)
        {
            var threadingContext = providerInstance.ThreadingContext;
            if (threadingContext != null)
            {
                await threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancel);
                await ClearSuggestionAsync(reason, cancel).ConfigureAwait(false);
            }
        }

        public override Task OnProposalUpdatedAsync(SuggestionSessionBase session, ProposalBase? originalProposal, ProposalBase? currentProposal, ReasonForUpdate reason, VirtualSnapshotPoint caret, CompletionState? completionState, CancellationToken cancel)
        {
            if (reason.HasFlag(ReasonForUpdate.Diverged))
            {
                return session.DismissAsync(ReasonForDismiss.DismissedAfterBufferChange, cancel);
            }

            return Task.CompletedTask;
        }

        public async Task<SuggestionSessionBase?> GetSuggestionSessionAsync(CancellationToken cancellationToken)
        {
            return _suggestionSession = await SuggestionManager.TryDisplaySuggestionAsync(this, cancellationToken).ConfigureAwait(false);
        }

        private async Task ClearSuggestionAsync(ReasonForDismiss reason, CancellationToken cancellationToken)
        {
            if (_suggestionSession != null)
            {
                await _suggestionSession.DismissAsync(reason, cancellationToken).ConfigureAwait(false);
            }

            _suggestionSession = null;
            await DisposeAsync().ConfigureAwait(false);
        }

        private async Task DisposeAsync()
        {
            if (IntellicodeLineCompletionsDisposable != null)
            {
                await IntellicodeLineCompletionsDisposable.DisposeAsync().ConfigureAwait(false);
                IntellicodeLineCompletionsDisposable = null;
            }
        }
    }
}
