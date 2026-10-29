using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Gaussian;
using XREngine.Rendering.Models.Gaussian;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh;

// GPU upload, per-frame sort, and draw of the splat cloud.
public partial class GaussianSplatComponent
{
    /// <summary>The quad extent of the renderer, in standard deviations. The culling volume uses it.</summary>
    private const float QuadExtentSigma = 3.0f;

    // Splats that the next PreRender call uploads. An empty array releases the GPU resources.
    private GaussianSplatGpuSplat[]? _pendingUpload;
    private AABB? _centerBounds;
    private float _maxSigma;

    // Render-thread state.
    private GaussianSplatRenderer? _splatRenderer;
    private XRMeshRenderer? _quad;
    private XRMaterial? _quadMaterialSource;
    private XRMaterial? _ownedQuadMaterial;

    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        _renderInfo.CullingOffsetMatrix = renderMatrix;
    }

    /// <summary>Copies the cloud to GPU splat records and queues them for the next PreRender call.</summary>
    private void QueueCloudUpload(GaussianSplatCloud? cloud)
    {
        GaussianSplatGpuSplat[] splats = BuildGpuSplats(cloud, out AABB? centerBounds, out float maxSigma);
        _centerBounds = centerBounds;
        _maxSigma = maxSigma;
        UpdateCullingVolume();
        Volatile.Write(ref _pendingUpload, splats);
    }

    private static GaussianSplatGpuSplat[] BuildGpuSplats(GaussianSplatCloud? cloud, out AABB? centerBounds, out float maxSigma)
    {
        centerBounds = null;
        maxSigma = 0.0f;
        if (cloud is not { Count: > 0 })
            return [];

        IReadOnlyList<GaussianSplat> source = cloud.Splats;
        int count = Math.Min(source.Count, GaussianSplatRenderer.MaxCapacity);
        if (count < source.Count)
            Debug.RenderingWarning($"Gaussian splat cloud '{cloud.Name}' has {source.Count} splats; the renderer draws the first {count}.");

        GaussianSplatGpuSplat[] splats = new GaussianSplatGpuSplat[count];
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        for (int i = 0; i < count; i++)
        {
            GaussianSplat splat = source[i];
            Vector3 scale = Vector3.Abs(splat.Scale);
            Quaternion rotation = splat.Rotation.LengthSquared() > 0.0f
                ? Quaternion.Normalize(splat.Rotation)
                : Quaternion.Identity;
            Vector3 color = Vector3.Clamp(splat.Color, Vector3.Zero, Vector3.One);
            Vector3 linearColor = new(SrgbToLinear(color.X), SrgbToLinear(color.Y), SrgbToLinear(color.Z));
            splats[i] = new GaussianSplatGpuSplat(splat.Position, Math.Clamp(splat.Opacity, 0.0f, 1.0f), rotation, scale, linearColor);

            min = Vector3.Min(min, splat.Position);
            max = Vector3.Max(max, splat.Position);
            maxSigma = MathF.Max(maxSigma, MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)));
        }

        centerBounds = new AABB(min, max);
        return splats;
    }

    private static float SrgbToLinear(float encoded)
        => encoded <= 0.04045f
            ? encoded / 12.92f
            : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);

    /// <summary>
    /// Sets the local culling volume to the splat centers grown by the quad extent of the largest splat.
    /// Without a cloud, the volume is null and the commands run every frame without work.
    /// </summary>
    private void UpdateCullingVolume()
    {
        if (_centerBounds is not { } bounds)
        {
            _renderInfo.LocalCullingVolume = null;
            return;
        }

        Vector3 extent = new(QuadExtentSigma * _maxSigma * _splatScale);
        _renderInfo.LocalCullingVolume = new AABB(bounds.Min - extent, bounds.Max + extent);
    }

    /// <summary>PreRender callback: uploads a new cloud, then sorts the splats for the rendering camera.</summary>
    private void PrepareSplats()
    {
        if (_cloudLoadTeardown || IsDestroyed)
            return;

        try
        {
            ApplyPendingUpload();
            if (_splatRenderer is not { } renderer)
                return;

            renderer.ModelMatrix = Transform.RenderMatrix;
            renderer.SplatScale = _splatScale;
            renderer.Prepare();
        }
        catch (Exception error)
        {
            Debug.RenderingException(error, "Failed to prepare gaussian splats.");
        }
    }

    /// <summary>TransparentForward callback: draws the splats in the order from the PreRender call.</summary>
    private void DrawSplats()
    {
        if (_cloudLoadTeardown || IsDestroyed || _splatRenderer is not { HasOrder: true } renderer)
            return;

        try
        {
            renderer.Draw(EnsureQuad());
        }
        catch (Exception error)
        {
            Debug.RenderingException(error, "Failed to draw gaussian splats.");
        }
    }

    private void ApplyPendingUpload()
    {
        GaussianSplatGpuSplat[]? splats = Interlocked.Exchange(ref _pendingUpload, null);
        if (splats is null)
            return;

        if (splats.Length == 0)
        {
            _splatRenderer?.Dispose();
            _splatRenderer = null;
            return;
        }

        if (_splatRenderer is null || _splatRenderer.Capacity != splats.Length)
        {
            _splatRenderer?.Dispose();
            _splatRenderer = null;
            _splatRenderer = new GaussianSplatRenderer(splats.Length);
        }

        _splatRenderer.Upload(splats);
    }

    /// <summary>Creates the quad on the first draw, and again when <see cref="OverrideMaterial"/> changes.</summary>
    private XRMeshRenderer EnsureQuad()
    {
        XRMaterial? requested = _overrideMaterial;
        if (_quad is not null && ReferenceEquals(_quadMaterialSource, requested))
            return _quad;

        DestroyQuad(_quad, _ownedQuadMaterial);
        _quad = null;
        _ownedQuadMaterial = requested is null ? GaussianSplatRenderer.CreateQuadMaterial() : null;
        _quadMaterialSource = requested;
        _quad = GaussianSplatRenderer.CreateQuadRenderer(requested ?? _ownedQuadMaterial);
        return _quad;
    }

    /// <summary>Releases the renderer and the quad on the render thread.</summary>
    private void ReleaseGpuResources()
    {
        Interlocked.Exchange(ref _pendingUpload, null);
        GaussianSplatRenderer? renderer = _splatRenderer;
        XRMeshRenderer? quad = _quad;
        XRMaterial? ownedMaterial = _ownedQuadMaterial;
        _splatRenderer = null;
        _quad = null;
        _ownedQuadMaterial = null;
        _quadMaterialSource = null;
        if (renderer is null && quad is null && ownedMaterial is null)
            return;

        RuntimeEngine.InvokeOnMainThread(
            () =>
            {
                renderer?.Dispose();
                DestroyQuad(quad, ownedMaterial);
            },
            "GaussianSplatComponent.ReleaseGpuResources",
            executeNowIfAlreadyMainThread: true);
    }

    private static void DestroyQuad(XRMeshRenderer? quad, XRMaterial? ownedMaterial)
    {
        XRMesh? mesh = quad?.Mesh;
        TryDestroy(quad);
        TryDestroy(mesh);
        TryDestroy(ownedMaterial);
    }
}
