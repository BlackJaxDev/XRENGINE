using XREngine.Rendering;

namespace XREngine.Browser;

public sealed partial class BrowserSceneSession
{
    private readonly Dictionary<string, BrowserCookedAnimationAsset> _cookedAnimations = new(StringComparer.Ordinal);
    private readonly List<BrowserCookedAnimationPlayer> _cookedAnimationPlayers = new();
    private int _cookedAnimationBones;
    private int _cookedAnimationVertices;

    public int CookedAnimationAssetCount => _cookedAnimations.Count;
    public int CookedAnimatedInstanceCount => _cookedAnimationPlayers.Count;

    private void RegisterCookedAnimation(string id, BrowserCookedAnimationAsset asset)
    {
        if (_cookedAnimations.Count >= 256 || !_cookedAnimations.TryAdd(id, asset))
            throw new ArgumentException("Cooked animation identity is duplicate or the asset capacity is exhausted.");
    }

    private BrowserCookedAnimationAsset GetCookedAnimationAsset(string id)
        => _cookedAnimations.TryGetValue(id, out BrowserCookedAnimationAsset? asset)
            ? asset : throw new ArgumentException("Cooked animation dependency is not resident.");

    private void ValidateCookedAnimationCapacity(int players, int bones, int vertices)
    {
        if (players < 0 || bones < 0 || vertices < 0 || _cookedAnimationPlayers.Count + players > 64 ||
            _cookedAnimationBones + bones > 8192 || _cookedAnimationVertices + vertices > 262144)
            throw new ArgumentException("Cooked playback exceeds its 64-instance, 8192-bone or 262144-vertex session budget.");
    }

    private void AttachCookedAnimation(BrowserMeshComponent component, BrowserCookedAnimationAsset asset)
    {
        ValidateCookedAnimationCapacity(1, asset.BoneCount, asset.VertexCount);
        if (component.MeshHandle == 0 || component.Mesh?.VertexCount != asset.VertexCount)
            throw new ArgumentException("Cooked animation requires its own resident output mesh.");
        BrowserCookedAnimationPlayer player = new(component, asset, _computeSkinning);
        try
        {
            if (_computeSkinning)
            {
                if (_renderer is not IBrowserComputeSkinningCapability capability)
                    throw new NotSupportedException("Requested compute skinning capability is unavailable.");
                capability.ConfigureComputeSkinning(BrowserResourceHandle.FromPacked(component.MeshHandle), asset.Skinning);
            }
            _uploads.EnsureCapacity(1, asset.VertexCount * 20);
            _cookedAnimationPlayers.Add(player);
            _cookedAnimationBones += asset.BoneCount;
            _cookedAnimationVertices += asset.VertexCount;
        }
        catch { player.Dispose(); throw; }
    }

    private void DetachCookedAnimation(BrowserMeshComponent component)
    {
        for (int i = 0; i < _cookedAnimationPlayers.Count; i++)
        {
            BrowserCookedAnimationPlayer player = _cookedAnimationPlayers[i];
            if (!ReferenceEquals(player.Component, component)) continue;
            if (player.Compute && _renderer.State == BrowserRendererState.Ready)
                ((IBrowserComputeSkinningCapability)_renderer).ReleaseComputeSkinning(BrowserResourceHandle.FromPacked(player.MeshHandle));
            _cookedAnimationPlayers.RemoveAt(i);
            _cookedAnimationBones -= player.Asset.BoneCount;
            _cookedAnimationVertices -= player.Asset.VertexCount;
            _cookedRetainedBytes -= player.Asset.PlayerRetainedBytes + player.Asset.OutputMeshBytes;
            _cookedGpuBytes -= player.Asset.OutputMeshBytes + (player.Compute ? player.Asset.ComputeGpuBytes : 0);
            player.Dispose();
            return;
        }
    }

    /// <summary>Selects an admitted named clip; changing a clip never changes the selected deformation backend.</summary>
    public void PlayCookedAnimation(int renderableIndex, string clipName)
    {
        ThrowIfFrameBusy();
        if ((uint)renderableIndex >= _renderables.Count) throw new ArgumentOutOfRangeException(nameof(renderableIndex));
        BrowserMeshComponent component = _renderables[renderableIndex];
        for (int i = 0; i < _cookedAnimationPlayers.Count; i++)
            if (ReferenceEquals(_cookedAnimationPlayers[i].Component, component))
            {
                _cookedAnimationPlayers[i].Play(clipName);
                return;
            }
        throw new InvalidOperationException("The selected renderable has no admitted cooked animation.");
    }

    private bool IsCookedAnimationMesh(BrowserMeshData mesh)
    {
        for (int i = 0; i < _cookedAnimationPlayers.Count; i++)
            if (ReferenceEquals(_cookedAnimationPlayers[i].Mesh, mesh)) return true;
        return false;
    }

    private void DisposeCookedAnimation()
    {
        for (int i = 0; i < _cookedAnimationPlayers.Count; i++) _cookedAnimationPlayers[i].Dispose();
        _cookedAnimationPlayers.Clear();
        _cookedAnimations.Clear();
        _cookedAnimationBones = 0;
        _cookedAnimationVertices = 0;
    }
}
