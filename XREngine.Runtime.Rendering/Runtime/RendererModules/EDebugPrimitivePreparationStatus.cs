namespace XREngine.Rendering;

/// <summary>Reports whether debug point and line pipelines can enter scene recording.</summary>
public enum EDebugPrimitivePreparationStatus
{
    Ready,
    Pending,
    Failed,
    Unsupported,
}
