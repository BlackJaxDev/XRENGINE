namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Exact WebGPU texture shape shared by artifact validation and backend binding.</summary>
public readonly record struct ShaderTextureBindingType(
    bool IsStorage, string ViewDimension, string SampleType, string? StorageFormat,
    string? StorageAccess, string WgslType)
{
    public static bool TryParse(string bindingType, out ShaderTextureBindingType shape)
    {
        shape = default;
        foreach (string dimension in new[] { "2d-array", "2d", "cube" })
        {
            string token = dimension.Replace('-', '_');
            if (bindingType == "texture-depth-" + dimension && dimension is "2d" or "2d-array")
            {
                shape = new(false, dimension, "depth", null, null, "texture_depth_" + token);
                return true;
            }
            string prefix = "texture-" + dimension + "-";
            if (bindingType.StartsWith(prefix, StringComparison.Ordinal))
            {
                string sample = bindingType[prefix.Length..];
                string? scalar = sample switch { "float" or "unfilterable-float" => "f32", "uint" => "u32", "sint" => "i32", _ => null };
                if (scalar is null) return false;
                shape = new(false, dimension, sample, null, null, $"texture_{token}<{scalar}>");
                return true;
            }
            if (dimension == "cube") continue;
            prefix = "storage-texture-" + dimension + "-";
            if (!bindingType.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string suffix = bindingType[prefix.Length..];
            foreach (string access in new[] { "read-write", "write", "read" })
            {
                if (!suffix.StartsWith(access + "-", StringComparison.Ordinal)) continue;
                string format = suffix[(access.Length + 1)..];
                if (!StorageFormatSupported(format) || access == "read-write" && format is not ("r32float" or "r32uint" or "r32sint")) return false;
                string apiAccess = access switch { "read" => "read-only", "write" => "write-only", _ => "read-write" };
                string wgslAccess = access.Replace('-', '_');
                shape = new(true, dimension, string.Empty, format, apiAccess, $"texture_storage_{token}<{format},{wgslAccess}>");
                return true;
            }
            return false;
        }
        return false;
    }

    private static bool StorageFormatSupported(string format) => format is
        "rgba8unorm" or "rgba16float" or "r32float" or "rg32float" or "rgba32float" or
        "r32uint" or "rg32uint" or "rgba32uint" or "r32sint" or "rg32sint" or "rgba32sint";
}
