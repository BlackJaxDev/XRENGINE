using System.Diagnostics;
using System.Text;

namespace XREngine.Rendering;

internal enum RenderWorkSubsystem
{
    TextureUpload,
    ShadowAtlas,
    ShaderUpload,
    MeshUpload,
}

internal readonly record struct RenderWorkBudgetSnapshot(
    long FrameId,
    double TextureUploadBudgetMilliseconds,
    double TextureUploadConsumedMilliseconds,
    int TextureUploadQueueDepth,
    int UrgentTextureRepairQueueDepth,
    int ShadowAtlasQueueDepth,
    int ShaderUploadQueueDepth,
    int MeshUploadQueueDepth,
    double OldestTextureQueueWaitMilliseconds,
    double LastShadowAtlasMilliseconds,
    bool StartupBoostActive);

internal static class RenderWorkBudgetCoordinator
{
    private const int StartupBoostMaxFrames = 300;
    private const double StartupBoostMaxMilliseconds = 5000.0;
    private const double StartupBoostBudgetMultiplier = 2.0;
    private const float LowPriorityShadowDeferThreshold = 5000.0f;

    private static long s_frameId = long.MinValue;
    private static long s_firstFrameId = long.MinValue;
    private static long s_firstFrameTimestamp;
    private static double s_textureUploadConsumedMilliseconds;
    private static double s_lastShadowAtlasMilliseconds;
    private static double s_oldestTextureQueueWaitMilliseconds;
    private static int s_textureUploadQueueDepth;
    private static int s_urgentTextureRepairQueueDepth;
    private static int s_shadowAtlasQueueDepth;
    private static int s_shaderUploadQueueDepth;
    private static int s_meshUploadQueueDepth;

    public static bool TryConsume(RenderWorkSubsystem subsystem, double estimatedMilliseconds)
    {
        EnsureFrame();
        if (subsystem != RenderWorkSubsystem.TextureUpload)
            return true;

        double budget = GetEffectiveTextureUploadBudgetMilliseconds();
        if (budget <= 0.0)
            return true;

        double estimate = Math.Max(0.0, estimatedMilliseconds);
        double consumed = Volatile.Read(ref s_textureUploadConsumedMilliseconds);
        if (consumed > 0.0 && consumed + estimate > budget)
            return false;

        return true;
    }

    public static void RecordCompleted(RenderWorkSubsystem subsystem, double elapsedMilliseconds)
    {
        EnsureFrame();
        if (subsystem == RenderWorkSubsystem.TextureUpload)
        {
            AddTextureUploadConsumed(Math.Max(0.0, elapsedMilliseconds));
        }
        else if (subsystem == RenderWorkSubsystem.ShadowAtlas)
        {
            Volatile.Write(ref s_lastShadowAtlasMilliseconds, Math.Max(0.0, elapsedMilliseconds));
        }
    }

    public static void RecordTextureQueue(int depth, double oldestWaitMilliseconds)
    {
        EnsureFrame();
        Volatile.Write(ref s_textureUploadQueueDepth, Math.Max(0, depth));
        Volatile.Write(ref s_oldestTextureQueueWaitMilliseconds, Math.Max(0.0, oldestWaitMilliseconds));
    }

    public static void RecordUrgentTextureRepairQueue(int depth)
    {
        EnsureFrame();
        Volatile.Write(ref s_urgentTextureRepairQueueDepth, Math.Max(0, depth));
    }

    public static void RecordShadowAtlasQueue(int depth)
    {
        EnsureFrame();
        Volatile.Write(ref s_shadowAtlasQueueDepth, Math.Max(0, depth));
    }

    public static bool ShouldDeferShadowAtlasLowPriorityTile(float priority, bool editorPinned)
    {
        EnsureFrame();
        return !editorPinned
            && Volatile.Read(ref s_urgentTextureRepairQueueDepth) > 0
            && priority < LowPriorityShadowDeferThreshold;
    }

    public static void RecordShaderUploadQueue(int depth)
    {
        EnsureFrame();
        Volatile.Write(ref s_shaderUploadQueueDepth, Math.Max(0, depth));
    }

    public static void RecordMeshUploadQueue(int depth)
    {
        EnsureFrame();
        Volatile.Write(ref s_meshUploadQueueDepth, Math.Max(0, depth));
    }

    public static RenderWorkBudgetSnapshot GetSnapshot()
    {
        EnsureFrame();
        return new RenderWorkBudgetSnapshot(
            Volatile.Read(ref s_frameId),
            GetEffectiveTextureUploadBudgetMilliseconds(),
            Volatile.Read(ref s_textureUploadConsumedMilliseconds),
            Volatile.Read(ref s_textureUploadQueueDepth),
            Volatile.Read(ref s_urgentTextureRepairQueueDepth),
            Volatile.Read(ref s_shadowAtlasQueueDepth),
            Volatile.Read(ref s_shaderUploadQueueDepth),
            Volatile.Read(ref s_meshUploadQueueDepth),
            Volatile.Read(ref s_oldestTextureQueueWaitMilliseconds),
            Volatile.Read(ref s_lastShadowAtlasMilliseconds),
            IsStartupBoostActive());
    }

    public static void AppendProfilerSummary(StringBuilder builder)
    {
        RenderWorkBudgetSnapshot snapshot = GetSnapshot();
        builder.Append("RenderWorkBudgetFrame: ").Append(snapshot.FrameId).AppendLine();
        builder.Append("RenderWorkTextureBudgetMs: ").Append(snapshot.TextureUploadBudgetMilliseconds.ToString("F3")).AppendLine();
        builder.Append("RenderWorkTextureConsumedMs: ").Append(snapshot.TextureUploadConsumedMilliseconds.ToString("F3")).AppendLine();
        builder.Append("RenderWorkTextureQueueDepth: ").Append(snapshot.TextureUploadQueueDepth).AppendLine();
        builder.Append("RenderWorkUrgentTextureRepairDepth: ").Append(snapshot.UrgentTextureRepairQueueDepth).AppendLine();
        builder.Append("RenderWorkShadowQueueDepth: ").Append(snapshot.ShadowAtlasQueueDepth).AppendLine();
        builder.Append("RenderWorkShaderQueueDepth: ").Append(snapshot.ShaderUploadQueueDepth).AppendLine();
        builder.Append("RenderWorkMeshQueueDepth: ").Append(snapshot.MeshUploadQueueDepth).AppendLine();
        builder.Append("RenderWorkOldestTextureWaitMs: ").Append(snapshot.OldestTextureQueueWaitMilliseconds.ToString("F3")).AppendLine();
        builder.Append("RenderWorkLastShadowMs: ").Append(snapshot.LastShadowAtlasMilliseconds.ToString("F3")).AppendLine();
        builder.Append("RenderWorkStartupBoostActive: ").Append(snapshot.StartupBoostActive).AppendLine();
    }

    private static void EnsureFrame()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Volatile.Read(ref s_frameId);
        if (previousFrame == currentFrame)
            return;

        if (Interlocked.CompareExchange(ref s_frameId, currentFrame, previousFrame) != previousFrame)
            return;

        if (Volatile.Read(ref s_firstFrameId) == long.MinValue)
        {
            Volatile.Write(ref s_firstFrameId, currentFrame);
            Volatile.Write(ref s_firstFrameTimestamp, Stopwatch.GetTimestamp());
        }

        Volatile.Write(ref s_textureUploadConsumedMilliseconds, 0.0);
        Volatile.Write(ref s_lastShadowAtlasMilliseconds, 0.0);
        Volatile.Write(ref s_oldestTextureQueueWaitMilliseconds, 0.0);
        Volatile.Write(ref s_textureUploadQueueDepth, 0);
        Volatile.Write(ref s_urgentTextureRepairQueueDepth, 0);
        Volatile.Write(ref s_shadowAtlasQueueDepth, 0);
        Volatile.Write(ref s_shaderUploadQueueDepth, 0);
        Volatile.Write(ref s_meshUploadQueueDepth, 0);
    }

    private static double GetEffectiveTextureUploadBudgetMilliseconds()
    {
        double configuredBudget = RuntimeRenderingHostServices.Current.TextureUploadFrameBudgetMilliseconds;
        if (configuredBudget <= 0.0 || !IsStartupBoostActive())
            return configuredBudget;

        return configuredBudget * StartupBoostBudgetMultiplier;
    }

    private static bool IsStartupBoostActive()
    {
        long firstFrame = Volatile.Read(ref s_firstFrameId);
        if (firstFrame == long.MinValue)
            return false;

        long currentFrame = Volatile.Read(ref s_frameId);
        if (currentFrame - firstFrame > StartupBoostMaxFrames)
            return false;

        long firstTimestamp = Volatile.Read(ref s_firstFrameTimestamp);
        if (firstTimestamp == 0L)
            return false;

        double elapsedMilliseconds = (Stopwatch.GetTimestamp() - firstTimestamp) * 1000.0 / Stopwatch.Frequency;
        return elapsedMilliseconds <= StartupBoostMaxMilliseconds;
    }

    private static void AddTextureUploadConsumed(double elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= 0.0)
            return;

        double current;
        double next;
        do
        {
            current = Volatile.Read(ref s_textureUploadConsumedMilliseconds);
            next = current + elapsedMilliseconds;
        }
        while (Interlocked.CompareExchange(ref s_textureUploadConsumedMilliseconds, next, current) != current);
    }
}
