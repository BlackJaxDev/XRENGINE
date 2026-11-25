namespace XREngine.Data.Core;

/// <summary>
/// Transfers a factory result or its original failure together with all constructor origins.
/// The caller must retain this ownership until its objects have a lifetime owner or are safely retired.
/// </summary>
public sealed class ObjectConstructionResult<T> where T : class
{
    internal ObjectConstructionResult(T? value, Exception? failure, ObjectConstructionOwnership ownership)
    {
        Value = value;
        Failure = failure;
        Ownership = ownership;
    }

    public T? Value { get; }
    public Exception? Failure { get; }
    public ObjectConstructionOwnership Ownership { get; }
    public bool Succeeded => Failure is null;
}
