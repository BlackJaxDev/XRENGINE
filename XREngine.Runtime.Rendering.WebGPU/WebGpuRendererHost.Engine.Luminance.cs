using System.Buffers.Binary;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int MaximumLuminancePixels = 4 * 1024 * 1024;
    private const int MaximumLuminanceMipmapPasses = 256;
    private ShaderProgramArtifact? _luminanceArtifact;
    private Task? _luminancePreparation;
    private ShaderProgramArtifact? _luminance2DArtifact;
    private Task? _luminance2DPreparation;
    private ShaderProgramArtifact? _luminanceMipmapArtifact;
    private Task? _luminanceMipmapPreparation;

    /// <summary>Installs an optional, hash-verified reduction kernel before engine resource activity.</summary>
    public void BindLuminanceArtifact(ShaderProgramArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || _luminancePreparation is not null)
            throw new InvalidOperationException("WebGPU.Luminance.AlreadyActive: bind the kernel before reduction requests.");
        WebComputeArtifactCatalog.ValidateLuminanceReduction(artifact);
        SetField(ref _luminanceArtifact, artifact);
    }

    public void BindLuminance2DArtifact(ShaderProgramArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || _luminance2DPreparation is not null)
            throw new InvalidOperationException("WebGPU.Luminance.AlreadyActive: bind the 2D kernel before reduction requests.");
        WebComputeArtifactCatalog.ValidateLuminanceReduction2D(artifact);
        SetField(ref _luminance2DArtifact, artifact);
    }

    public void BindLuminanceMipmapArtifact(ShaderProgramArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || _luminanceMipmapPreparation is not null)
            throw new InvalidOperationException("WebGPU.Luminance.AlreadyActive: bind the mipmap kernel before reduction requests.");
        WebComputeArtifactCatalog.ValidateLuminanceMipmap(artifact);
        SetField(ref _luminanceMipmapArtifact, artifact);
    }

    private async Task EnsureLuminanceReadyAsync(int session, bool twoDimensional, CancellationToken cancellationToken)
    {
        ShaderProgramArtifact? artifact = twoDimensional ? _luminance2DArtifact : _luminanceArtifact;
        if (artifact is null)
            throw new NotSupportedException("WebGPU.Luminance.ArtifactMissing: the world package has no hash-bound reduction kernel.");
        Task preparation = twoDimensional
            ? _luminance2DPreparation ??= WebGpuImports.PrepareLuminance2DAsync(session, artifact.Artifact.WgslSource)
            : _luminancePreparation ??= WebGpuImports.PrepareLuminanceAsync(session, artifact.Artifact.WgslSource);
        await preparation.WaitAsync(cancellationToken);
        RequireReadbackSession(session);
    }

    private async Task EnsureLuminanceMipmapReadyAsync(int session, CancellationToken cancellationToken)
    {
        if (_luminanceMipmapArtifact is not { } artifact)
            throw new NotSupportedException("WebGPU.Luminance.MipmapArtifactMissing: the world package has no hash-bound mipmap kernel.");
        Task preparation = _luminanceMipmapPreparation ??= WebGpuImports.PrepareLuminanceMipmapAsync(session, artifact.Artifact.WgslSource);
        await preparation.WaitAsync(cancellationToken);
        RequireReadbackSession(session);
    }

    private static void ValidateLuminanceWeights(Vector3 weights)
    {
        if (!float.IsFinite(weights.X) || !float.IsFinite(weights.Y) || !float.IsFinite(weights.Z))
            throw new ArgumentOutOfRangeException(nameof(weights), "Luminance weights must be finite.");
    }

    private static void ValidateLuminanceWork(int width, int height, int layers)
    {
        if (width <= 0 || height <= 0 || layers <= 0 || (long)width * height * layers > MaximumLuminancePixels)
            throw new NotSupportedException("WebGPU.Luminance.WorkBudget: at most four million texels may be reduced per request.");
    }

    private static void ValidateLuminanceMipmapPasses(int firstMip, int lastMip, int layers)
    {
        if (firstMip < 0 || lastMip < firstMip || (long)(lastMip - firstMip) * layers > MaximumLuminanceMipmapPasses)
            throw new NotSupportedException("WebGPU.Luminance.MipmapWorkBudget: generation exceeds 256 bounded mip/layer passes.");
    }

    /// <summary>Samples the first texel of the selected authored mip on the GPU, matching the scalar desktop read.</summary>
    public Task<float> ReadEngineLuminanceAsync(XRTexture2D texture, Vector3 weights,
        bool genMipmapsNow = true, CancellationToken cancellationToken = default)
        => ReadEngineLuminanceRequest(texture, weights, genMipmapsNow, averageBase: false, cancellationToken);

    /// <summary>Computes a linear RGB average of the base mip on the GPU without regenerating the texture's mip chain.</summary>
    public Task<float> ReadEngineAverageLuminanceAsync(XRTexture2D texture, Vector3 weights,
        CancellationToken cancellationToken = default)
        => ReadEngineLuminanceRequest(texture, weights, genMipmapsNow: false, averageBase: true, cancellationToken);

    private Task<float> ReadEngineLuminanceRequest(XRTexture2D texture, Vector3 weights,
        bool genMipmapsNow, bool averageBase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texture);
        RequireReady();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLuminanceWeights(weights);
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Luminance.InFrameUnsupported: a reduction during frame recording would sample stale contents.");
        WebGpuTexture2D api = (WebGpuTexture2D)GetOrCreateAPIRenderObject(texture, generateNow: true)!;
        RequireLuminanceTexture(api.Format, api.SampleCount, api.ProductionTicket, api.HasCommittedProduction);
        int mip = averageBase ? 0 : XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height,
            texture.SmallestAllowedMipmapLevel);
        if ((uint)mip >= (uint)(texture.Mipmaps?.Length ?? 0))
            throw new NotSupportedException("WebGPU.Luminance.MipUnavailable: the selected authored mip is not allocated.");
        if (!averageBase && (Math.Max(1u, texture.Width >> mip) != 1 || Math.Max(1u, texture.Height >> mip) != 1))
            throw new NotSupportedException("WebGPU.Luminance.ScalarMipUnavailable: the desktop 2D scalar read requires a 1x1 selected mip.");
        int width = averageBase ? checked((int)texture.Width) : 1;
        int height = averageBase ? checked((int)texture.Height) : 1;
        ValidateLuminanceWork(width, height, 1);
        int firstGeneratedMip = 0, lastGeneratedMip = 0;
        bool detailPreserving = false;
        if (genMipmapsNow && !averageBase)
        {
            ValidateLuminanceWork(checked((int)texture.Width), checked((int)texture.Height), 1);
            detailPreserving = RuntimeEngine.Rendering.Settings.UseDetailPreservingComputeMipmaps &&
                !texture.SparseTextureStreamingEnabled &&
                api.Format is ("rgba8unorm" or "rgba16float");
            firstGeneratedMip = detailPreserving ? 0 : texture.LargestMipmapLevel;
            lastGeneratedMip = detailPreserving ? (texture.Mipmaps?.Length ?? 0) - 1 : mip;
            if (firstGeneratedMip < 0 || firstGeneratedMip > mip || lastGeneratedMip >= (texture.Mipmaps?.Length ?? 0))
                throw new NotSupportedException("WebGPU.Luminance.MipmapRangeUnsupported: the desktop generation range is outside the authored chain.");
            ValidateLuminanceMipmapPasses(firstGeneratedMip, lastGeneratedMip, 1);
        }
        int handle = api.ResourceHandle;
        ulong productionTicket = api.ProductionTicket;
        int allowedMip = texture.SmallestAllowedMipmapLevel;
        int baseMip = texture.LargestMipmapLevel;
        bool sparseStreaming = texture.SparseTextureStreamingEnabled;
        bool detailSetting = RuntimeEngine.Rendering.Settings.UseDetailPreservingComputeMipmaps;
        return ReadEngineLuminanceCoreAsync(handle, mip, width, height, 1, weights,
            firstGeneratedMip, lastGeneratedMip, detailPreserving, strictEncodedSrgb: !averageBase,
            () => api.IsCurrentGpuAllocationForCopy && api.ResourceHandle == handle && api.ProductionTicket == productionTicket &&
                texture.SmallestAllowedMipmapLevel == allowedMip && texture.LargestMipmapLevel == baseMip &&
                texture.SparseTextureStreamingEnabled == sparseStreaming &&
                (!genMipmapsNow || RuntimeEngine.Rendering.Settings.UseDetailPreservingComputeMipmaps == detailSetting) &&
                (productionTicket == 0 || api.HasCommittedProduction), cancellationToken);
    }

    /// <summary>Samples the first texel of the selected authored mip from every array layer with equal weight.</summary>
    public Task<float> ReadEngineLuminanceAsync(XRTexture2DArray texture, Vector3 weights,
        bool genMipmapsNow = true, CancellationToken cancellationToken = default)
        => ReadEngineLuminanceRequest(texture, weights, genMipmapsNow, averageBase: false, cancellationToken);

    /// <summary>Computes a linear RGB base-mip average across all array layers without regenerating mips.</summary>
    public Task<float> ReadEngineAverageLuminanceAsync(XRTexture2DArray texture, Vector3 weights,
        CancellationToken cancellationToken = default)
        => ReadEngineLuminanceRequest(texture, weights, genMipmapsNow: false, averageBase: true, cancellationToken);

    private Task<float> ReadEngineLuminanceRequest(XRTexture2DArray texture, Vector3 weights,
        bool genMipmapsNow, bool averageBase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texture);
        RequireReady();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLuminanceWeights(weights);
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Luminance.InFrameUnsupported: a reduction during frame recording would sample stale contents.");
        WebGpuTexture2DArray api = (WebGpuTexture2DArray)GetOrCreateAPIRenderObject(texture, generateNow: true)!;
        RequireLuminanceTexture(api.Format, api.SampleCount, api.ProductionTicket, api.HasCommittedProduction);
        int mip = averageBase ? 0 : XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height,
            texture.SmallestAllowedMipmapLevel);
        if ((uint)mip >= (uint)(texture.Mipmaps?.Length ?? 0))
            throw new NotSupportedException("WebGPU.Luminance.MipUnavailable: the selected authored mip is not allocated.");
        int width = averageBase ? checked((int)texture.Width) : 1;
        int height = averageBase ? checked((int)texture.Height) : 1;
        int layers = checked((int)texture.Depth);
        ValidateLuminanceWork(width, height, layers);
        int firstGeneratedMip = 0, lastGeneratedMip = 0;
        if (genMipmapsNow && !averageBase)
        {
            ValidateLuminanceWork(checked((int)texture.Width), checked((int)texture.Height), layers);
            firstGeneratedMip = texture.LargestMipmapLevel;
            lastGeneratedMip = mip;
            if (firstGeneratedMip < 0 || firstGeneratedMip > mip)
                throw new NotSupportedException("WebGPU.Luminance.MipmapRangeUnsupported: the desktop generation range is outside the authored chain.");
            ValidateLuminanceMipmapPasses(firstGeneratedMip, lastGeneratedMip, layers);
        }
        int handle = api.ResourceHandle;
        ulong productionTicket = api.ProductionTicket;
        int allowedMip = texture.SmallestAllowedMipmapLevel;
        int baseMip = texture.LargestMipmapLevel;
        return ReadEngineLuminanceCoreAsync(handle, mip, width, height, layers, weights,
            firstGeneratedMip, lastGeneratedMip, detailPreserving: false, strictEncodedSrgb: !averageBase,
            () => api.IsCurrentGpuAllocationForCopy && api.ResourceHandle == handle && api.ProductionTicket == productionTicket &&
                texture.SmallestAllowedMipmapLevel == allowedMip && texture.LargestMipmapLevel == baseMip &&
                (productionTicket == 0 || api.HasCommittedProduction), cancellationToken);
    }

    private static void RequireLuminanceTexture(string format, uint samples, ulong productionTicket, bool committed)
    {
        if (format is not ("rgba8unorm" or "rgba8unorm-srgb" or "rgba16float") || samples != 1)
            throw new NotSupportedException("WebGPU.Luminance.TextureUnsupported: reduction requires single-sample RGBA8, sRGB8 or RGBA16F.");
        if (productionTicket != 0 && !committed)
            throw new InvalidOperationException("WebGPU.Luminance.TextureNotCommitted: the texture has no accepted producer frame.");
        // Strict encoded sRGB reads are lowered to a GPU raw-byte format copy.
    }

    private async Task<float> ReadEngineLuminanceCoreAsync(int handle, int mip, int width, int height,
        int layers, Vector3 weights, int firstGeneratedMip, int lastGeneratedMip,
        bool detailPreserving, bool strictEncodedSrgb, Func<bool> sourceIsCurrent, CancellationToken cancellationToken)
    {
        int session = _session;
        await EnsureLuminanceReadyAsync(session, twoDimensional: layers == 1, cancellationToken);
        if (lastGeneratedMip > firstGeneratedMip)
            await EnsureLuminanceMipmapReadyAsync(session, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_engineRecording || !sourceIsCurrent())
            throw new InvalidOperationException("WebGPU.Luminance.ObsoleteProducer: the source changed or entered an uncommitted producer frame during shader preparation.");
        RequireReadbackResource(handle);
        int ticket = WebGpuImports.BeginTextureLuminance(session, handle, mip, width, height, layers,
            weights.X, weights.Y, weights.Z, firstGeneratedMip, lastGeneratedMip, detailPreserving, strictEncodedSrgb);
        byte[] bytes = await FinishReadbackAsync(session, ticket, sizeof(float), cancellationToken);
        if (!sourceIsCurrent())
            throw new InvalidOperationException("WebGPU.Luminance.ObsoleteProducer: the source changed before reduction completed.");
        return DecodeLuminance(bytes);
    }

    /// <summary>Reduces the next complete presented canvas frame through the same producer gate as screenshots.</summary>
    public Task<float> ReadCanvasLuminanceAsync(BoundingRectangle region, bool withTransparency, Vector3 weights,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLuminanceWeights(weights);
        if (!TryDescribeFrameOutput(out RenderFrameOutputDescription output))
            throw new InvalidOperationException("WebGPU.Luminance.CanvasUnavailable: no drawable surface is configured.");
        int surfaceWidth = checked((int)output.Properties.Width);
        int surfaceHeight = checked((int)output.Properties.Height);
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X > surfaceWidth || region.Y > surfaceHeight ||
            region.Width > surfaceWidth - region.X || region.Height > surfaceHeight - region.Y)
            throw new ArgumentOutOfRangeException(nameof(region), "The luminance region must fit the current canvas.");
        ValidateLuminanceWork(region.Width, region.Height, 1);
        // Canvas presentation currently discards alpha. Both front APIs measure RGB;
        // withTransparency cannot recover discarded alpha or change RGB weighting.
        _ = withTransparency;
        return ReadCanvasLuminanceCoreAsync(checked((int)output.TargetGeneration), region.X,
            surfaceHeight - region.Y - region.Height, region.Width, region.Height, weights, cancellationToken);
    }

    private async Task<float> ReadCanvasLuminanceCoreAsync(int generation, int x, int top, int width, int height,
        Vector3 weights, CancellationToken cancellationToken)
    {
        int session = _session;
        await EnsureLuminanceReadyAsync(session, twoDimensional: true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryDescribeFrameOutput(out RenderFrameOutputDescription output) || output.TargetGeneration != (ulong)generation)
            throw new InvalidOperationException("WebGPU.Luminance.ObsoleteSurface: the canvas changed before reduction began.");
        int ticket = WebGpuImports.BeginCanvasLuminance(session, generation, x, top, width, height,
            weights.X, weights.Y, weights.Z);
        byte[] bytes = await FinishReadbackAsync(session, ticket, sizeof(float), cancellationToken);
        if (!TryDescribeFrameOutput(out output) || output.TargetGeneration != (ulong)generation)
            throw new InvalidOperationException("WebGPU.Luminance.ObsoleteSurface: the canvas changed before reduction completed.");
        return DecodeLuminance(bytes);
    }

    private static float DecodeLuminance(ReadOnlySpan<byte> bytes)
    {
        float result = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
        if (!float.IsFinite(result))
            throw new InvalidOperationException("WebGPU.Luminance.InvalidResult: GPU reduction returned a non-finite value.");
        return result;
    }

    public override void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback,
        Vector3 luminance, bool genMipmapsNow = true)
        => BeginLuminanceCallback(() => ReadEngineLuminanceAsync(texture, luminance, genMipmapsNow), callback);

    public override void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback,
        Vector3 luminance, bool genMipmapsNow = true)
        => BeginLuminanceCallback(() => ReadEngineLuminanceAsync(texture, luminance, genMipmapsNow), callback);

    public override void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency,
        Vector3 luminance, Action<bool, float> callback)
        => BeginLuminanceCallback(() => ReadCanvasLuminanceAsync(region, withTransparency, luminance), callback);

    public override void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency,
        Vector3 luminance, Action<bool, float> callback)
        => BeginLuminanceCallback(() => ReadCanvasLuminanceAsync(region, withTransparency, luminance), callback);

    private static void BeginLuminanceCallback(Func<Task<float>> begin, Action<bool, float> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Task<float> task;
        try { task = begin(); }
        catch (Exception error)
        {
            Debug.RenderingWarning("WebGPU.Luminance.Rejected: {0}", error.Message);
            try { callback(false, 0); }
            catch (Exception callbackError) { Debug.RenderingWarning("WebGPU.Luminance.CallbackFailed: {0}", callbackError.Message); }
            return;
        }
        _ = CompleteLuminanceCallbackAsync(task, callback);
    }

    private static async Task CompleteLuminanceCallbackAsync(Task<float> task, Action<bool, float> callback)
    {
        bool success;
        float result;
        try { result = await task; success = true; }
        catch (Exception error)
        {
            Debug.RenderingWarning("WebGPU.Luminance.Failed: {0}", error.Message);
            result = 0;
            success = false;
        }
        try { callback(success, result); }
        catch (Exception error) { Debug.RenderingWarning("WebGPU.Luminance.CallbackFailed: {0}", error.Message); }
    }
}
