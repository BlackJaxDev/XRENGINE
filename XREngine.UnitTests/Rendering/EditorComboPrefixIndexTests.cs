using NUnit.Framework;
using Shouldly;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class EditorComboPrefixIndexTests
{
    [Test]
    public void GalleryLabels_FindNextPrefixWithWrap()
    {
        string[] labels = new string[1000];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = $"Option {i + 1}";
        EditorComboPrefixIndex index = new(labels);

        index.Find("OPTION 9", 90).ShouldBe(90);
        index.Find("option 9", 999).ShouldBe(8);
        index.Find("opt", 999).ShouldBe(999);
        index.Find("missing", 0).ShouldBe(-1);
    }

    [Test]
    public void EnumNames_UseTheSameOrdinalPrefixAsTheCombo()
    {
        EditorComboPrefixIndex index = new(new[] { "None", "Input", "Image" });

        index.Find("i", 0).ShouldBe(1);
        index.Find("IM", 0).ShouldBe(2);
        index.Find("i", 2).ShouldBe(2);
    }
}
