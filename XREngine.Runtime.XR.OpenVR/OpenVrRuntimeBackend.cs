using OpenVR.NET;
using OpenVR.NET.Manifest;
using OpenVR.NET.Devices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valve.VR;
using XREngine.Input;

namespace XREngine;

/// <summary>Owns the process-wide OpenVR API and action set.</summary>
public static class OpenVrRuntimeBackend
{
    private static VR? _api;
    private static readonly Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>> ActionMap = [];

    public static VR Api
    {
        get => _api ??= new VR();
        set => _api = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static VR? ApiIfCreated => _api;

    public static Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>> Actions => ActionMap;

    public static event Action<Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>>>? ActionsChanged;

    public static void NotifyActionsChanged()
        => ActionsChanged?.Invoke(ActionMap);

    public static bool TryStartScene(
        IRuntimeOpenVrActionManifest actionManifest,
        RuntimeOpenVrApplicationManifest appManifest,
        out string? failure)
    {
        failure = null;
        OpenVrActionManifestAdapter nativeActions = new(actionManifest);
        VrManifest nativeApp = ToNativeManifest(appManifest);
        VR vr = Api;
        vr.DeviceDetected += OnDeviceDetected;
        if (!vr.TryStart(EVRApplicationType.VRApplication_Scene))
        {
            vr.DeviceDetected -= OnDeviceDetected;
            failure = "Failed to initialize SteamVR.";
            return false;
        }

        if (vr.CVR is null || Valve.VR.OpenVR.Compositor is null)
        {
            vr.DeviceDetected -= OnDeviceDetected;
            vr.Exit();
            failure = "SteamVR initialized without an IVRCompositor interface. OpenVR scene rendering will not start.";
            return false;
        }

        int appError = InstallApplicationManifest(nativeApp);
        if (appError != 0)
            Debug.LogWarning($"Error installing app manifest: {appError}");
        vr.SetActionManifest(nativeActions);
        CreateActions(nativeActions, vr);
        return true;
    }

    private static void OnDeviceDetected(VrDevice device)
        => Debug.Out($"Device detected: {device}");

    private static void CreateActions(IActionManifest actionManifest, VR vr)
    {
        ActionMap.Clear();
        foreach (var actionSet in actionManifest.ActionSets)
        {
            foreach (var action in actionManifest.ActionsForSet(actionSet))
            {
                OpenVR.NET.Input.Action? nativeAction = action.CreateAction(vr, null);
                if (nativeAction is null)
                    continue;

                string categoryName = actionSet.Name.ToString();
                if (!ActionMap.TryGetValue(categoryName, out var nameMap))
                    ActionMap.Add(categoryName, nameMap = []);
                nameMap.Add(action.Name.ToString(), nativeAction);
            }
        }

        NotifyActionsChanged();
    }

    public static void Register()
    {
        RuntimeOpenVrStateServices.Current = OpenVrStateProvider.Instance;
        RuntimeOpenVrCompositorServices.Current = OpenVrCompositorBackend.Instance;
    }

    public static bool TryGetRecommendedRenderTargetSize(out uint width, out uint height)
    {
        width = 0;
        height = 0;
        VR? vr = ApiIfCreated;
        if (vr?.CVR is null)
            return false;

        vr.CVR.GetRecommendedRenderTargetSize(ref width, ref height);
        return width > 0 && height > 0;
    }

    public static void UpdateDraw(RuntimeVrTrackingOrigin origin)
        => _ = Api.UpdateDraw((ETrackingUniverseOrigin)origin);

    public static bool ShouldReduceRenderingWork()
        => Api.CVR.ShouldApplicationReduceRenderingWork();

    public static bool TryGetDisplayFrequency(out float hz)
    {
        ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
        hz = Api.CVR.GetFloatTrackedDeviceProperty(
            0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success && hz > 0f;
    }

    public static void UpdateInputAndDevices()
    {
        VR vr = Api;
        if (vr.Headset is null)
        {
            vr.UpdateInput(0);
        }
        else
        {
            uint deviceIndex = vr.Headset.DeviceIndex;
            ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
            float secondsSinceLastVsync = 0f;
            ulong frameCount = 0;
            vr.CVR.GetTimeSinceLastVsync(ref secondsSinceLastVsync, ref frameCount);
            float displayFrequency = vr.CVR.GetFloatTrackedDeviceProperty(
                deviceIndex, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
            float motionToPhoton = vr.CVR.GetFloatTrackedDeviceProperty(
                deviceIndex, ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float, ref error);
            float frameDuration = 1f / displayFrequency;
            vr.UpdateInput(frameDuration - secondsSinceLastVsync + motionToPhoton);
        }

        vr.Update();
    }

    public static bool TryReadFrameStats(uint lastFrameSampleIndex, out RuntimeVrFrameStats stats)
    {
        stats = default;
        CVRCompositor? compositor = Valve.VR.OpenVR.Compositor;
        if (compositor is null)
            return false;

        uint size = (uint)Marshal.SizeOf<Compositor_FrameTiming>();
        Compositor_FrameTiming current = new() { m_nSize = size };
        Compositor_FrameTiming previous = new() { m_nSize = size };
        compositor.GetFrameTiming(ref current, 0);
        compositor.GetFrameTiming(ref previous, 1);
        uint latestFrameIndex = current.m_nFrameIndex;
        uint frameCount = latestFrameIndex - lastFrameSampleIndex;
        if (frameCount == 0)
            return false;

        double gpuMs = 0;
        double cpuMs = 0;
        double totalMs = 0;
        for (uint i = 0; i < frameCount; i++)
        {
            compositor.GetFrameTiming(ref current, i);
            compositor.GetFrameTiming(ref previous, i + 1);
            gpuMs += current.m_flTotalRenderGpuMs;
            cpuMs += current.m_flNewFrameReadyMs - current.m_flNewPosesReadyMs + current.m_flCompositorRenderCpuMs;
            totalMs += (current.m_flSystemTimeInSeconds - previous.m_flSystemTimeInSeconds) * 1000d;
        }

        gpuMs /= frameCount;
        cpuMs /= frameCount;
        totalMs /= frameCount;
        stats = new RuntimeVrFrameStats(
            latestFrameIndex,
            (float)gpuMs,
            (float)cpuMs,
            (float)totalMs,
            totalMs > 0d ? (int)(1000d / totalMs) : 0f);
        return true;
    }

    public static int InstallApplicationManifest(VrManifest manifest)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), ".vrmanifest");
        string json = JsonSerializer.Serialize(
            new VrManifestInstallDocument { Applications = [manifest] },
            XREnginePrettyJsonContext.Default.VrManifestInstallDocument);
        File.WriteAllText(path, json);
        CVRApplications applications = Valve.VR.OpenVR.Applications
            ?? throw new InvalidOperationException("The OpenVR applications interface is unavailable.");
        return (int)applications.AddApplicationManifest(path, false);
    }

    private static VrManifest ToNativeManifest(RuntimeOpenVrApplicationManifest manifest)
        => new()
        {
            AppKey = manifest.AppKey,
            WindowsPath = manifest.WindowsPath,
            WindowsArguments = manifest.WindowsArguments,
            OSXPath = manifest.OSXPath,
            OSXArguments = manifest.OSXArguments,
            LinuxPath = manifest.LinuxPath,
            LinuxArguments = manifest.LinuxArguments,
            Icon = manifest.Icon,
            IsDashboardOverlay = manifest.IsDashboardOverlay,
            ActionManifestPath = manifest.ActionManifestPath,
            LocalizedNames = manifest.LocalizedNames?.ToDictionary(
                static pair => pair.Key,
                static pair => new NameDescription
                {
                    Name = pair.Value.Name,
                    Description = pair.Value.Description,
                }),
        };

}
