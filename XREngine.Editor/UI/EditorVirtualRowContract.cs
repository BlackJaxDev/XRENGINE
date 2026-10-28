using System.Reflection;

namespace XREngine.Editor.UI;

/// <summary>
/// Collection-specific content rendered through the shared retained virtual row host. When
/// <paramref name="RowExtent"/> is null, every row uses <see cref="EditorVirtualRowExtent.SingleLineHeight"/>.
/// Otherwise it is the explicit measured-height contract: it returns the height of the row at an
/// index before the row binds, and the host does not measure row content.
/// </summary>
internal sealed record EditorVirtualRowContract(
    Type ValueType,
    PropertyInfo BindingProperty,
    int Count,
    string AddLabel,
    Func<int, string> RowLabel,
    Func<int, Func<bool>, Func<bool>, object[]> CreateBindings,
    Action<int, Func<bool>> Remove,
    Action Add,
    Func<int, string?>? ReadOnlyText = null,
    Func<int, float>? RowExtent = null);
