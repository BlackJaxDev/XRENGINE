namespace XREngine.Rendering;

/// <summary>Pipeline boundary observed while rendering a directional-shadow atlas target.</summary>
public enum EDirectionalShadowPipelineReceiptStage
{
    BeforeResourceGeneration,
    ResourceGenerationFailed,
    BeforePackageValidation,
    PackageValidationFailed,
    BeforeCommandChainExecute,
}
