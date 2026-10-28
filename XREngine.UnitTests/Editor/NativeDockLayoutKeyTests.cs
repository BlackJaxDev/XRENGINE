using System.Text.Json;
using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeDockLayoutKeyTests
{
    private static NativeDockLayout Layout(float bottom) => new() { BottomSize = bottom };

    [Test]
    public void CanonicalRecordWinsOverLegacyRecords()
    {
        var layouts = new Dictionary<string, NativeDockLayout?>
        {
            ["0:Custom"] = Layout(300),
            ["0:XRE Editor"] = Layout(310),
            [NativeDockLayoutKeys.Primary] = Layout(320),
        };

        var match = NativeDockLayoutKeys.ResolvePrimary(layouts, "Custom", out var layout, out _);

        Assert.That(match, Is.EqualTo(ENativeDockLayoutKeyMatch.Canonical));
        Assert.That(layout!.BottomSize, Is.EqualTo(320));
    }

    [Test]
    public void ExactLegacyTitleWinsOverDefaultTitle()
    {
        var layouts = new Dictionary<string, NativeDockLayout?>
        {
            ["0:XRE Editor"] = Layout(310),
            ["0:XRE Editor (Client 2)"] = Layout(330),
        };

        var match = NativeDockLayoutKeys.ResolvePrimary(layouts, "XRE Editor (Client 2)", out var layout, out _);

        Assert.That(match, Is.EqualTo(ENativeDockLayoutKeyMatch.CurrentTitle));
        Assert.That(layout!.BottomSize, Is.EqualTo(330));
    }

    [Test]
    public void TitleOverrideFallsBackToDefaultTitleRecord()
    {
        var layouts = new Dictionary<string, NativeDockLayout?> { ["0:XRE Editor"] = Layout(310) };

        var match = NativeDockLayoutKeys.ResolvePrimary(layouts, "Overridden", out var layout, out _);

        Assert.That(match, Is.EqualTo(ENativeDockLayoutKeyMatch.DefaultTitle));
        Assert.That(layout!.BottomSize, Is.EqualTo(310));
    }

    [Test]
    public void OtherIndexZeroRecordsAreNotInferred()
    {
        var layouts = new Dictionary<string, NativeDockLayout?>
        {
            ["0:Old A"] = Layout(300),
            ["0:Old B"] = Layout(310),
            ["1:Second"] = Layout(320),
            ["detached:abc"] = Layout(330),
            ["0:Null"] = null,
        };

        var match = NativeDockLayoutKeys.ResolvePrimary(layouts, "New", out var layout, out int unmatched);

        Assert.That(match, Is.EqualTo(ENativeDockLayoutKeyMatch.Unmatched));
        Assert.That(layout, Is.Null);
        Assert.That(unmatched, Is.EqualTo(2));
    }

    [Test]
    public void NullCanonicalRecordIsSkipped()
    {
        var layouts = new Dictionary<string, NativeDockLayout?>
        {
            [NativeDockLayoutKeys.Primary] = null,
            ["0:XRE Editor"] = Layout(310),
        };

        Assert.That(NativeDockLayoutKeys.ResolvePrimary(layouts, null, out _, out _),
            Is.EqualTo(ENativeDockLayoutKeyMatch.DefaultTitle));
    }

    [Test]
    public void EmptyRecordsResolveToNone()
        => Assert.That(NativeDockLayoutKeys.ResolvePrimary(
            new Dictionary<string, NativeDockLayout?>(), "XRE Editor", out _, out _),
            Is.EqualTo(ENativeDockLayoutKeyMatch.None));

    [Test]
    public void StoreLoadsLegacyRecordForPrimaryKeyAndKeepsDetachedKeys()
    {
        EditorPreferences prior = Engine.GlobalEditorPreferences;
        try
        {
            Engine.GlobalEditorPreferences = new EditorPreferences
            {
                NativeDockLayoutsJson = JsonSerializer.Serialize(new Dictionary<string, NativeDockLayout>
                {
                    ["0:Custom"] = Layout(300),
                    ["detached:abc"] = Layout(330),
                }),
            };

            Assert.That(NativeDockLayoutStore.Load(NativeDockLayoutKeys.Primary, "Custom").BottomSize, Is.EqualTo(300));
            Assert.That(NativeDockLayoutStore.Load("detached:abc").BottomSize, Is.EqualTo(330));
            Assert.That(NativeDockLayoutStore.HasLayout(NativeDockLayoutKeys.Primary), Is.False);
        }
        finally
        {
            Engine.GlobalEditorPreferences = prior;
        }
    }

    [Test]
    public void StoreRefusesToRewriteMalformedData()
    {
        EditorPreferences prior = Engine.GlobalEditorPreferences;
        const string malformed = "{ \"0:XRE Editor\": [";
        try
        {
            Engine.GlobalEditorPreferences = new EditorPreferences { NativeDockLayoutsJson = malformed };

            bool saved = NativeDockLayoutStore.Save(NativeDockLayoutKeys.Primary, Layout(320));

            Assert.That(saved, Is.False);
            Assert.That(Engine.GlobalEditorPreferences.NativeDockLayoutsJson, Is.EqualTo(malformed));
        }
        finally
        {
            Engine.GlobalEditorPreferences = prior;
        }
    }
}
