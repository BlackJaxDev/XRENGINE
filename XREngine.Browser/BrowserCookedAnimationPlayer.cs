using System.Diagnostics;
using System.Numerics;
using XREngine.Animation;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

/// <summary>Instance-local playback adapter using native transforms and the engine's canonical affine skin palette.</summary>
internal sealed class BrowserCookedAnimationPlayer : IDisposable
{
    private readonly Transform[] _bones;
    private readonly TransformState[] _pose;
    private readonly SkinPaletteMatrix[] _palette;
    private readonly Vector2[] _morphs;
    private readonly float[] _vertices;
    private BrowserCookedAnimationClip _clip;
    private long _ticks;
    private bool _dirty = true;

    internal BrowserCookedAnimationPlayer(BrowserMeshComponent component, BrowserCookedAnimationAsset asset, bool compute)
    {
        Component = component;
        Asset = asset;
        Compute = compute;
        MeshHandle = component.MeshHandle;
        Mesh = component.Mesh ?? throw new ArgumentException("Animated output mesh is required.");
        _clip = asset.DefaultClip;
        _bones = new Transform[asset.BoneCount];
        _pose = new TransformState[asset.BoneCount];
        _palette = new SkinPaletteMatrix[asset.BoneCount];
        _morphs = new Vector2[asset.Skinning.MorphCount];
        _vertices = new float[asset.VertexCount * 5];
        try
        {
            for (int bone = 0; bone < _bones.Length; bone++)
            {
                TransformState bind = asset.BindPose[bone];
                int parent = asset.Parents[bone];
                _bones[bone] = new Transform(bind.Scale, bind.Translation, bind.Rotation,
                    parent < 0 ? null : _bones[parent], ETransformOrder.TRS)
                {
                    ImmediateLocalMatrixRecalculation = false
                };
            }
        }
        catch { Dispose(); throw; }
    }

    public BrowserMeshComponent Component { get; }
    public BrowserCookedAnimationAsset Asset { get; }
    public BrowserMeshData Mesh { get; }
    public int MeshHandle { get; }
    public bool Compute { get; }

    public void Play(string name)
    {
        _clip = Asset.GetClip(name);
        _ticks = 0;
        _dirty = true;
    }

    public void Advance(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds is < 0 or > 0.1f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        long ticks = _ticks + (long)Math.Round((double)deltaSeconds * Stopwatch.Frequency);
        ticks = _clip.Loop ? ticks % _clip.LengthTicks : Math.Min(ticks, _clip.LengthTicks);
        if (ticks == _ticks) return;
        _ticks = ticks;
        _dirty = true;
    }

    public void Publish(IBrowserRendererHost renderer, BrowserUploadBatch uploads, int session)
    {
        if (!_dirty || !Component.RenderEnabled) return;
        if (Component.MeshHandle != MeshHandle || !ReferenceEquals(Component.Mesh, Mesh))
            throw new InvalidOperationException("Animated output ownership changed without detaching playback.");
        _clip.Sample(_ticks, _pose, _morphs);
        for (int bone = 0; bone < _bones.Length; bone++)
        {
            // Reuse native transform construction and hierarchy evaluation. No browser skeleton solver owns this pose.
            _bones[bone].SetFrameState(_pose[bone]);
            _bones[bone].RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);
            Matrix4x4 matrix = Asset.InverseBindMatrices[bone] * _bones[bone].WorldMatrix;
            _palette[bone] = SkinPaletteMatrix.FromRowVectorMatrix(matrix);
        }
        if (Compute)
        {
            if (renderer is not IBrowserComputeSkinningCapability capability)
                throw new NotSupportedException("Requested compute deformation capability is unavailable.");
            capability.UpdateComputeSkinning(BrowserResourceHandle.FromPacked(MeshHandle), _palette, _morphs);
        }
        else
        {
            Asset.Skinning.EvaluatePositionsCpu(_palette, _morphs, _vertices);
            uploads.Begin(session);
            try
            {
                uploads.AddMeshVertices(BrowserResourceHandle.FromPacked(MeshHandle), 0, _vertices);
                uploads.Seal();
                renderer.SubmitUploads(uploads);
            }
            catch { uploads.Abort(); throw; }
        }
        _dirty = false;
    }

    public void Dispose()
    {
        for (int bone = _bones.Length - 1; bone >= 0; bone--) _bones[bone]?.Destroy();
    }
}
