namespace XREngine.Core.Files;

/// <summary>
/// A secret member that a cook wrote as its default value, because the member had a value at write time. The object in
/// memory does not change.
/// </summary>
/// <param name="OwnerType">The name of the type that declares the member.</param>
/// <param name="Member">The name of the secret member.</param>
public readonly record struct ClearedCookedSecret(string OwnerType, string Member);
