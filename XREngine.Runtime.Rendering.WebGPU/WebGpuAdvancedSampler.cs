using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>A physical sampler derived from the frozen logical sampler, independently of its texture.</summary>
internal sealed class WebGpuAdvancedSampler : AbstractRenderAPIObject
{
    private readonly WebGpuRendererHost _renderer;
    private readonly AdvancedSamplerRecord _record;
    private readonly Action<WebGpuAdvancedSampler> _onUnused;
    private int _handle;
    private int _references;

    internal WebGpuAdvancedSampler(WebGpuRendererHost renderer, in AdvancedSamplerRecord record,
        (ulong Epoch, AdvancedGpuHandle Handle) key, Action<WebGpuAdvancedSampler> onUnused) : base(renderer)
    {
        _renderer = renderer;
        _record = record;
        Key = key;
        _onUnused = onUnused;
    }

    internal (ulong Epoch, AdvancedGpuHandle Handle) Key { get; }
    internal void Retain()
    {
        ObjectDisposedException.ThrowIf(IsRetired, this);
        SetField(ref _references, checked(_references + 1), publishNotifications: false);
    }
    internal void Release()
    {
        if (_references <= 0) throw new InvalidOperationException("WebGPU.Advanced.SamplerOwner: sampler reference ownership is unbalanced.");
        SetField(ref _references, _references - 1, publishNotifications: false);
        if (_references != 0) return;
        _onUnused(this);
        Dispose();
    }

    internal bool Matches(in AdvancedSamplerRecord record)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in _record, 1)).SequenceEqual(
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in record, 1)));
    internal int ResourceHandle { get { Generate(); return _handle; } }
    public override bool IsGenerated => _handle != 0;
    public override nint GetHandle() => _handle;
    public override string GetDescribingName() => "Advanced frozen sampler";

    internal static BrowserSamplerDescription Describe(in AdvancedSamplerRecord record)
    {
        if (WebGpuAdvancedMaterialContract.GetSamplerRejection(in record) is { } reason)
            throw new NotSupportedException($"WebGPU.Advanced.SamplerUnsupported: {reason}");
        bool mips = (record.Flags & EAdvancedSamplerRecordFlags.UsesMipmaps) != 0;
        string min = (record.Flags & EAdvancedSamplerRecordFlags.NearestMinification) != 0 ? "nearest" : "linear";
        string mag = (record.Flags & EAdvancedSamplerRecordFlags.NearestMagnification) != 0 ? "nearest" : "linear";
        string mip = (record.Flags & EAdvancedSamplerRecordFlags.LinearMipmapInterpolation) != 0 ? "linear" : "nearest";
        float anisotropy = (record.Flags & EAdvancedSamplerRecordFlags.AnisotropyEnabled) != 0 ? record.LodBiasMinMaxAnisotropy.W : 1;
        float minLod = mips ? Math.Max(0, record.LodBiasMinMaxAnisotropy.Y) : 0;
        float maxLod = mips ? Math.Min(32, record.LodBiasMinMaxAnisotropy.Z) : 0;
        return new(Address(record.AddressU), Address(record.AddressV), min, mag, mip,
            "Advanced frozen sampler", maxLod, (int)anisotropy, minLod,
            Compare: (record.Flags & EAdvancedSamplerRecordFlags.ComparisonEnabled) != 0 ? "less-equal" : null,
            AddressW: Address(record.AddressW));
    }

    public override void Generate()
    {
        ObjectDisposedException.ThrowIf(IsRetired, this);
        ValidateOwnerGeneration();
        if (_handle != 0) return;
        SetField(ref _handle, _renderer.CreateSampler(Describe(in _record)), publishNotifications: false);
    }

    public override void Destroy()
    {
        if (_handle == 0) return;
        _renderer.ReleaseEngineDrawDependencies(this);
        _renderer.RetireEngineResourceAfterFrame(_handle);
        SetField(ref _handle, 0, publishNotifications: false);
    }

    private static string Address(EAdvancedSamplerAddressMode address) => address switch
    {
        EAdvancedSamplerAddressMode.Repeat => "repeat",
        EAdvancedSamplerAddressMode.MirroredRepeat => "mirror-repeat",
        EAdvancedSamplerAddressMode.ClampToEdge => "clamp-to-edge",
        _ => throw Unsupported(),
    };
    private static NotSupportedException Unsupported()
        => new("WebGPU.Advanced.SamplerUnsupported: the frozen sampler requires a comparison other than less-equal, border, LOD bias, or filtering state outside the exact native bank contract.");
}
