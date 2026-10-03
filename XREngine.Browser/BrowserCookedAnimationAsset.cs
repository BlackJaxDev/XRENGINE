using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Animation;
using XREngine.Data.Transforms;
using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Immutable admission adapter; instances share cooked inputs but own their pose and deformed output.</summary>
internal sealed class BrowserCookedAnimationAsset
{
    private readonly BrowserMeshData _mesh;
    private readonly Dictionary<string, BrowserCookedAnimationClip> _clips = new(StringComparer.Ordinal);

    internal BrowserCookedAnimationAsset(BrowserCookedAnimationDto dto, BrowserMeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(dto.Parents);
        ArgumentNullException.ThrowIfNull(dto.BindPose);
        ArgumentNullException.ThrowIfNull(dto.InverseBindMatrices);
        ArgumentNullException.ThrowIfNull(dto.Clips);
        ArgumentNullException.ThrowIfNull(dto.CoreIndices);
        ArgumentNullException.ThrowIfNull(dto.CoreWeights);
        ArgumentNullException.ThrowIfNull(dto.SpillHeaders);
        ArgumentNullException.ThrowIfNull(dto.SpillEntries);
        ArgumentNullException.ThrowIfNull(dto.Normals);
        ArgumentNullException.ThrowIfNull(dto.Tangents);
        ArgumentNullException.ThrowIfNull(dto.ShapeRanges);
        ArgumentNullException.ThrowIfNull(dto.SparseRecords);
        ArgumentNullException.ThrowIfNull(dto.QuantizedDeltas);
        ArgumentNullException.ThrowIfNull(dto.QuantizationMetadata);
        if (dto.SchemaVersion != 1 || dto.Parents.Length is < 1 or > 128 ||
            dto.BindPose.Length != dto.Parents.Length * 10 || dto.InverseBindMatrices.Length != dto.Parents.Length * 16 ||
            dto.Clips.Length is < 1 or > 8 || mesh.VertexCount > 16384 ||
            dto.Normals.Length % 3 != 0 || dto.Tangents.Length % 4 != 0 || dto.QuantizationMetadata.Length % 4 != 0)
            throw new ArgumentException("Cooked animation dimensions exceed the admitted pose profile.");
        _mesh = mesh;
        MeshId = dto.Mesh;
        Parents = (int[])dto.Parents.Clone();
        BindPose = new TransformState[Parents.Length];
        InverseBindMatrices = new Matrix4x4[Parents.Length];
        Span<Matrix4x4> bindWorld = stackalloc Matrix4x4[128];
        for (int bone = 0; bone < Parents.Length; bone++)
        {
            int parent = Parents[bone];
            if (parent < -1 || parent >= bone) throw new ArgumentException("Cooked bone parents must precede their child.");
            TransformState bind = BrowserCookedAnimationClip.DecodePose(dto.BindPose.AsSpan(bone * 10, 10));
            BindPose[bone] = bind;
            Matrix4x4 local = AffineMatrix4x3.CreateTRS(bind.Scale, bind.Rotation, bind.Translation).ToMatrix4x4();
            bindWorld[bone] = parent < 0 ? local : local * bindWorld[parent];
            ReadOnlySpan<float> m = dto.InverseBindMatrices.AsSpan(bone * 16, 16);
            foreach (float value in m) if (!float.IsFinite(value)) throw new ArgumentException("Inverse bind matrices must be finite.");
            Matrix4x4 inverse = new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
            if (inverse.M14 != 0 || inverse.M24 != 0 || inverse.M34 != 0 || inverse.M44 != 1 || !Matrix4x4.Invert(inverse, out _))
                throw new ArgumentException("Inverse bind matrices must be invertible affine mesh-space transforms.");
            Matrix4x4 identity = inverse * bindWorld[bone];
            ReadOnlySpan<float> components = MemoryMarshal.CreateReadOnlySpan(ref identity.M11, 16);
            for (int i = 0; i < 16; i++)
                if (!float.IsFinite(components[i]) || MathF.Abs(components[i] - (i % 5 == 0 ? 1 : 0)) > 0.001f)
                    throw new ArgumentException("Inverse binds must match the mesh-space skeleton; bake external root conversions before cooking.");
            InverseBindMatrices[bone] = inverse;
        }
        Skinning = new BrowserSkinningData(mesh, Parents.Length, dto.CoreIndexFormat, dto.CoreIndices, dto.CoreWeights,
            MemoryMarshal.Cast<float, Vector3>(dto.Normals.AsSpan()), MemoryMarshal.Cast<float, Vector4>(dto.Tangents.AsSpan()),
            dto.SpillHeaders, dto.SpillEntries, dto.ShapeRanges, dto.SparseRecords, dto.QuantizedDeltas,
            MemoryMarshal.Cast<float, Vector4>(dto.QuantizationMetadata.AsSpan()), dto.InfluenceCap,
            dto.MaximumMorphAccumulation, dto.MorphWeightThreshold);
        int totalFrames = 0;
        long clipBytes = 0;
        foreach (BrowserCookedAnimationClipDto clipDto in dto.Clips)
        {
            BrowserCookedAnimationClip clip = new(clipDto, Parents.Length, Skinning.MorphCount);
            totalFrames += clip.FrameCount;
            if (totalFrames > 1200 || !_clips.TryAdd(clip.Name, clip)) throw new ArgumentException("Cooked clips exceed the frame budget or duplicate a name.");
            clipBytes += clip.RetainedBytes;
        }
        BrowserCookedAnimationClip.ValidateName(dto.DefaultClip);
        DefaultClip = GetClip(dto.DefaultClip);
        RetainedBytes = Skinning.PacketByteLength + clipBytes + (long)Parents.Length * 112 + 1024;
        OutputMeshBytes = (long)mesh.VertexCount * 20 + (long)mesh.IndexCount * 4;
        PlayerRetainedBytes = (long)Parents.Length * 1024 + (long)mesh.VertexCount * 20 + (long)Skinning.MorphCount * 8 + 1024;
        ComputeGpuBytes = Skinning.PacketByteLength + (long)Parents.Length * 48 + Math.Max(8, Skinning.MorphCount * 8) + (long)mesh.VertexCount * 32 + 16;
    }

    public string MeshId { get; }
    public int[] Parents { get; }
    public TransformState[] BindPose { get; }
    public Matrix4x4[] InverseBindMatrices { get; }
    public BrowserSkinningData Skinning { get; }
    public BrowserCookedAnimationClip DefaultClip { get; }
    public int BoneCount => Parents.Length;
    public int VertexCount => _mesh.VertexCount;
    public long RetainedBytes { get; }
    public long PlayerRetainedBytes { get; }
    public long OutputMeshBytes { get; }
    public long ComputeGpuBytes { get; }
    public BrowserMeshData CreateOutputMesh() => new(_mesh.CopyVertices(), _mesh.CopyIndices());
    public BrowserCookedAnimationClip GetClip(string name) => _clips.TryGetValue(name, out BrowserCookedAnimationClip? clip)
        ? clip : throw new ArgumentException("The requested cooked animation clip was not admitted.");
}
