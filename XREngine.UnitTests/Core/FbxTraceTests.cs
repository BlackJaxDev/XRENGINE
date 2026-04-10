using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Fbx;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class FbxTraceTests
{
    [Test]
    [NonParallelizable]
    public void TraceFallback_DoesNotWriteToTraceListeners_WhenNoSinkIsConfigured()
    {
        Action<string>? previousSink = FbxTrace.LogSink;
        FbxLogVerbosity previousVerbosity = FbxTrace.Verbosity;
        TextWriter previousConsoleOut = Console.Out;
        using var consoleCapture = new StringWriter();
        using var listener = new CapturingTraceListener();

        try
        {
            Console.SetOut(consoleCapture);
            Trace.Listeners.Add(listener);

            FbxTrace.LogSink = null;
            FbxTrace.Verbosity = FbxLogVerbosity.Info;

            FbxTrace.Info("Tests", "Trace fallback should stay off Trace listeners.");

            listener.Messages.ShouldBeEmpty();
            consoleCapture.ToString().ShouldContain("[FBX]");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Console.SetOut(previousConsoleOut);
            FbxTrace.LogSink = previousSink;
            FbxTrace.Verbosity = previousVerbosity;
        }
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message))
                Messages.Add(message);
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message))
                Messages.Add(message);
        }
    }
}