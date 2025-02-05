﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.LanguageServer.ExternalAccess.Copilot;

internal readonly struct CopilotLspServices(LspServices lspServices)
{
    public T GetRequiredService<T>() where T : notnull
        => lspServices.GetRequiredService<T>();

    public T? GetService<T>() where T : notnull
        => lspServices.GetService<T>();
}
