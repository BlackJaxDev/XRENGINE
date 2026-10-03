using System.Collections;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Retains the command's synchronous binding enumeration without iterator allocation.</summary>
internal sealed class ComputeTextureBindingEnumeration :
    IEnumerable<(uint unit, IRenderTextureResource texture, int level, int? layer, EImageAccess access, EImageFormat format)>,
    IEnumerator<(uint unit, IRenderTextureResource texture, int level, int? layer, EImageAccess access, EImageFormat format)>
{
    private List<ComputeTextureBinding>? _bindings;
    private List<ComputeTextureBinding>.Enumerator _enumerator;
    private bool _active;
    public (uint unit, IRenderTextureResource texture, int level, int? layer, EImageAccess access, EImageFormat format) Current { get; private set; }
    object IEnumerator.Current => Current;

    public void Configure(List<ComputeTextureBinding>? bindings)
    {
        if (_active) throw new InvalidOperationException("Compute bindings cannot change during dispatch.");
        _bindings = bindings;
    }

    public IEnumerator<(uint unit, IRenderTextureResource texture, int level, int? layer, EImageAccess access, EImageFormat format)> GetEnumerator()
    {
        if (_active) throw new InvalidOperationException("Compute binding dispatch cannot be reentered.");
        _active = true;
        _enumerator = _bindings?.GetEnumerator() ?? default;
        return this;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool MoveNext()
    {
        if (!_active) throw new InvalidOperationException("Compute binding enumeration has no active dispatch.");
        if (_bindings is null || !_enumerator.MoveNext()) return false;
        ComputeTextureBinding binding = _enumerator.Current;
        Current = (binding.Unit, binding.TextureFactory(), binding.Level, binding.Layer, binding.Access, binding.Format);
        return true;
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose() { _enumerator.Dispose(); _active = false; Current = default; }
}
