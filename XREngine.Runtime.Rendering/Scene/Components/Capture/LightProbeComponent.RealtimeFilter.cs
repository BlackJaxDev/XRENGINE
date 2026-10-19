using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Capture.Lights;

public partial class LightProbeComponent
{
    private XRTextureCube[] _realtimeFilterCubes = [];
    private XRQuadFrameBuffer[] _realtimeFilterFaces = [];
    private XRQuadFrameBuffer[] _realtimeFilterResamples = [];
    private XRQuadFrameBuffer[] _realtimeFilterOutputs = [];
    private XRTexture? _realtimeFilterSource;
    private bool _realtimeFilterPrepared;

    private void DestroyRealtimeFilterResources()
    {
        foreach (XRQuadFrameBuffer? target in _realtimeFilterOutputs)
            if (target is not null) DestroyIblMipTarget(target);
        foreach (XRQuadFrameBuffer? target in _realtimeFilterResamples)
            if (target is not null) DestroyIblMipTarget(target);
        foreach (XRQuadFrameBuffer? target in _realtimeFilterFaces)
            if (target is not null) DestroyIblMipTarget(target);
        foreach (XRTextureCube? cube in _realtimeFilterCubes)
            cube?.Destroy();
        SetField(ref _realtimeFilterOutputs, [], publishNotifications: false);
        SetField(ref _realtimeFilterResamples, [], publishNotifications: false);
        SetField(ref _realtimeFilterFaces, [], publishNotifications: false);
        SetField(ref _realtimeFilterCubes, [], publishNotifications: false);
        SetField(ref _realtimeFilterSource, null, publishNotifications: false);
        SetField(ref _realtimeFilterPrepared, false, publishNotifications: false);
    }

    private bool GenerateRealtimeFilteredProbe(XRTexture2D output)
    {
        XRTexture source = _prefilterSourceTexture
            ?? throw new InvalidOperationException("Reflection filter source is missing.");
        EnsureRealtimeFilterResources(source, output);

        for (int mip = 0; mip < _realtimeFilterOutputs.Length; mip++)
        {
            XRQuadFrameBuffer pass = _realtimeFilterOutputs[mip];
            if (pass.Targets is not { Length: 1 } attachments ||
                !ReferenceEquals(attachments[0].Target, output))
                pass.SetRenderTargets((output, EFrameBufferAttachment.ColorAttachment0, mip, -1));
        }
        if (!_realtimeFilterPrepared)
        {
            // Prepare every pass before retrying. A cold shader must not consume
            // one retry per face and prevent later passes from ever starting.
            bool ready = true;
            foreach (XRQuadFrameBuffer pass in _realtimeFilterFaces)
                ready &= TryPrepareProbePass(pass.FullScreenMesh, pass.Name);
            foreach (XRQuadFrameBuffer pass in _realtimeFilterResamples)
                ready &= TryPrepareProbePass(pass.FullScreenMesh, pass.Name);
            foreach (XRQuadFrameBuffer pass in _realtimeFilterOutputs)
                ready &= TryPrepareProbePass(pass.FullScreenMesh, pass.Name);
            if (!ready)
                return false;
            SetField(ref _realtimeFilterPrepared, true, publishNotifications: false);
        }

        // Separate images prevent a read/write alias between adjacent mip passes.
        // The enclosing IBL writer fence protects all intermediates from reuse.
        for (int index = 0; index < _realtimeFilterFaces.Length; index++)
        {
            int extent = Math.Max(1, (int)_realtimeFilterCubes[0].Extent >> (index / 6));
            if (!RunFullscreenProbePass(_realtimeFilterFaces[index], extent, extent))
                return false;
            if (index % 6 == 5)
                SynchronizeCaptureTextureWrites();
        }

        for (int index = 0; index < _realtimeFilterResamples.Length; index++)
        {
            int extent = Math.Max(1, (int)_realtimeFilterCubes[0].Extent >> (index / 6));
            if (!RunFullscreenProbePass(_realtimeFilterResamples[index], extent, extent))
                return false;
        }
        SynchronizeCaptureTextureWrites();

        for (int mip = 0; mip < _realtimeFilterOutputs.Length; mip++)
        {
            XRQuadFrameBuffer pass = _realtimeFilterOutputs[mip];
            if (!RunFullscreenProbePass(pass, Math.Max(1, (int)output.Width >> mip),
                Math.Max(1, (int)output.Height >> mip)))
                return false;
        }
        return true;
    }

    private void EnsureRealtimeFilterResources(XRTexture source, XRTexture2D output)
    {
        uint extent = Resolution;
        if (extent < 16u || (extent & (extent - 1u)) != 0u)
            throw new NotSupportedException("Unity realtime reflection filtering requires a power-of-two probe resolution of at least 16.");
        int mipCount = XRTexture.GetSmallestMipmapLevel(extent, extent) + 1;
        int resolvedMipCount = Math.Min(mipCount, 7);
        int outputCount = output.SmallestMipmapLevel + 1;
        if (output.Width < 512u || output.Height < 512u || outputCount != 7)
            throw new InvalidOperationException("Unity realtime reflection output requires seven authored mip levels from a base extent of at least 512.");
        if (ReferenceEquals(_realtimeFilterSource, source) &&
            _realtimeFilterCubes.Length == 3 && _realtimeFilterCubes[0].Extent == extent &&
            _realtimeFilterCubes[2].Mipmaps.Length == resolvedMipCount &&
            _realtimeFilterOutputs.Length == outputCount)
            return;

        DestroyRealtimeFilterResources();
        try
        {
            SetField(ref _realtimeFilterCubes, new XRTextureCube[3], publishNotifications: false);
            for (int index = 0; index < _realtimeFilterCubes.Length; index++)
                _realtimeFilterCubes[index] = new XRTextureCube(extent,
                    EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat,
                    false, index == 2 ? resolvedMipCount : mipCount)
                {
                    Name = $"LightProbe.RealtimeFilter.{index}",
                    SizedInternalFormat = ESizedInternalFormat.Rgba16f,
                    MinFilter = index == 2 ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.LinearMipmapNearest,
                    MagFilter = ETexMagFilter.Linear,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    SmallestAllowedMipmapLevel = (index == 2 ? resolvedMipCount : mipCount) - 1,
                    AutoGenerateMipmaps = false,
                    Resizable = false,
                };

            // Each deferred draw owns fixed parameters and one fixed attachment.
            // Material mutation cannot change a previously queued face or mip.
            SetField(ref _realtimeFilterFaces, new XRQuadFrameBuffer[mipCount * 6], publishNotifications: false);
            for (int mip = 0; mip < mipCount; mip++)
            {
                // The first reduction is a box average. Later levels use the
                // face-area-adjusted Gaussian kernel on the previous result.
                string shader = mip > 1 ? "ProbeCubeGaussian.fs" : mip == 1 || source is XRTextureCube
                    ? "ProbeCubeCopy.fs" : ReferenceEquals(source, EnvironmentTextureEquirect)
                        ? "ProbeEquirectToCube.fs" : "ProbeOctaToCube.fs";
                XRTexture input = mip == 0 ? source : _realtimeFilterCubes[(mip - 1) & 1];
                for (int face = 0; face < 6; face++)
                {
                    XRQuadFrameBuffer pass = CreateRealtimeFilterPass(
                        $"LightProbe.RealtimeFilter.Mip{mip}.Face{face}", shader,
                        [new ShaderInt(face, "Face"), new ShaderFloat(Math.Max(0, mip - 1), "SourceLevel")],
                        [input]);
                    _realtimeFilterFaces[mip * 6 + face] = pass;
                    pass.SetRenderTargets((_realtimeFilterCubes[mip & 1],
                        EFrameBufferAttachment.ColorAttachment0, mip, face));
                }
            }

            SetField(ref _realtimeFilterResamples, new XRQuadFrameBuffer[resolvedMipCount * 6], publishNotifications: false);
            for (int mip = 0; mip < resolvedMipCount; mip++)
            {
                float level = GetRealtimeFilterSourceLevel(mip, mipCount);
                for (int face = 0; face < 6; face++)
                {
                    XRQuadFrameBuffer pass = CreateRealtimeFilterPass(
                        $"LightProbe.RealtimeFilter.ResolveMip{mip}.Face{face}", "ProbeCubeResample.fs",
                        [new ShaderInt(face, "Face"), new ShaderFloat(level, "SourceLevel")],
                        [_realtimeFilterCubes[0], _realtimeFilterCubes[1]]);
                    _realtimeFilterResamples[mip * 6 + face] = pass;
                    pass.SetRenderTargets((_realtimeFilterCubes[2],
                        EFrameBufferAttachment.ColorAttachment0, mip, face));
                }
            }

            SetField(ref _realtimeFilterOutputs, new XRQuadFrameBuffer[outputCount], publishNotifications: false);
            for (int mip = 0; mip < outputCount; mip++)
            {
                // Keep the Unity mip index. Consumers map material roughness to
                // this authored grid when they sample the published texture.
                float level = Math.Min(mip, resolvedMipCount - 1);
                _realtimeFilterOutputs[mip] = CreateRealtimeFilterPass(
                    $"LightProbe.RealtimeFilter.OctaMip{mip}", "ProbeFilteredCubeToOcta.fs",
                    [new ShaderFloat(level, "SourceLevel")],
                    [_realtimeFilterCubes[2]]);
            }
            SetField(ref _realtimeFilterSource, source, publishNotifications: false);
        }
        catch
        {
            DestroyRealtimeFilterResources();
            throw;
        }
    }

    private static float GetRealtimeFilterSourceLevel(int mip, int mipCount)
    {
        // The discrete schedule is measured from the BuiltIn realtime producer.
        // It is independent of the captured scene and material roughness.
        ReadOnlySpan<float> levels = [0f, 2.0633168f, 3.9842191f, 5.0717735f, 5.8513417f, 6.4144440f, 7f];
        if (mip == 0)
            return 0f;
        if (mip == mipCount - 1)
            return Math.Min(7f, mipCount - 1);
        return Math.Clamp(levels[Math.Min(mip, levels.Length - 1)] + mipCount - 8, 0f, mipCount - 1);
    }

    private static XRQuadFrameBuffer CreateRealtimeFilterPass(string name, string shader,
        ShaderVar[] parameters, XRTexture[] textures)
    {
        RenderingParameters renderOptions = CreateIblRenderParams();
        renderOptions.RequiredEngineUniforms |= EUniformRequirements.ClipSpacePolicy;
        XRMaterial material = new(parameters, textures, GetFullscreenTriVertexShader(),
            ShaderHelper.LoadEngineShader($"Scene3D/{shader}", EShaderType.Fragment))
        {
            RenderOptions = renderOptions,
        };
        XRQuadFrameBuffer? pass = null;
        try
        {
            pass = new(material, deriveRenderTargetsFromMaterial: false) { Name = name };
            pass.FullScreenMesh.Name = name;
            ConfigureProbeFullscreenFramebuffer(pass);
            return pass;
        }
        catch
        {
            pass?.Destroy();
            material.Destroy();
            throw;
        }
    }
}
