namespace XREngine.Settings;

/// <summary>Editor-installed persistence policy for protected settings secrets.</summary>
public interface ISecretCipherServices
{
    string Resolve(string? serialized);
    string Protect(string? plaintext);
    string ReferenceEnvironmentVariable(string environmentVariableName);
    bool IsLegacyPlaintext(string? serialized);
    bool IsEnvironmentReference(string? serialized, out string environmentVariableName);
}
