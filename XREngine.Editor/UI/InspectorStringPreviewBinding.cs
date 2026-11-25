using System.Globalization;
using System.Reflection;

namespace XREngine.Editor.UI;

/// <summary>Keeps a leased inspector preview current without rebuilding its row.</summary>
internal sealed class InspectorStringPreviewBinding : IDisposable
{
    private readonly EditorPropertyAccess _access;
    private Action<string>? _publish;
    private ulong _revision;

    public InspectorStringPreviewBinding(PropertyInfo property, object?[] targets, Action<string> publish)
    {
        _access = new(property, targets);
        _publish = publish;
        _access.Changed += Refresh;
    }

    public void Refresh()
    {
        if (_publish is null)
            return;
        ulong revision = ++_revision;
        string preview;
        try
        {
            preview = _access.TryGetCommonValue(out object? value)
                ? Format(value as string ?? string.Empty) : "Multiple values";
        }
        catch (Exception)
        {
            preview = "Value unavailable";
        }
        // A getter can replace or retire the row while its value is read.
        if (_revision == revision)
            _publish?.Invoke(preview);
    }

    public void Dispose()
    {
        _publish = null;
        ++_revision;
        _access.Changed -= Refresh;
    }

    private static string Format(string value)
    {
        if (value.Length == 0)
            return "Open text details";
        int end = 0;
        int count = 0;
        while (end < value.Length && value[end] is not ('\r' or '\n') && count < 56)
        {
            end += StringInfo.GetNextTextElementLength(value.AsSpan(end));
            count++;
        }
        return end < value.Length && value[end] is not ('\r' or '\n')
            ? value[..end] + "…" : value[..end];
    }
}
