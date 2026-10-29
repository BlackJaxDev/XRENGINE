using System.ComponentModel;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Gaussian;
using YamlDotNet.Serialization;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Component that loads a static gaussian splat cloud and draws it back to front with
/// <see cref="XREngine.Rendering.Gaussian.GaussianSplatRenderer"/>.
/// </summary>
/// <remarks>
/// The component uploads the cloud to the GPU one time. Each frame it sorts the splats in the PreRender
/// pass and draws them in the TransparentForward pass. It treats the splat colors as sRGB values.
/// </remarks>
[Serializable]
public partial class GaussianSplatComponent : XRComponent, IRenderable
{
    private const float LegacyPointScale = 3.0f;

    private readonly RenderInfo3D _renderInfo;
    private readonly RenderInfo[] _renderedObjects;

    public GaussianSplatComponent()
    {
        _renderInfo = RenderInfo3D.New(
            this,
            new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, PrepareSplats),
            new RenderCommandMethod3D((int)EDefaultRenderPass.TransparentForward, DrawSplats));
        // Splats do not cast or receive shadows, so shadow passes do not collect them.
        _renderInfo.CastsShadows = false;
        _renderInfo.ReceivesShadows = false;
        _renderedObjects = [_renderInfo];
    }

    /// <inheritdoc />
    public RenderInfo[] RenderedObjects => _renderedObjects;

    private string? _sourcePath;
    private long _sourcePathSetterSerial;
    private long _cloudLoadIntentSerial;

    /// <summary>Path of a .splat file to load. A new path cancels a load that has not finished.</summary>
    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            long setterSerial = Interlocked.Increment(ref _sourcePathSetterSerial);
            long loadIntent = Interlocked.Increment(ref _cloudLoadIntentSerial);
            if (!SetField(ref _sourcePath, value))
                return;
            if (setterSerial != Volatile.Read(ref _sourcePathSetterSerial) ||
                loadIntent != Volatile.Read(ref _cloudLoadIntentSerial) ||
                !string.Equals(_sourcePath, value, StringComparison.Ordinal))
                return;
            CancelPendingCloudLoad();
            if (setterSerial != Volatile.Read(ref _sourcePathSetterSerial) ||
                loadIntent != Volatile.Read(ref _cloudLoadIntentSerial))
                return;
            _externalCloudBinding = false;
            if (!string.IsNullOrWhiteSpace(value))
                LoadFromFileCore(value, loadIntent);
        }
    }

    private GaussianSplatCloud? _cloud;

    /// <summary>
    /// The splat cloud to draw. Assigning a cloud cancels a pending file load. The component copies the
    /// splats when the cloud is assigned; later changes to the same cloud do not show.
    /// </summary>
    public GaussianSplatCloud? Cloud
    {
        get => _cloud;
        set
        {
            CloudLoadRequest? admission = _adoptingCloudRequest;
            _adoptingCloudRequest = null;
            bool adopting = admission is not null && ReferenceEquals(_pendingCloudLoad, admission) &&
                ReferenceEquals(value, admission.Cloud);
            if (ReferenceEquals(_cloud, value))
            {
                if (!adopting)
                    MarkExternalCloudBinding();
                return;
            }
            if (!adopting)
            {
                long cloudAssignment = Interlocked.Increment(ref _externalCloudAssignmentSerial);
                long loadIntent = Interlocked.Increment(ref _cloudLoadIntentSerial);
                CancelPendingCloudLoad();
                if (cloudAssignment != Volatile.Read(ref _externalCloudAssignmentSerial) ||
                    loadIntent != Volatile.Read(ref _cloudLoadIntentSerial))
                    return;
                _externalCloudBinding = true;
            }

            GaussianSplatCloud? previousOwned = _ownedCloud;
            if (!SetField(ref _cloud, value))
                return;
            // A change notification can assign another cloud; that assignment queues its own upload.
            if (ReferenceEquals(_cloud, value))
                QueueCloudUpload(value);
            if (!adopting && previousOwned is not null && !ReferenceEquals(previousOwned, _cloud))
            {
                _ownedCloud = null;
                RetireOwnedCloud(previousOwned);
            }
        }
    }

    private float _splatScale = 1.0f;

    /// <summary>Multiplier for the standard deviation of every splat. Values that are not positive hide the splats.</summary>
    [Category("Rendering")]
    [Description("Multiplier for the standard deviation of every splat.")]
    public float SplatScale
    {
        get => _splatScale;
        set
        {
            float sanitized = float.IsFinite(value) && value > 0.0f ? value : 0.0f;
            if (SetField(ref _splatScale, sanitized))
                UpdateCullingVolume();
        }
    }

    /// <summary>
    /// Has no effect. The point sprite renderer used this value; the sorted renderer uses the splat
    /// covariance and <see cref="SplatScale"/>. The setter accepts a value so that old scenes still load.
    /// </summary>
    [Obsolete("PointScale has no effect. Use SplatScale to scale the splats.")]
    [Browsable(false)]
    [YamlIgnore]
    public float PointScale
    {
        get => LegacyPointScale;
        set { }
    }

    private XRMaterial? _overrideMaterial;

    /// <summary>
    /// Optional material for the splat quads. It must use the gaussian splat quad shaders.
    /// Null uses the default quad material.
    /// </summary>
    public XRMaterial? OverrideMaterial
    {
        get => _overrideMaterial;
        set => SetField(ref _overrideMaterial, value);
    }

    /// <summary>Loads a .splat file and assigns its cloud when the load completes.</summary>
    public void LoadFromFile(string path)
    {
        long loadIntent = Interlocked.Increment(ref _cloudLoadIntentSerial);
        LoadFromFileCore(path, loadIntent);
    }

    private bool _externalCloudBinding;
    private CloudLoadRequest? _adoptingCloudRequest;
    private long _externalCloudAssignmentSerial;

    private void MarkExternalCloudBinding()
    {
        Interlocked.Increment(ref _externalCloudAssignmentSerial);
        Interlocked.Increment(ref _cloudLoadIntentSerial);
        CancelPendingCloudLoad();
        _externalCloudBinding = true;
    }

    private void LoadFromFileCore(string path, long loadIntent)
    {
        if (loadIntent != Volatile.Read(ref _cloudLoadIntentSerial))
            return;
        _externalCloudBinding = false;
        if (OperatingSystem.IsBrowser() || XREngine.Execution.RuntimeWorkScheduler.IsCallerThread)
        {
            StartCloudLoad(path, loadIntent);
            return;
        }

        try
        {
            Cloud = GaussianSplatCloud.Load(path);
        }
        catch (Exception ex)
        {
            Debug.RenderingException(ex, $"Failed to load gaussian splat data from '{path}'.");
        }
    }

    private static void TryDestroy(XREngine.Data.Core.XRObjectBase? asset)
    {
        if (asset is null || asset.IsDestroyed)
            return;
        try { asset.Destroy(now: true); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Failed to retire a Gaussian asset: {0}", error);
        }
    }
}
