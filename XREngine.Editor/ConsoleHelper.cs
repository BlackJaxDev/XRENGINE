using System;
using System.IO;
using XREngine.Native;

namespace XREngine.Editor
{
    internal static class ConsoleHelper
    {
        private static bool _initialized;

        internal static void EnsureConsoleAttached()
        {
            if (_initialized)
                return;

            if (!OperatingSystem.IsWindows())
            {
                _initialized = true;
                return;
            }

            if (!NativeMethods.EnsureConsoleForProcess())
                return;

            ResetStandardStreams();
            _initialized = true;
        }

        private static void ResetStandardStreams()
        {
            var standardOutput = Console.OpenStandardOutput();
            var standardError = Console.OpenStandardError();
            var standardInput = Console.OpenStandardInput();

            Console.SetOut(new StreamWriter(standardOutput) { AutoFlush = true });
            Console.SetError(new StreamWriter(standardError) { AutoFlush = true });
            Console.SetIn(new StreamReader(standardInput));
        }
    }
}
