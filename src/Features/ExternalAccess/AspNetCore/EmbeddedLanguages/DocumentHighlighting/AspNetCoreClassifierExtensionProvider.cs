﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;
using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.Internal.EmbeddedLanguages
{
    internal sealed class AspNetCoreDocumentHighlighterExtensionProvider
        : AbstractProjectExtensionProvider<
            AspNetCoreDocumentHighlighterExtensionProvider,
            IAspNetCoreEmbeddedLanguageDocumentHighlighter,
            ExportAspNetCoreEmbeddedLanguageDocumentHighlighterAttribute>
    {
        protected override ImmutableArray<string> GetLanguages(ExportAspNetCoreEmbeddedLanguageDocumentHighlighterAttribute exportAttribute)
            => [exportAttribute.Language];

        protected override bool TryGetExtensionsFromReference(AnalyzerReference reference, out ImmutableArray<IAspNetCoreEmbeddedLanguageDocumentHighlighter> extensions)
        {
            extensions = default;
            return false;
        }
    }
}
