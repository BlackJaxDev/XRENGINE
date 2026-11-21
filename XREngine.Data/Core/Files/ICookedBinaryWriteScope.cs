namespace XREngine.Core.Files;

/// <summary>
/// A hook for a nested object that the generic cooked serializer writes with reflection. Before the serializer reads the
/// members of the object for a size pass, a write, or a schema, it calls <see cref="BeginCookedBinaryWrite"/>, and it
/// disposes the result after the last member. While the scope is open, the object can give cooked-only member values,
/// for example a split view of its data. The members stay ordinary values of the enclosing stream, so references to
/// shared objects in them use the reference table of that stream.
/// </summary>
/// <remarks>
/// The size pass and the write call the hook one time each, so the member values must be the same in both scopes.
/// Other reflection users (YAML, asset graph walks) do not open the scope, so they see the live members.
/// </remarks>
public interface ICookedBinaryWriteScope
{
    /// <summary>Opens the write scope of the object, or returns null for the live members.</summary>
    IDisposable? BeginCookedBinaryWrite();
}
