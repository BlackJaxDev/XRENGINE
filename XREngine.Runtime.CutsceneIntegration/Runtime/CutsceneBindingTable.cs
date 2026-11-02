using System.Text;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Holds the scene node of each binding of a compiled cutscene.
/// The table resolves all bindings once, when it is created. A later query never searches the scene.
/// It owns the instances that it spawned and destroys them when it is disposed.
/// </summary>
/// <remarks>
/// Each slot reports at most one diagnostic for each state change. A query per frame never adds one.
/// </remarks>
public sealed class CutsceneBindingTable : IDisposable
{
    private enum ESlotState : byte
    {
        /// <summary>The slot has no node and the diagnostic is already reported.</summary>
        Empty,
        /// <summary>The slot has a node that was alive at the last check.</summary>
        Live,
        /// <summary>The node was destroyed and the diagnostic is already reported.</summary>
        Lost,
    }

    private readonly CutsceneAsset _asset;
    private readonly SceneNode?[] _nodes;
    private readonly bool[] _spawned;
    private readonly ESlotState[] _states;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics = [];
    private bool _disposed;

    private CutsceneBindingTable(CutsceneAsset asset)
    {
        _asset = asset;
        int count = asset.Bindings.Count;
        _nodes = new SceneNode?[count];
        _spawned = new bool[count];
        _states = new ESlotState[count];
    }

    /// <summary>The problems that the table found. A slot adds an entry only when its state changes.</summary>
    public IReadOnlyList<CutsceneCompileDiagnostic> Diagnostics => _diagnostics;

    /// <summary>The number of binding slots.</summary>
    public int SlotCount => _nodes.Length;

    /// <summary>The number of tracks in the cutscene.</summary>
    public int TrackCount => _asset.Tracks.Count;

    /// <summary>Gets a track of the cutscene.</summary>
    /// <param name="trackIndex">The index of the track.</param>
    public CutsceneTrack GetTrack(int trackIndex) => _asset.Tracks[trackIndex];

    /// <summary>
    /// Resolves all bindings of a compiled cutscene. A spawned binding creates its instance now, in a hidden state.
    /// </summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="resolver">The object that finds and creates the nodes.</param>
    /// <returns>A table that holds the result. The caller must dispose it when the cutscene closes.</returns>
    /// <param name="runtimeSlots">The nodes of runtime slots by slot name. A named slot is live when the open ends.</param>
    public static CutsceneBindingTable Resolve(CompiledCutscene compiled, ICutsceneBindingResolver resolver, IReadOnlyDictionary<string, SceneNode>? runtimeSlots = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(resolver);

        CutsceneBindingTable table = new(compiled.Asset);
        table.ReportInvalidTrackSlots();
        for (int slot = 0; slot < table._nodes.Length; slot++)
            table.ResolveSlot(slot, resolver, runtimeSlots);
        return table;
    }

    /// <summary>Gets the node of a slot.</summary>
    /// <param name="slot">The binding slot.</param>
    /// <returns>The node, or null if the slot is empty, out of range, or its node was destroyed.</returns>
    public SceneNode? GetNode(int slot)
        => IsLive(slot) ? _nodes[slot] : null;

    /// <summary>
    /// Checks if a slot has a node that is not destroyed. The first loss of a node adds one diagnostic.
    /// </summary>
    /// <param name="slot">The binding slot.</param>
    public bool IsLive(int slot)
    {
        if ((uint)slot >= (uint)_nodes.Length)
            return false;

        SceneNode? node = _nodes[slot];
        if (node is null)
            return false;

        if (!node.IsDestroyed && !node.IsDestroyQueued)
            return true;

        if (_states[slot] == ESlotState.Live)
        {
            _states[slot] = ESlotState.Lost;
            Report(slot, $"The node was destroyed after the cutscene opened. {DescribeTracks(slot)} are skipped.");
        }
        return false;
    }

    /// <summary>
    /// Fills or clears every runtime slot binding that has a slot name.
    /// Clearing a slot that held a live node adds one diagnostic.
    /// </summary>
    /// <param name="slotName">The slot name.</param>
    /// <param name="node">The node to use, or null to clear the slot.</param>
    public void SetRuntimeSlot(string slotName, SceneNode? node)
    {
        ArgumentException.ThrowIfNullOrEmpty(slotName);
        if (_disposed)
            return;

        for (int slot = 0; slot < _nodes.Length; slot++)
        {
            CutsceneBindingReference? binding = _asset.Bindings[slot];
            if (binding is null || binding.Kind != ECutsceneBindingKind.RuntimeSlot || binding.SlotName != slotName)
                continue;

            bool wasLive = _states[slot] == ESlotState.Live;
            _nodes[slot] = node;
            _states[slot] = node is null ? ESlotState.Empty : ESlotState.Live;
            if (node is null && wasLive)
                Report(slot, $"The runtime slot '{slotName}' was cleared. {DescribeTracks(slot)} are skipped.");
        }
    }

    /// <summary>Gets the slot that a track acts on.</summary>
    /// <param name="trackIndex">The index of the track.</param>
    public int GetTrackSlot(int trackIndex) => _asset.Tracks[trackIndex].BindingSlot;

    /// <summary>Destroys the instances that the table spawned and releases all nodes.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        for (int slot = 0; slot < _nodes.Length; slot++)
        {
            SceneNode? node = _nodes[slot];
            _nodes[slot] = null;
            _states[slot] = ESlotState.Empty;
            if (_spawned[slot] && node is not null && !node.IsDestroyed)
                node.Destroy(true);
        }
    }

    private void ResolveSlot(int slot, ICutsceneBindingResolver resolver, IReadOnlyDictionary<string, SceneNode>? runtimeSlots)
    {
        CutsceneBindingReference? binding = _asset.Bindings[slot];
        if (binding is null)
        {
            Report(slot, $"The binding is missing. {DescribeTracks(slot)} are skipped.");
            return;
        }

        SceneNode? node = null;
        switch (binding.Kind)
        {
            case ECutsceneBindingKind.SceneObject:
                node = resolver.FindNodeById(binding.ObjectId);
                if (node is null && !string.IsNullOrEmpty(binding.NodePath))
                {
                    node = resolver.FindNodeByPath(binding.NodePath);
                    if (node is not null)
                        Report(slot, $"The object identifier was not found. The binding was repaired by the path '{binding.NodePath}'.");
                }
                if (node is null)
                    Report(slot, $"The scene object was not found by identifier or by path. {DescribeTracks(slot)} are skipped.");
                break;

            case ECutsceneBindingKind.PrefabNode:
                node = resolver.FindPrefabNode(binding.PrefabAssetId, binding.PrefabNodeId);
                if (node is null)
                    Report(slot, $"The prefab node was not found in the scene. {DescribeTracks(slot)} are skipped.");
                break;

            case ECutsceneBindingKind.Spawned:
                node = resolver.InstantiatePrefab(binding.PrefabAssetId);
                if (node is null)
                {
                    Report(slot, $"The prefab could not be instantiated. {DescribeTracks(slot)} are skipped.");
                    break;
                }
                node.IsActiveSelf = false;
                _spawned[slot] = true;
                break;

            case ECutsceneBindingKind.RuntimeSlot:
                if (binding.SlotName is { } slotName && runtimeSlots?.TryGetValue(slotName, out SceneNode? filled) == true)
                    node = filled;
                else
                    Report(slot, $"The runtime slot '{binding.SlotName}' is empty. {DescribeTracks(slot)} are skipped until it is filled.");
                break;
        }

        _nodes[slot] = node;
        _states[slot] = node is null ? ESlotState.Empty : ESlotState.Live;
    }

    private void ReportInvalidTrackSlots()
    {
        for (int trackIndex = 0; trackIndex < _asset.Tracks.Count; trackIndex++)
        {
            CutsceneTrack track = _asset.Tracks[trackIndex];
            if ((uint)track.BindingSlot >= (uint)_nodes.Length)
                AddDiagnostic(trackIndex, $"The track '{track.Name}' uses the binding slot {track.BindingSlot}, which does not exist. The track is skipped.");
        }
    }

    private string DescribeTracks(int slot)
    {
        StringBuilder builder = new();
        for (int trackIndex = 0; trackIndex < _asset.Tracks.Count; trackIndex++)
        {
            CutsceneTrack track = _asset.Tracks[trackIndex];
            if (track.BindingSlot != slot)
                continue;
            if (builder.Length > 0)
                builder.Append(", ");
            builder.Append("track '").Append(track.Name).Append('\'');
        }
        return builder.Length == 0 ? "No track" : builder.ToString();
    }

    private int FirstTrackOfSlot(int slot)
    {
        for (int trackIndex = 0; trackIndex < _asset.Tracks.Count; trackIndex++)
            if (_asset.Tracks[trackIndex].BindingSlot == slot)
                return trackIndex;
        return -1;
    }

    private void Report(int slot, string message)
        => AddDiagnostic(FirstTrackOfSlot(slot), $"Binding slot {slot}: {message}");

    private void AddDiagnostic(int trackIndex, string message)
    {
        _diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, -1, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene binding: {0}", message);
    }
}
