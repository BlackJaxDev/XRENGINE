using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>Tracks one capture source and discards queued notifications after replacement.</summary>
internal sealed class UIDrawSourceObserver : IDisposable
{
    private readonly Action _changed;
    private readonly Action _dispatch;
    private readonly List<XRBase> _objects = [];
    private readonly List<ShaderVar> _parameters = [];
    private readonly XRShader[] _shaders;
    private readonly XRMesh.BufferCollection[] _collections;
    private readonly XRDataBuffer[] _buffers;
    private readonly UITextureSnapshot?[] _textures;
    private int _disposed;
    private int _queued;

    internal UIDrawSourceObserver(RenderCommandMesh2D command, XRMeshRenderer renderer, XRMesh mesh,
        XRMaterial material, XRShader[] shaders, KeyValuePair<string, XRDataBuffer>[] meshBuffers,
        KeyValuePair<string, XRDataBuffer>[] rendererBuffers, UITextureSnapshot?[] textures, Action changed)
    {
        _changed = changed;
        _dispatch = Dispatch;
        _shaders = shaders;
        _textures = textures;
        _collections = [mesh.Buffers, renderer.Buffers];
        _buffers = new XRDataBuffer[meshBuffers.Length + rendererBuffers.Length];
        Observe(command);
        Observe(renderer);
        Observe(mesh);
        Observe(material);
        ObserveRaster(command.RenderOptionsOverride);
        ObserveRaster(material.RenderOptions);
        foreach (ShaderVar parameter in material.Parameters)
            ObserveParameter(parameter);
        foreach (XRShader shader in shaders)
        {
            Observe(shader);
            shader.SourceChanged += ShaderChanged;
        }
        foreach (XRMesh.BufferCollection collection in _collections)
            collection.Changed += Notify;
        int index = 0;
        foreach (var entry in meshBuffers)
            _buffers[index++] = entry.Value;
        foreach (var entry in rendererBuffers)
            _buffers[index++] = entry.Value;
        foreach (XRDataBuffer buffer in _buffers)
        {
            Observe(buffer);
            buffer.RevisionCommitted += BufferChanged;
        }
        foreach (UITextureSnapshot? texture in textures)
            if (texture is not null)
                texture.SourceChanged += Notify;
    }

    private void Observe(XRBase value)
    {
        if (_objects.Contains(value))
            return;
        _objects.Add(value);
        value.PropertyChanged += PropertyChanged;
    }

    private void ObserveRaster(RenderingParameters? raster)
    {
        if (raster is null)
            return;
        Observe(raster);
        Observe(raster.DepthTest);
        Observe(raster.StencilTest);
        Observe(raster.StencilTest.FrontFace);
        Observe(raster.StencilTest.BackFace);
        if (raster.BlendModeAllDrawBuffers is { } all)
            Observe(all);
        if (raster.BlendModesPerDrawBuffer is { } modes)
            foreach (BlendMode mode in modes.Values)
                Observe(mode);
    }

    private void ObserveParameter(ShaderVar parameter)
    {
        if (_parameters.Contains(parameter))
            return;
        _parameters.Add(parameter);
        parameter.ValueChanged += ParameterChanged;
        if (parameter is ShaderArrayBase { CaptureValues: { } values })
            foreach (ShaderVar? child in values)
                if (child is not null)
                    ObserveParameter(child);
    }

    private void PropertyChanged(object? sender, IXRPropertyChangedEventArgs args) => Notify();
    private void ParameterChanged(ShaderVar parameter) => Notify();
    private void ShaderChanged(XRShader shader) => Notify();
    private void BufferChanged(XRDataBuffer buffer, ulong revision) => Notify();

    private void Notify()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (RuntimeRenderingHostServices.FrameTiming.IsAppThread)
            Dispatch();
        else if (Interlocked.Exchange(ref _queued, 1) == 0)
            RuntimeEngine.EnqueueAppThreadTask(_dispatch, "UI.DrawCapture.SourceChanged");
    }

    private void Dispatch()
    {
        Volatile.Write(ref _queued, 0);
        if (Volatile.Read(ref _disposed) == 0)
            _changed();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (XRBase value in _objects)
            value.PropertyChanged -= PropertyChanged;
        foreach (ShaderVar parameter in _parameters)
            parameter.ValueChanged -= ParameterChanged;
        foreach (XRShader shader in _shaders)
            shader.SourceChanged -= ShaderChanged;
        foreach (XRMesh.BufferCollection collection in _collections)
            collection.Changed -= Notify;
        foreach (XRDataBuffer buffer in _buffers)
            buffer.RevisionCommitted -= BufferChanged;
        foreach (UITextureSnapshot? texture in _textures)
            if (texture is not null)
                texture.SourceChanged -= Notify;
    }
}
