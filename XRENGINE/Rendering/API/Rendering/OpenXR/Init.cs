using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Silk.NET.Windowing;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene.Transforms;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Provides an implementation of XR functionality using the OpenXR standard.
/// Handles initialization, session management, swapchain creation, and frame rendering.
/// </summary>
public unsafe partial class OpenXRAPI : XRBase
{
    public OpenXRAPI()
    {
        EnsureOpenXRLoaderResolutionConfigured();
        try
        {
            Api = XR.GetApi();
        }
        catch (FileNotFoundException ex)
        {
            Debug.LogWarning($"OpenXR loader was not found. BaseDir='{AppContext.BaseDirectory}', CWD='{Environment.CurrentDirectory}', ActiveRuntime='{TryGetOpenXRActiveRuntime() ?? "<unknown>"}'. {ex.Message}");
            throw;
        }
    }

    ~OpenXRAPI()
    {
        CleanUp();
        Api?.Dispose();
    }
}
