using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>
/// The Lucide icons that the native editor draws, as one-character strings for a <see cref="Rendering.UI.UITextComponent"/>
/// that uses <see cref="Font"/>. Each value is the icon's code point in the Lucide font (codepoints.json in the
/// Fonts/Lucide folder). To add an icon, add a constant here and to <see cref="Codepoints"/>; the font atlas
/// holds only the listed icons.
/// </summary>
public static class EditorIcons
{
    /// <summary>Lucide "chevron-right".</summary>
    public const string ChevronRight = "\uE06F";
    /// <summary>Lucide "chevron-down".</summary>
    public const string ChevronDown = "\uE06D";
    /// <summary>Lucide "chevron-left".</summary>
    public const string ChevronLeft = "\uE06E";
    /// <summary>Lucide "chevron-up".</summary>
    public const string ChevronUp = "\uE070";
    /// <summary>Lucide "search".</summary>
    public const string Search = "\uE151";
    /// <summary>Lucide "plus".</summary>
    public const string Plus = "\uE13D";
    /// <summary>Lucide "minus".</summary>
    public const string Minus = "\uE11C";
    /// <summary>Lucide "x".</summary>
    public const string X = "\uE1B2";
    /// <summary>Lucide "check".</summary>
    public const string Check = "\uE06C";
    /// <summary>Lucide "eye".</summary>
    public const string Eye = "\uE0BA";
    /// <summary>Lucide "eye-off".</summary>
    public const string EyeOff = "\uE0BB";
    /// <summary>Lucide "lock".</summary>
    public const string Lock = "\uE10B";
    /// <summary>Lucide "lock-open".</summary>
    public const string LockOpen = "\uE10C";
    /// <summary>Lucide "box".</summary>
    public const string Box = "\uE061";
    /// <summary>Lucide "boxes".</summary>
    public const string Boxes = "\uE2D0";
    /// <summary>Lucide "folder".</summary>
    public const string Folder = "\uE0D7";
    /// <summary>Lucide "folder-open".</summary>
    public const string FolderOpen = "\uE247";
    /// <summary>Lucide "file".</summary>
    public const string File = "\uE0C0";
    /// <summary>Lucide "file-text".</summary>
    public const string FileText = "\uE0CC";
    /// <summary>Lucide "image".</summary>
    public const string Image = "\uE0F6";
    /// <summary>Lucide "play".</summary>
    public const string Play = "\uE13C";
    /// <summary>Lucide "pause".</summary>
    public const string Pause = "\uE12E";
    /// <summary>Lucide "square".</summary>
    public const string Square = "\uE167";
    /// <summary>Lucide "step-forward".</summary>
    public const string StepForward = "\uE3EA";
    /// <summary>Lucide "trash-2".</summary>
    public const string Trash2 = "\uE18E";
    /// <summary>Lucide "settings".</summary>
    public const string Settings = "\uE154";
    /// <summary>Lucide "camera".</summary>
    public const string Camera = "\uE064";
    /// <summary>Lucide "video".</summary>
    public const string Video = "\uE1A5";
    /// <summary>Lucide "sun".</summary>
    public const string Sun = "\uE178";
    /// <summary>Lucide "lightbulb".</summary>
    public const string Lightbulb = "\uE1C2";
    /// <summary>Lucide "move-3d".</summary>
    public const string Move3D = "\uE2E5";
    /// <summary>Lucide "rotate-3d".</summary>
    public const string Rotate3D = "\uE2EA";
    /// <summary>Lucide "scale-3d".</summary>
    public const string Scale3D = "\uE2EB";
    /// <summary>Lucide "globe".</summary>
    public const string Globe = "\uE0E8";
    /// <summary>Lucide "layers".</summary>
    public const string Layers = "\uE529";
    /// <summary>Lucide "list".</summary>
    public const string List = "\uE106";
    /// <summary>Lucide "copy".</summary>
    public const string Copy = "\uE09E";
    /// <summary>Lucide "undo-2".</summary>
    public const string Undo2 = "\uE2A1";
    /// <summary>Lucide "redo-2".</summary>
    public const string Redo2 = "\uE2A0";
    /// <summary>Lucide "save".</summary>
    public const string Save = "\uE14D";
    /// <summary>Lucide "refresh-cw".</summary>
    public const string RefreshCw = "\uE145";
    /// <summary>Lucide "ellipsis".</summary>
    public const string Ellipsis = "\uE0B6";
    /// <summary>Lucide "grip-vertical".</summary>
    public const string GripVertical = "\uE0EB";
    /// <summary>Lucide "info".</summary>
    public const string Info = "\uE0F9";
    /// <summary>Lucide "triangle-alert".</summary>
    public const string TriangleAlert = "\uE193";
    /// <summary>Lucide "link".</summary>
    public const string Link = "\uE102";
    /// <summary>Lucide "mouse-pointer-2".</summary>
    public const string MousePointer2 = "\uE1C3";
    /// <summary>Lucide "cuboid".</summary>
    public const string Cuboid = "\uE524";
    /// <summary>Lucide "sparkles".</summary>
    public const string Sparkles = "\uE412";
    /// <summary>Lucide "volume-2".</summary>
    public const string Volume2 = "\uE1AB";
    /// <summary>Lucide "user".</summary>
    public const string User = "\uE19F";
    /// <summary>Lucide "bone".</summary>
    public const string Bone = "\uE358";
    /// <summary>Lucide "palette".</summary>
    public const string Palette = "\uE1DD";
    /// <summary>Lucide "monitor".</summary>
    public const string Monitor = "\uE11D";
    /// <summary>Lucide "zap".</summary>
    public const string Zap = "\uE1B4";
    /// <summary>Lucide "terminal".</summary>
    public const string Terminal = "\uE181";
    /// <summary>Lucide "bug".</summary>
    public const string Bug = "\uE20C";
    /// <summary>Lucide "magnet".</summary>
    public const string Magnet = "\uE2B5";
    /// <summary>Lucide "list-tree".</summary>
    public const string ListTree = "\uE408";
    /// <summary>Lucide "sliders-horizontal".</summary>
    public const string SlidersHorizontal = "\uE29A";
    /// <summary>Lucide "activity".</summary>
    public const string Activity = "\uE038";
    /// <summary>Lucide "film".</summary>
    public const string Film = "\uE0D0";
    /// <summary>Lucide "circle-x".</summary>
    public const string CircleX = "\uE084";
    /// <summary>Lucide "file-code".</summary>
    public const string FileCode = "\uE0C3";
    /// <summary>Lucide "file-image".</summary>
    public const string FileImage = "\uE31C";
    /// <summary>Lucide "file-audio".</summary>
    public const string FileAudio = "\uE31A";
    /// <summary>Lucide "package".</summary>
    public const string Package = "\uE129";
    /// <summary>Lucide "axis-3d".</summary>
    public const string Axis3D = "\uE2FE";
    /// <summary>Lucide "git-fork".</summary>
    public const string GitFork = "\uE28D";

    /// <summary>The code points of every icon above. The icon font atlas holds exactly these glyphs.</summary>
    public static readonly uint[] Codepoints =
    [
        0xE06F, 0xE06D, 0xE06E, 0xE070, 0xE151, 0xE13D, 0xE11C, 0xE1B2,
        0xE06C, 0xE0BA, 0xE0BB, 0xE10B, 0xE10C, 0xE061, 0xE2D0, 0xE0D7,
        0xE247, 0xE0C0, 0xE0CC, 0xE0F6, 0xE13C, 0xE12E, 0xE167, 0xE3EA,
        0xE18E, 0xE154, 0xE064, 0xE1A5, 0xE178, 0xE1C2, 0xE2E5, 0xE2EA,
        0xE2EB, 0xE0E8, 0xE529, 0xE106, 0xE09E, 0xE2A1, 0xE2A0, 0xE14D,
        0xE145, 0xE0B6, 0xE0EB, 0xE0F9, 0xE193, 0xE102, 0xE1C3, 0xE524,
        0xE412, 0xE1AB, 0xE19F, 0xE358, 0xE1DD, 0xE11D, 0xE1B4, 0xE181,
        0xE20C, 0xE2B5, 0xE408, 0xE29A, 0xE038, 0xE0D0, 0xE084, 0xE0C3, 0xE31C, 0xE31A, 0xE129, 0xE2FE, 0xE28D,
    ];

    private static FontGlyphSet? _font;

    /// <summary>The Lucide icon font with the icons above, loaded on first use.</summary>
    public static FontGlyphSet Font => _font ??= FontGlyphSet.LoadIconFont(Codepoints);

    /// <summary>
    /// Creates an icon in <paramref name="parent"/>: a <see cref="UITextComponent"/> that draws one Lucide glyph,
    /// <paramref name="size"/> pixels square, centered at <paramref name="anchor"/> and moved by
    /// <paramref name="offset"/>. The text color tints the icon, so it follows the theme like a label.
    /// </summary>
    /// <param name="glyph">An icon constant of this class, such as <see cref="Play"/>.</param>
    public static UITextComponent CreateGlyph(SceneNode parent, string glyph, Vector2 anchor, Vector2 offset,
        float size, ColorF4 color)
    {
        SceneNode node = parent.NewChild<UITextComponent>(out var text);
        node.Name = "Icon";
        UIBoundableTransform bounds = text.BoundableTransform;
        bounds.MinAnchor = anchor;
        bounds.MaxAnchor = anchor;
        bounds.NormalizedPivot = new Vector2(0.5f, 0.5f);
        bounds.Translation = offset;
        bounds.Width = size;
        bounds.Height = size;
        bounds.BlocksInputBehind = false;
        text.Font = Font;
        text.FontSize = size;
        text.Color = color;
        text.HorizontalAlignment = EHorizontalAlignment.Center;
        text.VerticalAlignment = EVerticalAlignment.Center;
        text.Text = glyph;
        return text;
    }
}
