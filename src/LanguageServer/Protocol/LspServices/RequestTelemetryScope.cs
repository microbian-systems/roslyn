﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer;

internal sealed class RequestTelemetryScope(string name, RequestTelemetryLogger telemetryLogger)
    : AbstractRequestScope(name)
{
    private readonly RequestTelemetryLogger _telemetryLogger = telemetryLogger;
    private RequestTelemetryLogger.Result _result = RequestTelemetryLogger.Result.Succeeded;
    private readonly SharedStopwatch _stopwatch = SharedStopwatch.StartNew();
    private TimeSpan _queuedDuration;

    public override void RecordExecutionStart()
    {
        _queuedDuration = _stopwatch.Elapsed;
    }

    public override void RecordCancellation()
    {
        _result = RequestTelemetryLogger.Result.Cancelled;
    }

    public override void RecordException(Exception exception)
    {
        // Report a NFW report for the request failure, as well as recording statistics on the failure.
        ReportNonFatalError(exception);

        _result = RequestTelemetryLogger.Result.Failed;
    }

    public override void RecordWarning(string message)
    {
        _result = RequestTelemetryLogger.Result.Failed;
    }

    public override void Dispose()
    {
        var requestDuration = _stopwatch.Elapsed;

        _telemetryLogger.UpdateTelemetryData(Name, Language, _queuedDuration, requestDuration, _result);
    }

    private static void ReportNonFatalError(Exception exception)
    {
        if (exception is StreamJsonRpc.LocalRpcException localRpcException && localRpcException.ErrorCode == LspErrorCodes.ContentModified)
        {
            // Content modified exceptions are expected and should not be reported as NFWs.
            return;
        }

        FatalError.ReportAndPropagateUnlessCanceled(exception, ErrorSeverity.Critical);
    }
}
