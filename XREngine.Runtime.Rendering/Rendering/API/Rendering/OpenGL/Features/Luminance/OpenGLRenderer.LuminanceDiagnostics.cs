using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    public override bool SupportsGpuAutoExposure
    {
        get
        {
            if (_supportsGpuAutoExposure == false)
                return false;

            _supportsGpuAutoExposure ??= ComputeSupportsGpuAutoExposure();
            if (_supportsGpuAutoExposure != true)
                return false;

            // Lazily compile/initialize once we know the context should support it.
            EnsureAutoExposureComputeResources();

            if (!_autoExposureComputeInitialized)
                _supportsGpuAutoExposure = false;

            return _autoExposureComputeInitialized;
        }
    }

    private bool ComputeSupportsGpuAutoExposure()
    {
        try
        {
            // Compute shaders are core in GL 4.3+. Image load/store is core in GL 4.2+.
            int major = Api.GetInteger(GLEnum.MajorVersion);
            int minor = Api.GetInteger(GLEnum.MinorVersion);

            bool hasCompute = (major > 4) || (major == 4 && minor >= 3) || IsExtensionSupported("GL_ARB_compute_shader");
            bool hasImageLoadStore = (major > 4) || (major == 4 && minor >= 2) || IsExtensionSupported("GL_ARB_shader_image_load_store");

            return hasCompute && hasImageLoadStore;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if GL_ARB_texture_filter_minmax extension is available.
    /// This extension provides hardware-accelerated min/max/average filtering.
    /// </summary>
    public bool SupportsTextureFilterMinmax => HasTextureFilterMinmax;
}
