namespace XREngine.Rendering.WebGPU;

/// <summary>Keeps an engine material's authored stages and callbacks attached to its cooked program.</summary>
public sealed class WebGpuMaterial(WebGpuRendererHost renderer, XRMaterial data)
    : WebGpuObject<XRMaterial>(renderer, data), IRenderPreparationState
{
    private XRRenderProgram? _program;
    private WebGpuRenderProgram? _apiProgram;
    private long _shaderRevision;

    public WebGpuRenderProgram Program => _apiProgram
        ?? throw new InvalidOperationException("WebGPU.Material.ProgramPending: the material has not been prepared.");
    public override bool IsGenerated => _apiProgram?.IsGenerated == true;
    public bool IsPreparedForRendering => IsGenerated;

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (_program is not null && _shaderRevision != Data.ShaderStateRevision)
            throw new NotSupportedException($"WebGPU.Material.ShadersChanged: material '{Data.Name}' requires replacement at a resource-generation boundary.");
        if (_program is null)
        {
            using IDisposable publication = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            XRRenderProgram program = new(false, false, Data.Shaders) { Name = Data.Name };
            SetField(ref _program, program);
            SetField(ref _shaderRevision, Data.ShaderStateRevision);
            SetField(ref _apiProgram, (WebGpuRenderProgram)Renderer.GetOrCreateAPIRenderObject(program)!);
        }
        _apiProgram!.Generate();
    }

    public bool TryPrepareForRendering()
    {
        Generate();
        return IsGenerated;
    }

    public override void Destroy()
    {
        _apiProgram?.Dispose();
        _program?.Destroy();
        SetField(ref _apiProgram, null);
        SetField(ref _program, null);
    }
}
