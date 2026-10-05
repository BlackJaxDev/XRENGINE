using System.Diagnostics;
using System.Text.Json;

namespace XREngine.RenderBench;

/// <summary>Exports queue-local GPU intervals alongside CPU spans only when clock calibration is available.</summary>
public static class RenderBenchTraceExporter
{
    public static void Write(string path, string? cpuTracePath, RenderBenchGpuDiagnosticSnapshot gpu)
    {
        using FileStream stream = File.Create(path);
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartObject();
        writer.WriteString("displayTimeUnit", "ms");
        bool correlated = gpu.PreCalibration.HasValue;
        writer.WriteBoolean("clocksCorrelated", correlated);
        writer.WriteString("hostTimeDomain", "QueryPerformanceCounter");
        writer.WriteString("deviceTimeDomain", "Device");
        double uncertainty = gpu.PreCalibration?.MaximumDeviationNanoseconds ?? 0;
        if (gpu.PostCalibration is { } post && gpu.PreCalibration is { } pre)
        {
            double hostElapsed = SignedDelta(post.QueryPerformanceCounterTicks, pre.QueryPerformanceCounterTicks, 64) * 1e9 / Stopwatch.Frequency;
            double gpuElapsed = SignedDelta(post.DeviceTicks, pre.DeviceTicks, gpu.TimestampValidBits) * gpu.TimestampPeriodNanoseconds;
            uncertainty = Math.Max(pre.MaximumDeviationNanoseconds, post.MaximumDeviationNanoseconds) + Math.Abs(hostElapsed - gpuElapsed);
        }
        if (correlated)
            writer.WriteNumber("calibrationUncertaintyNanoseconds", uncertainty);
        writer.WritePropertyName("traceEvents");
        writer.WriteStartArray();
        if (correlated && cpuTracePath is not null)
        {
            using JsonDocument cpu = JsonDocument.Parse(File.ReadAllText(cpuTracePath));
            foreach (JsonElement entry in cpu.RootElement.GetProperty("traceEvents").EnumerateArray())
                entry.WriteTo(writer);
        }
        foreach (RenderBenchGpuPassSample sample in gpu.Samples)
        {
            double start = sample.BeginTicks * gpu.TimestampPeriodNanoseconds / 1000.0;
            if (gpu.PreCalibration is { } calibration)
                start = calibration.QueryPerformanceCounterTicks * 1e6 / Stopwatch.Frequency +
                    SignedDelta(sample.BeginTicks, calibration.DeviceTicks, gpu.TimestampValidBits) * gpu.TimestampPeriodNanoseconds / 1000.0;
            writer.WriteStartObject();
            writer.WriteString("name", sample.Target);
            writer.WriteString("cat", correlated ? "Vulkan GPU calibrated" : "Vulkan GPU queue-local");
            writer.WriteString("ph", "X");
            writer.WriteNumber("pid", Environment.ProcessId);
            writer.WriteNumber("tid", -(long)sample.QueueFamilyIndex - 1);
            writer.WriteNumber("ts", start);
            writer.WriteNumber("dur", sample.ElapsedNanoseconds / 1000.0);
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            writer.WriteNumber("frameId", sample.SourceFrameId);
            writer.WriteNumber("queueFamily", sample.QueueFamilyIndex);
            writer.WriteBoolean("correlated", correlated);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static double SignedDelta(ulong value, ulong origin, uint bits)
    {
        if (bits == 64)
            return unchecked((long)(value - origin));
        ulong modulus = 1UL << checked((int)bits);
        ulong delta = unchecked(value - origin) & (modulus - 1);
        return delta >= modulus / 2 ? (double)delta - modulus : delta;
    }
}
