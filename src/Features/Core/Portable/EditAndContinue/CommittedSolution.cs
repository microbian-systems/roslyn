﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Debugging;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.EditAndContinue;

/// <summary>
/// Encapsulates access to the last committed solution.
/// We don't want to expose the solution directly since access to documents must be gated by out-of-sync checks.
/// </summary>
internal sealed class CommittedSolution
{
    private readonly DebuggingSession _debuggingSession;

    private Solution _solution;

    internal enum DocumentState
    {
        None = 0,

        /// <summary>
        /// The current document content does not match the content the module was compiled with.
        /// This document state may change to <see cref="MatchesBuildOutput"/> or <see cref="DesignTimeOnly"/>.
        /// </summary>
        OutOfSync = 1,

        /// <summary>
        /// It hasn't been possible to determine whether the current document content does matches the content 
        /// the module was compiled with due to error while reading the PDB or the source file.
        /// This document state may change to <see cref="MatchesBuildOutput"/> or <see cref="DesignTimeOnly"/>.
        /// </summary>
        Indeterminate = 2,

        /// <summary>
        /// The document is not compiled into the module. It's only included in the project
        /// to support design-time features such as completion, etc.
        /// This is a final state. Once a document is in this state it won't switch to a different one.
        /// </summary>
        DesignTimeOnly = 3,

        /// <summary>
        /// The current document content matches the content the built module was compiled with.
        /// This is a final state. Once a document is in this state it won't switch to a different one.
        /// </summary>
        MatchesBuildOutput = 4
    }

    /// <summary>
    /// Implements workaround for https://github.com/dotnet/project-system/issues/5457.
    /// 
    /// When debugging is started we capture the current solution snapshot.
    /// The documents in this snapshot might not match exactly to those that the compiler used to build the module 
    /// that's currently loaded into the debuggee. This is because there is no reliable synchronization between
    /// the (design-time) build and Roslyn workspace. Although Roslyn uses file-watchers to watch for changes in 
    /// the files on disk, the file-changed events raised by the build might arrive to Roslyn after the debugger
    /// has attached to the debuggee and EnC service captured the solution.
    /// 
    /// Ideally, the Project System would notify Roslyn at the end of each build what the content of the source
    /// files generated by various targets is. Roslyn would then apply these changes to the workspace and 
    /// the EnC service would capture a solution snapshot that includes these changes.
    /// 
    /// Since this notification is currently not available we check the current content of source files against
    /// the corresponding checksums stored in the PDB. Documents for which we have not observed source file content 
    /// that maches the PDB checksum are considered <see cref="DocumentState.OutOfSync"/>. 
    /// 
    /// Some documents in the workspace are added for design-time-only purposes and are not part of the compilation
    /// from which the assembly is built. These documents won't have a record in the PDB and will be tracked as 
    /// <see cref="DocumentState.DesignTimeOnly"/>.
    /// 
    /// A document state can only change from <see cref="DocumentState.OutOfSync"/> to <see cref="DocumentState.MatchesBuildOutput"/>.
    /// Once a document state is <see cref="DocumentState.MatchesBuildOutput"/> or <see cref="DocumentState.DesignTimeOnly"/>
    /// it will never change.
    /// </summary>
    private readonly Dictionary<DocumentId, DocumentState> _documentState = [];

    private readonly object _guard = new();

    public CommittedSolution(DebuggingSession debuggingSession, Solution solution, IEnumerable<KeyValuePair<DocumentId, DocumentState>> initialDocumentStates)
    {
        _solution = solution;
        _debuggingSession = debuggingSession;
        _documentState.AddRange(initialDocumentStates);
    }

    // test only
    internal void Test_SetDocumentState(DocumentId documentId, DocumentState state)
    {
        lock (_guard)
        {
            _documentState[documentId] = state;
        }
    }

    // test only
    internal ImmutableArray<(DocumentId id, DocumentState state)> Test_GetDocumentStates()
    {
        lock (_guard)
        {
            return _documentState.SelectAsArray(e => (e.Key, e.Value));
        }
    }

    public bool HasNoChanges(Solution solution)
        => _solution == solution;

    public Project? GetProject(ProjectId id)
        => _solution.GetProject(id);

    public Project GetRequiredProject(ProjectId id)
        => _solution.GetRequiredProject(id);

    public ImmutableArray<DocumentId> GetDocumentIdsWithFilePath(string path)
        => _solution.GetDocumentIdsWithFilePath(path);

    public bool ContainsDocument(DocumentId documentId)
        => _solution.ContainsDocument(documentId);

    /// <summary>
    /// Returns a document snapshot for given <see cref="Document"/> whose content exactly matches
    /// the source file used to compile the binary currently loaded in the debuggee. Returns null
    /// if it fails to find a document snapshot whose content hash maches the one recorded in the PDB.
    /// 
    /// The result is cached and the next lookup uses the cached value, including failures unless <paramref name="reloadOutOfSyncDocument"/> is true.
    /// </summary>
    public async Task<(Document? Document, DocumentState State)> GetDocumentAndStateAsync(DocumentId documentId, Document? currentDocument, CancellationToken cancellationToken, bool reloadOutOfSyncDocument = false)
    {
        Contract.ThrowIfFalse(currentDocument == null || documentId == currentDocument.Id);

        Solution solution;
        var documentState = DocumentState.None;

        lock (_guard)
        {
            solution = _solution;
            _documentState.TryGetValue(documentId, out documentState);
        }

        var committedDocument = solution.GetDocument(documentId);

        switch (documentState)
        {
            case DocumentState.MatchesBuildOutput:
                // Note: committedDocument is null if we previously validated that a document that is not in
                // the committed solution is also not in the PDB. This means the document has been added during debugging.
                return (committedDocument, documentState);

            case DocumentState.DesignTimeOnly:
                return (null, documentState);

            case DocumentState.OutOfSync:
                if (reloadOutOfSyncDocument)
                {
                    break;
                }

                return (null, documentState);

            case DocumentState.Indeterminate:
                // Previous attempt resulted in a read error. Try again.
                break;

            case DocumentState.None:
                // Have not seen the document before, the document is not in the solution, or the document is source generated.

                if (committedDocument == null)
                {
                    var sourceGeneratedDocument = await solution.GetSourceGeneratedDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
                    if (sourceGeneratedDocument != null)
                    {
                        // source generated files are never out-of-date:
                        return (sourceGeneratedDocument, DocumentState.MatchesBuildOutput);
                    }

                    // The current document is source-generated therefore the corresponding one is not present in the base solution.
                    if (currentDocument is SourceGeneratedDocument)
                    {
                        return (null, DocumentState.MatchesBuildOutput);
                    }
                }

                break;
        }

        // Document compiled into the baseline DLL/PDB may have been added to the workspace
        // after the committed solution snapshot was taken.
        var document = committedDocument ?? currentDocument;
        if (document == null)
        {
            // Document has been deleted.
            return (null, DocumentState.None);
        }

        // TODO: Handle case when the old project does not exist and needs to be added. https://github.com/dotnet/roslyn/issues/1204
        if (committedDocument == null && !solution.ContainsProject(document.Project.Id))
        {
            // Document in a new project that does not exist in the committed solution.
            // Pretend this document is design-time-only and ignore it.
            return (null, DocumentState.DesignTimeOnly);
        }

        if (!document.DocumentState.SupportsEditAndContinue())
        {
            return (null, DocumentState.DesignTimeOnly);
        }

        Contract.ThrowIfNull(document.FilePath);

        var sourceText = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
        var sourceTextVersion = (committedDocument == null) ? await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false) : default;

        var (maybeMatchingSourceText, maybePdbHasDocument) = await TryGetMatchingSourceTextAsync(document, sourceText, currentDocument, cancellationToken).ConfigureAwait(false);

        lock (_guard)
        {
            // only listed document states can be changed:
            if (_documentState.TryGetValue(documentId, out documentState) &&
                documentState != DocumentState.OutOfSync &&
                documentState != DocumentState.Indeterminate)
            {
                return (document, documentState);
            }

            DocumentState newState;
            Document? matchingDocument;

            if (!maybePdbHasDocument.HasValue)
            {
                // Unable to determine due to error reading the PDB.
                return (document, DocumentState.Indeterminate);
            }

            if (!maybePdbHasDocument.Value)
            {
                // Source file is not listed in the PDB.
                // It could either be a newly added document or a design-time-only document (e.g. WPF .g.i.cs files).
                // We can't distinguish between newly added document and newly added design-time-only document.
                matchingDocument = null;
                newState = (committedDocument != null) ? DocumentState.DesignTimeOnly : DocumentState.MatchesBuildOutput;
            }
            else if (!maybeMatchingSourceText.HasValue)
            {
                // Unable to determine due to error reading the source file.
                return (document, DocumentState.Indeterminate);
            }
            else
            {
                // Document exists in the PDB but not in the committed solution.
                // Add the document to the committed solution with its current (possibly out-of-sync) text.
                if (committedDocument == null)
                {
                    // TODO: Handle case when the old project does not exist and needs to be added. https://github.com/dotnet/roslyn/issues/1204
                    Debug.Assert(_solution.ContainsProject(documentId.ProjectId));

                    // TODO: Use API proposed in https://github.com/dotnet/roslyn/issues/56253.
                    _solution = _solution.AddDocument(DocumentInfo.Create(
                        documentId,
                        name: document.Name,
                        sourceCodeKind: document.SourceCodeKind,
                        folders: document.Folders,
                        loader: TextLoader.From(TextAndVersion.Create(sourceText, sourceTextVersion, document.Name)),
                        filePath: document.FilePath,
                        isGenerated: document.State.Attributes.IsGenerated)
                        .WithDesignTimeOnly(document.State.Attributes.DesignTimeOnly)
                        .WithDocumentServiceProvider(document.State.DocumentServiceProvider));
                }

                var matchingSourceText = maybeMatchingSourceText.Value;
                if (matchingSourceText != null)
                {
                    if (committedDocument != null && sourceText.ContentEquals(matchingSourceText))
                    {
                        matchingDocument = document;
                    }
                    else
                    {
                        _solution = _solution.WithDocumentText(documentId, matchingSourceText, PreservationMode.PreserveValue);
                        matchingDocument = _solution.GetDocument(documentId);
                    }

                    newState = DocumentState.MatchesBuildOutput;
                }
                else
                {
                    matchingDocument = null;
                    newState = DocumentState.OutOfSync;
                }
            }

            _documentState[documentId] = newState;
            return (matchingDocument, newState);
        }
    }

    private async ValueTask<(Optional<SourceText?> matchingSourceText, bool? hasDocument)> TryGetMatchingSourceTextAsync(Document document, SourceText sourceText, Document? currentDocument, CancellationToken cancellationToken)
    {
        Contract.ThrowIfNull(document.FilePath);

        var maybePdbHasDocument = TryReadSourceFileChecksumFromPdb(document, out var requiredChecksum, out var checksumAlgorithm);

        var maybeMatchingSourceText = (maybePdbHasDocument == true) ?
            await TryGetMatchingSourceTextAsync(sourceText, document.FilePath, currentDocument, _debuggingSession.SourceTextProvider, requiredChecksum, checksumAlgorithm, cancellationToken).ConfigureAwait(false) : default;

        return (maybeMatchingSourceText, maybePdbHasDocument);
    }

    private static async ValueTask<Optional<SourceText?>> TryGetMatchingSourceTextAsync(
        SourceText sourceText, string filePath, Document? currentDocument, IPdbMatchingSourceTextProvider sourceTextProvider, ImmutableArray<byte> requiredChecksum, SourceHashAlgorithm checksumAlgorithm, CancellationToken cancellationToken)
    {
        if (IsMatchingSourceText(sourceText, requiredChecksum, checksumAlgorithm))
        {
            return sourceText;
        }

        if (currentDocument != null)
        {
            var currentDocumentSourceText = await currentDocument.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
            if (IsMatchingSourceText(currentDocumentSourceText, requiredChecksum, checksumAlgorithm))
            {
                return currentDocumentSourceText;
            }
        }

        var text = await sourceTextProvider.TryGetMatchingSourceTextAsync(filePath, requiredChecksum, checksumAlgorithm, cancellationToken).ConfigureAwait(false);
        if (text != null)
        {
            return SourceText.From(text, sourceText.Encoding, checksumAlgorithm);
        }

        return await Task.Run(() => TryGetPdbMatchingSourceTextFromDisk(filePath, sourceText.Encoding, requiredChecksum, checksumAlgorithm), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IEnumerable<KeyValuePair<DocumentId, DocumentState>>> GetMatchingDocumentsAsync(
        IEnumerable<(Project, IEnumerable<CodeAnalysis.DocumentState>)> documentsByProject,
        Func<Project, CompilationOutputs> compilationOutputsProvider,
        IPdbMatchingSourceTextProvider sourceTextProvider,
        CancellationToken cancellationToken)
    {
        var projectTasks = documentsByProject.Select(async projectDocumentStates =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (project, documentStates) = projectDocumentStates;

            // Skip projects that do not support Roslyn EnC (e.g. F#, etc).
            // Source files of these may not even be captured in the solution snapshot.
            if (!project.SupportsEditAndContinue())
            {
                return Array.Empty<DocumentId?>();
            }

            using var debugInfoReaderProvider = GetMethodDebugInfoReader(compilationOutputsProvider(project), project.Name);
            if (debugInfoReaderProvider == null)
            {
                return Array.Empty<DocumentId?>();
            }

            var debugInfoReader = debugInfoReaderProvider.CreateEditAndContinueMethodDebugInfoReader();

            var documentTasks = documentStates.Select(async documentState =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (documentState.SupportsEditAndContinue())
                {
                    var sourceFilePath = documentState.FilePath;
                    Contract.ThrowIfNull(sourceFilePath);

                    // Hydrate the solution snapshot with the content of the file.
                    // It's important to do this before we start watching for changes so that we have a baseline we can compare future snapshots to.
                    var sourceText = await documentState.GetTextAsync(cancellationToken).ConfigureAwait(false);

                    // TODO: https://github.com/dotnet/roslyn/issues/51993
                    // avoid rereading the file in common case - the workspace should create source texts with the right checksum algorithm and encoding
                    if (TryReadSourceFileChecksumFromPdb(debugInfoReader, sourceFilePath, out var requiredChecksum, out var checksumAlgorithm) == true &&
                        await TryGetMatchingSourceTextAsync(sourceText, sourceFilePath, currentDocument: null, sourceTextProvider, requiredChecksum, checksumAlgorithm, cancellationToken).ConfigureAwait(false) is { HasValue: true, Value: not null })
                    {
                        return documentState.Id;
                    }
                }

                return null;
            });

            return await Task.WhenAll(documentTasks).ConfigureAwait(false);
        });

        var documentIdArrays = await Task.WhenAll(projectTasks).ConfigureAwait(false);

        return documentIdArrays.SelectMany(ids => ids.WhereNotNull()).Select(id => KeyValuePairUtil.Create(id, DocumentState.MatchesBuildOutput));
    }

    private static DebugInformationReaderProvider? GetMethodDebugInfoReader(CompilationOutputs compilationOutputs, string projectName)
    {
        DebugInformationReaderProvider? debugInfoReaderProvider;
        try
        {
            debugInfoReaderProvider = compilationOutputs.OpenPdb();

            if (debugInfoReaderProvider == null)
            {
                EditAndContinueService.Log.Write("Source file of project '{0}' doesn't match output PDB: PDB '{1}' (assembly: '{2}') not found", projectName, compilationOutputs.PdbDisplayPath, compilationOutputs.AssemblyDisplayPath);
            }

            return debugInfoReaderProvider;
        }
        catch (Exception e)
        {
            EditAndContinueService.Log.Write("Source file of project '{0}' doesn't match output PDB: error opening PDB '{1}' (assembly: '{2}'): {3}", projectName, compilationOutputs.PdbDisplayPath, compilationOutputs.AssemblyDisplayPath, e.Message);
            return null;
        }
    }

    public void CommitSolution(Solution solution)
    {
        lock (_guard)
        {
            _solution = solution;
        }
    }

    private static bool IsMatchingSourceText(SourceText sourceText, ImmutableArray<byte> requiredChecksum, SourceHashAlgorithm checksumAlgorithm)
        => checksumAlgorithm == sourceText.ChecksumAlgorithm && sourceText.GetChecksum().SequenceEqual(requiredChecksum);

    private static Optional<SourceText?> TryGetPdbMatchingSourceTextFromDisk(string sourceFilePath, Encoding? encoding, ImmutableArray<byte> requiredChecksum, SourceHashAlgorithm checksumAlgorithm)
    {
        try
        {
            using var fileStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            // We must use the encoding of the document as determined by the IDE (the editor).
            // This might differ from the encoding that the compiler chooses, so if we just relied on the compiler we 
            // might end up updating the committed solution with a document that has a different encoding than 
            // the one that's in the workspace, resulting in false document changes when we compare the two.
            var sourceText = SourceText.From(fileStream, encoding, checksumAlgorithm);

            if (IsMatchingSourceText(sourceText, requiredChecksum, checksumAlgorithm))
            {
                return sourceText;
            }

            EditAndContinueService.Log.Write("Checksum differs for source file '{0}'", sourceFilePath);

            // does not match:
            return null;
        }
        catch (Exception e)
        {
            EditAndContinueService.Log.Write("Error calculating checksum for source file '{0}': '{1}'", sourceFilePath, e.Message);

            // unable to determine:
            return default;
        }
    }

    private bool? TryReadSourceFileChecksumFromPdb(Document document, out ImmutableArray<byte> requiredChecksum, out SourceHashAlgorithm checksumAlgorithm)
    {
        Contract.ThrowIfNull(document.FilePath);

        var compilationOutputs = _debuggingSession.GetCompilationOutputs(document.Project);
        using var debugInfoReaderProvider = GetMethodDebugInfoReader(compilationOutputs, document.Project.Name);
        if (debugInfoReaderProvider == null)
        {
            // unable to determine whether document is in the PDB
            requiredChecksum = default;
            checksumAlgorithm = default;
            return null;
        }

        var debugInfoReader = debugInfoReaderProvider.CreateEditAndContinueMethodDebugInfoReader();
        return TryReadSourceFileChecksumFromPdb(debugInfoReader, document.FilePath, out requiredChecksum, out checksumAlgorithm);
    }

    /// <summary>
    /// Returns true if the PDB contains a document record for given <paramref name="sourceFilePath"/>,
    /// in which case <paramref name="checksum"/> and <paramref name="algorithm"/> contain its checksum.
    /// False if the document is not found in the PDB.
    /// Null if it can't be determined because the PDB is not available or an error occurred while reading the PDB.
    /// </summary>
    private static bool? TryReadSourceFileChecksumFromPdb(EditAndContinueMethodDebugInfoReader debugInfoReader, string sourceFilePath, out ImmutableArray<byte> checksum, out SourceHashAlgorithm algorithm)
    {
        checksum = default;
        algorithm = default;

        try
        {
            if (!debugInfoReader.TryGetDocumentChecksum(sourceFilePath, out checksum, out var algorithmId))
            {
                EditAndContinueService.Log.Write("Source '{0}' doesn't match output PDB: no document", sourceFilePath);
                return false;
            }

            algorithm = SourceHashAlgorithms.GetSourceHashAlgorithm(algorithmId);
            if (algorithm == SourceHashAlgorithm.None)
            {
                // This can only happen if the PDB was post-processed by a misbehaving tool.
                EditAndContinueService.Log.Write("Source '{0}' doesn't match PDB: unknown checksum alg", sourceFilePath);
            }

            return true;
        }
        catch (Exception e)
        {
            EditAndContinueService.Log.Write("Source '{0}' doesn't match output PDB: error reading symbols: {1}", sourceFilePath, e.Message);
        }

        // unable to determine
        return null;
    }
}
