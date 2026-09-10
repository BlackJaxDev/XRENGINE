using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Two bounded, opt-in native frontend pilots with retained authored GLSL counterparts.</summary>
internal static class SlangShaderPilots
{
    internal static string ResolvePath(string relativePath)
    {
        if (Environment.GetEnvironmentVariable("XRE_SLANG_PILOTS") != "1")
            return relativePath;
        return relativePath.Replace('\\', '/') switch
        {
            "Compute/Indirect/GPURenderCopyCount3.comp" => "FrontendPilots/CopyCount3.comp.slang",
            "Scene3D/SceneCopy.fs" => "FrontendPilots/SceneCopy.frag.slang",
            _ => relativePath,
        };
    }

    internal static void Configure(XRShader shader, string path)
    {
        switch (path.Replace('\\', '/'))
        {
            case "FrontendPilots/CopyCount3.comp.slang":
                shader.SourceLanguage = ShaderSourceLanguage.Slang;
                shader.EntryPoint = "copyCounts";
                shader.SlangOptions = new SlangShaderOptions
                {
                    Resources =
                    [
                        new("SrcCountBuffer", "SrcCountBuffer", 0, 0, ShaderAbiResourceKind.StorageBuffer,
                            ShaderAbiResourceOwner.External, ShaderAbiFrequency.Pass, 12,
                            [new("first", "Src0", 0, 4, "uint"), new("second", "Src1", 4, 4, "uint"), new("third", "Src2", 8, 4, "uint")], ShaderAbiDescriptorLifetime.Globals),
                        new("DstCountBuffer", "DstCountBuffer", 0, 1, ShaderAbiResourceKind.StorageBuffer,
                            ShaderAbiResourceOwner.External, ShaderAbiFrequency.Pass, 12,
                            [new("first", "Dst0", 0, 4, "uint"), new("second", "Dst1", 4, 4, "uint"), new("third", "Dst2", 8, 4, "uint")], ShaderAbiDescriptorLifetime.Globals),
                    ],
                };
                break;
            case "FrontendPilots/SceneCopy.frag.slang":
                shader.SourceLanguage = ShaderSourceLanguage.Slang;
                shader.EntryPoint = "copyScene";
                shader.SlangOptions = new SlangShaderOptions
                {
                    Resources =
                    [
                        new("HDRSceneTex", "HDRSceneTex", 2, 0, ShaderAbiResourceKind.CombinedImageSampler,
                            ShaderAbiResourceOwner.Material, ShaderAbiFrequency.Material, 0, [], ShaderAbiDescriptorLifetime.Material),
                    ],
                };
                break;
        }
    }
}
