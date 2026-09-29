using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XREngine.Tools.BrowserContentCooker;

internal static partial class Program
{
    private static string AnimationMeshId(string animationId, Dictionary<string, JsonElement> assets, string directory,
        Dictionary<string, string> cache)
    {
        if (cache.TryGetValue(animationId, out string? meshId)) return meshId;
        JsonElement variant = Array(assets[animationId], "variants", 1, 1)[0];
        string source = SourcePath(directory, variant.GetProperty("source").GetString()!);
        using JsonDocument document = ReadJson(ReadBounded(source, JsonLimit));
        meshId = Identifier(document.RootElement.GetProperty("mesh"));
        cache.Add(animationId, meshId);
        return meshId;
    }

    private static void ValidateAnimationPayload(JsonElement payload, Dictionary<string, JsonElement> assets,
        HashSet<string> used, string directory)
    {
        MembersOptional(payload,
            ["schemaVersion", "mesh", "parents", "bindPose", "inverseBindMatrices", "coreIndexFormat",
             "coreIndices", "coreWeights", "spillHeaders", "spillEntries", "normals", "tangents",
             "shapeRanges", "sparseRecords", "quantizedDeltas", "quantizationMetadata", "defaultClip", "clips"],
            ["influenceCap", "maximumMorphAccumulation", "morphWeightThreshold"]);
        Require(Integer(payload.GetProperty("schemaVersion"), 1, 1) == 1, "Unsupported animation payload version.");
        Reference(payload.GetProperty("mesh"), "mesh", assets, used);
        string meshId = Identifier(payload.GetProperty("mesh"));
        JsonElement meshVariant = Array(assets[meshId], "variants", 1, 1)[0];
        using JsonDocument mesh = ReadJson(ReadBounded(SourcePath(directory,
            meshVariant.GetProperty("source").GetString()!), JsonLimit));
        int vertices = Array(mesh.RootElement, "vertices", 65535 * 5, 15).Length / 5;
        Require(vertices is >= 3 and <= 16384, "Animated mesh vertex count must be 3–16384.");

        JsonElement[] parents = Array(payload, "parents", 128, 1);
        int bones = parents.Length;
        for (int i = 0; i < bones; i++)
            Integer(parents[i], -1, i - 1);
        JsonElement[] bindPose = Array(payload, "bindPose", bones * 10, bones * 10);
        ValidateTrs(bindPose);
        JsonElement[] inverseBind = Array(payload, "inverseBindMatrices", bones * 16, bones * 16);
        Matrix4x4[] bindWorld = new Matrix4x4[bones];
        for (int i = 0; i < bones; i++)
        {
            int poseOffset = i * 10;
            Vector3 translation = new(bindPose[poseOffset].GetSingle(), bindPose[poseOffset + 1].GetSingle(),
                bindPose[poseOffset + 2].GetSingle());
            Quaternion rotation = Quaternion.Normalize(new(bindPose[poseOffset + 3].GetSingle(),
                bindPose[poseOffset + 4].GetSingle(), bindPose[poseOffset + 5].GetSingle(),
                bindPose[poseOffset + 6].GetSingle()));
            Vector3 scale = new(bindPose[poseOffset + 7].GetSingle(), bindPose[poseOffset + 8].GetSingle(),
                bindPose[poseOffset + 9].GetSingle());
            Matrix4x4 local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
                Matrix4x4.CreateTranslation(translation);
            int parent = parents[i].GetInt32();
            bindWorld[i] = parent < 0 ? local : local * bindWorld[parent];
            float[] m = inverseBind.Skip(i * 16).Take(16).Select(value => Number(value)).ToArray();
            Require(m[3] == 0 && m[7] == 0 && m[11] == 0 && m[15] == 1,
                "Inverse bind matrices must be affine row-major matrices.");
            Matrix4x4 matrix = new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            Require(Matrix4x4.Invert(matrix, out _), "Inverse bind matrices must be invertible.");
            Matrix4x4 identity = matrix * bindWorld[i];
            float[] elements = [identity.M11, identity.M12, identity.M13, identity.M14,
                identity.M21, identity.M22, identity.M23, identity.M24,
                identity.M31, identity.M32, identity.M33, identity.M34,
                identity.M41, identity.M42, identity.M43, identity.M44];
            for (int element = 0; element < elements.Length; element++)
                Require(float.IsFinite(elements[element]) &&
                    MathF.Abs(elements[element] - (element % 5 == 0 ? 1 : 0)) <= 0.001f,
                    "Inverse bind matrices must match the mesh-space skeleton.");
        }
        int indexFormat = Integer(payload.GetProperty("coreIndexFormat"), 1, 2);
        Require(indexFormat is 1 or 2, "Unsupported core skin index format.");
        uint[] coreIndices = UnsignedArray(payload, "coreIndices", vertices * indexFormat, vertices * indexFormat);
        uint[] coreWeights = UnsignedArray(payload, "coreWeights", vertices, vertices);
        for (int vertex = 0; vertex < vertices; vertex++)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                if (((coreWeights[vertex] >> (slot * 8)) & 0xff) == 0) continue;
                uint index = indexFormat == 1
                    ? (coreIndices[vertex] >> (slot * 8)) & 0xff
                    : (coreIndices[vertex * 2 + slot / 2] >> ((slot % 2) * 16)) & 0xffff;
                Require(index < bones, "Used core bone index exceeds the skeleton.");
            }
        }
        uint[] headers = UnsignedArray(payload, "spillHeaders", vertices);
        uint[] entries = UnsignedArray(payload, "spillEntries", 65536);
        Require(headers.Length == 0 || headers.Length == vertices, "Spill headers must cover every vertex.");
        Require(headers.Length != 0 || entries.Length == 0, "Spill entries require headers.");
        foreach (uint header in headers)
            Require((long)(header & 0x00ffffff) + (header >> 24) <= entries.Length,
                "Spill header range exceeds the entry array.");
        foreach (uint entry in entries)
            Require((entry >> 24) == 0 && (((entry >> 16) & 0xff) == 0 || (entry & 0xffff) < bones),
                "Spill entry has unsupported bits or bone index.");
        float[] normals = FloatArray(payload, "normals", vertices * 3);
        float[] tangents = FloatArray(payload, "tangents", vertices * 4);
        Require(normals.Length is 0 || normals.Length == vertices * 3,
            "Normals must cover all animated vertices or be empty.");
        Require(tangents.Length is 0 || tangents.Length == vertices * 4,
            "Tangents must cover all animated vertices or be empty.");
        uint[] ranges = UnsignedArray(payload, "shapeRanges", 256 * 4);
        uint[] records = UnsignedArray(payload, "sparseRecords", 65536 * 4);
        uint[] deltas = UnsignedArray(payload, "quantizedDeltas", 65536 * 2);
        float[] metadata = FloatArray(payload, "quantizationMetadata", 256 * 16);
        Require(ranges.Length % 4 == 0 && records.Length % 4 == 0 && deltas.Length % 2 == 0 &&
            metadata.Length == ranges.Length * 4, "Sparse morph arrays have inconsistent element strides.");
        int shapes = ranges.Length / 4;
        int recordCount = records.Length / 4;
        int deltaCount = deltas.Length / 2;
        if (deltaCount > 0)
            Require(deltas[0] == 0 && deltas[1] == 0, "Quantized delta zero must be the null sentinel.");
        for (int i = 0; i < shapes; i++)
        {
            uint start = ranges[i * 4], count = ranges[i * 4 + 1];
            Require((long)start + count <= recordCount, "Sparse shape record range exceeds the array.");
            uint previousVertex = 0;
            for (int j = 0; j < count; j++)
            {
                int offset = checked((int)(start + j) * 4);
                uint vertex = records[offset];
                Require(vertex < vertices && (j == 0 || vertex > previousVertex),
                    "Sparse records must have unique ascending vertices per shape.");
                previousVertex = vertex;
                for (int lane = 1; lane < 4; lane++)
                    Require(records[offset + lane] == 0 || records[offset + lane] < deltaCount,
                        "Sparse record references a missing quantized delta.");
            }
        }
        if (payload.TryGetProperty("influenceCap", out JsonElement influenceCap)) Integer(influenceCap, 1, 259);
        if (payload.TryGetProperty("maximumMorphAccumulation", out JsonElement accumulation))
            Require(accumulation.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "maximumMorphAccumulation must be Boolean.");
        if (payload.TryGetProperty("morphWeightThreshold", out JsonElement threshold)) Number(threshold, 0);
        string defaultClip = ClipName(payload.GetProperty("defaultClip"));
        HashSet<string> names = new(StringComparer.Ordinal);
        int totalFrames = 0;
        foreach (JsonElement clip in Array(payload, "clips", 8, 1))
        {
            Members(clip, "name", "framesPerSecond", "frameCount", "loop", "frames", "morphWeights");
            names.Add(ClipName(clip.GetProperty("name")));
            Integer(clip.GetProperty("framesPerSecond"), 1, 120);
            int frames = Integer(clip.GetProperty("frameCount"), 2, 600);
            totalFrames += frames;
            Require(totalFrames <= 1200, "Animation clip frames exceed the package limit.");
            Boolean(clip, "loop");
            ValidateTrs(Array(clip, "frames", frames * bones * 10, frames * bones * 10));
            float[] weights = FloatArray(clip, "morphWeights", frames * shapes);
            Require(weights.Length == 0 || weights.Length == frames * shapes,
                "Morph weights must include every shape in every frame or be empty.");
            foreach (float weight in weights)
                Require(MathF.Abs(weight) <= 100, "Morph weights must stay within ±100.");
        }
        Require(names.Count == payload.GetProperty("clips").GetArrayLength() && names.Contains(defaultClip),
            "Animation clip names must be unique and contain defaultClip.");
    }

    private static float[] FloatArray(JsonElement owner, string name, int maximum, int minimum = 0)
        => Array(owner, name, maximum, minimum).Select(value => Number(value)).ToArray();

    private static string ClipName(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "Animation clip name must be a string.");
        string name = value.GetString()!;
        Require(Regex.IsMatch(name, "^[A-Za-z0-9_.-]{1,64}\\z", RegexOptions.CultureInvariant),
            "Animation clip name must be 1–64 ASCII letters, digits, dots, underscores or hyphens.");
        return name;
    }

    private static void ValidateTrs(JsonElement[] values)
    {
        Require(values.Length % 10 == 0, "TRS data needs ten floats per bone.");
        for (int i = 0; i < values.Length; i += 10)
        {
            float x = Number(values[i]), y = Number(values[i + 1]), z = Number(values[i + 2]);
            Require(x * x + y * y + z * z <= 1e8f, "Bone translation exceeds the supported range.");
            float qx = Number(values[i + 3]), qy = Number(values[i + 4]);
            float qz = Number(values[i + 5]), qw = Number(values[i + 6]);
            float lengthSquared = qx * qx + qy * qy + qz * qz + qw * qw;
            Require(float.IsFinite(lengthSquared) && MathF.Abs(lengthSquared - 1) <= 0.001f,
                "Bone rotation must be a unit quaternion.");
            for (int axis = 7; axis < 10; axis++) Number(values[i + axis], 0.001f, 100);
        }
    }
}
