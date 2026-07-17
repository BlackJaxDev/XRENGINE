using System;
using XREngine.Timers;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                private static void AddNonNegative(ref int field, int value)
                {
                    if (value > 0)
                        Interlocked.Add(ref field, value);
                }

                private static string NormalizeDescriptorBindingClass(string? bindingClass)
                {
                    if (string.IsNullOrWhiteSpace(bindingClass))
                        return "sampled-image";

                    ReadOnlySpan<char> value = bindingClass.AsSpan().Trim();
                    if (value.Equals("storage-image", StringComparison.OrdinalIgnoreCase))
                        return "storage-image";
                    if (value.Equals("uniform-buffer", StringComparison.OrdinalIgnoreCase))
                        return "uniform-buffer";
                    if (value.Equals("storage-buffer", StringComparison.OrdinalIgnoreCase))
                        return "storage-buffer";
                    if (value.Equals("texel-buffer", StringComparison.OrdinalIgnoreCase))
                        return "texel-buffer";
                    if (value.Equals("sampled-image", StringComparison.OrdinalIgnoreCase))
                        return "sampled-image";

                    return bindingClass;
                }

                private static string AppendDiagnosticToken(string existing, string token, int maxLength = 2048)
                {
                    token = TruncateDiagnosticText(token, 512);
                    if (string.IsNullOrEmpty(token))
                        return existing;

                    if (string.IsNullOrEmpty(existing))
                        return token.Length <= maxLength ? token : token[..maxLength];

                    string appended = existing + " | " + token;
                    return appended.Length <= maxLength ? appended : appended[..maxLength];
                }

                private static string TruncateDiagnosticText(string? value, int maxLength = 512)
                {
                    if (string.IsNullOrWhiteSpace(value))
                        return string.Empty;

                    value = value.Trim();
                    return value.Length <= maxLength
                        ? value
                        : value[..maxLength];
                }

                private static void UpdateMaxCounter(ref int target, int candidate)
                {
                    int current;
                    while (candidate > (current = Volatile.Read(ref target)) &&
                           Interlocked.CompareExchange(ref target, candidate, current) != current)
                    {
                    }
                }

                private static void UpdateMaxCounter(ref long target, long candidate)
                {
                    long current;
                    while (candidate > (current = Interlocked.Read(ref target)) &&
                           Interlocked.CompareExchange(ref target, candidate, current) != current)
                    {
                    }
                }

                private static double StopwatchTicksToMilliseconds(long ticks)
                    => EngineTimer.TicksToSeconds(Math.Max(0L, ticks)) * 1000.0;
            }
        }
    }
}
