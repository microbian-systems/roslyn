// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.ImplementType;
using Microsoft.CodeAnalysis.Options;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.ExternalAccess.OmniSharp.Options;

using Workspace = CodeAnalysis.Workspace;

internal static class OmniSharpSolutionAnalyzerConfigOptionsUpdater
{
    internal static bool UpdateOptions(Workspace workspace, OmniSharpEditorConfigOptions editorConfigOptions)
    {
        try
        {
            var oldSolution = workspace.CurrentSolution;
            var oldFallbackOptions = oldSolution.FallbackAnalyzerOptions;
            oldFallbackOptions.TryGetValue(LanguageNames.CSharp, out var csharpFallbackOptions);

            var builder = ImmutableDictionary.CreateBuilder<string, string>(AnalyzerConfigOptions.KeyComparer);
            if (csharpFallbackOptions is not null)
            {
                // copy existing option values:
                foreach (var oldKey in csharpFallbackOptions.Keys)
                {
                    if (csharpFallbackOptions.TryGetValue(oldKey, out var oldValue))
                    {
                        builder.Add(oldKey, oldValue);
                    }
                }
            }

            // add o# option values:
            var lineFormattingOptions = editorConfigOptions.LineFormattingOptions;
            AddOption(FormattingOptions2.UseTabs, lineFormattingOptions.UseTabs, builder);
            AddOption(FormattingOptions2.TabSize, lineFormattingOptions.TabSize, builder);
            AddOption(FormattingOptions2.IndentationSize, lineFormattingOptions.IndentationSize, builder);
            AddOption(FormattingOptions2.NewLine, lineFormattingOptions.NewLine, builder);

            var implementTypeOptions = editorConfigOptions.ImplementTypeOptions;
            AddOption(ImplementTypeOptionsStorage.InsertionBehavior, (ImplementTypeInsertionBehavior)implementTypeOptions.InsertionBehavior, builder);
            AddOption(ImplementTypeOptionsStorage.PropertyGenerationBehavior, (ImplementTypePropertyGenerationBehavior)implementTypeOptions.PropertyGenerationBehavior, builder);

            var newFallbackOptions = oldFallbackOptions.SetItem(
                LanguageNames.CSharp,
                StructuredAnalyzerConfigOptions.Create(new DictionaryAnalyzerConfigOptions(builder.ToImmutable())));

            var newSolution = oldSolution.WithFallbackAnalyzerOptions(newFallbackOptions);
            return workspace.TryApplyChanges(newSolution);
        }
        catch (Exception e) when (FatalError.ReportAndPropagate(e, ErrorSeverity.Diagnostic))
        {
            throw ExceptionUtilities.Unreachable();
        }

        static void AddOption<T>(
            PerLanguageOption2<T> option,
            T value,
            ImmutableDictionary<string, string>.Builder builder)
        {
            var configName = option.Definition.ConfigName;
            var configValue = option.Definition.Serializer.Serialize(value);
            builder.Add(configName, configValue);
        }
    }
}
