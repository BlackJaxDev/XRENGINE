using System;
using JoltPhysicsSharp;

namespace XREngine.Scene.Physics.Jolt
{
    /// <summary>
    /// Owns process-wide Jolt initialization without requiring host filesystem services.
    /// </summary>
    internal static class JoltBootstrap
    {
        private static readonly Lock InitializationLock = new();
        private static bool _initialized;

        public static void EnsureInitialized()
        {
            lock (InitializationLock)
            {
                if (_initialized)
                    return;

                Foundation.SetTraceHandler(static message =>
                {
                    System.Diagnostics.Debug.WriteLine($"[Jolt] {message}");
                    Console.WriteLine($"[Jolt] {message}");
                });

#if DEBUG
                Foundation.SetAssertFailureHandler(static (expression, message, file, line) =>
                {
                    string outMessage = $"[Jolt] Assertion failure at {file}:{line}: {message ?? expression}";
                    System.Diagnostics.Debug.WriteLine(outMessage);
                    Console.WriteLine(outMessage);
                    return true;
                });
#endif
                if (!Foundation.Init(doublePrecision: false))
                    throw new InvalidOperationException("Jolt Foundation.Init() failed. The selected native joltc module may be missing or incompatible.");

                // Publish success only after initialization; a failed attempt must not cause a
                // later scene to silently use an uninitialized native module.
                _initialized = true;
                if (!OperatingSystem.IsBrowser())
                {
                    AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
                    {
                        try { Foundation.Shutdown(); }
                        catch { /* The process is already exiting. */ }
                    };
                }
            }
        }
    }
}
