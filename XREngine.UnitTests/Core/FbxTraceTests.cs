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
        Func<string, IDisposable?>? previousProfilerScopeFactory = FbxTrace.ProfilerScopeFactory;
        FbxLogVerbosity previousVerbosity = FbxTrace.Verbosity;
        TextWriter previousConsoleOut = Console.Out;
        using var consoleCapture = new StringWriter();
        using var listener = new CapturingTraceListener();

        try
        {
            Console.SetOut(consoleCapture);
            Trace.Listeners.Add(listener);

            FbxTrace.LogSink = null;
            FbxTrace.ProfilerScopeFactory = null;
            FbxTrace.Verbosity = FbxLogVerbosity.Info;

            FbxTrace.Info("Tests", "Trace fallback should stay off Trace listeners.");

            listener.Messages.ShouldBeEmpty();
            consoleCapture.ToString().ShouldStartWith("[FBX][Info][Tests] Trace fallback should stay off Trace listeners.");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Console.SetOut(previousConsoleOut);
            FbxTrace.LogSink = previousSink;
            FbxTrace.ProfilerScopeFactory = previousProfilerScopeFactory;
            FbxTrace.Verbosity = previousVerbosity;
        }
    }

    [Test]
    public void StartProfilerScopeNamed_UsesConfiguredFactoryWithFbxPrefix()
    {
        Func<string, IDisposable?>? previousProfilerScopeFactory = FbxTrace.ProfilerScopeFactory;
        string? capturedScopeName = null;

        try
        {
            FbxTrace.ProfilerScopeFactory = scopeName =>
            {
                capturedScopeName = scopeName;
                return new TestDisposable();
            };

            using IDisposable? scope = FbxTrace.StartProfilerScopeNamed("Tests", "NamedScope");

            capturedScopeName.ShouldBe("FBX.Tests.NamedScope");
            scope.ShouldNotBeNull();
        }
        finally
        {
            FbxTrace.ProfilerScopeFactory = previousProfilerScopeFactory;
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

    private sealed class TestDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}