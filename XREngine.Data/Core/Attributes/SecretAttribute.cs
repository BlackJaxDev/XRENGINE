using System;

namespace XREngine;

/// <summary>
/// Marks a settings member whose value must never ship in a published archive: a token, an API key, an admission grant,
/// or an account or session identity of the cook machine. A publish clears every marked member, and its environment
/// variable companion (<c>&lt;Name&gt;EnvVar</c>), on the copy of each settings asset that it writes. The live settings
/// do not change.
/// </summary>
/// <remarks>
/// This attribute does not change YAML persistence or the editor UI. To mask a value in the inspector, also use
/// <c>PasswordAttribute</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SecretAttribute : Attribute
{
}
