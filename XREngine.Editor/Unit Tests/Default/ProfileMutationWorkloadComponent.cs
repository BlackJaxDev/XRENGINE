using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// Runs one explicitly selected mutation workload for timed renderer profiles.
/// </summary>
public sealed class ProfileMutationWorkloadComponent : XRComponent
{
    private const float UpdatePeriodSeconds = 0.25f;
    private const float ResizePeriodSeconds = 10.0f;
    private const float SetupTimeoutSeconds = 5.0f;
    private const string AuxiliaryLogName = "profile-mutation-workload";
    private readonly XRMaterial[] _materials;
    private readonly Vector3[] _materialColors;
    private readonly string _mode;
    private readonly string? _streamingAssetPath;
    private readonly XRTexture2D?[] _streamingTextures = new XRTexture2D?[2];
    private readonly EnumeratorJob?[] _streamingJobs = new EnumeratorJob?[2];
    private readonly int[] _streamingReady = new int[2];
    private readonly Action<XRTexture2D> _streamingFinished;
    private readonly Action<Exception> _streamingFailed;
    private readonly Action _streamingCanceled;
    private SceneNode? _streamingPlaneNode;
    private XRMaterial? _streamingMaterial;
    private SceneNode? _volatileUiRoot;
    private UICanvasComponent? _volatileUiCanvas;
    private UITextComponent? _volatileUiText;
    private CameraComponent? _volatileUiCamera;
    private IRuntimeScreenSpaceUserInterface? _previousCameraUi;
    private XRWindow? _resizeWindow;
    private IDisposable? _streamingScope;
    private bool _streamingStarted;
    private float _nextUpdateTime;
    private float _nextResizeTime;
    private float _setupDeadline;
    private int _updateCount;
    private int _resizeCount;
    private bool _nextResizeIsLarge;
    private int _streamingSchedules;
    private int _streamingCompletions;
    private int _streamingFailures;
    private int _streamingCancellations;
    private int _streamingVisibleBindings;
    private string? _streamingFailureDetail;
    private bool _volatileUiSetupPending;
    private bool _resizeSetupPending;

    private ProfileMutationWorkloadComponent(
        string mode,
        XRMaterial[] materials,
        string? streamingAssetPath)
    {
        _mode = mode;
        _materials = materials;
        _streamingAssetPath = streamingAssetPath;
        _materialColors = CreateMaterialColors(materials.Length);
        _streamingFinished = OnStreamingFinished;
        _streamingFailed = OnStreamingFailed;
        _streamingCanceled = OnStreamingCanceled;
    }

    /// <summary>
    /// Reads the opt-in workload selection once during unit-world construction.
    /// </summary>
    public static ProfileMutationWorkloadComponent? CreateRequested(XRMaterial[] materials)
    {
        string? mode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileMutationWorkload);
        if (!IsSupportedMode(mode))
            return null;

        string selectedMode = mode!;
        string? streamingAssetPath = string.Equals(selectedMode, "Streaming", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ProfileStreamingAsset)
            : null;
        return new ProfileMutationWorkloadComponent(selectedMode, materials, streamingAssetPath);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();

        _nextUpdateTime = Engine.Time.Timer.Time();
        _setupDeadline = _nextUpdateTime + SetupTimeoutSeconds;
        _updateCount = 0;
        if (string.Equals(_mode, "VolatileUi", StringComparison.OrdinalIgnoreCase))
            _volatileUiSetupPending = true;
        else if (string.Equals(_mode, "Resize", StringComparison.OrdinalIgnoreCase))
            _resizeSetupPending = true;

        Log($"Activated mode={_mode} cadenceHz={1.0f / UpdatePeriodSeconds:F1} materials={_materials.Length} streamingAsset={_streamingAssetPath ?? "<unset>"}");
        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, Tick);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, Tick);
        StopStreaming();
        DetachVolatileUi();
        ReportIncompleteSetup();
        Log($"Deactivated mode={_mode} updates={_updateCount} cadenceHz={1.0f / UpdatePeriodSeconds:F1} resizes={_resizeCount} streamingSchedules={_streamingSchedules} completed={_streamingCompletions} failed={_streamingFailures} canceled={_streamingCancellations} visibleBindings={_streamingVisibleBindings} failureDetail={_streamingFailureDetail ?? "<none>"}");
        base.OnComponentDeactivated();
    }

    private void Tick()
    {
        float now = Engine.Time.Timer.Time();
        ResolveDeferredSetup(now);
        if (now < _nextUpdateTime)
            return;

        _nextUpdateTime = now + UpdatePeriodSeconds;
        _updateCount++;
        if (string.Equals(_mode, "MaterialEdits", StringComparison.OrdinalIgnoreCase))
            UpdateMaterials();
        else if (string.Equals(_mode, "VolatileUi", StringComparison.OrdinalIgnoreCase))
            UpdateVolatileUi();
        else if (string.Equals(_mode, "Streaming", StringComparison.OrdinalIgnoreCase))
            UpdateStreaming();
        else if (string.Equals(_mode, "Resize", StringComparison.OrdinalIgnoreCase))
            UpdateResizeWorkload(now);
    }

    private void UpdateMaterials()
    {
        int colorOffset = (_updateCount & 7) * _materials.Length;
        for (int materialIndex = 0; materialIndex < _materials.Length; materialIndex++)
            _materials[materialIndex].SetVector3("BaseColor", _materialColors[colorOffset + materialIndex]);
    }

    private bool TryCreateVolatileUi()
    {
        SceneNode? root = SceneNode;
        if (root is null)
            return false;

        // The play-mode pawn can differ from the bootstrap editor camera.
        _volatileUiCamera ??= FindActiveViewportCamera();
        if (_volatileUiCamera is null || !_volatileUiCamera.IsActiveInHierarchy)
            return false;

        if (_volatileUiRoot is null)
        {
            _volatileUiRoot = new SceneNode(root, "Profile Mutation Workload UI");
            _volatileUiCanvas = _volatileUiRoot.AddComponent<UICanvasComponent>();
            if (_volatileUiCanvas is null)
                return false;

            _volatileUiCanvas.CanvasTransform.DrawSpace = ECanvasDrawSpace.Screen;
            _volatileUiCanvas.CanvasTransform.SetSize(new Vector2(1920.0f, 1080.0f));
            SceneNode textNode = new(_volatileUiRoot) { Name = "Profile Mutation Workload Text" };
            _volatileUiText = textNode.AddComponent<UITextComponent>();
            if (_volatileUiText is null)
                return false;

            _volatileUiText.FontSize = 22;
            _volatileUiText.Color = XREngine.Data.Colors.ColorF4.White;
            _volatileUiText.HorizontalAlignment = EHorizontalAlignment.Left;
            _volatileUiText.VerticalAlignment = EVerticalAlignment.Top;
            _volatileUiText.Text = "Profile workload A";
            _volatileUiText.RenderPass = (int)EDefaultRenderPass.OnTopForward;
            var textTransform = textNode.GetTransformAs<UIBoundableTransform>(true)!;
            textTransform.Width = 260.0f;
            textTransform.Height = 40.0f;
            textTransform.MinAnchor = new Vector2(0.0f, 1.0f);
            textTransform.MaxAnchor = new Vector2(0.0f, 1.0f);
            textTransform.NormalizedPivot = new Vector2(0.0f, 1.0f);
            textTransform.Margins = new Vector4(12.0f, -12.0f, 0.0f, 0.0f);
        }

        _volatileUiRoot.IsActiveSelf = true;
        _previousCameraUi = _volatileUiCamera.UserInterface;
        _volatileUiCamera.UserInterface = _volatileUiCanvas;
        return true;
    }

    private void UpdateVolatileUi()
    {
        if (_volatileUiText is not null)
            _volatileUiText.Text = (_updateCount & 1) == 0 ? "Profile workload A" : "Profile workload B";
    }

    private void StartStreaming()
    {
        if (string.IsNullOrWhiteSpace(_streamingAssetPath) || !File.Exists(_streamingAssetPath))
        {
            Log("Streaming setup failed: XRE_PROFILE_STREAMING_ASSET is missing or does not name an existing file; no stand-in was created. Invalidate this profile run.");
            return;
        }
        string streamingAssetPath = _streamingAssetPath;

        _streamingSchedules = 0;
        _streamingCompletions = 0;
        _streamingFailures = 0;
        _streamingCancellations = 0;
        _streamingVisibleBindings = 0;
        _streamingFailureDetail = null;
        _streamingScope = XRTexture2D.EnterImportedTextureStreamingScope();
        ScheduleStreamingSlot(0);
        ScheduleStreamingSlot(1);
        Log($"Streaming source='{streamingAssetPath}' uses real file preview loads into fresh textures, at most two pending jobs, with completed uploads replacing the visible material. Cache-hit classification remains manager-owned.");
    }

    private XRTexture2D CreateStreamingTexture(int slot)
        => new()
        {
            Name = $"Profile Streaming Texture {slot + 1}",
            FilePath = _streamingAssetPath,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };

    private void ScheduleStreamingSlot(int slot)
    {
        if (string.IsNullOrWhiteSpace(_streamingAssetPath))
            return;

        EnumeratorJob? current = _streamingJobs[slot];
        if (current is not null && !current.IsCompleted && !current.IsFaulted && !current.IsCanceled)
            return;

        // These allocations are the requested asset-streaming workload, outside render submission.
        XRTexture2D texture = CreateStreamingTexture(slot);
        Volatile.Write(ref _streamingReady[slot], 0);
        _streamingTextures[slot] = texture;

        _streamingSchedules++;
        _streamingJobs[slot] = XRTexture2D.ScheduleImportedTexturePreviewJob(
            _streamingAssetPath,
            texture,
            _streamingFinished,
            _streamingFailed,
            _streamingCanceled,
            priority: JobPriority.Low);
    }

    private void UpdateStreaming()
    {
        if (!_streamingStarted)
        {
            // World activation precedes native renderer initialization. Let ordinary
            // frames bind the upload service before publishing any streamed material.
            if (RuntimeEngine.Rendering.State.RenderFrameId < 10)
                return;
            _streamingStarted = true;
            StartStreaming();
        }

        int slot = _updateCount & 1;
        // Publish the first visible reference only after its native upload has completed.
        // Keep the previous resident object visible while another asset uploads.
        if (Volatile.Read(ref _streamingReady[slot]) == 0)
        {
            ScheduleStreamingSlot(slot);
            return;
        }
        if (_streamingPlaneNode is null)
            CreateStreamingPlane(slot);
        XRTexture2D? texture = _streamingTextures[slot];
        if (_streamingMaterial?.Textures is { Count: > 0 } && texture is not null)
        {
            _streamingMaterial.Textures[0] = texture;
            _streamingVisibleBindings++;
        }
        ScheduleStreamingSlot(slot);
    }

    private void CreateStreamingPlane(int slot)
    {
        SceneNode? root = SceneNode;
        XRTexture2D? firstTexture = _streamingTextures[slot];
        if (root is null || firstTexture is null)
            return;

        _streamingMaterial = ModelAssetImporter.MakeMaterialDeferred(
            [firstTexture],
            [],
            "Profile Streaming Material");
        _streamingMaterial.RenderOptions.CullMode = ECullMode.Back;
        _streamingPlaneNode = new SceneNode(root, "Profile Streaming Plane");
        _streamingPlaneNode.GetTransformAs<Transform>(false)!.Translation = new Vector3(0.0f, 0.0f, -2.0f);
        ModelComponent model = _streamingPlaneNode.AddComponent<ModelComponent>()!;
        model.Model = new Model(
        [
            new SubMesh(
                XRMesh.Shapes.SolidPlane(Vector3.Zero, Vector3.UnitZ, 1.5f, 1.5f),
                _streamingMaterial),
        ]);
    }

    private void StopStreaming()
    {
        for (int slot = 0; slot < _streamingJobs.Length; slot++)
        {
            _streamingJobs[slot]?.Cancel();
            _streamingJobs[slot] = null;
        }

        _streamingScope?.Dispose();
        _streamingScope = null;
        _streamingStarted = false;
        if (_streamingPlaneNode is not null)
            _streamingPlaneNode.IsActiveSelf = false;
    }

    private void OnStreamingFinished(XRTexture2D texture)
    {
        for (int slot = 0; slot < _streamingTextures.Length; slot++)
            if (ReferenceEquals(_streamingTextures[slot], texture))
                Volatile.Write(ref _streamingReady[slot], 1);
        Interlocked.Increment(ref _streamingCompletions);
    }

    private void OnStreamingFailed(Exception _)
    {
        _streamingFailureDetail = _.Message;
        Interlocked.Increment(ref _streamingFailures);
        Log($"Streaming preview failed source='{_streamingAssetPath ?? "<unset>"}' detail='{_.Message}'. Invalidate this profile run.");
    }

    private void OnStreamingCanceled()
        => Interlocked.Increment(ref _streamingCancellations);

    private void DetachVolatileUi()
    {
        if (_volatileUiCamera is not null && ReferenceEquals(_volatileUiCamera.UserInterface, _volatileUiCanvas))
            _volatileUiCamera.UserInterface = _previousCameraUi;
        if (_volatileUiRoot is not null)
            _volatileUiRoot.IsActiveSelf = false;
        _previousCameraUi = null;
    }

    private void ResolveDeferredSetup(float now)
    {
        if (_volatileUiSetupPending && TryCreateVolatileUi())
            _volatileUiSetupPending = false;
        if (_resizeSetupPending && TryStartResizeWorkload())
            _resizeSetupPending = false;

        if (now < _setupDeadline)
            return;

        if (_volatileUiSetupPending)
        {
            _volatileUiSetupPending = false;
            Log("VolatileUi setup timed out waiting for an active unit-world camera. Invalidate this profile run.");
        }

        if (_resizeSetupPending)
        {
            _resizeSetupPending = false;
            Log("Resize setup timed out waiting for an engine-owned desktop window. Invalidate this profile run.");
        }
    }

    private void ReportIncompleteSetup()
    {
        if (_volatileUiSetupPending)
            Log("VolatileUi deactivated before an active unit-world camera became available. Invalidate this profile run.");
        if (_resizeSetupPending)
            Log("Resize deactivated before an engine-owned desktop window became available. Invalidate this profile run.");
    }

    private static void Log(string message)
        => Debug.WriteAuxiliaryLog(AuxiliaryLogName, message);

    private static CameraComponent? FindActiveViewportCamera()
    {
        if (RuntimeEngine.Rendering.State.RenderFrameId < 10)
            return null;
        foreach (XRWindow window in RuntimeEngine.Windows)
        {
            foreach (XRViewport viewport in window.Viewports)
                if (viewport.ActiveCamera?.Transform.SceneNode?.GetComponent<CameraComponent>() is { } camera)
                    return camera;
        }
        return null;
    }

    private bool TryStartResizeWorkload()
    {
        foreach (XRWindow window in RuntimeEngine.Windows)
        {
            _resizeWindow = window;
            break;
        }

        if (_resizeWindow is null)
            return false;

        _nextResizeTime = Engine.Time.Timer.Time() + ResizePeriodSeconds;
        _nextResizeIsLarge = false;
        _resizeCount = 0;
        return true;
    }

    private void UpdateResizeWorkload(float now)
    {
        if (_resizeWindow is null || now < _nextResizeTime)
            return;

        _nextResizeTime = now + ResizePeriodSeconds;
        _resizeWindow.RequestResize(_nextResizeIsLarge ? 1920 : 1600, _nextResizeIsLarge ? 1080 : 900);
        _nextResizeIsLarge = !_nextResizeIsLarge;
        _resizeCount++;
    }

    private static bool IsSupportedMode(string? mode)
        => string.Equals(mode, "MaterialEdits", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Streaming", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "VolatileUi", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Resize", StringComparison.OrdinalIgnoreCase);

    private static Vector3[] CreateMaterialColors(int materialCount)
    {
        const int phaseCount = 8;
        var colors = new Vector3[Math.Max(1, materialCount) * phaseCount];
        for (int phase = 0; phase < phaseCount; phase++)
        {
            for (int materialIndex = 0; materialIndex < materialCount; materialIndex++)
            {
                float hue = (phase * materialCount + materialIndex) / (float)(phaseCount * Math.Max(1, materialCount));
                colors[phase * materialCount + materialIndex] = ColorFromHue(hue);
            }
        }

        return colors;
    }

    private static Vector3 ColorFromHue(float hue)
    {
        float red = MathF.Abs(hue * 6.0f - 3.0f) - 1.0f;
        float green = 2.0f - MathF.Abs(hue * 6.0f - 2.0f);
        float blue = 2.0f - MathF.Abs(hue * 6.0f - 4.0f);
        return Vector3.Clamp(new Vector3(red, green, blue), Vector3.Zero, Vector3.One);
    }
}
