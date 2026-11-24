using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.Gaussian;

/// <summary>
/// Interns avatar submesh materials and textures into storage buffers and sampler tables consumed by
/// <c>GaussianSplatUberQuad.fs</c>. Each submesh material maps to one <see cref="AdvancedMaterialRecord"/> row
/// using either the full <see cref="NativeUberMaterialSchema"/> ABI or the standard material fallback layout.
/// </summary>
internal sealed class GaussianAvatarSplatMaterialTable : IDisposable
{
    /// <summary>Storage buffer binding for <see cref="AdvancedMaterialRecord"/> rows.</summary>
    public const uint MaterialsBinding = 61u;

    /// <summary>Storage buffer binding for packed constant words referenced by <see cref="AdvancedMaterialRecord"/>.</summary>
    public const uint MaterialConstantsBinding = 62u;

    /// <summary>Storage buffer binding for <see cref="AdvancedMaterialTextureBinding"/> records.</summary>
    public const uint MaterialTextureBindingsBinding = 63u;

    /// <summary>Total capacity across the five 32-element 2D sampler banks (<c>uSplatTextures2D_0..4</c>).</summary>
    public const int MaxTextures2D = 160;

    /// <summary>Length of the <c>uSplatTexturesCube</c> descriptor array at binding 160.</summary>
    public const int MaxTexturesCube = 8;

    /// <summary>Total texture slots placed onto the Uber splat quad material.</summary>
    public const int TotalQuadTextureSlots = MaxTextures2D + MaxTexturesCube;

    private const uint FallbackSlotWhite2D = 0u;
    private const uint FallbackSlotBlack2D = 1u;
    private const uint FallbackSlotFlatNormal2D = 2u;
    private const uint FallbackSlotIdentityRamp2D = 3u;
    private const uint FallbackSlotWhiteCube = 0u;
    private const uint FallbackSlotBlackCube = 1u;

    private static readonly Regex UberUniformDeclarationRegex = new(
        @"\buniform\s+(?:(?:lowp|mediump|highp)\s+)?(bool|int|uint|float|vec2|vec3|vec4)\s+([A-Za-z0-9_]+)\s*[;=]",
        RegexOptions.Compiled);

    private readonly Dictionary<XRMaterial, InternedMaterialEntry> _internedMaterials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRTexture2D, uint> _texture2DSlots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TextureContentKey, uint> _texture2DContentSlots = [];
    private readonly Dictionary<string, uint> _texture2DSourceSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<XRTextureCube, uint> _textureCubeSlots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ShaderUiProperty> _uberManifestProperties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uberUniformGlslTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _uberManifestFeatureDefaults = new(StringComparer.Ordinal);
    private readonly List<XRTexture2D> _textures2D = new(MaxTextures2D);
    private readonly List<XRTextureCube> _texturesCube = new(MaxTexturesCube);
    private readonly List<TextureResidencyTicket> _residencyTickets = [];
    private readonly List<AdvancedMaterialRecord> _materials = [];
    private readonly List<uint> _materialConstants = [];
    private readonly List<AdvancedMaterialTextureBinding> _textureBindings = [];

    private readonly XRTexture2D _fallbackWhite2D;
    private readonly XRTexture2D _fallbackBlack2D;
    private readonly XRTexture2D _fallbackFlatNormal2D;
    private readonly XRTexture2D _fallbackIdentityRamp2D;
    private readonly XRTextureCube _fallbackWhiteCube;
    private readonly XRTextureCube _fallbackBlackCube;

    private InternedMaterialEntry? _nullMaterialEntry;
    private XRDataBuffer? _materialsBuffer;
    private XRDataBuffer? _materialConstantsBuffer;
    private XRDataBuffer? _textureBindingsBuffer;
    private uint _activeUberFeatureMask;
    private int _texture2DReuseCount;
    private int _constantTextureBindingCount;
    private int _disabledRoleTextureCount;
    private int _texture2DOverflowCount;
    private bool _fallbackTexturesPushed;
    private bool _disposed;

    public GaussianAvatarSplatMaterialTable()
    {
        LoadUberManifestMetadata();

        _fallbackWhite2D = CreateFallbackTexture2D(
            "GaussianSplatFallbackWhite",
            1u,
            1u,
            ColorF4.White,
            ETextureImportUsage.Color,
            ETexWrapMode.Repeat,
            pixelBytes: null);
        _fallbackBlack2D = CreateFallbackTexture2D(
            "GaussianSplatFallbackBlack",
            1u,
            1u,
            new ColorF4(0.0f, 0.0f, 0.0f, 1.0f),
            ETextureImportUsage.Data,
            ETexWrapMode.Repeat,
            pixelBytes: null);
        _fallbackFlatNormal2D = CreateFallbackTexture2D(
            "GaussianSplatFallbackFlatNormal",
            1u,
            1u,
            new ColorF4(0.5f, 0.5f, 1.0f, 1.0f),
            ETextureImportUsage.Normal,
            ETexWrapMode.Repeat,
            pixelBytes: null);
        _fallbackIdentityRamp2D = CreateFallbackTexture2D(
            "GaussianSplatFallbackIdentityRamp",
            2u,
            1u,
            ColorF4.White,
            ETextureImportUsage.Color,
            ETexWrapMode.ClampToEdge,
            pixelBytes: [0, 0, 0, 255, 255, 255, 255, 255]);
        _fallbackWhiteCube = CreateFallbackCube("GaussianSplatFallbackWhiteCube", [255, 255, 255, 255]);
        _fallbackBlackCube = CreateFallbackCube("GaussianSplatFallbackBlackCube", [0, 0, 0, 0]);

        _textures2D.Add(_fallbackWhite2D);
        _textures2D.Add(_fallbackBlack2D);
        _textures2D.Add(_fallbackFlatNormal2D);
        _textures2D.Add(_fallbackIdentityRamp2D);
        _texturesCube.Add(_fallbackWhiteCube);
        _texturesCube.Add(_fallbackBlackCube);
    }

    /// <summary>GPU storage buffer bound to <see cref="MaterialsBinding"/>.</summary>
    public XRDataBuffer? MaterialsBuffer => _materialsBuffer;

    /// <summary>GPU storage buffer bound to <see cref="MaterialConstantsBinding"/>.</summary>
    public XRDataBuffer? MaterialConstantsBuffer => _materialConstantsBuffer;

    /// <summary>GPU storage buffer bound to <see cref="MaterialTextureBindingsBinding"/>.</summary>
    public XRDataBuffer? MaterialTextureBindingsBuffer => _textureBindingsBuffer;

    /// <summary>Number of interned material rows.</summary>
    public int MaterialCount => _materials.Count;

    /// <summary>Number of interned 2D textures including the four shared fallback textures.</summary>
    public int Texture2DCount => _textures2D.Count;

    /// <summary>
    /// Number of material texture references that use an already interned slot because a different texture object
    /// has the same pixels or the same source file. Separately deserialized copies of one texture use one slot.
    /// </summary>
    public int Texture2DReuseCount => _texture2DReuseCount;

    /// <summary>
    /// Number of texture bindings that point to a shared fallback slot because the authored texture is a 1x1 or 2x2
    /// solid white, black, or flat-normal texture. These textures do not use their own slot.
    /// </summary>
    public int ConstantTextureBindingCount => _constantTextureBindingCount;

    /// <summary>Number of authored textures that are not interned because the feature that samples them is off.</summary>
    public int DisabledRoleTextureCount => _disabledRoleTextureCount;

    /// <summary>
    /// Number of texture bindings that use the role default fallback because all <see cref="MaxTextures2D"/> slots
    /// were full. A value above zero means that some materials render without one or more authored textures.
    /// </summary>
    public int Texture2DOverflowCount => _texture2DOverflowCount;

    /// <summary>True when all interned textures have been uploaded to the active render backend and streamed 2D textures have reached mip-0 residency.</summary>
    public bool AreStreamedTexturesReady
    {
        get
        {
            if (!Volatile.Read(ref _fallbackTexturesPushed))
                return false;

            if (_residencyTickets.Count == 0)
                return true;

            ImportedTextureStreamingManager manager = ImportedTextureStreamingManager.Instance;
            for (int i = 0; i < _residencyTickets.Count; i++)
            {
                ETextureResidencyRequestState state = manager.GetResidencyRequestState(_residencyTickets[i]);
                if (state is ETextureResidencyRequestState.Pending)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Interns <paramref name="material"/> and returns its dense material row index alongside culling and UDIM
    /// tile-discard metadata used during surface sampling.
    /// </summary>
    public uint GetOrAddMaterial(
        XRMaterial? material,
        out uint cullMode,
        out ushort udimDiscardMask,
        out int udimDiscardMode,
        out int udimDiscardUv)
    {
        if (material is null)
        {
            InternedMaterialEntry fallback = _nullMaterialEntry ??= EncodeStandardMaterial(null);
            cullMode = fallback.CullMode;
            udimDiscardMask = fallback.UdimDiscardMask;
            udimDiscardMode = fallback.UdimDiscardMode;
            udimDiscardUv = fallback.UdimDiscardUv;
            return fallback.DenseIndex;
        }

        if (_internedMaterials.TryGetValue(material, out InternedMaterialEntry existing))
        {
            cullMode = existing.CullMode;
            udimDiscardMask = existing.UdimDiscardMask;
            udimDiscardMode = existing.UdimDiscardMode;
            udimDiscardUv = existing.UdimDiscardUv;
            return existing.DenseIndex;
        }

        InternedMaterialEntry created = IsUberMaterial(material)
            ? EncodeUberMaterial(material)
            : EncodeStandardMaterial(material);
        _internedMaterials.Add(material, created);
        cullMode = created.CullMode;
        udimDiscardMask = created.UdimDiscardMask;
        udimDiscardMode = created.UdimDiscardMode;
        udimDiscardUv = created.UdimDiscardUv;
        return created.DenseIndex;
    }

    /// <summary>Uploads the packed material headers, constant words, texture-binding records, and interned textures on first render-thread use.</summary>
    public void EnsureBuffersUploaded()
    {
        if (!Volatile.Read(ref _fallbackTexturesPushed))
        {
            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (renderer is not null)
            {
                for (int i = 0; i < _textures2D.Count; i++)
                {
                    XRTexture2D texture = _textures2D[i];
                    if (texture.IsApiWrapperPublicationReady)
                        _ = renderer.GetOrCreateAPIRenderObject(texture, generateNow: true);
                    if (i < 4 || (texture.Mipmaps is { Length: > 0 } mips && mips[0]?.Data is { Length: > 0 }))
                        texture.PushData();
                }

                for (int i = 0; i < _texturesCube.Count; i++)
                {
                    XRTextureCube cubemap = _texturesCube[i];
                    if (cubemap.IsApiWrapperPublicationReady)
                        _ = renderer.GetOrCreateAPIRenderObject(cubemap, generateNow: true);
                    cubemap.PushData();
                }

                Volatile.Write(ref _fallbackTexturesPushed, true);
            }
        }

        if (_materialsBuffer is not null && _materialConstantsBuffer is not null && _textureBindingsBuffer is not null)
            return;

        UploadBuffers();
    }

    /// <summary>Uploads the packed material headers, constant words, and texture-binding records to GPU storage buffers.</summary>
    public void UploadBuffers()
    {
        if (_materials.Count == 0)
            _ = GetOrAddMaterial(null, out _, out _, out _, out _);

        _materialsBuffer = UploadBuffer(
            _materialsBuffer,
            "GaussianAvatarSplatMaterials",
            MaterialsBinding,
            CollectionsMarshal.AsSpan(_materials));
        _materialConstantsBuffer = UploadBuffer(
            _materialConstantsBuffer,
            "GaussianAvatarSplatMaterialConstants",
            MaterialConstantsBinding,
            CollectionsMarshal.AsSpan(_materialConstants));
        _textureBindingsBuffer = UploadBuffer(
            _textureBindingsBuffer,
            "GaussianAvatarSplatMaterialTextureBindings",
            MaterialTextureBindingsBinding,
            CollectionsMarshal.AsSpan(_textureBindings));
    }

    /// <summary>
    /// Creates an instanced quad <see cref="XRMaterial"/> that shades each splat with its source submesh's
    /// <c>NativeUber</c> or <c>Standard</c> material and binds all interned avatar textures.
    /// </summary>
    public XRMaterial CreateUberQuadMaterial()
        => CreateUberQuadMaterialCore(depthOcclusionOnly: false);

    /// <summary>
    /// Creates a color-masked depth occlusion <see cref="XRMaterial"/> for the <see cref="EDefaultRenderPass.MaskedForward"/>
    /// pass so opaque and cutout outer-layer splat cores prevent underlying skin/body splats from bleeding through
    /// concentric clothing at grazing angles.
    /// </summary>
    public XRMaterial CreateUberDepthOcclusionQuadMaterial()
        => CreateUberQuadMaterialCore(depthOcclusionOnly: true);

    private XRMaterial CreateUberQuadMaterialCore(bool depthOcclusionOnly)
    {
        XRShader vertexTemplate = ShaderHelper.LoadEngineShader("Gaussian/GaussianSplatUberQuad.vs", EShaderType.Vertex);
        XRShader fragmentTemplate = ShaderHelper.LoadEngineShader("Gaussian/GaussianSplatUberQuad.fs", EShaderType.Fragment);
        bool isVulkan = RuntimeEngine.Rendering.State.IsVulkan ||
                        RuntimeEngine.Rendering.IsVulkanRendererActive() ||
                        RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan;

        // The color pass shades each splat once in the vertex shader. That stage then reads all material textures,
        // which Vulkan allows, but OpenGL limits a vertex shader to far fewer texture units than the table binds, so
        // OpenGL shades each pixel in the fragment shader. The depth pass needs only the alpha cutout of each pixel.
        bool shadePerSplat = isVulkan && !depthOcclusionOnly;
        string header = BuildQuadShaderHeader(isVulkan, depthOcclusionOnly, shadePerSplat);

        XRShader vertexShader = new(EShaderType.Vertex, new TextFile
        {
            FilePath = vertexTemplate.Source.FilePath,
            Text = header + (vertexTemplate.Source.Text ?? string.Empty),
        })
        {
            Name = depthOcclusionOnly ? "GaussianSplatUberDepth.vs" : "GaussianSplatUberQuad.vs",
        };

        XRShader fragmentShader = new(EShaderType.Fragment, new TextFile
        {
            FilePath = fragmentTemplate.Source.FilePath,
            Text = header + (fragmentTemplate.Source.Text ?? string.Empty),
        })
        {
            Name = depthOcclusionOnly ? "GaussianSplatUberDepth.fs" : "GaussianSplatUberQuad.fs",
        };

        RenderingParameters renderOptions = depthOcclusionOnly
            ? new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Lequal,
                    UpdateDepth = true,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                WriteRed = false,
                WriteGreen = false,
                WriteBlue = false,
                WriteAlpha = false,
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions,
            }
            : GaussianSplatRenderer.CreateRenderOptions();

        XRMaterial quadMaterial = new(vertexShader, fragmentShader)
        {
            Name = depthOcclusionOnly ? "GaussianSplatUberDepth" : "GaussianSplatUberQuad",
            RenderPass = (int)(depthOcclusionOnly ? EDefaultRenderPass.MaskedForward : EDefaultRenderPass.TransparentForward),
            RenderOptions = renderOptions,
        };

        EventList<XRTexture?> slots = new(TotalQuadTextureSlots);
        for (int i = 0; i < MaxTextures2D; i++)
            slots.Add(i < _textures2D.Count ? _textures2D[i] : _fallbackWhite2D);
        for (int i = 0; i < MaxTexturesCube; i++)
            slots.Add(i < _texturesCube.Count ? _texturesCube[i] : _fallbackWhiteCube);
        quadMaterial.Textures = slots;
        return quadMaterial;
    }

    /// <summary>
    /// Builds the lines that the material table puts before the quad vertex and fragment shader sources: the version,
    /// the backend extensions, the pass and shading-mode defines, and the material schema defines that the Uber
    /// shading code reads. Both stages get the same lines, so their shared declarations match.
    /// </summary>
    private string BuildQuadShaderHeader(bool isVulkan, bool depthOcclusionOnly, bool shadePerSplat)
    {
        StringBuilder sb = new(32768);
        sb.AppendLine("#version 460");
        if (isVulkan)
        {
            sb.AppendLine("#extension GL_EXT_nonuniform_qualifier : require");
            sb.AppendLine("#define XRENGINE_VULKAN 1");
        }

        if (depthOcclusionOnly)
            sb.AppendLine("#define XR_SPLAT_DEPTH_OCCLUSION_PASS 1");
        if (shadePerSplat)
            sb.AppendLine("#define XR_SPLAT_SHADE_PER_SPLAT 1");

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"#define XR_SPLAT_ACTIVE_UBER_FEATURES 0x{_activeUberFeatureMask:X8}u"));
        AdvancedStandardMaterialShaderContract.AppendDefines(sb);
        NativeUberMaterialSchema.AppendShaderDefines(sb);
        return sb.ToString();
    }

    /// <summary>Releases GPU storage buffers, residency holds, and owned fallback textures.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_residencyTickets.Count > 0)
        {
            ImportedTextureStreamingManager manager = ImportedTextureStreamingManager.Instance;
            for (int i = 0; i < _residencyTickets.Count; i++)
                _ = manager.CancelResidency(_residencyTickets[i]);
            _residencyTickets.Clear();
        }

        _materialsBuffer?.Dispose();
        _materialConstantsBuffer?.Dispose();
        _textureBindingsBuffer?.Dispose();
        _materialsBuffer = null;
        _materialConstantsBuffer = null;
        _textureBindingsBuffer = null;

        TryDestroyAsset(_fallbackWhite2D);
        TryDestroyAsset(_fallbackBlack2D);
        TryDestroyAsset(_fallbackFlatNormal2D);
        TryDestroyAsset(_fallbackIdentityRamp2D);
        TryDestroyAsset(_fallbackWhiteCube);
        TryDestroyAsset(_fallbackBlackCube);
    }

    private void LoadUberManifestMetadata()
    {
        XRShader? canonicalShader = ShaderHelper.UberFragForward();
        if (canonicalShader is null)
            return;

        ShaderUiManifest manifest = canonicalShader.GetUiManifest();
        foreach (ShaderUiProperty property in manifest.Properties)
        {
            if (!string.IsNullOrWhiteSpace(property.Name))
            {
                _uberManifestProperties[property.Name] = property;
                if (!string.IsNullOrWhiteSpace(property.GlslType))
                    _uberUniformGlslTypes.TryAdd(property.Name, property.GlslType);
            }
        }

        foreach (ShaderUiFeature feature in manifest.Features)
        {
            if (!string.IsNullOrWhiteSpace(feature.Id))
                _uberManifestFeatureDefaults[feature.Id] = feature.DefaultEnabled;
        }

        if (canonicalShader.TryGetResolvedSource(out string resolvedSource, annotateIncludes: false, logFailures: false))
        {
            foreach (Match match in UberUniformDeclarationRegex.Matches(resolvedSource))
                _uberUniformGlslTypes.TryAdd(match.Groups[2].Value, match.Groups[1].Value);
        }
    }

    private string? ResolveParameterGlslType(string name)
    {
        if (_uberUniformGlslTypes.TryGetValue(name, out string? glslType))
            return glslType;
        if (_uberManifestProperties.TryGetValue(name, out ShaderUiProperty? prop))
            return prop.GlslType;
        return null;
    }

    private static void SeedNativeUberVectorDefaults(uint[] words)
    {
        WriteFloat(words, "_WorldAmbientOcclusionStrength", 1.0f);
        Span<uint> globalMaskMax = words.AsSpan(
            NativeUberMaterialSchema.ParameterWordOffset(NativeUberMaterialSchema.ParameterIndex("_GlobalMaskMax")), 4);
        globalMaskMax.Fill(BitConverter.SingleToUInt32Bits(1.0f));
        _ = TryWriteGlslLiteralWords(
            "vec4",
            "vec4(0.0, 1.0, 1.0, 1.0)",
            words.AsSpan(NativeUberMaterialSchema.ParameterWordOffset(NativeUberMaterialSchema.ParameterIndex("_SourcePostProcess")), 4));
    }

    private InternedMaterialEntry EncodeUberMaterial(XRMaterial material)
    {
        MaterialBindingLayout layout = NativeUberMaterialSchema.Opaque;
        int rowWordCount = (int)layout.RowWordCount;
        uint[] words = new uint[rowWordCount];

        bool isCanonicalNativeUber =
            material.EngineSemantic == EngineMaterialSemanticIdentity.NativeUberV1 &&
            material.TryGetUberMaterialState(out _, out _);
        if (!isCanonicalNativeUber &&
            material.ActiveUberVariant.IsEmpty &&
            material.RequestedUberVariant.IsEmpty &&
            material.UberAuthoredState.Features.Length == 0 &&
            material.Parameters.Length == 0)
        {
            material.EnsureUberStateInitialized();
        }

        uint features;
        if (isCanonicalNativeUber)
        {
            // Match NativeUberMaterialSourceEncoder.TryEncode directly so canonical NativeUberV1
            // materials preserve composite source vectors (such as _RimShapeSettings,
            // _ToonLightSettings, and _SpecularAASettings) without manifest static-literal overrides.
            SeedNativeUberVectorDefaults(words);
            foreach (ShaderVar? parameter in material.Parameters)
            {
                if (parameter?.Name is not { } name)
                    continue;
                int paramIndex = NativeUberMaterialSchema.ParameterIndex(name);
                if (paramIndex < 0)
                    continue;

                Span<uint> dst = words.AsSpan(NativeUberMaterialSchema.ParameterWordOffset(paramIndex), 4);
                TryWriteCanonicalUberParameter(parameter, dst);
            }

            features = ResolveCanonicalUberFeatureMask(material);
        }
        else
        {
            // Non-canonical Uber fallback: seed manifest defaults, authored static literals, and
            // baked variant properties first, then apply runtime material parameters last.
            ApplyManifestDefaults(words);
            SeedNativeUberVectorDefaults(words);
            WriteFloat(words, "_Cutoff", material.AlphaCutoff);

            UberMaterialPropertyState[] authoredProperties = material.UberAuthoredState.Properties;
            for (int i = 0; i < authoredProperties.Length; i++)
            {
                UberMaterialPropertyState property = authoredProperties[i];
                if (string.IsNullOrWhiteSpace(property.Name) || string.IsNullOrWhiteSpace(property.StaticLiteral))
                    continue;

                int paramIndex = NativeUberMaterialSchema.ParameterIndex(property.Name);
                if (paramIndex < 0)
                    continue;

                Span<uint> dst = words.AsSpan(NativeUberMaterialSchema.ParameterWordOffset(paramIndex), 4);
                _ = TryWriteGlslLiteralWords(ResolveParameterGlslType(property.Name), property.StaticLiteral, dst);
            }

            ApplyBakedStaticProperties(
                material.RequestedUberVariant.StaticProperties,
                material.RequestedUberVariant.AnimatedProperties,
                material,
                words);
            ApplyBakedStaticProperties(
                material.ActiveUberVariant.StaticProperties,
                material.ActiveUberVariant.AnimatedProperties,
                material,
                words);

            foreach (ShaderVar? parameter in material.Parameters)
            {
                if (parameter?.Name is not { } name)
                    continue;
                int paramIndex = NativeUberMaterialSchema.ParameterIndex(name);
                if (paramIndex < 0)
                    continue;

                Span<uint> dst = words.AsSpan(NativeUberMaterialSchema.ParameterWordOffset(paramIndex), 4);
                WriteParameterWords(parameter, ResolveParameterGlslType(name), dst);
            }

            features = ResolveUberFeatureMask(material, words, isCanonicalNativeUber: false);
        }

        words[NativeUberMaterialSchema.FeatureWordOffset] = features;
        _activeUberFeatureMask |= features;

        uint cullMode = ResolveCullMode(material, words, isCanonicalNativeUber);
        ResolveUdimDiscard(words, out ushort udimDiscardMask, out int udimDiscardMode, out int udimDiscardUv);

        int roleCount = NativeUberMaterialSchema.TextureRoleCount;
        XRTexture?[] roleTextures = new XRTexture?[roleCount];
        for (int slot = 0; slot < material.Textures.Count; slot++)
        {
            XRTexture? candidate = material.Textures[slot];
            if (candidate is null)
                continue;
            string samplerName = candidate.ResolveSamplerName(slot);
            int roleIndex = NativeUberMaterialSchema.TextureIndex(samplerName);
            if (roleIndex >= 0 && roleTextures[roleIndex] is null)
                roleTextures[roleIndex] = candidate;
        }

        uint textureOffset = (uint)_textureBindings.Count;
        ReadOnlySpan<string> roleNames = NativeUberMaterialSchema.TextureRoles;
        for (int role = 0; role < roleCount; role++)
        {
            string roleName = roleNames[role];
            XRTexture? candidate = roleTextures[role];
            if (candidate is not null && IsGeneratedPlaceholderTexture(candidate, roleName))
                candidate = null;

            // A disabled feature never samples its roles, so its textures must not use one of the shared slots.
            if (candidate is not null && !IsTextureRoleFeatureEnabled(roleName, features))
            {
                _disabledRoleTextureCount++;
                candidate = null;
            }

            _textureBindings.Add(EncodeUberTextureRole(roleName, candidate));
        }

        uint constantOffset = (uint)_materialConstants.Count;
        for (int i = 0; i < words.Length; i++)
            _materialConstants.Add(words[i]);

        int mode = ReadInt(words, "_Mode");
        bool isMasked = mode == 1;
        string layoutHashText = isMasked ? NativeUberMaterialSchema.Masked.LayoutHash : NativeUberMaterialSchema.Opaque.LayoutHash;

        uint denseIndex = (uint)_materials.Count;
        _materials.Add(new AdvancedMaterialRecord
        {
            StableRowId = denseIndex,
            Generation = 1u,
            MaterialLayoutHash = ComputeFnv1a64(layoutHashText),
            CoverageMode = isMasked ? EAdvancedMaterialCoverageMode.Masked : EAdvancedMaterialCoverageMode.Opaque,
            TextureReferenceOffset = textureOffset,
            TextureReferenceCount = (uint)roleCount,
            ConstantWordOffset = constantOffset,
            ConstantWordCount = (uint)rowWordCount,
            SourceContract = EAdvancedMaterialSourceContract.NativeUberSurface,
        });

        return new InternedMaterialEntry(denseIndex, cullMode, udimDiscardMask, udimDiscardMode, udimDiscardUv);
    }

    private void ApplyBakedStaticProperties(
        IReadOnlyList<string>? staticProperties,
        IReadOnlyList<string>? animatedProperties,
        XRMaterial material,
        Span<uint> words)
    {
        if (staticProperties is null || staticProperties.Count == 0)
            return;

        for (int i = 0; i < staticProperties.Count; i++)
        {
            string entry = staticProperties[i];
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            int eq = entry.IndexOf('=');
            if (eq <= 0 || eq >= entry.Length - 1)
                continue;

            string name = entry[..eq].Trim();
            if (ContainsString(animatedProperties, name) && HasMaterialParameter(material, name))
                continue;

            int paramIndex = NativeUberMaterialSchema.ParameterIndex(name);
            if (paramIndex < 0)
                continue;

            string literal = entry[(eq + 1)..].Trim();
            Span<uint> dst = words.Slice(NativeUberMaterialSchema.ParameterWordOffset(paramIndex), 4);
            _ = TryWriteGlslLiteralWords(ResolveParameterGlslType(name), literal, dst);
        }
    }

    private static bool ContainsString(IReadOnlyList<string>? values, string target)
    {
        if (values is null)
            return false;

        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], target, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasBakedStaticProperty(IReadOnlyList<string>? staticProperties, string propertyName)
    {
        if (staticProperties is null || staticProperties.Count == 0)
            return false;

        for (int i = 0; i < staticProperties.Count; i++)
        {
            string entry = staticProperties[i];
            int eq = entry.IndexOf('=');
            if (eq > 0 && entry.AsSpan(0, eq).Trim().SequenceEqual(propertyName.AsSpan()))
                return true;
        }

        return false;
    }

    private static bool HasMaterialParameter(XRMaterial material, string name)
    {
        foreach (ShaderVar? parameter in material.Parameters)
        {
            if (parameter is not null && string.Equals(parameter.Name, name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static uint ResolveCanonicalUberFeatureMask(XRMaterial material)
    {
        uint features = 0u;
        UberMaterialFeatureState[] authoredFeatures = material.UberAuthoredState.Features;
        for (int i = 0; i < authoredFeatures.Length; i++)
        {
            UberMaterialFeatureState feature = authoredFeatures[i];
            if (!feature.Enabled || IsExcludedSplatUberFeature(feature.Id))
                continue;

            int bit = NativeUberMaterialSchema.FeatureIndex(feature.Id);
            if (bit >= 0 && bit < 32)
                features |= 1u << bit;
        }

        return features;
    }

    private uint ResolveUberFeatureMask(XRMaterial material, ReadOnlySpan<uint> words, bool isCanonicalNativeUber)
    {
        if (material.ActiveUberVariant.EnabledFeatures is { Length: > 0 } activeFeatures)
            return EncodeFeatureIdList(activeFeatures);

        if (material.RequestedUberVariant.EnabledFeatures is { Length: > 0 } requestedFeatures)
            return EncodeFeatureIdList(requestedFeatures);

        if (isCanonicalNativeUber && material.UberAuthoredState.Features.Length > 0)
            return ResolveCanonicalUberFeatureMask(material);

        uint features = 0u;
        ReadOnlySpan<string> schemaFeatures = NativeUberMaterialSchema.FeatureIds;
        for (int bit = 0; bit < schemaFeatures.Length && bit < 32; bit++)
        {
            string featureId = schemaFeatures[bit];
            if (IsExcludedSplatUberFeature(featureId))
                continue;

            UberMaterialFeatureState? authored = material.UberAuthoredState.GetFeature(featureId);
            bool enabled;
            if (authored is { ExplicitlyAuthored: true })
            {
                enabled = authored.Enabled;
            }
            else
            {
                bool defaultEnabled = authored?.Enabled ?? _uberManifestFeatureDefaults.GetValueOrDefault(featureId, false);
                enabled = ResolveInferredUberFeature(material, words, featureId, defaultEnabled);
            }

            if (enabled)
                features |= 1u << bit;
        }

        return features;
    }

    private static uint EncodeFeatureIdList(IReadOnlyList<string> featureIds)
    {
        uint features = 0u;
        for (int i = 0; i < featureIds.Count; i++)
        {
            string featureId = featureIds[i];
            if (IsExcludedSplatUberFeature(featureId))
                continue;

            int bit = NativeUberMaterialSchema.FeatureIndex(featureId);
            if (bit >= 0 && bit < 32)
                features |= 1u << bit;
        }

        return features;
    }

    private static bool IsExcludedSplatUberFeature(string featureId)
        => featureId is "render-time" or "forward-ambient-occlusion" or "forward-lighting" or
           "forward-shadows" or "forward-contact-shadows" or "outline" or "vertex-effects";

    private static bool ResolveInferredUberFeature(
        XRMaterial material,
        ReadOnlySpan<uint> words,
        string featureId,
        bool defaultEnabled)
    {
        if (string.Equals(featureId, "alpha-masks", StringComparison.Ordinal))
            return ReadInt(words, "_MainAlphaMaskMode") > 0 || HasAuthoredSampler(material, "_AlphaMask");

        if (string.Equals(featureId, "normal-map", StringComparison.Ordinal))
            return IsParameterEnabled(words, "_BumpScale", 1.0e-6f) || HasAuthoredSampler(material, "_BumpMap");

        return defaultEnabled;
    }

    private static bool HasAuthoredSampler(XRMaterial material, string samplerName)
    {
        for (int i = 0; i < material.Textures.Count; i++)
        {
            XRTexture? texture = material.Textures[i];
            if (texture is null ||
                IsGeneratedPlaceholderTexture(texture, samplerName) ||
                !string.Equals(texture.ResolveSamplerName(i), samplerName, StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private void ApplyManifestDefaults(Span<uint> words)
    {
        uint oneBits = BitConverter.SingleToUInt32Bits(1.0f);
        ReadOnlySpan<string> names = NativeUberMaterialSchema.Parameters;
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            Span<uint> dst = words.Slice(NativeUberMaterialSchema.ParameterWordOffset(i), 4);
            if (name.EndsWith("_ST", StringComparison.Ordinal))
            {
                dst[0] = oneBits;
                dst[1] = oneBits;
                dst[2] = 0u;
                dst[3] = 0u;
            }

            if (!_uberManifestProperties.TryGetValue(name, out ShaderUiProperty? property) ||
                string.IsNullOrWhiteSpace(property.DefaultLiteral))
            {
                continue;
            }

            _ = TryWriteGlslLiteralWords(ResolveParameterGlslType(name) ?? property.GlslType, property.DefaultLiteral, dst);
        }
    }

    private InternedMaterialEntry EncodeStandardMaterial(XRMaterial? material)
    {
        uint cullMode = ResolveStandardCullMode(material);
        MaterialBindingLayout stdLayout = MaterialBindingLayouts.ForwardOpaque;
        uint[] words = new uint[stdLayout.RowWordCount];
        MaterialBindingRowPacker.WriteDefaultRow(stdLayout, words);

        MaterialBindingSourceSnapshot snapshot = MaterialBindingSourceEncoder.Encode(material);
        _ = MaterialBindingRowPacker.TryWriteOpaqueDeferred(
            stdLayout,
            snapshot.Entry,
            snapshot.EmissionColor,
            snapshot.EmissionStrength,
            snapshot.EmissionTextureMetadata,
            snapshot.EmissionUvScaleOffset,
            snapshot.EmissionUvRotation,
            words,
            out _);

        uint textureOffset = (uint)_textureBindings.Count;
        _textureBindings.Add(EncodeStandardTexture2D(snapshot.Albedo, FallbackSlotWhite2D, EAdvancedResourceFallback.White));
        _textureBindings.Add(EncodeStandardTexture2D(snapshot.Normal, FallbackSlotFlatNormal2D, EAdvancedResourceFallback.FlatNormal));
        _textureBindings.Add(EncodeStandardTexture2D(snapshot.RM, FallbackSlotWhite2D, EAdvancedResourceFallback.White));
        _textureBindings.Add(EncodeStandardTexture2D(snapshot.Emissive, FallbackSlotBlack2D, EAdvancedResourceFallback.Black));
        _textureBindings.Add(EncodeStandardTexture2D(snapshot.Opacity, FallbackSlotWhite2D, EAdvancedResourceFallback.White));

        uint constantOffset = (uint)_materialConstants.Count;
        for (int i = 0; i < words.Length; i++)
            _materialConstants.Add(words[i]);

        bool isMasked = material?.GetEffectiveTransparencyMode() is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage;
        MaterialBindingLayout targetLayout = isMasked ? MaterialBindingLayouts.MaskedForward : MaterialBindingLayouts.ForwardOpaque;

        uint denseIndex = (uint)_materials.Count;
        _materials.Add(new AdvancedMaterialRecord
        {
            StableRowId = denseIndex,
            Generation = 1u,
            MaterialLayoutHash = ComputeFnv1a64(targetLayout.LayoutHash),
            CoverageMode = isMasked ? EAdvancedMaterialCoverageMode.Masked : EAdvancedMaterialCoverageMode.Opaque,
            TextureReferenceOffset = textureOffset,
            TextureReferenceCount = MaterialBindingLayouts.StandardTextureCount,
            ConstantWordOffset = constantOffset,
            ConstantWordCount = stdLayout.RowWordCount,
            SourceContract = EAdvancedMaterialSourceContract.StandardSurface,
        });

        return new InternedMaterialEntry(denseIndex, cullMode, 0, 0, 0);
    }

    private AdvancedMaterialTextureBinding EncodeUberTextureRole(string roleName, XRTexture? authoredTexture)
    {
        if (string.Equals(roleName, "_CubeMap", StringComparison.Ordinal))
        {
            if (authoredTexture is XRTextureCube cube)
            {
                uint cubeSlot = InternTextureCube(cube);
                return CreateTextureBinding(cubeSlot, EAdvancedResourceFallback.Zero);
            }

            return CreateTextureBinding(FallbackSlotBlackCube, EAdvancedResourceFallback.Zero);
        }

        if (authoredTexture is XRTexture2D tex2D)
        {
            // Solid 1x1 or 2x2 textures, such as importer placeholders for unassigned Unity samplers, sample the
            // same value as a shared fallback texture. Bind that fallback instead of using a slot per copy.
            if (TryResolveConstantFallback(tex2D, out uint constantSlot, out EAdvancedResourceFallback constantKind))
            {
                _constantTextureBindingCount++;
                return CreateTextureBinding(constantSlot, constantKind);
            }

            if (TryInternTexture2D(tex2D, out uint slot2D))
                return CreateTextureBinding(slot2D, EAdvancedResourceFallback.Zero);

            // All slots are full. The role default keeps the neutral value of the role. A white fallback would
            // turn multiply decals, masks, and emission maps fully on.
            _texture2DOverflowCount++;
        }

        ResolveDefaultRoleFallback(roleName, out uint fallbackSlot, out EAdvancedResourceFallback fallbackKind);
        return CreateTextureBinding(fallbackSlot, fallbackKind);
    }

    private AdvancedMaterialTextureBinding EncodeStandardTexture2D(
        XRTexture? texture,
        uint fallbackSlot,
        EAdvancedResourceFallback fallbackKind)
    {
        if (texture is XRTexture2D tex2D)
        {
            if (TryInternTexture2D(tex2D, out uint slot2D))
                return CreateTextureBinding(slot2D, EAdvancedResourceFallback.Zero);

            _texture2DOverflowCount++;
        }

        return CreateTextureBinding(fallbackSlot, fallbackKind);
    }

    /// <summary>
    /// Finds the 2D slot of <paramref name="texture"/> or adds the texture to the next free slot.
    /// Textures that hold CPU pixels share a slot when their size, format, wrap modes, and pixels are equal.
    /// Textures that stream their pixels share a slot when they use the same source file and sampler state.
    /// Returns false when the texture needs a new slot and all <see cref="MaxTextures2D"/> slots are full.
    /// </summary>
    private bool TryInternTexture2D(XRTexture2D texture, out uint slot)
    {
        if (_texture2DSlots.TryGetValue(texture, out slot))
            return true;

        ReadOnlySpan<byte> pixels = GetMip0Pixels(texture);
        bool hasContentKey = !pixels.IsEmpty;
        TextureContentKey contentKey = hasContentKey ? CreateContentKey(texture, pixels) : default;
        string? sourceKey = hasContentKey ? null : CreateSourceKey(texture);

        if (hasContentKey &&
            _texture2DContentSlots.TryGetValue(contentKey, out uint contentSlot) &&
            GetMip0Pixels(_textures2D[(int)contentSlot]).SequenceEqual(pixels))
        {
            return ReuseSlot(texture, contentSlot, out slot);
        }

        if (sourceKey is not null && _texture2DSourceSlots.TryGetValue(sourceKey, out uint sourceSlot))
            return ReuseSlot(texture, sourceSlot, out slot);

        if (_textures2D.Count >= MaxTextures2D)
        {
            slot = 0u;
            return false;
        }

        texture.PreferSynchronousGpuUpload = true;
        slot = (uint)_textures2D.Count;
        _textures2D.Add(texture);
        _texture2DSlots.Add(texture, slot);
        if (hasContentKey)
            _texture2DContentSlots.TryAdd(contentKey, slot);
        else if (sourceKey is not null)
            _texture2DSourceSlots.TryAdd(sourceKey, slot);

        if (hasContentKey)
        {
            texture.AllowAutomaticImportedTextureStreaming = false;
        }
        else
        {
            ImportedTextureStreamingManager manager = ImportedTextureStreamingManager.Instance;
            manager.SetBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);
            TextureResidencyTicket ticket = manager.RequestResidency(
                texture,
                targetMip: 0,
                deadlineFrame: 0L,
                priority: JobPriority.Highest,
                generation: 1);
            if (ticket.IsValid)
                _residencyTickets.Add(ticket);
        }

        return true;
    }

    private bool ReuseSlot(XRTexture2D texture, uint sharedSlot, out uint slot)
    {
        _texture2DSlots.Add(texture, sharedSlot);
        _texture2DReuseCount++;
        slot = sharedSlot;
        return true;
    }

    /// <summary>
    /// Returns the CPU pixels of mip 0, or an empty span when the texture streams its pixels or holds only a
    /// placeholder of four bytes or less.
    /// </summary>
    private static ReadOnlySpan<byte> GetMip0Pixels(XRTexture2D texture)
    {
        // A loading placeholder is not the texture content. Such a texture streams its source.
        if (!texture.HasHydratedSourceMipmaps() ||
            texture.Mipmaps is not { Length: > 0 } mips ||
            mips[0]?.Data is not { Length: > 4 } data ||
            data.IsDisposed)
        {
            return default;
        }

        return data.AsReadOnlySpan();
    }

    private static TextureContentKey CreateContentKey(XRTexture2D texture, ReadOnlySpan<byte> pixels)
    {
        HashCode hash = new();
        hash.AddBytes(pixels);
        return new TextureContentKey(
            texture.Width,
            texture.Height,
            texture.SizedInternalFormat,
            texture.UWrap,
            texture.VWrap,
            pixels.Length,
            hash.ToHashCode());
    }

    private static string? CreateSourceKey(XRTexture2D texture)
    {
        string? path = !string.IsNullOrWhiteSpace(texture.OriginalPath)
            ? texture.OriginalPath
            : texture.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{path}|{texture.SizedInternalFormat}|{texture.UWrap}|{texture.VWrap}");
    }

    /// <summary>
    /// Maps a 1x1 or 2x2 texture whose pixels all hold solid white, solid black, or the flat tangent normal
    /// to the matching shared fallback slot. Other textures return false and use their own slot.
    /// </summary>
    private static bool TryResolveConstantFallback(
        XRTexture2D texture,
        out uint slot,
        out EAdvancedResourceFallback kind)
    {
        slot = FallbackSlotWhite2D;
        kind = EAdvancedResourceFallback.White;
        if (texture.Width > 2 || texture.Height > 2 ||
            texture.Mipmaps is not { Length: > 0 } mips ||
            mips[0] is not { PixelType: EPixelType.UnsignedByte } mip ||
            mip.Data is not { Length: > 0 } data ||
            data.IsDisposed)
        {
            return false;
        }

        int stride = mip.PixelFormat switch
        {
            EPixelFormat.Rgba => 4,
            EPixelFormat.Rgb => 3,
            _ => 0,
        };
        ReadOnlySpan<byte> pixels = data.AsReadOnlySpan();
        if (stride == 0 || pixels.Length < stride || pixels.Length % stride != 0)
            return false;

        ReadOnlySpan<byte> first = pixels[..stride];
        for (int offset = stride; offset < pixels.Length; offset += stride)
        {
            if (!pixels.Slice(offset, stride).SequenceEqual(first))
                return false;
        }

        byte r = first[0];
        byte g = first[1];
        byte b = first[2];
        byte a = stride == 4 ? first[3] : (byte)255;
        if (a < 254)
            return false;

        if (r >= 254 && g >= 254 && b >= 254)
            return true;

        if (r <= 1 && g <= 1 && b <= 1)
        {
            slot = FallbackSlotBlack2D;
            kind = EAdvancedResourceFallback.Black;
            return true;
        }

        if (Math.Abs(r - 128) <= 2 && Math.Abs(g - 128) <= 2 && b >= 254)
        {
            slot = FallbackSlotFlatNormal2D;
            kind = EAdvancedResourceFallback.FlatNormal;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns false when the canonical Uber manifest declares <paramref name="roleName"/> inside a feature guard
    /// and that feature bit is off in <paramref name="features"/>. Roles without a feature guard always return true.
    /// </summary>
    private bool IsTextureRoleFeatureEnabled(string roleName, uint features)
    {
        if (!_uberManifestProperties.TryGetValue(roleName, out ShaderUiProperty? property) ||
            string.IsNullOrWhiteSpace(property.FeatureId))
        {
            return true;
        }

        int bit = NativeUberMaterialSchema.FeatureIndex(property.FeatureId);
        return bit is < 0 or >= 32 || (features & (1u << bit)) != 0u;
    }

    private uint InternTextureCube(XRTextureCube texture)
    {
        if (_textureCubeSlots.TryGetValue(texture, out uint existing))
            return existing;

        if (_texturesCube.Count >= MaxTexturesCube)
            return FallbackSlotBlackCube;

        uint slot = (uint)_texturesCube.Count;
        _texturesCube.Add(texture);
        _textureCubeSlots.Add(texture, slot);
        return slot;
    }

    private static void ResolveDefaultRoleFallback(
        string roleName,
        out uint fallbackSlot,
        out EAdvancedResourceFallback fallbackKind)
    {
        if (string.Equals(roleName, "_ToonRamp", StringComparison.Ordinal))
        {
            fallbackSlot = FallbackSlotIdentityRamp2D;
            fallbackKind = EAdvancedResourceFallback.Zero;
            return;
        }

        if (roleName.Contains("Normal", StringComparison.OrdinalIgnoreCase) ||
            roleName.Contains("Bump", StringComparison.OrdinalIgnoreCase))
        {
            fallbackSlot = FallbackSlotFlatNormal2D;
            fallbackKind = EAdvancedResourceFallback.FlatNormal;
            return;
        }

        if (string.Equals(roleName, "_PBRMetallicMaps", StringComparison.Ordinal) ||
            string.Equals(roleName, "_ColorMaskTexture", StringComparison.Ordinal) ||
            string.Equals(roleName, "_DissolveDetailNoise", StringComparison.Ordinal) ||
            string.Equals(roleName, "_AudioLinkTexture", StringComparison.Ordinal))
        {
            fallbackSlot = FallbackSlotBlack2D;
            fallbackKind = EAdvancedResourceFallback.Black;
            return;
        }

        fallbackSlot = FallbackSlotWhite2D;
        fallbackKind = EAdvancedResourceFallback.White;
    }

    private static AdvancedMaterialTextureBinding CreateTextureBinding(uint slot, EAdvancedResourceFallback fallback)
        => new(
            new AdvancedTextureReference(new AdvancedGpuHandle(slot, 1u), fallback, 0u),
            new AdvancedSamplerReference(new AdvancedGpuHandle(0u, 1u), EAdvancedResourceFallback.Zero, 0u));

    private static void WriteFloat(Span<uint> words, string parameterName, float value)
    {
        int index = NativeUberMaterialSchema.ParameterIndex(parameterName);
        if (index < 0)
            return;
        words[NativeUberMaterialSchema.ParameterWordOffset(index)] = BitConverter.SingleToUInt32Bits(value);
    }

    private static int ReadInt(ReadOnlySpan<uint> words, string parameterName)
    {
        int index = NativeUberMaterialSchema.ParameterIndex(parameterName);
        if (index < 0)
            return 0;
        return unchecked((int)words[NativeUberMaterialSchema.ParameterWordOffset(index)]);
    }

    private static void TryWriteCanonicalUberParameter(ShaderVar parameter, Span<uint> words)
    {
        switch (parameter)
        {
            case ShaderFloat sf when float.IsFinite(sf.Value):
                words[0] = BitConverter.SingleToUInt32Bits(sf.Value);
                break;
            case ShaderInt si:
                words[0] = unchecked((uint)si.Value);
                break;
            case ShaderUInt sui:
                words[0] = sui.Value;
                break;
            case ShaderBool sb:
                words[0] = sb.Value ? 1u : 0u;
                break;
            case ShaderVector2 sv2 when float.IsFinite(sv2.Value.X) && float.IsFinite(sv2.Value.Y):
                words[0] = BitConverter.SingleToUInt32Bits(sv2.Value.X);
                words[1] = BitConverter.SingleToUInt32Bits(sv2.Value.Y);
                break;
            case ShaderVector3 sv3 when float.IsFinite(sv3.Value.X) && float.IsFinite(sv3.Value.Y) && float.IsFinite(sv3.Value.Z):
                words[0] = BitConverter.SingleToUInt32Bits(sv3.Value.X);
                words[1] = BitConverter.SingleToUInt32Bits(sv3.Value.Y);
                words[2] = BitConverter.SingleToUInt32Bits(sv3.Value.Z);
                break;
            case ShaderVector4 sv4 when float.IsFinite(sv4.Value.X) && float.IsFinite(sv4.Value.Y) && float.IsFinite(sv4.Value.Z) && float.IsFinite(sv4.Value.W):
                words[0] = BitConverter.SingleToUInt32Bits(sv4.Value.X);
                words[1] = BitConverter.SingleToUInt32Bits(sv4.Value.Y);
                words[2] = BitConverter.SingleToUInt32Bits(sv4.Value.Z);
                words[3] = BitConverter.SingleToUInt32Bits(sv4.Value.W);
                break;
        }
    }

    private static bool TryWriteGlslLiteralWords(string? glslType, string literal, Span<uint> dst)
    {
        ReadOnlySpan<char> trimmed = literal.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return false;

        if (string.Equals(glslType, "bool", StringComparison.Ordinal))
        {
            if (bool.TryParse(trimmed, out bool b))
            {
                dst[0] = b ? 1u : 0u;
                return true;
            }

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bi))
            {
                dst[0] = bi != 0 ? 1u : 0u;
                return true;
            }

            return false;
        }

        if (string.Equals(glslType, "int", StringComparison.Ordinal))
        {
            Span<byte> intBytes = MemoryMarshal.AsBytes(dst[..1]);
            if (UberBaseStaticLiteral.TryWrite("i32", literal, intBytes))
                return true;
            if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f))
            {
                dst[0] = unchecked((uint)(int)MathF.Round(f));
                return true;
            }

            return false;
        }

        if (string.Equals(glslType, "uint", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> uintText = trimmed.EndsWith("u", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^1]
                : trimmed;
            if (uint.TryParse(uintText, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u))
            {
                dst[0] = u;
                return true;
            }

            return false;
        }

        if (string.Equals(glslType, "vec2", StringComparison.Ordinal) || trimmed.StartsWith("vec2", StringComparison.Ordinal))
            return UberBaseStaticLiteral.TryWrite("vec2<f32>", literal, MemoryMarshal.AsBytes(dst[..2]));

        if (string.Equals(glslType, "vec3", StringComparison.Ordinal) || trimmed.StartsWith("vec3", StringComparison.Ordinal))
            return TryWriteVec3Literal(trimmed, dst);

        if (string.Equals(glslType, "vec4", StringComparison.Ordinal) || trimmed.StartsWith("vec4", StringComparison.Ordinal))
            return UberBaseStaticLiteral.TryWrite("vec4<f32>", literal, MemoryMarshal.AsBytes(dst[..4]));

        if (string.Equals(glslType, "float", StringComparison.Ordinal))
            return UberBaseStaticLiteral.TryWrite("f32", literal, MemoryMarshal.AsBytes(dst[..1]));

        // Unknown manifest type: infer from literal syntax.
        if (bool.TryParse(trimmed, out bool inferredBool))
        {
            dst[0] = inferredBool ? 1u : 0u;
            return true;
        }

        if (!trimmed.Contains('.') && !trimmed.Contains('e') && !trimmed.Contains('E') &&
            int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int inferredInt))
        {
            dst[0] = unchecked((uint)inferredInt);
            return true;
        }

        return UberBaseStaticLiteral.TryWrite("f32", literal, MemoryMarshal.AsBytes(dst[..1]));
    }

    private static bool TryWriteVec3Literal(ReadOnlySpan<char> text, Span<uint> dst)
    {
        if (text.StartsWith("vec3", StringComparison.Ordinal))
        {
            text = text[4..].Trim();
            if (text.Length < 3 || text[0] != '(' || text[^1] != ')')
                return false;
            text = text[1..^1].Trim();
        }

        Span<float> values = stackalloc float[3];
        int count = 0;
        while (!text.IsEmpty)
        {
            if (count >= 3)
                return false;
            int sep = text.IndexOf(',');
            ReadOnlySpan<char> part = (sep < 0 ? text : text[..sep]).Trim();
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || !float.IsFinite(v))
                return false;
            values[count++] = v;
            if (sep < 0)
                break;
            text = text[(sep + 1)..].Trim();
        }

        if (count != 1 && count != 3)
            return false;

        dst[0] = BitConverter.SingleToUInt32Bits(values[0]);
        dst[1] = BitConverter.SingleToUInt32Bits(count == 1 ? values[0] : values[1]);
        dst[2] = BitConverter.SingleToUInt32Bits(count == 1 ? values[0] : values[2]);
        return true;
    }

    private static void WriteParameterWords(ShaderVar parameter, string? glslType, Span<uint> dst)
    {
        bool expectsInt = string.Equals(glslType, "int", StringComparison.Ordinal);
        bool expectsBool = string.Equals(glslType, "bool", StringComparison.Ordinal);
        bool expectsUInt = string.Equals(glslType, "uint", StringComparison.Ordinal);

        switch (parameter)
        {
            case ShaderFloat sf when float.IsFinite(sf.Value):
                if (expectsInt)
                    dst[0] = unchecked((uint)(int)MathF.Round(sf.Value));
                else if (expectsBool)
                    dst[0] = MathF.Abs(sf.Value) > 0.5f ? 1u : 0u;
                else if (expectsUInt)
                    dst[0] = (uint)MathF.Max(0.0f, MathF.Round(sf.Value));
                else
                    dst[0] = BitConverter.SingleToUInt32Bits(sf.Value);
                break;
            case ShaderInt si:
                if (expectsBool)
                    dst[0] = si.Value != 0 ? 1u : 0u;
                else if (string.Equals(glslType, "float", StringComparison.Ordinal))
                    dst[0] = BitConverter.SingleToUInt32Bits(si.Value);
                else
                    dst[0] = unchecked((uint)si.Value);
                break;
            case ShaderUInt sui:
                if (string.Equals(glslType, "float", StringComparison.Ordinal))
                    dst[0] = BitConverter.SingleToUInt32Bits(sui.Value);
                else
                    dst[0] = sui.Value;
                break;
            case ShaderBool sb:
                if (string.Equals(glslType, "float", StringComparison.Ordinal))
                    dst[0] = BitConverter.SingleToUInt32Bits(sb.Value ? 1.0f : 0.0f);
                else
                    dst[0] = sb.Value ? 1u : 0u;
                break;
            case ShaderVector2 sv2 when float.IsFinite(sv2.Value.X) && float.IsFinite(sv2.Value.Y):
                dst[0] = BitConverter.SingleToUInt32Bits(sv2.Value.X);
                dst[1] = BitConverter.SingleToUInt32Bits(sv2.Value.Y);
                break;
            case ShaderVector3 sv3 when float.IsFinite(sv3.Value.X) && float.IsFinite(sv3.Value.Y) && float.IsFinite(sv3.Value.Z):
                dst[0] = BitConverter.SingleToUInt32Bits(sv3.Value.X);
                dst[1] = BitConverter.SingleToUInt32Bits(sv3.Value.Y);
                dst[2] = BitConverter.SingleToUInt32Bits(sv3.Value.Z);
                break;
            case ShaderVector4 sv4 when float.IsFinite(sv4.Value.X) && float.IsFinite(sv4.Value.Y) && float.IsFinite(sv4.Value.Z) && float.IsFinite(sv4.Value.W):
                dst[0] = BitConverter.SingleToUInt32Bits(sv4.Value.X);
                dst[1] = BitConverter.SingleToUInt32Bits(sv4.Value.Y);
                dst[2] = BitConverter.SingleToUInt32Bits(sv4.Value.Z);
                dst[3] = BitConverter.SingleToUInt32Bits(sv4.Value.W);
                break;
        }
    }

    private static bool IsUberMaterial(XRMaterial material)
        => material.EngineSemantic == EngineMaterialSemanticIdentity.NativeUberV1 ||
           material.TryGetUberMaterialState(out _, out _) ||
           material.Parameter<ShaderInt>("_LightingMode") is not null ||
           material.UberAuthoredState.Features.Length > 0;

    private static bool IsGeneratedPlaceholderTexture(XRTexture texture, string roleName)
        => string.IsNullOrEmpty(texture.FilePath) &&
           string.Equals(texture.Name, roleName, StringComparison.Ordinal) &&
           (texture is XRTexture2D { Width: <= 2, Height: <= 2 } ||
            texture is XRTextureCube { Extent: <= 2 });

    private static bool HasAuthoredStaticLiteral(UberMaterialAuthoredState authoredState, string propertyName)
    {
        UberMaterialPropertyState[] properties = authoredState.Properties;
        for (int i = 0; i < properties.Length; i++)
        {
            if (string.Equals(properties[i].Name, propertyName, StringComparison.Ordinal))
                return !string.IsNullOrEmpty(properties[i].StaticLiteral);
        }

        return false;
    }

    private static uint ResolveCullMode(XRMaterial material, ReadOnlySpan<uint> words, bool isCanonicalNativeUber)
    {
        if (isCanonicalNativeUber)
            return ResolveStandardCullMode(material);

        if (NativeUberMaterialSchema.ParameterIndex("_Cull") >= 0 &&
            (material.Parameter<ShaderInt>("_Cull") is not null ||
             material.Parameter<ShaderFloat>("_Cull") is not null ||
             HasAuthoredStaticLiteral(material.UberAuthoredState, "_Cull") ||
             HasBakedStaticProperty(material.ActiveUberVariant.StaticProperties, "_Cull") ||
             HasBakedStaticProperty(material.RequestedUberVariant.StaticProperties, "_Cull")))
        {
            int cull = ReadInt(words, "_Cull");
            return cull switch
            {
                0 => 0u,
                1 => 1u,
                _ => 2u,
            };
        }

        return ResolveStandardCullMode(material);
    }

    private static uint ResolveStandardCullMode(XRMaterial? material)
    {
        if (material is null)
            return 2u;

        return material.RenderOptions?.CullMode switch
        {
            ECullMode.None => 0u,
            ECullMode.Front => 1u,
            _ => 2u,
        };
    }

    private static bool IsParameterEnabled(ReadOnlySpan<uint> words, string parameterName, float floatThreshold)
    {
        int index = NativeUberMaterialSchema.ParameterIndex(parameterName);
        if (index < 0)
            return false;

        uint raw = words[NativeUberMaterialSchema.ParameterWordOffset(index)];
        if (raw == 0u)
            return false;

        float asFloat = BitConverter.UInt32BitsToSingle(raw);
        if (float.IsFinite(asFloat) && MathF.Abs(asFloat) <= 1024.0f && MathF.Abs(asFloat) >= 1.0e-6f)
            return asFloat > floatThreshold;

        return unchecked((int)raw) > 0;
    }

    private static void ResolveUdimDiscard(
        ReadOnlySpan<uint> words,
        out ushort udimDiscardMask,
        out int udimDiscardMode,
        out int udimDiscardUv)
    {
        udimDiscardMask = 0;
        udimDiscardMode = 0;
        udimDiscardUv = 0;

        if (!IsParameterEnabled(words, "_UvTileDiscard", 0.5f))
            return;

        udimDiscardMode = ReadInt(words, "_UDIMDiscardMode");
        udimDiscardUv = Math.Clamp(ReadInt(words, "_UDIMDiscardUV"), 0, 3);
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                string cellName = string.Create(CultureInfo.InvariantCulture, $"_UDIMDiscardRow{row}_{col}");
                if (IsParameterEnabled(words, cellName, 0.001f))
                    udimDiscardMask |= (ushort)(1 << ((row * 4) + col));
            }
        }
    }

    private static ulong ComputeFnv1a64(string text)
    {
        ulong hash = 14695981039346656037ul;
        foreach (char c in text)
            hash = unchecked((hash ^ c) * 1099511628211ul);
        return hash;
    }

    private static XRDataBuffer UploadBuffer<T>(
        XRDataBuffer? existing,
        string name,
        uint binding,
        ReadOnlySpan<T> data) where T : unmanaged
    {
        existing ??= new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, false)
        {
            BindingIndexOverride = binding,
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };
        uint count = (uint)Math.Max(data.Length, 1);
        existing.EnsureRawCapacity<T>(count, growCapacityGeometrically: false);
        if (!data.IsEmpty)
        {
            uint byteLength = existing.WriteDataRaw(data, 0u);
            existing.PushSubData(0, byteLength);
        }

        return existing;
    }

    private static XRTexture2D CreateFallbackTexture2D(
        string name,
        uint width,
        uint height,
        ColorF4 fillColor,
        ETextureImportUsage usage,
        ETexWrapMode wrapMode,
        byte[]? pixelBytes)
    {
        XRTexture2D texture = new(width, height, fillColor)
        {
            Name = name,
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.Linear,
            UWrap = wrapMode,
            VWrap = wrapMode,
            AutoGenerateMipmaps = false,
            Resizable = false,
            PreferSynchronousGpuUpload = true,
        };

        if (pixelBytes is not null)
            texture.Mipmaps[0].Data = new DataSource(pixelBytes);

        texture.ImportedUsage = usage;
        texture.ImportedColorSpace = usage == ETextureImportUsage.Color
            ? ETextureColorSpace.Srgb
            : ETextureColorSpace.Linear;
        texture.AlphaAsTransparency = usage == ETextureImportUsage.Color;
        return texture;
    }

    private static XRTextureCube CreateFallbackCube(string name, byte[] faceRgba)
    {
        XRTextureCube cube = new(
            1u,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            allocateData: true,
            mipCount: 1)
        {
            Name = name,
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.Linear,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };
        for (int face = 0; face < cube.Mipmaps[0].Sides.Length; face++)
            cube.Mipmaps[0].Sides[face].Data = new DataSource(faceRgba);
        return cube;
    }

    private static void TryDestroyAsset(XREngine.Data.Core.XRObjectBase? asset)
    {
        if (asset is null || asset.IsDestroyed)
            return;
        try
        {
            asset.Destroy(now: true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Identifies a texture by its size, format, wrap modes, and a hash of its mip-0 pixels. Two textures with an
    /// equal key share a slot only after a byte comparison confirms that the pixels are equal.
    /// </summary>
    private readonly record struct TextureContentKey(
        uint Width,
        uint Height,
        ESizedInternalFormat Format,
        ETexWrapMode UWrap,
        ETexWrapMode VWrap,
        int ByteLength,
        int ContentHash);

    private readonly record struct InternedMaterialEntry(
        uint DenseIndex,
        uint CullMode,
        ushort UdimDiscardMask,
        int UdimDiscardMode,
        int UdimDiscardUv);
}
