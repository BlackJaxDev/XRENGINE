using Silk.NET.Core.Contexts;
using Silk.NET.Shaderc;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Loads the packaged shader compiler that supports the Vulkan 1.4 target.
/// </summary>
internal static class VulkanShaderCompilerLibrary
{
    internal static string LibraryPath => Path.Combine(AppContext.BaseDirectory, "xr_shaderc.dll");

    /// <summary>Creates bindings to the exact compiler shipped with the engine.</summary>
    public static Shaderc CreateApi()
    {
        if (!File.Exists(LibraryPath))
            throw new DllNotFoundException("The packaged Vulkan shader compiler xr_shaderc.dll is missing. Rebuild or reinstall the engine.");

        return new Shaderc(new DefaultNativeContext(LibraryPath));
    }
}
