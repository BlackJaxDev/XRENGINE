using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using JoltPhysicsSharp;

namespace XREngine.Scene.Physics.Jolt
{
    /// <summary>
    /// Minimal Jolt initialization - matches official JoltPhysicsSharp samples exactly.
    /// </summary>
    internal static class JoltBootstrap
    {
        private static int _initialized;

        public static void EnsureInitialized()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0)
                return;

            // Write to console and file immediately for debugging
            var logPath = Path.Combine(Path.GetTempPath(), "jolt_init.log");
            var msg = $"[{DateTime.Now:O}] JoltBootstrap.EnsureInitialized() called. BaseDir={AppContext.BaseDirectory}, Arch={RuntimeInformation.ProcessArchitecture}";
            Console.WriteLine(msg);
            try { File.AppendAllText(logPath, msg + Environment.NewLine); } catch { }

            // Set up trace handler for diagnostics (optional but helps debugging)
            Foundation.SetTraceHandler((message) =>
            {
                System.Diagnostics.Debug.WriteLine($"[Jolt] {message}");
                Console.WriteLine($"[Jolt] {message}");
            });

#if DEBUG
            // Set up assert handler in debug builds
            Foundation.SetAssertFailureHandler((expression, message, file, line) =>
            {
                string outMessage = $"[Jolt] Assertion failure at {file}:{line}: {message ?? expression}";
                System.Diagnostics.Debug.WriteLine(outMessage);
                Console.WriteLine(outMessage);
                // Return true to break into debugger, false to continue
                return true;
            });
#endif

            // Initialize Jolt - this MUST be called before any other Jolt API
            Console.WriteLine("[JoltBootstrap] Calling Foundation.Init()...");
            try { File.AppendAllText(logPath, $"[{DateTime.Now:O}] Calling Foundation.Init()...{Environment.NewLine}"); } catch { }
            
            bool initResult;
            try
            {
                initResult = Foundation.Init(doublePrecision: false);
            }
            catch (Exception ex)
            {
                var errMsg = $"[JoltBootstrap] Foundation.Init() threw: {ex.GetType().Name}: {ex.Message}";
                Console.WriteLine(errMsg);
                try { File.AppendAllText(logPath, errMsg + Environment.NewLine); } catch { }
                throw;
            }

            if (!initResult)
            {
                throw new InvalidOperationException("Jolt Foundation.Init() failed. The native joltc.dll may not be found or is incompatible.");
            }

            Console.WriteLine("[JoltBootstrap] Foundation.Init() succeeded!");
            try { File.AppendAllText(logPath, $"[{DateTime.Now:O}] Foundation.Init() succeeded!{Environment.NewLine}"); } catch { }

            // Register shutdown handler
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    Foundation.Shutdown();
                }
                catch
                {
                    // Best-effort shutdown
                }
            };
        }
    }
}
