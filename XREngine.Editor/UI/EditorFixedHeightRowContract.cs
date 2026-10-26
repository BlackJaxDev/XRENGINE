using System.Reflection;

namespace XREngine.Editor.UI;

/// <summary>Collection-specific content rendered through the shared retained fixed-height row host.</summary>
internal sealed record EditorFixedHeightRowContract(
    Type ValueType,
    PropertyInfo BindingProperty,
    int Count,
    string AddLabel,
    Func<int, string> RowLabel,
    Func<int, Func<bool>, Func<bool>, object[]> CreateBindings,
    Action<int, Func<bool>> Remove,
    Action Add,
    Func<int, string?>? ReadOnlyText = null);
