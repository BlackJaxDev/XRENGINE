using System.Reflection;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

/// <summary>Runs the panel's selection handlers without constructing its visual tree.</summary>
public sealed class AssetsPanelSelectionProbe : AssetsPanel
{
    protected override void OnComponentActivated() { }
    protected override void OnComponentDeactivated() { }
    protected override void RefreshRows() { }

    public void AttachSelection()
    {
        SetOwnerActive(true);
        Invoke("AttachTableCallbacks");
    }

    public void SetOwnerActive(bool active)
        => Field("_ownerSubscriptionsActive").SetValue(this, active);

    public void AddPath(ulong id, string path)
        => ((Dictionary<ulong, string>)Field("_paths").GetValue(this)!)[id] = path;

    public void Select(ulong? id)
    {
        object table = Field("_table").GetValue(this)!;
        table.GetType().GetProperty("SelectedId")!.SetValue(table, id);
    }

    public void Activate(ulong id) => Invoke("ActivateAssetRow", id);

    private void Invoke(string name, params object?[] args)
        => typeof(AssetsPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, args);

    private static FieldInfo Field(string name)
        => typeof(AssetsPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
