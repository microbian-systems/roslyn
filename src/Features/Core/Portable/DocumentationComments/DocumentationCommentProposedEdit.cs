﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.DocumentationComments;

internal sealed class DocumentationCommentProposedEdit
{
    public TextSpan SpanToReplace { get; }

    public string? SymbolName { get; }

    public DocumentationCommentTagType TagType { get; }

    internal DocumentationCommentProposedEdit(TextSpan spanToReplace, string? symbolName, DocumentationCommentTagType tagType)
    {
        SpanToReplace = spanToReplace;
        SymbolName = symbolName;
        TagType = tagType;
    }
}
