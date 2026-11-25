using System.Text.Json;
using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeDockLayoutPersistenceTests
{
    [Test]
    public void NewerLoadDuringReadKeepsItsAdmission()
    {
        string source = "{}";
        bool enter = false;
        NativeDockLayout? newer = null;
        NativeDockLayoutPersistence? store = null;
        store = new(() =>
        {
            if (!enter)
            {
                enter = true;
                newer = store!.Load("primary");
            }
            return source;
        }, updated => source = updated);
        NativeDockLayout older = store.Load("primary");
        older.AssetSearch = "older";
        store.Save("primary", older);
        Assert.That(source, Is.EqualTo("{}"));
        newer!.AssetSearch = "newer";
        store.Save("primary", newer);
        Assert.That(source, Does.Contain("newer"));
    }

    [Test]
    [NonParallelizable]
    public void DiagnosticCallbackLoadKeepsItsAdmission()
    {
        IRuntimeDebugHostServices previousHost = RuntimeDebugHostServices.Current;
        bool previousOutput = Debug.AllowOutput;
        Func<bool>? previousRenderThread = Debug.IsRenderThreadEvaluator;
        string source = "{\"primary\":null,\"0:XRE Editor\":{}}";
        NativeDockLayoutPersistence store = new(() => source, updated => source = updated);
        NativeDockLayout? newer = null;
        void OnEntry(LogEntry entry)
        {
            if (newer is not null || !entry.Message.Contains("[NativeDock]")) return;
            source = "{}";
            newer = store.Load("primary");
        }
        try
        {
            RuntimeDebugHostServices.Current = new NativeDockTestDebugHost();
            Debug.AllowOutput = true;
            Debug.IsRenderThreadEvaluator = () => false;
            Debug.UI("Start native layout diagnostic callback check.");
            Debug.ConsoleEntryAdded += OnEntry;
            store.Load("primary");
            Assert.That(newer, Is.Not.Null);
            newer!.AssetSearch = "newer";
            store.Save("primary", newer);
            Assert.That(source, Does.Contain("newer"));
        }
        finally
        {
            Debug.ConsoleEntryAdded -= OnEntry;
            Debug.IsRenderThreadEvaluator = previousRenderThread;
            Debug.AllowOutput = previousOutput;
            RuntimeDebugHostServices.Current = previousHost;
        }
    }

    [Test]
    public void NestedSaveIsRejectedBeforeItCanBeOverwritten()
    {
        string source = "{}";
        NativeDockLayoutPersistence? store = null;
        store = new(() => source, updated =>
        {
            Assert.Throws<InvalidOperationException>(() => store!.Save("detached:B", new()));
            source = updated;
        });
        store.Save("detached:A", new());
        using JsonDocument document = JsonDocument.Parse(source);
        Assert.That(document.RootElement.TryGetProperty("detached:A", out _), Is.True);
        Assert.That(document.RootElement.TryGetProperty("detached:B", out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreferenceCallbackReplacementRejectsTheOldWrite(bool sameText)
    {
        EditorPreferences preferences = new() { NativeDockLayoutsJson = "{}" };
        bool entered = false;
        string replacement = sameText ? "{}" : "{\"newer\":{}}";
        preferences.PropertyChanging += (_, args) =>
        {
            if (entered || args.PropertyName != nameof(EditorPreferences.NativeDockLayoutsJson)) return;
            entered = true;
            preferences.NativeDockLayoutsJson = replacement;
        };
        Assert.Throws<InvalidOperationException>(() =>
            preferences.TrySetNativeDockLayoutsJson("{}", "{\"old\":{}}", () => true));
        Assert.That(preferences.NativeDockLayoutsJson, Is.EqualTo(replacement));
    }

    [Test]
    public void PrimaryLoadDuringPreferenceCallbackRejectsTheOldHostWrite()
    {
        EditorPreferences preferences = new() { NativeDockLayoutsJson = "{}" };
        NativeDockLayoutPersistence store = new(() => preferences.NativeDockLayoutsJson,
            (expected, updated, current) => preferences.TrySetNativeDockLayoutsJson(expected, updated, current));
        NativeDockLayout old = store.Load("primary");
        old.AssetSearch = "old host";
        bool entered = false;
        NativeDockLayout? newer = null;
        preferences.PropertyChanging += (_, args) =>
        {
            if (entered || args.PropertyName != nameof(EditorPreferences.NativeDockLayoutsJson)) return;
            entered = true;
            newer = store.Load("primary");
        };
        Assert.Throws<InvalidOperationException>(() => store.Save("primary", old));
        Assert.That(preferences.NativeDockLayoutsJson, Is.EqualTo("{}"));
        newer!.AssetSearch = "new host";
        store.Save("primary", newer);
        Assert.That(preferences.NativeDockLayoutsJson, Does.Contain("new host"));
    }

    [Test]
    public void PreferenceOwnerChangeAndVetoDoNotReportAnAcceptedWrite()
    {
        EditorPreferences preferences = new() { NativeDockLayoutsJson = "{}" };
        bool current = true;
        preferences.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == nameof(EditorPreferences.NativeDockLayoutsJson)) current = false;
        };
        Assert.Throws<InvalidOperationException>(() =>
            preferences.TrySetNativeDockLayoutsJson("{}", "{\"old\":{}}", () => current));
        Assert.That(preferences.NativeDockLayoutsJson, Is.EqualTo("{}"));

        EditorPreferences vetoed = new() { NativeDockLayoutsJson = "{}" };
        vetoed.PropertyChanging += (_, args) =>
            ((XREngine.Data.Core.XRPropertyChangingEventArgs<string>)args).AllowChange = false;
        Assert.That(vetoed.TrySetNativeDockLayoutsJson("{}", "{\"old\":{}}", () => true), Is.False);
        Assert.That(vetoed.NativeDockLayoutsJson, Is.EqualTo("{}"));
    }

    [TestCase("{\"primary\":{\"AssetSearch\":\"canonical\"},\"0:Custom\":{\"AssetSearch\":\"exact\"},\"0:XRE Editor\":{\"AssetSearch\":\"default\"}}", "canonical")]
    [TestCase("{\"0:Custom\":{\"AssetSearch\":\"exact\"},\"0:XRE Editor\":{\"AssetSearch\":\"default\"}}", "exact")]
    [TestCase("{\"0:XRE Editor\":{\"AssetSearch\":\"default\"}}", "default")]
    public void PrimaryLoadUsesOnlyTheApprovedKeyOrder(string source, string expected)
    {
        int writes = 0;
        NativeDockLayoutPersistence store = new(() => source, _ => writes++);
        Assert.That(store.Load("primary", "Custom").AssetSearch, Is.EqualTo(expected));
        Assert.That(writes, Is.Zero);
    }

    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("{\"primary\":null}")]
    [TestCase("{\"primary\":17,\"0:XRE Editor\":{}}")]
    [TestCase("{\"primary\":{},\"primary\":{}}")]
    [TestCase("{\"0:Custom\":null,\"0:XRE Editor\":{}}")]
    [TestCase("{\"0:XRE Editor\":null}")]
    [TestCase("{\"0:Other\":{}}")]
    [TestCase("{\"0:First\":{},\"0:Second\":{}}")]
    public void InvalidUnmatchedOrAmbiguousDataCannotAuthorizePrimarySave(string original)
    {
        string source = original;
        int writes = 0;
        NativeDockLayoutPersistence store = new(() => source, updated => { writes++; source = updated; });
        NativeDockLayout layout = store.Load("primary", "Custom");
        layout.AssetSearch = "changed";
        store.Save("primary", layout);
        Assert.That(writes, Is.Zero);
        Assert.That(source, Is.EqualTo(original));
    }

    [Test]
    public void AdmittedMigrationPreservesLegacyDetachedAndUnknownRecords()
    {
        const string legacy = "{\"AssetSearch\":\"legacy\",\"FutureField\":7}";
        string source = "{\"0:XRE Editor\":" + legacy +
            ",\"detached:other\":{\"AssetSearch\":\"detached\"},\"unknown\":[1,2,3]}";
        NativeDockLayoutPersistence store = new(() => source, updated => source = updated);
        NativeDockLayout layout = store.Load("primary", "Custom title");
        layout.AssetSearch = "new primary";
        store.Save("primary", layout);
        using JsonDocument document = JsonDocument.Parse(source);
        JsonElement root = document.RootElement;
        Assert.That(root.GetProperty("0:XRE Editor").GetRawText(), Is.EqualTo(legacy));
        Assert.That(root.GetProperty("unknown").GetRawText(), Is.EqualTo("[1,2,3]"));
        Assert.That(root.GetProperty("detached:other").GetProperty("AssetSearch").GetString(), Is.EqualTo("detached"));
        Assert.That(root.GetProperty("primary").GetProperty("AssetSearch").GetString(), Is.EqualTo("new primary"));
    }

    [Test]
    public void ExistingPrimaryRetainsUnknownFieldsOnSave()
    {
        string source = "{\"primary\":{\"AssetSearch\":\"old\",\"FutureField\":{\"value\":9}}}";
        NativeDockLayoutPersistence store = new(() => source, updated => source = updated);
        NativeDockLayout layout = store.Load("primary");
        layout.AssetSearch = "new";
        store.Save("primary", layout);
        using JsonDocument document = JsonDocument.Parse(source);
        Assert.That(document.RootElement.GetProperty("primary").GetProperty("FutureField").GetRawText(),
            Is.EqualTo("{\"value\":9}"));
    }

    [Test]
    public void LaterLoadRevokesTheEarlierHostEvenForIdenticalJson()
    {
        string source = "{\"primary\":{}}";
        int writes = 0;
        NativeDockLayoutPersistence store = new(() => source, updated => { writes++; source = updated; });
        NativeDockLayout old = store.Load("primary");
        NativeDockLayout current = store.Load("primary");
        store.Save("primary", old);
        Assert.That(writes, Is.Zero);
        store.Save("primary", current);
        Assert.That(writes, Is.EqualTo(1));
    }

    [Test]
    public void SourceReplacementRevokesTheCapturedPrimaryWrite()
    {
        string source = "{\"primary\":{}}";
        int writes = 0;
        NativeDockLayoutPersistence store = new(() => source, _ => writes++);
        NativeDockLayout layout = store.Load("primary");
        source = "{\"primary\":{\"AssetSearch\":\"newer\"}}";
        store.Save("primary", layout);
        Assert.That(writes, Is.Zero);
        Assert.That(source, Does.Contain("newer"));
    }

    [TestCase("detached:other", true)]
    [TestCase("0:Other", false)]
    public void OnlyAnUnrelatedDetachedSaveAdvancesPrimaryAdmission(string key, bool acceptsPrimary)
    {
        string source = "{\"primary\":{}}";
        int writes = 0;
        NativeDockLayoutPersistence store = new(() => source, updated => { writes++; source = updated; });
        NativeDockLayout primary = store.Load("primary");
        store.Save(key, new NativeDockLayout { AssetSearch = "other" });
        store.Save("primary", primary);
        Assert.That(writes, Is.EqualTo(acceptsPrimary ? 2 : 1));
        Assert.That(store.Load(key).AssetSearch, Is.EqualTo("other"));
    }

    [Test]
    public void PersistenceFailureRevokesOnlyItsOwnAdmission()
    {
        string source = "{}";
        int writes = 0;
        NativeDockLayoutPersistence store = new(() => source, updated =>
        {
            source = updated;
            if (++writes == 1) throw new IOException("save failed");
        });
        NativeDockLayout layout = store.Load("primary");
        Assert.Throws<IOException>(() => store.Save("primary", layout));
        store.Save("primary", layout);
        Assert.That(writes, Is.EqualTo(1));
        store.Save("primary", store.Load("primary"));
        Assert.That(writes, Is.EqualTo(2));
    }

    [Test]
    public void FailedOlderSavePreservesAReentrantNewLoadAdmission()
    {
        string source = "{}";
        int writes = 0;
        NativeDockLayout? replacement = null;
        NativeDockLayoutPersistence store = null!;
        store = new(() => source, updated =>
        {
            source = updated;
            if (++writes != 1) return;
            replacement = store.Load("primary");
            throw new IOException("older save failed");
        });
        NativeDockLayout old = store.Load("primary");
        Assert.Throws<IOException>(() => store.Save("primary", old));
        Assert.That(replacement, Is.Not.Null);
        store.Save("primary", replacement!);
        Assert.That(writes, Is.EqualTo(2));
    }

    [TestCase("")]
    [TestCase("{}")]
    public void EmptyPreferencesAllowTheExactFreshPrimaryLayoutToBeSaved(string initial)
    {
        string source = initial;
        NativeDockLayoutPersistence store = new(() => source, updated => source = updated);
        NativeDockLayout layout = store.Load("primary");
        layout.AssetSearch = "fresh";
        store.Save("primary", layout);
        Assert.That(store.Load("primary").AssetSearch, Is.EqualTo("fresh"));
    }

    [Test]
    public void DetachedKeysLoadIndependentlyAndDoNotAcquirePrimaryAuthority()
    {
        string source = "{\"detached:one\":{\"AssetSearch\":\"one\"},\"detached:two\":{\"AssetSearch\":\"two\"}}";
        int writes = 0;
        NativeDockLayoutPersistence store = new(() => source, _ => writes++);
        NativeDockLayout detached = store.Load("detached:one");
        Assert.That(detached.AssetSearch, Is.EqualTo("one"));
        Assert.That(store.Load("detached:two").AssetSearch, Is.EqualTo("two"));
        Assert.That(store.HasLayout("detached:one"), Is.True);
        Assert.That(store.HasLayout("primary"), Is.False);
        store.Save("primary", detached);
        Assert.That(writes, Is.Zero);
    }
}
