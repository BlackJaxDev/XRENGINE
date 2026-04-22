using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

namespace XREngine.Rendering;

public static class UberShaderVariantTelemetry
{
    private static readonly Regex VariantHashRegex = new(
        @"^[ \t]*//[ \t]*variant-hash:[ \t]*0x(?<hash>[0-9A-Fa-f]{1,16})[ \t]*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<ulong, BackendSnapshot> _backendSnapshots = new();

    private static long _requestCount;
    private static long _successCount;
    private static long _failureCount;
    private static long _cacheHitCount;
    private static long _totalPreparationTicks;
    private static long _totalAdoptionTicks;
    private static long _totalCompileTicks;
    private static long _totalLinkTicks;
    private static long _totalGeneratedSourceBytes;
    private static long _adoptionSampleCount;
    private static long _compileSampleCount;
    private static long _linkSampleCount;

    internal static void ResetForTests()
    {
        _backendSnapshots.Clear();
        Interlocked.Exchange(ref _requestCount, 0);
        Interlocked.Exchange(ref _successCount, 0);
        Interlocked.Exchange(ref _failureCount, 0);
        Interlocked.Exchange(ref _cacheHitCount, 0);
        Interlocked.Exchange(ref _totalPreparationTicks, 0);
        Interlocked.Exchange(ref _totalAdoptionTicks, 0);
        Interlocked.Exchange(ref _totalCompileTicks, 0);
        Interlocked.Exchange(ref _totalLinkTicks, 0);
        Interlocked.Exchange(ref _totalGeneratedSourceBytes, 0);
        Interlocked.Exchange(ref _adoptionSampleCount, 0);
        Interlocked.Exchange(ref _compileSampleCount, 0);
        Interlocked.Exchange(ref _linkSampleCount, 0);
    }

    public enum BackendStage
    {
        None,
        Compiling,
        Linking,
        Ready,
        Failed,
    }

    public readonly record struct BackendSnapshot(
        BackendStage Stage,
        double CompileMilliseconds,
        double LinkMilliseconds,
        string? FailureReason);

    public readonly record struct Snapshot(
        long RequestCount,
        long SuccessCount,
        long FailureCount,
        long CacheHitCount,
        double AveragePreparationMilliseconds,
        double AverageAdoptionMilliseconds,
        double AverageCompileMilliseconds,
        double AverageLinkMilliseconds,
        double AverageGeneratedSourceBytes)
    {
        public double CacheHitRate
            => SuccessCount <= 0 ? 0.0 : (double)CacheHitCount / SuccessCount;
    }

    internal static void RecordRequest()
        => Interlocked.Increment(ref _requestCount);

    internal static void RecordSuccess(UberMaterialVariantStatus status)
    {
        Interlocked.Increment(ref _successCount);
        if (status.CacheHit)
            Interlocked.Increment(ref _cacheHitCount);

        Interlocked.Add(ref _totalPreparationTicks, TimeSpan.FromMilliseconds(status.PreparationMilliseconds).Ticks);
        Interlocked.Add(ref _totalGeneratedSourceBytes, status.GeneratedSourceLength);

        Interlocked.Increment(ref _adoptionSampleCount);
        Interlocked.Add(ref _totalAdoptionTicks, TimeSpan.FromMilliseconds(status.AdoptionMilliseconds).Ticks);
    }

    internal static void RecordFailure()
        => Interlocked.Increment(ref _failureCount);

    public static bool TryParseVariantHash(string? shaderSource, out ulong variantHash)
    {
        variantHash = 0;
        if (string.IsNullOrWhiteSpace(shaderSource))
            return false;

        Match match = VariantHashRegex.Match(shaderSource);
        return match.Success &&
               ulong.TryParse(match.Groups["hash"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out variantHash);
    }

    public static bool TryGetBackendSnapshot(ulong variantHash, out BackendSnapshot snapshot)
    {
        if (variantHash == 0)
        {
            snapshot = default;
            return false;
        }

        return _backendSnapshots.TryGetValue(variantHash, out snapshot);
    }

    public static void RecordBackendCompileStarted(ulong variantHash)
    {
        if (variantHash == 0)
            return;

        _backendSnapshots.AddOrUpdate(
            variantHash,
            static _ => new BackendSnapshot(BackendStage.Compiling, 0.0, 0.0, null),
            static (_, existing) => existing with
            {
                Stage = BackendStage.Compiling,
                CompileMilliseconds = 0.0,
                LinkMilliseconds = 0.0,
                FailureReason = null,
            });
    }

    public static void RecordBackendLinkStarted(ulong variantHash, double compileMilliseconds)
    {
        if (variantHash == 0)
            return;

        _backendSnapshots.AddOrUpdate(
            variantHash,
            _ => new BackendSnapshot(BackendStage.Linking, compileMilliseconds, 0.0, null),
            (_, existing) => existing with
            {
                Stage = BackendStage.Linking,
                CompileMilliseconds = compileMilliseconds > 0.0 ? compileMilliseconds : existing.CompileMilliseconds,
                LinkMilliseconds = 0.0,
                FailureReason = null,
            });
    }

    public static void RecordBackendSuccess(ulong variantHash, double compileMilliseconds, double linkMilliseconds)
    {
        if (variantHash == 0)
            return;

        _backendSnapshots.AddOrUpdate(
            variantHash,
            _ => new BackendSnapshot(BackendStage.Ready, compileMilliseconds, linkMilliseconds, null),
            (_, existing) => existing with
            {
                Stage = BackendStage.Ready,
                CompileMilliseconds = compileMilliseconds > 0.0 ? compileMilliseconds : existing.CompileMilliseconds,
                LinkMilliseconds = linkMilliseconds > 0.0 ? linkMilliseconds : existing.LinkMilliseconds,
                FailureReason = null,
            });

        if (compileMilliseconds > 0.0)
        {
            Interlocked.Increment(ref _compileSampleCount);
            Interlocked.Add(ref _totalCompileTicks, TimeSpan.FromMilliseconds(compileMilliseconds).Ticks);
        }

        if (linkMilliseconds > 0.0)
        {
            Interlocked.Increment(ref _linkSampleCount);
            Interlocked.Add(ref _totalLinkTicks, TimeSpan.FromMilliseconds(linkMilliseconds).Ticks);
        }
    }

    public static void RecordBackendFailure(ulong variantHash, string? failureReason, double compileMilliseconds, double linkMilliseconds)
    {
        if (variantHash == 0)
            return;

        _backendSnapshots.AddOrUpdate(
            variantHash,
            _ => new BackendSnapshot(BackendStage.Failed, compileMilliseconds, linkMilliseconds, failureReason),
            (_, existing) => existing with
            {
                Stage = BackendStage.Failed,
                CompileMilliseconds = compileMilliseconds > 0.0 ? compileMilliseconds : existing.CompileMilliseconds,
                LinkMilliseconds = linkMilliseconds > 0.0 ? linkMilliseconds : existing.LinkMilliseconds,
                FailureReason = failureReason,
            });
    }

    public static Snapshot GetSnapshot()
    {
        long requestCount = Interlocked.Read(ref _requestCount);
        long successCount = Interlocked.Read(ref _successCount);
        long failureCount = Interlocked.Read(ref _failureCount);
        long cacheHitCount = Interlocked.Read(ref _cacheHitCount);
        long totalPreparationTicks = Interlocked.Read(ref _totalPreparationTicks);
        long totalAdoptionTicks = Interlocked.Read(ref _totalAdoptionTicks);
        long totalCompileTicks = Interlocked.Read(ref _totalCompileTicks);
        long totalLinkTicks = Interlocked.Read(ref _totalLinkTicks);
        long totalGeneratedSourceBytes = Interlocked.Read(ref _totalGeneratedSourceBytes);
        long adoptionSampleCount = Interlocked.Read(ref _adoptionSampleCount);
        long compileSampleCount = Interlocked.Read(ref _compileSampleCount);
        long linkSampleCount = Interlocked.Read(ref _linkSampleCount);

        double averagePreparationMs = successCount <= 0 ? 0.0 : TimeSpan.FromTicks(totalPreparationTicks / successCount).TotalMilliseconds;
        double averageAdoptionMs = adoptionSampleCount <= 0 ? 0.0 : TimeSpan.FromTicks(totalAdoptionTicks / adoptionSampleCount).TotalMilliseconds;
        double averageCompileMs = compileSampleCount <= 0 ? 0.0 : TimeSpan.FromTicks(totalCompileTicks / compileSampleCount).TotalMilliseconds;
        double averageLinkMs = linkSampleCount <= 0 ? 0.0 : TimeSpan.FromTicks(totalLinkTicks / linkSampleCount).TotalMilliseconds;
        double averageGeneratedSourceBytes = successCount <= 0 ? 0.0 : (double)totalGeneratedSourceBytes / successCount;

        return new Snapshot(
            requestCount,
            successCount,
            failureCount,
            cacheHitCount,
            averagePreparationMs,
            averageAdoptionMs,
            averageCompileMs,
            averageLinkMs,
            averageGeneratedSourceBytes);
    }
}