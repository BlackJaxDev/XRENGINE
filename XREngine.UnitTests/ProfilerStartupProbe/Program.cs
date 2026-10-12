using System.Reflection;
using XREngine;
using XREngine.Execution;
using XREngine.RenderBench;
using XREngine.Runtime.Diagnostics.Native;

namespace XREngine.UnitTests.ProfilerStartupProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Use one fresh process per mode: registered-engine, missing-provider, or renderbench-scope.");
            return 2;
        }

        try
        {
            NativeDebugBackendRegistration.EnsureRegistered();
            switch (args[0])
            {
                case "registered-engine":
                    ThreadedWorkerBackend.EnsureRegistered();
                    VerifyEngineStartup();
                    break;
                case "missing-provider":
                    VerifyMissingProvider();
                    break;
                case "renderbench-scope":
                    VerifyRenderBenchScope();
                    break;
                default:
                    Console.Error.WriteLine($"Unknown mode: {args[0]}");
                    return 2;
            }

            Console.WriteLine($"PASS {args[0]}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void VerifyEngineStartup()
    {
        Engine.CodeProfiler profiler = Engine.Profiler;
        try
        {
#if DEBUG
            Require(profiler.EnableFrameLogging, "DEBUG Engine did not enable frame logging at first access.");
#else
            Require(!profiler.EnableFrameLogging, "Release Engine enabled frame logging before an explicit request.");
            profiler.EnableFrameLogging = true;
            Require(profiler.EnableFrameLogging, "Release Engine did not enable requested frame logging.");
#endif
        }
        finally
        {
            profiler.EnableFrameLogging = false;
        }
    }

    private static void VerifyMissingProvider()
    {
#if DEBUG
        try
        {
            _ = Engine.Profiler;
        }
        catch (TypeInitializationException ex) when (ContainsUnavailableDiagnostic(ex))
        {
            return;
        }
        throw new InvalidOperationException("DEBUG Engine first access did not report the missing profiler worker.");
#else
        Engine.CodeProfiler profiler = Engine.Profiler;
        Require(!profiler.EnableFrameLogging, "Release Engine enabled frame logging without an explicit request.");
        try
        {
            profiler.EnableFrameLogging = true;
        }
        catch (InvalidOperationException ex) when (ContainsUnavailableDiagnostic(ex))
        {
            profiler.EnableFrameLogging = false;
            return;
        }
        profiler.EnableFrameLogging = false;
        throw new InvalidOperationException("Release frame-logging enable did not report the missing profiler worker.");
#endif
    }

    private static void VerifyRenderBenchScope()
    {
        // Reference the production executable and invoke its internal scope in this fresh process.
        Assembly assembly = typeof(RenderBenchPhase).Assembly;
        Type scopeType = assembly.GetType("XREngine.RenderBench.RenderBenchWorkSchedulerScope", throwOnError: true)!;
        MethodInfo ensureInstalled = scopeType.GetMethod("EnsureInstalled", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(scopeType.FullName, "EnsureInstalled");
        IDisposable scope = ensureInstalled.Invoke(null, null) as IDisposable
            ?? throw new InvalidOperationException("RenderBench did not return its scheduler scope.");
        try
        {
            Require(RuntimeWorkScheduler.Scheduler is not null, "RenderBench did not install a work scheduler.");
            VerifyEngineStartup();
        }
        finally
        {
            scope.Dispose();
        }
        Require(RuntimeWorkScheduler.Scheduler is null, "RenderBench did not release its owned work scheduler.");
    }

    private static bool ContainsUnavailableDiagnostic(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException &&
                current.Message.Contains("Execution.ProfilerWorkerUnavailable", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
