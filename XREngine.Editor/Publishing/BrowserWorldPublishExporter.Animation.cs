using System.Buffers.Binary;
using System.Numerics;
using XREngine.Animation;
using XREngine.Browser;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Publishing;

public sealed partial class BrowserWorldPublishExporter
{
    private readonly HashSet<AnimationClipComponent> _animationComponents = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AnimationClipComponent> _consumedAnimations = new(ReferenceEqualityComparer.Instance);
    private int _animatedMeshCount;

    private bool IsExportedAnimationComponent(XRComponent component)
    {
        if (component.GetType() != typeof(AnimationClipComponent))
            return false;
        _animationComponents.Add((AnimationClipComponent)component);
        return true;
    }

    private void CompleteAnimations()
    {
        foreach (AnimationClipComponent component in _animationComponents)
            if (!_consumedAnimations.Contains(component))
                throw Unsupported(component.SceneNode.GetPath(), "animation component has no exported skinned mesh consumer");
    }

    /// <summary>Copies canonical skin buffers and samples native generic curves into the browser's existing clip contract.</summary>
    private (string Mesh, string? Animation, Matrix4x4 Model) AddAnimatedMesh(ModelComponent component, XRMesh mesh)
    {
        string path = component.SceneNode.GetPath();
        if (!mesh.HasSkinning || mesh.HasBlendshapes)
            throw Unsupported(path, "native morph export is unavailable; only bone-skinned meshes without blendshapes can be baked");
        // Native renderers have several root-space policies. Admit the unambiguous common identity basis only.
        if (!Near(component.Transform.WorldMatrix, Matrix4x4.Identity) ||
            !Near(mesh.BindRootMatrix ?? Matrix4x4.Identity, Matrix4x4.Identity))
            throw Unsupported(path, "skinned model placement and mesh bind-root basis must be identity for browser export");
        if (mesh.VertexCount is < 3 or > 16384 || mesh.Type != EPrimitiveType.Triangles ||
            mesh.Triangles is not { Count: > 0 } || mesh.TexCoordCount != 1 || mesh.ColorCount != 0)
            throw Unsupported(path, "skinned mesh requires 3..16384 vertices, indexed triangles, UV0, and no vertex colors");
        AnimationClipComponent source = FindAnimation(component.SceneNode);
        AnimationClip clip = source.Animation ?? throw Unsupported(path, "animation component has no clip");
        if (!source.StartOnActivate || source.Speed != 1.0f || source.Weight != 1.0f ||
            clip.SourceImportManifest is not null || clip.ImportedGenericBindings.Length != 0 || clip.ImportedEvents.Length != 0 ||
            clip.HasMuscleChannels || clip.HasRootMotion || clip.HasIKGoals || clip.RootMember is null)
            throw Unsupported(path, "animation requires one automatically started, full-weight, normal-speed generic transform clip without imported adapters, events, humanoid, root motion, or IK");
        int fps = clip.SampleRate;
        double intervals = (double)clip.LengthInSeconds * fps;
        if (fps is < 1 or > 120 || !double.IsFinite(intervals) || intervals < 1 || intervals > 599 ||
            Math.Abs(intervals - Math.Round(intervals)) > .001)
            throw Unsupported(path, "clip duration must contain 1..599 whole authored sample intervals at 1..120 FPS");
        int frameCount = checked((int)Math.Round(intervals) + 1);

        // This is a detached startup world: use the engine's canonical transactional packer, never a second weight compressor.
        mesh.EnsureComputeSkinningBuffers();
        XRMeshSkinningBufferState state = mesh.GetSkinningBufferStateSnapshot();
        if (state.UtilizedBones.Length is < 1 or > 128 || state.CoreIndices?.ClientSideSource is null || state.CoreWeights?.ClientSideSource is null)
            throw Unsupported(path, "canonical skinning buffers must be resident");
        if (state.ShaderConvention != ESkinningShaderConvention.ExplicitRowMajorRowVector)
            throw Unsupported(path, "skin matrices must use the canonical explicit row-major row-vector convention");
        long revision = mesh.GeometryRevision;
        List<TransformBase> bones = [];
        Dictionary<TransformBase, int> boneIndices = new(ReferenceEqualityComparer.Instance);
        HashSet<TransformBase> visiting = new(ReferenceEqualityComparer.Instance);
        TransformBase sceneRoot = HierarchyRoot(component.Transform, path);
        foreach (var bone in state.UtilizedBones)
        {
            AddBone(bone.tfm, bones, boneIndices, visiting, path);
            TransformBase boneRoot = HierarchyRoot(bone.tfm, path);
            if (!ReferenceEquals(boneRoot, sceneRoot))
                throw Unsupported(path, "serialized skin bones must be rebound to this authored hierarchy before export");
        }
        int[] parents = new int[bones.Count];
        float[] bindPose = new float[bones.Count * 10];
        float[] inverseBinds = new float[bones.Count * 16];
        Transform[] detached = new Transform[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            TransformBase bone = bones[i];
            parents[i] = bone.Parent is null ? -1 : boneIndices[bone.Parent];
            if (!Matrix4x4.Invert(bone.ParentBindMatrix, out Matrix4x4 inverseParent) ||
                !Matrix4x4.Invert(bone.BindMatrix, out Matrix4x4 inverseBind))
                throw Unsupported(path, "singular native bind transform");
            Matrix4x4 local = bone.BindMatrix * inverseParent;
            if (!Near(local, bone.LocalMatrix))
                throw Unsupported(path, "export requires the authored skeleton at its bind pose, not a live sampled pose");
            if (!Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 translation) ||
                !Near(local, Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation)))
                throw Unsupported(path, "bind hierarchy contains a transform that cannot be represented as local TRS");
            // Preserve the authored quaternion sign for partial scalar quaternion channels.
            Transform authored = (Transform)bone;
            detached[i] = new Transform(authored.Scale, authored.Translation, authored.Rotation);
            WritePose(detached[i], bindPose, i * 10, path);
            Matrix(inverseBind).CopyTo(inverseBinds, i * 16);
        }
        foreach (var bone in state.UtilizedBones)
        {
            if (!Near(bone.invBindWorldMtx * bone.tfm.BindMatrix, Matrix4x4.Identity))
                throw Unsupported(path, "mesh inverse-bind matrices do not match the native hierarchy bind pose");
            Matrix(bone.invBindWorldMtx).CopyTo(inverseBinds, boneIndices[bone.tfm] * 16);
        }
        List<(BasePropAnim Curve, AnimationMember Setter)> channels = [];
        CollectChannels(clip.RootMember, source, boneIndices, detached, channels, new HashSet<AnimationMember>(ReferenceEqualityComparer.Instance), path);
        if (channels.Count == 0)
            throw Unsupported(path, "clip has no supported transform channels");
        float[] frames = new float[checked(frameCount * bones.Count * 10)];
        for (int frame = 0; frame < frameCount; frame++)
        {
            _cancellation.ThrowIfCancellationRequested();
            for (int i = 0; i < detached.Length; i++)
            {
                int offset = i * 10;
                detached[i].Translation = new(bindPose[offset], bindPose[offset + 1], bindPose[offset + 2]);
                detached[i].Rotation = new(bindPose[offset + 3], bindPose[offset + 4], bindPose[offset + 5], bindPose[offset + 6]);
                detached[i].Scale = new(bindPose[offset + 7], bindPose[offset + 8], bindPose[offset + 9]);
            }
            float seconds = frame == frameCount - 1 ? clip.LengthInSeconds : (float)frame / fps;
            foreach (var channel in channels)
                channel.Setter.ApplyAnimationValue(channel.Curve.GetValueGeneric(seconds));
            for (int i = 0; i < detached.Length; i++)
                WritePose(detached[i], frames, (frame * bones.Count + i) * 10, path);
        }
        int format = state.CoreIndexFormat == SkinningCoreIndexFormat.Core4x8 ? 1 :
            state.CoreIndexFormat == SkinningCoreIndexFormat.Core4x16 ? 2 : throw Unsupported(path, "unknown canonical skin index format");
        byte[] indexBytes = state.CoreIndices.GetRawBytes(checked((uint)(mesh.VertexCount * format * 4)));
        for (int lane = 0; lane < mesh.VertexCount * 4; lane++)
        {
            uint sourceIndex = format == 1 ? indexBytes[lane] : BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan(lane * 2, 2));
            uint target = RemapBone(sourceIndex, state, boneIndices, path);
            if (format == 1) indexBytes[lane] = checked((byte)target);
            else BinaryPrimitives.WriteUInt16LittleEndian(indexBytes.AsSpan(lane * 2, 2), checked((ushort)target));
        }
        uint[] spillHeaders = [], spillEntries = [];
        if (state.HasSpillInfluences)
        {
            if (state.SpillHeaders?.ClientSideSource is null || state.SpillEntries?.ClientSideSource is null || state.SpillEntries.Length > 65536 * 4)
                throw Unsupported(path, "resident canonical spill buffers within the browser budget are required");
            spillHeaders = Words(state.SpillHeaders.GetRawBytes(checked((uint)(mesh.VertexCount * 4))));
            spillEntries = Words(state.SpillEntries.GetRawBytes(state.SpillEntries.Length));
            for (int i = 0; i < spillEntries.Length; i++)
                spillEntries[i] = (spillEntries[i] & 0xffff0000u) | RemapBone(spillEntries[i] & 0xffffu, state, boneIndices, path);
        }
        BrowserMeshData geometry = BrowserAssetAdapter.CopyGeometry(mesh);
        if (revision != mesh.GeometryRevision || !ReferenceEquals(state, mesh.GetSkinningBufferStateSnapshot()))
            throw Unsupported(path, "mesh or canonical skin publication changed during export");
        string meshId = $"animated-mesh-{_animatedMeshCount++}", animationId = meshId + "-animation";
        BrowserCookedAnimationDto animation = new()
        {
            Mesh = meshId, Parents = parents, BindPose = bindPose, InverseBindMatrices = inverseBinds,
            CoreIndexFormat = format, CoreIndices = Words(indexBytes),
            CoreWeights = Words(state.CoreWeights.GetRawBytes(checked((uint)(mesh.VertexCount * 4)))),
            SpillHeaders = spillHeaders, SpillEntries = spillEntries, DefaultClip = "default",
            Clips = [new BrowserCookedAnimationClipDto { Name = "default", FramesPerSecond = fps,
                FrameCount = frameCount, EndInclusiveSamples = true, Loop = clip.Looped, Frames = frames }],
        };
        WriteAsset(meshId, "mesh", new BrowserCookedMeshDto { Vertices = geometry.CopyVertices(), Indices = geometry.CopyIndices() }, BrowserPublishJsonContext.Default.BrowserCookedMeshDto, []);
        WriteAsset(animationId, "animation", animation, BrowserPublishJsonContext.Default.BrowserCookedAnimationDto, [meshId]);
        _consumedAnimations.Add(source);
        return (meshId, animationId, Matrix4x4.Identity);
    }

    private static AnimationClipComponent FindAnimation(SceneNode node)
    {
        AnimationClipComponent? found = null;
        int depth = 0;
        for (SceneNode? current = node; current is not null; current = current.Parent)
        {
            if (++depth > 128) throw Unsupported(node.GetPath(), "cyclic or oversized animation owner hierarchy");
            foreach (XRComponent component in current.Components)
                if (component is AnimationClipComponent clip && clip.IsActive)
                {
                    if (found is not null)
                        throw Unsupported(node.GetPath(), "multiple ancestor clip components could drive this mesh");
                    found = clip;
                }
        }
        return found ?? throw Unsupported(node.GetPath(), "skinned mesh requires one ancestor AnimationClipComponent");
    }

    private static TransformBase HierarchyRoot(TransformBase transform, string path)
    {
        int depth = 0;
        while (transform.Parent is TransformBase parent)
        {
            if (++depth > 128) throw Unsupported(path, "cyclic or oversized skeleton hierarchy");
            transform = parent;
        }
        return transform;
    }

    private static void AddBone(TransformBase bone, List<TransformBase> bones, Dictionary<TransformBase, int> indices, HashSet<TransformBase> visiting, string path)
    {
        if (indices.ContainsKey(bone)) return;
        if (!visiting.Add(bone) || visiting.Count > 128 || bone.GetType() != typeof(Transform) || ((Transform)bone).Order != ETransformOrder.TRS)
            throw Unsupported(path, "skeleton requires an acyclic standard TRS hierarchy of at most 128 transforms including ancestors");
        if (bone.Parent is not null) AddBone(bone.Parent, bones, indices, visiting, path);
        if (bones.Count >= 128) throw Unsupported(path, "skeleton exceeds 128 transforms including ancestors");
        // A model can precede its skeleton descendants in the export walk; native matrix getters read cached values.
        bone.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
        indices.Add(bone, bones.Count); bones.Add(bone); visiting.Remove(bone);
    }

    private static uint RemapBone(uint oneBasedIndex, XRMeshSkinningBufferState state, Dictionary<TransformBase, int> indices, string path)
    {
        if (oneBasedIndex == 0) return 0;
        if (oneBasedIndex > state.UtilizedBones.Length) throw Unsupported(path, "canonical packed influence references an invalid bone");
        return checked((uint)indices[state.UtilizedBones[oneBasedIndex - 1].tfm] + 1);
    }

    private static uint[] Words(byte[] bytes)
    {
        if (bytes.Length % 4 != 0) throw new InvalidOperationException("Canonical skin buffer is not word aligned.");
        uint[] words = new uint[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        return words;
    }

    private static void WritePose(Transform transform, float[] output, int offset, string path)
    {
        Vector3 t = transform.Translation, s = transform.Scale;
        Quaternion q = transform.Rotation;
        if (!float.IsFinite(q.LengthSquared()) || q.LengthSquared() < .000001f)
            throw Unsupported(path, "invalid quaternion sample");
        q = Quaternion.Normalize(q);
        float[] pose = [t.X,t.Y,t.Z,q.X,q.Y,q.Z,q.W,s.X,s.Y,s.Z];
        if (pose.Any(x => !float.IsFinite(x)) || t.LengthSquared() > 100_000_000 || s.X is < .001f or > 100 || s.Y is < .001f or > 100 || s.Z is < .001f or > 100)
            throw Unsupported(path, "TRS sample is outside the browser animation profile");
        pose.CopyTo(output, offset);
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        float[] left = Matrix(a), right = Matrix(b);
        for (int i = 0; i < 16; i++) if (!float.IsFinite(left[i]) || MathF.Abs(left[i] - right[i]) > .001f) return false;
        return true;
    }
}
