using System.Numerics;
using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Owns Advanced-pipeline ARB_bindless_texture sampler-pair handles. A lease is
/// retained for every encoded table entry and only released after its slot fence
/// proves the GPU no longer reads that entry.
/// </summary>
public partial class OpenGLRenderer
{
    private const int AdvancedBindlessSamplerCapacity = 4096;
    private const string TextureFilterAnisotropicExtension = "GL_EXT_texture_filter_anisotropic";
    private const GLEnum TextureMaxAnisotropyExt = (GLEnum)0x84FE;
    private const GLEnum MaxTextureMaxAnisotropyExt = (GLEnum)0x84FF;

    private readonly Dictionary<ulong, AdvancedBindlessHandleEntry> _advancedBindlessHandles = [];
    private readonly Dictionary<AdvancedSamplerKey, uint> _advancedBindlessSamplers = [];
    private bool? _advancedBindlessAnisotropySupported;
    private float _advancedBindlessMaxAnisotropy = 1.0f;

    private sealed class AdvancedBindlessHandleEntry(IGLBindlessTexture texture)
    {
        public IGLBindlessTexture Texture { get; } = texture;
        public int ReferenceCount;
    }

    private readonly record struct AdvancedSamplerKey(
        EAdvancedSamplerFilter Filter,
        EAdvancedSamplerRecordFlags Flags,
        EAdvancedSamplerAddressMode AddressU,
        EAdvancedSamplerAddressMode AddressV,
        EAdvancedSamplerAddressMode AddressW,
        EAdvancedCompareOperation CompareOperation,
        int LodBiasBits,
        int MinLodBits,
        int MaxLodBits,
        int AnisotropyBits,
        int BorderRBits,
        int BorderGBits,
        int BorderBBits,
        int BorderABits)
    {
        public static AdvancedSamplerKey From(in AdvancedSamplerRecord sampler)
            => new(
                sampler.Filter, sampler.Flags, sampler.AddressU, sampler.AddressV, sampler.AddressW,
                sampler.CompareOperation,
                SemanticFloatBits(sampler.LodBiasMinMaxAnisotropy.X),
                SemanticFloatBits(sampler.LodBiasMinMaxAnisotropy.Y),
                SemanticFloatBits(sampler.LodBiasMinMaxAnisotropy.Z),
                SemanticFloatBits(sampler.LodBiasMinMaxAnisotropy.W),
                SemanticFloatBits(sampler.BorderColor.X),
                SemanticFloatBits(sampler.BorderColor.Y),
                SemanticFloatBits(sampler.BorderColor.Z),
                SemanticFloatBits(sampler.BorderColor.W));

        private static int SemanticFloatBits(float value)
            => value == 0.0f ? 0 : BitConverter.SingleToInt32Bits(value);
    }

    internal bool TryGetResidentBindlessTextureSamplerHandle(
        XRTexture texture,
        in AdvancedSamplerRecord sampler,
        out ulong handle,
        out string reason)
    {
        handle = 0ul;
        if (ARBBindlessTexture is null)
        {
            reason = "OpenGL ARB_bindless_texture is unavailable.";
            return false;
        }
        if (!RuntimeEngine.IsRenderThread)
        {
            reason = "OpenGL bindless sampler-pair residency must be acquired on the render thread.";
            return false;
        }
        if (!TryValidateAdvancedSampler(in sampler, out reason))
            return false;
        if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not GLObjectBase glObject ||
            glObject is not IGLTexture glTexture || glObject is not IGLBindlessTexture bindlessTexture ||
            !IsSupportedAdvancedTexturePair(texture, glObject, glTexture.TextureTarget))
        {
            reason = "Advanced OpenGL bindless sampler pairs require matching physical XRTexture2D, XRTexture2DArray, or XRTextureCube wrappers.";
            return false;
        }

        bindlessTexture.PrepareForBindlessHandle();
        glTexture.Bind();
        if (!bindlessTexture.IsReadyForBindlessHandle())
        {
            reason = "The texture still has a pending sampling-parameter transition.";
            return false;
        }

        uint textureId = glObject.BindingId;
        if (textureId == GLObjectBase.InvalidBindingId || textureId == 0u || !Api.IsTexture(textureId))
        {
            reason = "The physical OpenGL texture identity is unavailable.";
            return false;
        }
        if (!TryGetOrCreateAdvancedSampler(in sampler, out uint samplerId, out reason))
            return false;

        handle = ARBBindlessTexture.GetTextureSamplerHandle(textureId, samplerId);
        if (handle == 0ul)
        {
            reason = "OpenGL failed to create a bindless texture/sampler handle.";
            return false;
        }

        if (_advancedBindlessHandles.TryGetValue(handle, out AdvancedBindlessHandleEntry? existing))
        {
            existing.ReferenceCount++;
            existing.Texture.AcquireAdvancedBindlessPairLease();
            reason = "Ready";
            return true;
        }

        if (!ARBBindlessTexture.IsTextureHandleResident(handle))
            ARBBindlessTexture.MakeTextureHandleResident(handle);

        bindlessTexture.AcquireAdvancedBindlessPairLease();
        _advancedBindlessHandles.Add(handle, new AdvancedBindlessHandleEntry(bindlessTexture) { ReferenceCount = 1 });
        reason = "Ready";
        return true;
    }

    internal void ReleaseAdvancedBindlessTextureSamplerHandle(ulong handle)
    {
        if (handle == 0ul || !_advancedBindlessHandles.Remove(handle, out AdvancedBindlessHandleEntry? entry))
            return;

        if (--entry.ReferenceCount > 0)
        {
            _advancedBindlessHandles.Add(handle, entry);
            entry.Texture.ReleaseAdvancedBindlessPairLease();
            return;
        }

        if (ARBBindlessTexture is not null && ARBBindlessTexture.IsTextureHandleResident(handle))
            ARBBindlessTexture.MakeTextureHandleNonResident(handle);
        entry.Texture.ReleaseAdvancedBindlessPairLease();
    }

    /// <summary>Releases every Advanced pair before the GL context is destroyed.</summary>
    internal void DisposeAdvancedBindlessResidency()
    {
        foreach ((ulong handle, AdvancedBindlessHandleEntry entry) in _advancedBindlessHandles)
        {
            if (ARBBindlessTexture is not null && ARBBindlessTexture.IsTextureHandleResident(handle))
                ARBBindlessTexture.MakeTextureHandleNonResident(handle);
            for (int count = 0; count < entry.ReferenceCount; ++count)
                entry.Texture.ReleaseAdvancedBindlessPairLease();
        }
        _advancedBindlessHandles.Clear();

        if (RuntimeEngine.IsRenderThread && _advancedBindlessSamplers.Count != 0)
        {
            uint[] samplers = [.. _advancedBindlessSamplers.Values];
            Api.DeleteSamplers((uint)samplers.Length, samplers);
        }
        _advancedBindlessSamplers.Clear();
    }

    private bool TryGetOrCreateAdvancedSampler(in AdvancedSamplerRecord sampler, out uint samplerId, out string reason)
    {
        AdvancedSamplerKey key = AdvancedSamplerKey.From(in sampler);
        if (_advancedBindlessSamplers.TryGetValue(key, out samplerId))
        {
            reason = "Ready";
            return true;
        }
        if (_advancedBindlessSamplers.Count >= AdvancedBindlessSamplerCapacity)
        {
            samplerId = 0u;
            reason = $"Advanced OpenGL bindless sampler cache reached its {AdvancedBindlessSamplerCapacity} entry capacity.";
            return false;
        }

        uint[] created = new uint[1];
        Api.CreateSamplers(1u, created);
        samplerId = created[0];
        if (samplerId == 0u)
        {
            reason = "OpenGL could not allocate an Advanced bindless sampler.";
            return false;
        }
        try
        {
            ConfigureAdvancedSampler(samplerId, in sampler);
            _advancedBindlessSamplers.Add(key, samplerId);
            reason = "Ready";
            return true;
        }
        catch
        {
            Api.DeleteSampler(samplerId);
            samplerId = 0u;
            throw;
        }
    }

    private unsafe void ConfigureAdvancedSampler(uint samplerId, in AdvancedSamplerRecord sampler)
    {
        Api.SamplerParameter(samplerId, GLEnum.TextureMinFilter, (int)ResolveMinFilter(in sampler));
        Api.SamplerParameter(samplerId, GLEnum.TextureMagFilter, (int)ResolveMagFilter(in sampler));
        Api.SamplerParameter(samplerId, GLEnum.TextureWrapS, (int)ResolveAddress(sampler.AddressU));
        Api.SamplerParameter(samplerId, GLEnum.TextureWrapT, (int)ResolveAddress(sampler.AddressV));
        Api.SamplerParameter(samplerId, GLEnum.TextureWrapR, (int)ResolveAddress(sampler.AddressW));
        Api.SamplerParameter(samplerId, GLEnum.TextureLodBias, sampler.LodBiasMinMaxAnisotropy.X);
        Api.SamplerParameter(samplerId, GLEnum.TextureMinLod, sampler.LodBiasMinMaxAnisotropy.Y);
        Api.SamplerParameter(samplerId, GLEnum.TextureMaxLod, sampler.LodBiasMinMaxAnisotropy.Z);
        bool comparison = (sampler.Flags & EAdvancedSamplerRecordFlags.ComparisonEnabled) != 0;
        Api.SamplerParameter(samplerId, GLEnum.TextureCompareMode, (int)(comparison ? GLEnum.CompareRefToTexture : GLEnum.None));
        if (comparison)
            Api.SamplerParameter(samplerId, GLEnum.TextureCompareFunc, (int)ResolveCompare(sampler.CompareOperation));
        if (sampler.AddressU == EAdvancedSamplerAddressMode.ClampToBorder ||
            sampler.AddressV == EAdvancedSamplerAddressMode.ClampToBorder ||
            sampler.AddressW == EAdvancedSamplerAddressMode.ClampToBorder)
        {
            Vector4 color = sampler.BorderColor;
            Api.SamplerParameter(samplerId, GLEnum.TextureBorderColor, (float*)&color);
        }
        if ((sampler.Flags & EAdvancedSamplerRecordFlags.AnisotropyEnabled) != 0)
        {
            _ = TryGetAdvancedAnisotropySupport(out float maximum);
            Api.SamplerParameter(samplerId, TextureMaxAnisotropyExt, Math.Clamp(sampler.LodBiasMinMaxAnisotropy.W, 1.0f, maximum));
        }
    }

    private bool TryValidateAdvancedSampler(in AdvancedSamplerRecord sampler, out string reason)
    {
        const EAdvancedSamplerRecordFlags supported = EAdvancedSamplerRecordFlags.UsesMipmaps |
            EAdvancedSamplerRecordFlags.LinearMipmapInterpolation |
            EAdvancedSamplerRecordFlags.NearestMinification |
            EAdvancedSamplerRecordFlags.NearestMagnification |
            EAdvancedSamplerRecordFlags.ComparisonEnabled |
            EAdvancedSamplerRecordFlags.AnisotropyEnabled;
        if ((sampler.Flags & ~supported) != 0 || (uint)sampler.Filter > (uint)EAdvancedSamplerFilter.Linear ||
            (uint)sampler.AddressU > (uint)EAdvancedSamplerAddressMode.ClampToBorder ||
            (uint)sampler.AddressV > (uint)EAdvancedSamplerAddressMode.ClampToBorder ||
            (uint)sampler.AddressW > (uint)EAdvancedSamplerAddressMode.ClampToBorder ||
            (uint)sampler.CompareOperation > (uint)EAdvancedCompareOperation.Always)
        {
            reason = "The Advanced sampler contains an unsupported flag or enum value.";
            return false;
        }
        if (!IsFinite(sampler.LodBiasMinMaxAnisotropy) ||
            sampler.LodBiasMinMaxAnisotropy.Y < 0.0f ||
            sampler.LodBiasMinMaxAnisotropy.Y > sampler.LodBiasMinMaxAnisotropy.Z ||
            sampler.BorderColor != new Vector4(0f, 0f, 0f, 1f))
        {
            reason = "The Advanced sampler contains invalid LOD, anisotropy, or border values.";
            return false;
        }
        bool usesMipmaps = (sampler.Flags & EAdvancedSamplerRecordFlags.UsesMipmaps) != 0;
        bool linearMipmaps = (sampler.Flags & EAdvancedSamplerRecordFlags.LinearMipmapInterpolation) != 0;
        bool nearestMinification = (sampler.Flags & EAdvancedSamplerRecordFlags.NearestMinification) != 0;
        bool nearestMagnification = (sampler.Flags & EAdvancedSamplerRecordFlags.NearestMagnification) != 0;
        EAdvancedSamplerFilter expectedFilter = nearestMinification && nearestMagnification
            ? EAdvancedSamplerFilter.Nearest
            : EAdvancedSamplerFilter.Linear;
        bool anisotropy = (sampler.Flags & EAdvancedSamplerRecordFlags.AnisotropyEnabled) != 0;
        if (sampler.Filter != expectedFilter || (linearMipmaps && !usesMipmaps) ||
            anisotropy != (sampler.LodBiasMinMaxAnisotropy.W > 1.0f))
        {
            reason = "The Advanced sampler is not canonically normalized for its enabled features.";
            return false;
        }
        if (anisotropy &&
            (sampler.LodBiasMinMaxAnisotropy.W <= 1.0f || !TryGetAdvancedAnisotropySupport(out _)))
        {
            reason = "The Advanced sampler requests anisotropy but GL_EXT_texture_filter_anisotropic is unavailable.";
            return false;
        }
        reason = "Ready";
        return true;
    }

    private bool TryGetAdvancedAnisotropySupport(out float maximum)
    {
        if (!_advancedBindlessAnisotropySupported.HasValue)
        {
            bool supported = Array.IndexOf(RuntimeEngine.Rendering.State.OpenGLExtensions, TextureFilterAnisotropicExtension) >= 0;
            if (supported)
            {
                try { _advancedBindlessMaxAnisotropy = MathF.Max(1.0f, Api.GetFloat(MaxTextureMaxAnisotropyExt)); }
                catch { supported = false; }
            }
            _advancedBindlessAnisotropySupported = supported;
        }
        maximum = _advancedBindlessMaxAnisotropy;
        return _advancedBindlessAnisotropySupported.Value;
    }

    private static bool IsSupportedAdvancedTexturePair(XRTexture source, GLObjectBase physical, ETextureTarget target)
        => (source, physical, target) switch
        {
            (XRTexture2D, GLTexture2D, ETextureTarget.Texture2D) => true,
            (XRTexture2DArray, GLTexture2DArray, ETextureTarget.Texture2DArray) => true,
            (XRTextureCube, GLTextureCube, ETextureTarget.TextureCubeMap) => true,
            _ => false,
        };

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static GLEnum ResolveMinFilter(in AdvancedSamplerRecord sampler)
    {
        bool nearest = (sampler.Flags & EAdvancedSamplerRecordFlags.NearestMinification) != 0;
        if ((sampler.Flags & EAdvancedSamplerRecordFlags.UsesMipmaps) == 0)
            return nearest ? GLEnum.Nearest : GLEnum.Linear;
        bool linearMip = (sampler.Flags & EAdvancedSamplerRecordFlags.LinearMipmapInterpolation) != 0;
        return (nearest, linearMip) switch
        {
            (true, false) => GLEnum.NearestMipmapNearest,
            (true, true) => GLEnum.NearestMipmapLinear,
            (false, false) => GLEnum.LinearMipmapNearest,
            _ => GLEnum.LinearMipmapLinear,
        };
    }

    private static GLEnum ResolveMagFilter(in AdvancedSamplerRecord sampler)
        => (sampler.Flags & EAdvancedSamplerRecordFlags.NearestMagnification) != 0 ? GLEnum.Nearest : GLEnum.Linear;

    private static GLEnum ResolveAddress(EAdvancedSamplerAddressMode address)
        => address switch
        {
            EAdvancedSamplerAddressMode.Repeat => GLEnum.Repeat,
            EAdvancedSamplerAddressMode.MirroredRepeat => GLEnum.MirroredRepeat,
            EAdvancedSamplerAddressMode.ClampToEdge => GLEnum.ClampToEdge,
            EAdvancedSamplerAddressMode.ClampToBorder => GLEnum.ClampToBorder,
            _ => throw new ArgumentOutOfRangeException(nameof(address)),
        };

    private static GLEnum ResolveCompare(EAdvancedCompareOperation operation)
        => operation switch
        {
            EAdvancedCompareOperation.Never => GLEnum.Never,
            EAdvancedCompareOperation.Less => GLEnum.Less,
            EAdvancedCompareOperation.Equal => GLEnum.Equal,
            EAdvancedCompareOperation.LessOrEqual => GLEnum.Lequal,
            EAdvancedCompareOperation.Greater => GLEnum.Greater,
            EAdvancedCompareOperation.NotEqual => GLEnum.Notequal,
            EAdvancedCompareOperation.GreaterOrEqual => GLEnum.Gequal,
            EAdvancedCompareOperation.Always => GLEnum.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
}
