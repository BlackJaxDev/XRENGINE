using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Editor.HotReload;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Settings;

namespace XREngine.Editor;

/// <summary>
/// Coordinates live switching between OpenGL and Vulkan graphics backends in the running editor.
/// </summary>
public static class EditorGraphicsBackendSwitcher
{
    private static int _isSwitching;

    /// <summary>Gets whether a graphics backend switch is currently in progress.</summary>
    public static bool IsSwitching => Volatile.Read(ref _isSwitching) != 0;

    /// <summary>Gets the most recent graphics backend switch error, if any.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Gets the currently active graphics backend for the primary editor window.</summary>
    public static ERenderLibrary ActiveBackend
    {
        get
        {
            if (RuntimeEngine.Windows.FirstOrDefault()?.Renderer is { } renderer)
            {
                return renderer.BackendId == RendererBackendId.Vulkan
                    ? ERenderLibrary.Vulkan
                    : ERenderLibrary.OpenGL;
            }

            return Engine.EffectiveSettings.PreferredRenderBackend;
        }
    }

    /// <summary>Gets whether the editor can switch graphics backends right now.</summary>
    public static bool CanSwitch
        => !IsSwitching &&
           !EditorOpenXrPawnSwitcher.IsRequested &&
           !RuntimeEngine.VRState.IsInVR &&
           RuntimeEngine.Windows.FirstOrDefault()?.Renderer is not null &&
           !RendererReplacementCoordinator.Current.IsReloadInProgress;

    /// <summary>Gets a short status or tooltip description for the graphics backend switcher.</summary>
    public static string Status
    {
        get
        {
            if (IsSwitching)
                return "Switching graphics backend...";
            if (EditorOpenXrPawnSwitcher.IsRequested || RuntimeEngine.VRState.IsInVR)
                return "Disable OpenXR/VR before switching graphics backends.";
            return LastError ?? $"Switch the active graphics backend (current: {ActiveBackend}).";
        }
    }

    /// <summary>Starts an asynchronous switch to <paramref name="targetBackend"/> if allowed.</summary>
    public static bool TrySwitchBackend(ERenderLibrary targetBackend)
    {
        if (!CanSwitch || targetBackend == ActiveBackend)
            return false;

        _ = SwitchBackendAsync(targetBackend);
        return true;
    }

    /// <summary>Switches the active editor window and renderer to <paramref name="targetBackend"/>.</summary>
    public static async Task<bool> SwitchBackendAsync(ERenderLibrary targetBackend)
    {
        if (targetBackend == ActiveBackend)
            return true;

        if (EditorOpenXrPawnSwitcher.IsRequested || RuntimeEngine.VRState.IsInVR)
        {
            LastError = "Disable OpenXR/VR before switching graphics backends.";
            return false;
        }

        if (Interlocked.CompareExchange(ref _isSwitching, 1, 0) != 0)
            return false;

        LastError = null;
        try
        {
            XRWindow window = RuntimeEngine.Windows.FirstOrDefault()
                ?? throw new InvalidOperationException("No active editor window is available.");

            Engine.InvokeOnMainThread(
                EditorImGuiUI.PrepareForImGuiContextReplacement,
                "EditorGraphicsBackendSwitcher.FlushImGuiLayoutBeforeSwitch",
                executeNowIfAlreadyMainThread: true);

            var configuration = new GraphicsBackendReplacementConfiguration(targetBackend);
            RendererReplacementResult result = await RendererHotReloadService.Current
                .RestartCurrentGenerationWithConfigurationAsync(
                    window.Renderer.BackendId,
                    configuration,
                    TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                LastError = result.Error ?? $"Failed to switch graphics backend to {targetBackend}.";
                Debug.LogWarning($"[GraphicsBackendSwitcher] Failed to switch to {targetBackend}: {LastError}");
                return false;
            }

            LastError = null;
            Debug.Out($"[GraphicsBackendSwitcher] Switched active graphics backend to {targetBackend}.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.LogWarning($"[GraphicsBackendSwitcher] Exception while switching to {targetBackend}: {ex}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _isSwitching, 0);
        }
    }

    private sealed class GraphicsBackendReplacementConfiguration(ERenderLibrary targetBackend)
        : IRendererReplacementConfiguration
    {
        private readonly ERenderLibrary _previousBootstrapBackend = RuntimeBootstrapState.Settings.Rendering.RenderBackend;
        private readonly ERenderLibrary _previousUserBackend = Engine.EffectiveSettings.PreferredRenderBackend;
        private readonly Dictionary<XRWindow, RuntimeGraphicsApiKind> _previousWindowApis = new();

        public void ApplyAfterDetach()
        {
            EditorImGuiUI.PrepareForImGuiContextReplacement();

            RuntimeBootstrapState.Settings.Rendering.RenderBackend = targetBackend;
            Engine.SetSessionSetting<UserSettings, ERenderLibrary>(s => s.PreferredRenderBackend, targetBackend);
            Engine.SetSessionSetting<UserSettings, ERenderLibrary>(s => s.RenderLibrary, targetBackend);
            BootstrapRenderSettings.Apply();

            RuntimeGraphicsApiKind targetApi = targetBackend == ERenderLibrary.Vulkan
                ? RuntimeGraphicsApiKind.Vulkan
                : RuntimeGraphicsApiKind.OpenGL;

            _previousWindowApis.Clear();
            foreach (XRWindow window in RuntimeEngine.Windows.ToArray())
            {
                if (window.IsDisposed || window.IsDisposing || window.IsSecondaryGpuContext)
                    continue;

                RuntimeGraphicsApiKind currentApi = window.DesktopVulkanSurface is not null
                    ? RuntimeGraphicsApiKind.Vulkan
                    : RuntimeGraphicsApiKind.OpenGL;
                _previousWindowApis[window] = currentApi;
                window.ReplaceDesktopWindowBackend(targetApi);
            }
        }

        public void RestoreBeforeRollback()
        {
            EditorImGuiUI.PrepareForImGuiContextReplacement();

            RuntimeBootstrapState.Settings.Rendering.RenderBackend = _previousBootstrapBackend;
            Engine.SetSessionSetting<UserSettings, ERenderLibrary>(s => s.PreferredRenderBackend, _previousUserBackend);
            Engine.SetSessionSetting<UserSettings, ERenderLibrary>(s => s.RenderLibrary, _previousUserBackend);
            BootstrapRenderSettings.Apply();

            foreach ((XRWindow window, RuntimeGraphicsApiKind previousApi) in _previousWindowApis)
            {
                if (window.IsDisposed || window.IsDisposing || window.IsSecondaryGpuContext)
                    continue;

                window.ReplaceDesktopWindowBackend(previousApi);
            }
        }
    }
}
