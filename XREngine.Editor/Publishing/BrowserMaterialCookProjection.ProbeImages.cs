using System.Security.Cryptography;
using System.Text;
using XREngine.Components.Capture.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    private readonly Dictionary<XRTexture2D, XRTexture2D> _derivedProbeImages = new(ReferenceEqualityComparer.Instance);

    private XRTexture2D ProjectRetainedProbeImage(XRTexture2D source)
    {
        if (source.SizedInternalFormat != ESizedInternalFormat.Rgb16f) return source;
        if (_derivedProbeImages.TryGetValue(source, out XRTexture2D? existing)) return existing;
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        Mipmap2D[] mips = new Mipmap2D[source.Mipmaps.Length];
        using IncrementalHash identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identity.AppendData(Encoding.UTF8.GetBytes("xrengine.retained-probe.rgb16f-half-to-rgba16f.v1/" + source.ID.ToString("N")));
        XRTexture2D? target = null;
        Span<byte> dimensions = stackalloc byte[8];
        try
        {
            for (int level = 0; level < mips.Length; level++)
            {
                Mipmap2D mip = source.Mipmaps[level];
                if (mip.PixelFormat != EPixelFormat.Rgb || mip.PixelType != EPixelType.HalfFloat || mip.Data is null)
                    throw new NotSupportedException("BrowserCook.RetainedProbe.ConversionUnsupported: RGB16F requires original CPU Rgb/HalfFloat data for every mip; Float uploads and missing bytes cannot prove a lossless target conversion.");
                byte[] input = mip.Data.GetBytes();
                int pixels = checked((int)((long)mip.Width * mip.Height));
                if (input.Length != checked(pixels * 6)) throw new InvalidDataException("BrowserCook.RetainedProbe.MipBytesMismatch: RGB16F mip length differs from its dimensions.");
                byte[] output = new byte[checked(pixels * 8)];
                for (int pixel = 0; pixel < pixels; pixel++)
                {
                    input.AsSpan(pixel * 6, 6).CopyTo(output.AsSpan(pixel * 8, 6));
                    output[pixel * 8 + 7] = 0x3c;
                }
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dimensions, mip.Width);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dimensions[4..], mip.Height);
                identity.AppendData(dimensions);
                identity.AppendData(input);
                mips[level] = new(mip.Width, mip.Height, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false)
                    { Data = new XREngine.Data.DataSource(output) };
            }
            target = new XRTexture2D(1, 1, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false)
            {
                Mipmaps = mips, Name = source.Name, SamplerName = source.SamplerName, SizedInternalFormat = ESizedInternalFormat.Rgba16f,
                MinFilter = source.MinFilter, MagFilter = source.MagFilter, UWrap = source.UWrap, VWrap = source.VWrap,
                MinLOD = source.MinLOD, MaxLOD = source.MaxLOD, LodBias = source.LodBias,
                LargestMipmapLevel = source.LargestMipmapLevel, SmallestAllowedMipmapLevel = source.SmallestAllowedMipmapLevel,
                AutoGenerateMipmaps = false, Resizable = false,
            };
            PublishedStandardLitTextureSettings.Capture(source).ApplyTo(target);
            target.AdoptPersistentID(new Guid(identity.GetHashAndReset().AsSpan(0, 16)));
            RetainedLightProbeImageConversion.Validate(source, target);
            _derivedProbeImages.Add(source, target);
            return target;
        }
        catch
        {
            target?.Destroy(now: true);
            foreach (Mipmap2D? mip in mips) mip?.Data?.Dispose();
            throw;
        }
    }

    private void ReleaseDerivedProbeImages()
    {
        foreach (XRTexture2D image in _derivedProbeImages.Values)
        {
            image.Destroy(now: true);
            foreach (Mipmap2D mip in image.Mipmaps) mip.Data?.Dispose();
        }
        _derivedProbeImages.Clear();
    }
}
