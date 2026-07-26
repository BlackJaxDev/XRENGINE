using System.Diagnostics;
using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

public enum EMaterialAuthoringActionKind
{
    SetProperty,
    SetTag,
    SetShader,
    Url,
    OpenEditor,
    SetRenderState,
}

public sealed record MaterialAuthoringAction(
    EMaterialAuthoringActionKind Kind,
    string Target,
    string? Value);

/// <summary>
/// Closed command registry for imported editor actions. Shader metadata can
/// select registered IDs but cannot reflect or instantiate editor types.
/// </summary>
public static class MaterialAuthoringCommandRegistry
{
    private static readonly Dictionary<string, Action<XRMaterial, ShaderAuthoringNode>> Commands =
        new(StringComparer.Ordinal);

    public static event Action<Uri>? SafeLinkConfirmationRequested;

    public static void Register(
        string id,
        Action<XRMaterial, ShaderAuthoringNode> command)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A command ID is required.", nameof(id));
        Commands[id] = command ?? throw new ArgumentNullException(nameof(command));
    }

    public static bool TryExecute(
        string id,
        XRMaterial material,
        ShaderAuthoringNode node,
        out string? diagnostic)
    {
        if (!Commands.TryGetValue(id, out Action<XRMaterial, ShaderAuthoringNode>? command))
        {
            diagnostic = $"Editor command '{id}' is not allowlisted.";
            return false;
        }

        command(material, node);
        diagnostic = null;
        return true;
    }

    public static bool RequestSafeLink(string? rawUrl, out string? diagnostic)
    {
        diagnostic = null;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = "Only absolute HTTPS help links are allowed.";
            return false;
        }

        if (SafeLinkConfirmationRequested is null)
        {
            diagnostic = "No editor safe-link confirmation handler is registered.";
            return false;
        }

        SafeLinkConfirmationRequested(uri);
        return true;
    }

    public static IReadOnlyCollection<string> RegisteredCommandIds => Commands.Keys;
}

public static class MaterialAuthoringActionParser
{
    public static IReadOnlyList<MaterialAuthoringAction> Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return [];

        List<MaterialAuthoringAction> actions = [];
        foreach (string rawAction in source.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = rawAction.IndexOf(':');
            string kindText = separator >= 0 ? rawAction[..separator] : rawAction;
            string payload = separator >= 0 ? rawAction[(separator + 1)..] : string.Empty;
            if (!Enum.TryParse(kindText.Replace("_", string.Empty), true, out EMaterialAuthoringActionKind kind))
                continue;

            int assignment = payload.IndexOf('=');
            string target = assignment >= 0 ? payload[..assignment].Trim() : payload.Trim();
            string? value = assignment >= 0 ? payload[(assignment + 1)..].Trim() : null;
            actions.Add(new(kind, target, value));
        }

        return actions;
    }
}
