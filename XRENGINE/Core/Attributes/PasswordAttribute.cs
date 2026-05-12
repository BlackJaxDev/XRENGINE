using System;

namespace XREngine
{
    /// <summary>
    /// Marks a string property/field as containing sensitive data (API key, password, bearer
    /// token, etc.). The ImGui inspector switches the underlying text input to
    /// <c>ImGuiInputTextFlags.Password</c> so the value is rendered as bullets instead of plain
    /// text, and clipboard/log paths can opt into masked output by checking this attribute.
    ///
    /// This attribute does not affect persistence — pair with the secret-cipher persistence
    /// strategy (DPAPI on Windows, env-var indirection) on <c>EditorPreferences</c> for
    /// at-rest protection.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class PasswordAttribute : Attribute
    {
    }
}
