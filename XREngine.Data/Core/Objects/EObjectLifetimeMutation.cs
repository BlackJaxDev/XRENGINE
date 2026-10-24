namespace XREngine.Data.Core;

/// <summary>Identifies a cold identity, publication, or destruction operation.</summary>
public enum EObjectLifetimeMutation
{
    Generate,
    Identity,
    Publish,
    Unpublish,
    QueueDestruction,
    DestroyNow,
    AbortConstruction,
}
