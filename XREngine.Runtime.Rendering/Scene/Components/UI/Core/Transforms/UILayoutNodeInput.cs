using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Frozen values required to measure and arrange one UI element.</summary>
internal readonly record struct UILayoutNodeInput(
    UILayoutOperator Operator,
    int Parent,
    int FirstChild,
    int ChildCount,
    float? Width,
    float? Height,
    float? MinWidth,
    float? MinHeight,
    float? MaxWidth,
    float? MaxHeight,
    Vector2 MinAnchor,
    Vector2 MaxAnchor,
    Vector2 Pivot,
    Vector2 Translation,
    Vector4 Margins,
    Vector4 Padding,
    bool Collapsed,
    bool Hidden,
    bool ExcludeAutoWidth,
    bool ExcludeAutoHeight,
    float? CapturedAutoWidth,
    float? CapturedAutoHeight,
    Vector4 OperatorValues,
    float OperatorExtra,
    int OperatorFlags,
    ReadOnlyMemory<UILayoutGridTrack> GridRows,
    ReadOnlyMemory<UILayoutGridTrack> GridColumns,
    UITextLayoutInput? TextInput);
