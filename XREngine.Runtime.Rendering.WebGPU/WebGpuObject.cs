namespace XREngine.Rendering.WebGPU;

/// <summary>Owns the engine object's managed link for one WebGPU renderer generation.</summary>
public abstract class WebGpuObject<T> : AbstractRenderAPIObject where T : GenericRenderObject
{
    protected WebGpuObject(WebGpuRendererHost renderer, T data) : base(renderer)
    {
        ArgumentNullException.ThrowIfNull(data);
        Renderer = renderer;
        Data = data;
        data.AddWrapper(this);
    }

    public WebGpuRendererHost Renderer { get; }
    public T Data { get; }

    public override string GetDescribingName() => $"{GetType().Name}: {Data.Name ?? Data.GetType().Name}";

    protected override void OnRetiring() => Data.RemoveWrapper(this);
}
