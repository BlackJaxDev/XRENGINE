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

    private static readonly Regex UberUniformDeclarationRegex = new(
        @"\buniform\s+(?:(?:lowp|mediump|highp)\s+)?(bool|int|uint|float|vec2|vec3|vec4)\s+([A-Za-z0-9_]+)\s*[;=]",
        RegexOptions.Compiled);

    private readonly Dictionary<XRMaterial, InternedMaterialEntry> _internedMaterials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRTexture2D, uint> _texture2DSlots = new(ReferenceEqualityComparer.Instance);
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

    private InternedMaterialEntry? _nullMaterialEntry;
    private XRDataBuffer? _materialsBuffer;
    private XRDataBuffer? _materialConstantsBuffer;
    private XRDataBuffer? _textureBindingsBuffer;
    private uint _activeUberFeatureMask;
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
        _fallbackWhiteCube = CreateFallbackWhiteCube();

        _textures2D.Add(_fallbackWhite2D);
        _textures2D.Add(_fallbackBlack2D);
        _textures2D.Add(_fallbackFlatNormal2D);
        _textures2D.Add(_fallbackIdentityRamp2D);
        _texturesCube.Add(_fallbackWhiteCube);
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

    /// <summary>True when all streamed 2D textures requested by this table have reached mip-0 residency.</summary>
    public bool AreStreamedTexturesReady
    {
        get
        {
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

    /// <summary>Uploads the packed material headers, constant words, texture-binding records, and fallback ramp data on first render-thread use.</summary>
    public void EnsureBuffersUploaded()
    {
        if (!_fallbackTexturesPushed)
        {
            _fallbackTexturesPushed = true;
            _fallbackWhite2D.PushData();
            _fallbackBlack2D.PushData();
            _fallbackFlatNormal2D.PushData();
            _fallbackIdentityRamp2D.PushData();
            _fallbackWhiteCube.PushData();
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
    /// Creates the <see cref="XRMaterial"/> for the instanced splat quad draw pass, injecting the exact
    /// <see cref="NativeUberMaterialSchema"/> and <see cref="AdvancedStandardMaterialShaderContract"/> shader
    /// defines plus the union of active Uber features across interned avatar materials, and populating all 168 sampler slots.
    /// </summary>
    /// <summary>
    /// Creates an instanced quad <see cref="XRMaterial"/> that shades every splat with its source submesh's
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
        XRShader vertexShader = ShaderHelper.LoadEngineShader("Gaussian/GaussianSplatUberQuad.vs", EShaderType.Vertex);
        XRShader fragmentTemplate = ShaderHelper.LoadEngineShader("Gaussian/GaussianSplatUberQuad.fs", EShaderType.Fragment);
        bool isVulkan = RuntimeEngine.Rendering.State.IsVulkan ||
                        RuntimeEngine.Rendering.IsVulkanRendererActive() ||
                        RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan;

        StringBuilder sb = new(32768);
        sb.AppendLine("#version 460");
        if (isVulkan)
        {
            sb.AppendLine("#extension GL_EXT_nonuniform_qualifier : require");
            sb.AppendLine("#define XRENGINE_VULKAN 1");
        }

        if (depthOcclusionOnly)
            sb.AppendLine("#define XR_SPLAT_DEPTH_OCCLUSION_PASS 1");

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"#define XR_SPLAT_ACTIVE_UBER_FEATURES 0x{_activeUberFeatureMask:X8}u"));
        AdvancedStandardMaterialShaderContract.AppendDefines(sb);
        NativeUberMaterialSchema.AppendShaderDefines(sb);
        sb.AppendLine(fragmentTemplate.Source.Text ?? string.Empty);

        TextFile fragmentSource = new()
        {
            FilePath = fragmentTemplate.Source.FilePath,
            Text = sb.ToString(),
        };
        XRShader fragmentShader = new(EShaderType.Fragment, fragmentSource)
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

    private InternedMaterialEntry EncodeUberMaterial(XRMaterial material)
    {
        material.EnsureUberStateInitialized();

        MaterialBindingLayout layout = NativeUberMaterialSchema.Opaque;
        int rowWordCount = (int)layout.RowWordCount;
        uint[] words = new uint[rowWordCount];

        // Seed world AO strength and manifest defaults, then overlay serialized ShaderVar parameters
        // and any explicit static literal overrides from UberAuthoredState.
        WriteFloat(words, "_WorldAmbientOcclusionStrength", 1.0f);
        ApplyManifestDefaults(words);

        uint features = ResolveUberFeatureMask(material);
        words[NativeUberMaterialSchema.FeatureWordOffset] = features;
        _activeUberFeatureMask |= features;

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

        UberMaterialPropertyState[] authoredProps = material.UberAuthoredState.Properties;
        for (int i = 0; i < authoredProps.Length; i++)
        {
            UberMaterialPropertyState propState = authoredProps[i];
            if (propState.Mode == EShaderUiPropertyMode.Animated || string.IsNullOrWhiteSpace(propState.StaticLiteral))
                continue;

            int paramIndex = NativeUberMaterialSchema.ParameterIndex(propState.Name);
            if (paramIndex < 0)
                continue;

            Span<uint> dst = words.AsSpan(NativeUberMaterialSchema.ParameterWordOffset(paramIndex), 4);
            _ = TryWriteGlslLiteralWords(ResolveParameterGlslType(propState.Name), propState.StaticLiteral, dst);
        }

        uint cullMode = ResolveCullMode(material, words);
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

    private uint ResolveUberFeatureMask(XRMaterial material)
    {
        uint features = 0u;
        ReadOnlySpan<string> schemaFeatures = NativeUberMaterialSchema.FeatureIds;
        for (int bit = 0; bit < schemaFeatures.Length && bit < 32; bit++)
        {
            string featureId = schemaFeatures[bit];
            if (featureId is "render-time" or "forward-ambient-occlusion" or "forward-lighting" or
                "forward-shadows" or "forward-contact-shadows" or "outline" or "vertex-effects")
                continue;

            bool defaultEnabled = _uberManifestFeatureDefaults.GetValueOrDefault(featureId, false);
            if (material.IsUberFeatureEnabled(featureId, defaultEnabled))
                features |= 1u << bit;
        }

        return features;
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

            return CreateTextureBinding(FallbackSlotWhiteCube, EAdvancedResourceFallback.White);
        }

        if (authoredTexture is XRTexture2D tex2D)
        {
            uint slot2D = InternTexture2D(tex2D);
            return CreateTextureBinding(slot2D, EAdvancedResourceFallback.Zero);
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
            return CreateTextureBinding(InternTexture2D(tex2D), EAdvancedResourceFallback.Zero);

        return CreateTextureBinding(fallbackSlot, fallbackKind);
    }

    private uint InternTexture2D(XRTexture2D texture)
    {
        if (_texture2DSlots.TryGetValue(texture, out uint existing))
            return existing;

        if (_textures2D.Count >= MaxTextures2D)
            return FallbackSlotWhite2D;

        texture.PreferSynchronousGpuUpload = true;
        uint slot = (uint)_textures2D.Count;
        _textures2D.Add(texture);
        _texture2DSlots.Add(texture, slot);

        ImportedTextureStreamingManager manager = ImportedTextureStreamingManager.Instance;
        if (manager.TryGetSourceMaxDimension(texture, out _))
        {
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

        return slot;
    }

    private uint InternTextureCube(XRTextureCube texture)
    {
        if (_textureCubeSlots.TryGetValue(texture, out uint existing))
            return existing;

        if (_texturesCube.Count >= MaxTexturesCube)
            return FallbackSlotWhiteCube;

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

        if (string.Equals(roleName, "_AudioLinkTexture", StringComparison.Ordinal) ||
            string.Equals(roleName, "_PBRMetallicMaps", StringComparison.Ordinal) ||
            string.Equals(roleName, "_ColorMaskTexture", StringComparison.Ordinal) ||
            string.Equals(roleName, "_DissolveDetailNoise", StringComparison.Ordinal))
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

    private static float ReadFloat(ReadOnlySpan<uint> words, string parameterName)
        => ReadFloatOffset(words, parameterName, 0);

    private static float ReadFloatOffset(ReadOnlySpan<uint> words, string parameterName, int component)
    {
        int index = NativeUberMaterialSchema.ParameterIndex(parameterName);
        if (index < 0)
            return 0.0f;
        return BitConverter.UInt32BitsToSingle(words[NativeUberMaterialSchema.ParameterWordOffset(index) + component]);
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
           texture is XRTexture2D { Width: <= 2, Height: <= 2 };

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

    private static uint ResolveCullMode(XRMaterial material, ReadOnlySpan<uint> words)
    {
        if (NativeUberMaterialSchema.ParameterIndex("_Cull") >= 0 &&
            (material.Parameter<ShaderInt>("_Cull") is not null ||
             material.Parameter<ShaderFloat>("_Cull") is not null ||
             HasAuthoredStaticLiteral(material.UberAuthoredState, "_Cull")))
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

    private static void ResolveUdimDiscard(
        ReadOnlySpan<uint> words,
        out ushort udimDiscardMask,
        out int udimDiscardMode,
        out int udimDiscardUv)
    {
        udimDiscardMask = 0;
        udimDiscardMode = 0;
        udimDiscardUv = 0;

        if (ReadFloat(words, "_UvTileDiscard") <= 0.5f)
            return;

        udimDiscardMode = ReadInt(words, "_UDIMDiscardMode");
        udimDiscardUv = Math.Clamp(ReadInt(words, "_UDIMDiscardUV"), 0, 3);
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                string cellName = string.Create(CultureInfo.InvariantCulture, $"_UDIMDiscardRow{row}_{col}");
                if (ReadFloat(words, cellName) > 0.001f)
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

    private static XRTextureCube CreateFallbackWhiteCube()
    {
        XRTextureCube cube = new(
            1u,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            allocateData: true,
            mipCount: 1)
        {
            Name = "GaussianSplatFallbackWhiteCube",
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.Linear,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };
        for (int face = 0; face < cube.Mipmaps[0].Sides.Length; face++)
            cube.Mipmaps[0].Sides[face].Data = new DataSource([255, 255, 255, 255]);
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

    private readonly record struct InternedMaterialEntry(
        uint DenseIndex,
        uint CullMode,
        ushort UdimDiscardMask,
        int UdimDiscardMode,
        int UdimDiscardUv);
}
