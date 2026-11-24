using System.Globalization;
using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Parses official NVIDIA SOMA BVH mocap files (centimeters, Y-up, arbitrary channel order)
/// directly into normalized 30 Hz 30-joint SOMA local pose arrays matching
/// <see cref="SomaModelContract.JointNames"/>. Mirrors <c>motionbricks/io/bvh.py</c>.
/// </summary>
public static class SomaBvhClipLoader
{
    private const int JointCount = SomaModelContract.JointCount;
    private const float CentimetersToMeters = 0.01f;
    private const float DegreesToRadians = MathF.PI / 180.0f;

    private sealed class BvhHierarchy
    {
        public readonly List<string> JointNames = [];
        public readonly List<int> ParentIndices = [];
        public readonly List<Vector3> OffsetsMeters = [];
        public readonly List<int[]> PositionChannelCols = [];
        public readonly List<int[]> RotationChannelCols = [];
        public readonly List<int[]> RotationAxisOrder = [];
        public int TotalChannels;
        public int SourceFrameCount;
        public float SourceFrameTimeSeconds;
        public int[] SomaToBvhIndex = new int[JointCount];
    }

    /// <summary>
    /// Loads a full SOMA BVH clip resampled to 30 Hz and normalized so frame 0 begins at
    /// ground XZ <c>(0, 0)</c> facing canonical <c>+Z</c>.
    /// </summary>
    public static SomaMotionClip LoadClip(string bvhPath, SomaRestSkeletonCalibration calibration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bvhPath);
        ArgumentNullException.ThrowIfNull(calibration);

        using var reader = new StreamReader(bvhPath);
        BvhHierarchy hierarchy = ReadHierarchyAndMotionHeader(reader, bvhPath);
        if (hierarchy.SourceFrameCount <= 0)
            throw new InvalidDataException($"BVH file '{bvhPath}' contains zero motion frames.");

        float sourceFps = hierarchy.SourceFrameTimeSeconds > 1.0e-6f
            ? 1.0f / hierarchy.SourceFrameTimeSeconds
            : SomaModelContract.SampleRateHz;
        int stride = Math.Max(1, (int)MathF.Round(sourceFps / SomaModelContract.SampleRateHz));
        int outputFrames = Math.Max(1, (hierarchy.SourceFrameCount + stride - 1) / stride);

        var poses = new SomaPoseTransform[outputFrames * JointCount];
        var rowValues = new float[hierarchy.TotalChannels];
        var worldPos = new Vector3[hierarchy.JointNames.Count];
        var worldRot = new Quaternion[hierarchy.JointNames.Count];

        int outIndex = 0;
        for (int srcFrame = 0; srcFrame < hierarchy.SourceFrameCount; srcFrame++)
        {
            string? line = ReadNextNonEmptyLine(reader);
            if (line is null)
                break;

            if (srcFrame % stride != 0 || outIndex >= outputFrames)
                continue;

            ParseFloatRow(line, rowValues);
            EvaluateSomaFrame(hierarchy, calibration, rowValues, worldPos, worldRot, poses.AsSpan(outIndex * JointCount, JointCount));
            outIndex++;
        }

        if (outIndex < outputFrames)
        {
            var trimmed = new SomaPoseTransform[Math.Max(1, outIndex) * JointCount];
            Array.Copy(poses, trimmed, trimmed.Length);
            poses = trimmed;
            outputFrames = Math.Max(1, outIndex);
        }

        NormalizeSequenceInPlace(poses, outputFrames);
        return new SomaMotionClip(Path.GetFileNameWithoutExtension(bvhPath), poses, SomaModelContract.SampleRateHz);
    }

    /// <summary>
    /// Extracts exactly four consecutive 30 Hz SOMA poses starting at
    /// <paramref name="sampleTimeSeconds"/>, reading only the required rows for fast startup.
    /// Normalizes frame 0 to ground XZ <c>(0, 0)</c> facing canonical <c>+Z</c>.
    /// </summary>
    public static SomaPoseTransform[] ExtractFourPoseToken(
        string bvhPath,
        double sampleTimeSeconds,
        SomaRestSkeletonCalibration calibration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bvhPath);
        ArgumentNullException.ThrowIfNull(calibration);

        using var reader = new StreamReader(bvhPath);
        BvhHierarchy hierarchy = ReadHierarchyAndMotionHeader(reader, bvhPath);
        if (hierarchy.SourceFrameCount <= 0)
            throw new InvalidDataException($"BVH file '{bvhPath}' contains zero motion frames.");

        double sourceFrameTime = hierarchy.SourceFrameTimeSeconds > 1.0e-6f
            ? hierarchy.SourceFrameTimeSeconds
            : 1.0 / SomaModelContract.SampleRateHz;
        double stepSeconds = 1.0 / SomaModelContract.SampleRateHz;

        Span<int> targetSourceFrames = stackalloc int[SomaModelContract.TargetPoseCapacity];
        int maxNeededFrame = 0;
        for (int slot = 0; slot < SomaModelContract.TargetPoseCapacity; slot++)
        {
            double t = Math.Max(0.0, sampleTimeSeconds) + slot * stepSeconds;
            int idx = Math.Clamp((int)Math.Round(t / sourceFrameTime), 0, hierarchy.SourceFrameCount - 1);
            targetSourceFrames[slot] = idx;
            if (idx > maxNeededFrame)
                maxNeededFrame = idx;
        }

        var tokenPoses = new SomaPoseTransform[SomaModelContract.TargetPoseCapacity * JointCount];
        var rowValues = new float[hierarchy.TotalChannels];
        var worldPos = new Vector3[hierarchy.JointNames.Count];
        var worldRot = new Quaternion[hierarchy.JointNames.Count];
        var filled = new bool[SomaModelContract.TargetPoseCapacity];

        for (int srcFrame = 0; srcFrame <= maxNeededFrame; srcFrame++)
        {
            string? line = ReadNextNonEmptyLine(reader);
            if (line is null)
                break;

            bool needed = false;
            for (int slot = 0; slot < SomaModelContract.TargetPoseCapacity; slot++)
            {
                if (targetSourceFrames[slot] == srcFrame)
                {
                    needed = true;
                    break;
                }
            }

            if (!needed)
                continue;

            ParseFloatRow(line, rowValues);
            for (int slot = 0; slot < SomaModelContract.TargetPoseCapacity; slot++)
            {
                if (targetSourceFrames[slot] != srcFrame)
                    continue;

                EvaluateSomaFrame(
                    hierarchy,
                    calibration,
                    rowValues,
                    worldPos,
                    worldRot,
                    tokenPoses.AsSpan(slot * JointCount, JointCount));
                filled[slot] = true;
            }
        }

        // Pad any unreached tail slot with the last valid frame if the sample sat near clip end.
        int lastFilled = -1;
        for (int slot = 0; slot < SomaModelContract.TargetPoseCapacity; slot++)
        {
            if (filled[slot])
                lastFilled = slot;
            else if (lastFilled >= 0)
                tokenPoses.AsSpan(lastFilled * JointCount, JointCount).CopyTo(tokenPoses.AsSpan(slot * JointCount, JointCount));
        }

        NormalizeSequenceInPlace(tokenPoses, SomaModelContract.TargetPoseCapacity);
        return tokenPoses;
    }

    private static BvhHierarchy ReadHierarchyAndMotionHeader(StreamReader reader, string bvhPath)
    {
        var hierarchy = new BvhHierarchy();
        var jointStack = new Stack<int>();
        bool inEndSite = false;
        int columnCursor = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.Equals("MOTION", StringComparison.Ordinal))
                break;

            string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            string keyword = tokens[0];
            if (keyword is "ROOT" or "JOINT")
            {
                string rawName = tokens.Length > 1 ? tokens[1] : string.Empty;
                int colonIndex = rawName.LastIndexOf(':');
                string cleanName = colonIndex >= 0 ? rawName[(colonIndex + 1)..] : rawName;
                int parentIndex = jointStack.Count > 0 ? jointStack.Peek() : -1;
                int newIndex = hierarchy.JointNames.Count;

                hierarchy.JointNames.Add(cleanName);
                hierarchy.ParentIndices.Add(parentIndex);
                hierarchy.OffsetsMeters.Add(Vector3.Zero);
                hierarchy.PositionChannelCols.Add([-1, -1, -1]);
                hierarchy.RotationChannelCols.Add([-1, -1, -1]);
                hierarchy.RotationAxisOrder.Add([0, 1, 2]);
                jointStack.Push(newIndex);
            }
            else if (tokens.Length >= 2 && keyword == "End" && tokens[1] == "Site")
            {
                inEndSite = true;
            }
            else if (keyword == "OFFSET" && !inEndSite && jointStack.Count > 0 && tokens.Length >= 4)
            {
                float x = float.Parse(tokens[1], CultureInfo.InvariantCulture) * CentimetersToMeters;
                float y = float.Parse(tokens[2], CultureInfo.InvariantCulture) * CentimetersToMeters;
                float z = float.Parse(tokens[3], CultureInfo.InvariantCulture) * CentimetersToMeters;
                hierarchy.OffsetsMeters[jointStack.Peek()] = new Vector3(x, y, z);
            }
            else if (keyword == "CHANNELS" && !inEndSite && jointStack.Count > 0 && tokens.Length >= 2)
            {
                int count = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                int jointIdx = jointStack.Peek();
                int[] posCols = hierarchy.PositionChannelCols[jointIdx];
                int[] rotCols = hierarchy.RotationChannelCols[jointIdx];
                int[] rotOrder = hierarchy.RotationAxisOrder[jointIdx];
                int rotSlot = 0;

                for (int c = 0; c < count && c + 2 < tokens.Length; c++)
                {
                    string ch = tokens[c + 2];
                    if (ch.EndsWith("position", StringComparison.OrdinalIgnoreCase))
                    {
                        int axis = char.ToLowerInvariant(ch[0]) switch
                        {
                            'x' => 0,
                            'y' => 1,
                            _ => 2,
                        };
                        posCols[axis] = columnCursor;
                    }
                    else if (ch.EndsWith("rotation", StringComparison.OrdinalIgnoreCase) && rotSlot < 3)
                    {
                        int axis = char.ToLowerInvariant(ch[0]) switch
                        {
                            'x' => 0,
                            'y' => 1,
                            _ => 2,
                        };
                        rotCols[rotSlot] = columnCursor;
                        rotOrder[rotSlot] = axis;
                        rotSlot++;
                    }

                    columnCursor++;
                }
            }
            else if (keyword == "}")
            {
                if (inEndSite)
                    inEndSite = false;
                else if (jointStack.Count > 0)
                    jointStack.Pop();
            }
        }

        hierarchy.TotalChannels = columnCursor;
        Array.Fill(hierarchy.SomaToBvhIndex, -1);
        for (int bvhIdx = 0; bvhIdx < hierarchy.JointNames.Count; bvhIdx++)
        {
            if (SomaModelContract.TryGetJointIndex(hierarchy.JointNames[bvhIdx], out int somaIdx))
                hierarchy.SomaToBvhIndex[somaIdx] = bvhIdx;
        }

        if (hierarchy.SomaToBvhIndex[0] < 0)
            throw new InvalidDataException($"BVH file '{bvhPath}' does not contain SOMA root joint 'Hips'.");

        bool gotFrameTime = false;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith("Frames:", StringComparison.OrdinalIgnoreCase))
            {
                string val = trimmed["Frames:".Length..].Trim();
                hierarchy.SourceFrameCount = int.Parse(val, CultureInfo.InvariantCulture);
            }
            else if (trimmed.StartsWith("Frame Time:", StringComparison.OrdinalIgnoreCase))
            {
                string val = trimmed["Frame Time:".Length..].Trim();
                hierarchy.SourceFrameTimeSeconds = float.Parse(val, CultureInfo.InvariantCulture);
                gotFrameTime = true;
                break;
            }
        }

        if (!gotFrameTime)
            throw new InvalidDataException($"BVH file '{bvhPath}' is missing 'Frame Time:'.");

        return hierarchy;
    }

    private static void EvaluateSomaFrame(
        BvhHierarchy hierarchy,
        SomaRestSkeletonCalibration calibration,
        float[] rowValues,
        Vector3[] worldPos,
        Quaternion[] worldRot,
        Span<SomaPoseTransform> destination)
    {
        int bvhCount = hierarchy.JointNames.Count;
        for (int i = 0; i < bvhCount; i++)
        {
            int[] posCols = hierarchy.PositionChannelCols[i];
            Vector3 localPos = posCols[0] >= 0 && posCols[1] >= 0 && posCols[2] >= 0
                ? new Vector3(
                    rowValues[posCols[0]] * CentimetersToMeters,
                    rowValues[posCols[1]] * CentimetersToMeters,
                    rowValues[posCols[2]] * CentimetersToMeters)
                : hierarchy.OffsetsMeters[i];

            int[] rotCols = hierarchy.RotationChannelCols[i];
            int[] rotOrder = hierarchy.RotationAxisOrder[i];
            Quaternion localRot = Quaternion.Identity;
            for (int r = 0; r < 3; r++)
            {
                int col = rotCols[r];
                if (col < 0)
                    continue;

                Vector3 axis = rotOrder[r] switch
                {
                    0 => Vector3.UnitX,
                    1 => Vector3.UnitY,
                    _ => Vector3.UnitZ,
                };
                Quaternion step = Quaternion.CreateFromAxisAngle(axis, rowValues[col] * DegreesToRadians);
                localRot = SomaPoseTransform.NormalizeOrIdentity(localRot * step);
            }

            int parent = hierarchy.ParentIndices[i];
            if (parent < 0)
            {
                worldPos[i] = localPos;
                worldRot[i] = localRot;
            }
            else
            {
                worldPos[i] = worldPos[parent] + Vector3.Transform(localPos, worldRot[parent]);
                worldRot[i] = SomaPoseTransform.NormalizeOrIdentity(worldRot[parent] * localRot);
            }
        }

        int hipsBvh = hierarchy.SomaToBvhIndex[0];
        destination[0] = new SomaPoseTransform(worldPos[hipsBvh], worldRot[hipsBvh]);

        int[] somaParents = SomaModelContract.ParentIndices;
        for (int joint = 1; joint < JointCount; joint++)
        {
            int bvhIdx = hierarchy.SomaToBvhIndex[joint];
            int parentBvhIdx = hierarchy.SomaToBvhIndex[somaParents[joint]];
            if (bvhIdx < 0 || parentBvhIdx < 0)
            {
                destination[joint] = calibration.ReferenceLocalPoses[joint];
                continue;
            }

            Quaternion localRot = SomaPoseTransform.NormalizeOrIdentity(
                Quaternion.Inverse(worldRot[parentBvhIdx]) * worldRot[bvhIdx]);
            destination[joint] = new SomaPoseTransform(calibration.ReferenceLocalPoses[joint].Position, localRot);
        }
    }

    /// <summary>
    /// Translates frame 0's ground XZ to <c>(0, 0)</c> and rotates the entire sequence around
    /// world <c>+Y</c> so frame 0's hip-line heading faces canonical <c>+Z</c> (<c>yaw = 0</c>).
    /// </summary>
    private static void NormalizeSequenceInPlace(SomaPoseTransform[] poses, int frameCount)
    {
        if (frameCount <= 0)
            return;

        Span<SomaPoseTransform> frame0World = stackalloc SomaPoseTransform[JointCount];
        SomaTensorCodec.ComputeWorldPose(poses.AsSpan(0, JointCount), SomaPoseTransform.Identity, frame0World);

        Vector3 hips0 = frame0World[SomaModelContract.RootJointIndex].Position;
        float heading0 = SomaTensorCodec.ComputeHeadingFromHipPositions(
            frame0World[SomaModelContract.RightLegJointIndex].Position,
            frame0World[SomaModelContract.LeftLegJointIndex].Position);

        var originXZ = new Vector3(hips0.X, 0.0f, hips0.Z);
        Quaternion invYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -heading0);

        for (int f = 0; f < frameCount; f++)
        {
            int rootIdx = f * JointCount;
            SomaPoseTransform root = poses[rootIdx];
            Vector3 shifted = root.Position - originXZ;
            Vector3 rotated = Vector3.Transform(shifted, invYaw);
            Quaternion rotatedQ = SomaPoseTransform.NormalizeOrIdentity(invYaw * root.Rotation);
            poses[rootIdx] = new SomaPoseTransform(new Vector3(rotated.X, root.Position.Y, rotated.Z), rotatedQ);
        }
    }

    private static string? ReadNextNonEmptyLine(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }

        return null;
    }

    private static void ParseFloatRow(string line, float[] destination)
    {
        ReadOnlySpan<char> span = line.AsSpan();
        int index = 0;
        int pos = 0;
        int len = span.Length;

        while (pos < len && index < destination.Length)
        {
            while (pos < len && char.IsWhiteSpace(span[pos]))
                pos++;
            if (pos >= len)
                break;

            int start = pos;
            while (pos < len && !char.IsWhiteSpace(span[pos]))
                pos++;

            destination[index++] = float.Parse(span[start..pos], CultureInfo.InvariantCulture);
        }

        while (index < destination.Length)
            destination[index++] = 0.0f;
    }
}
