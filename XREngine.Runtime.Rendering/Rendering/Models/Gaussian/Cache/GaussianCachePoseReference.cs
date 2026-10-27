using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>
/// Computes on the CPU what the two playback compute passes of a gaussian cache compute on the GPU: the driver pose
/// pass (<c>GaussianCacheDriverPose.comp</c>) and the cut and splat pose pass (<c>GaussianCacheSplats.comp</c>).
/// Tests compare the passes with this reference, and the playback component uses its helpers for the shader inputs.
/// </summary>
/// <remarks>
/// <para><b>Time.</b> The fractional frame is <c>f = t × FrameRate</c> after the loop mode maps the time:
/// <see cref="GaussianCacheLoopMode.Loop"/> wraps over <c>FrameCount / FrameRate</c> seconds, so the last frame blends
/// into frame 0. <see cref="GaussianCacheLoopMode.PingPong"/> uses <c>period − |t mod 2·period − period|</c> with
/// <c>period = (FrameCount − 1) / FrameRate</c>, the time of the last stored frame. <see cref="GaussianCacheLoopMode.Once"/>
/// clamps the time to that period.</para>
/// <para><b>Driver pose.</b> Each driver vertex uses the step <c>8 >> DecodedTemporalLevel</c> of the driver level that
/// adds it. With <c>a = floor(f / step) · step</c> and <c>b = NextNeighbor(a, step)</c>, the position is
/// <c>mix(frame a, frame b, (f − a) / step)</c>. A driver level with no vertices of its own counts as decoded.</para>
/// <para><b>Cut.</b> A node refines when it is not in the last level, all of its children are loaded, and its projected
/// 3σ radius is at least the LOD threshold. A loaded node is chosen when it does not refine and each ancestor
/// refines. A level is not drawn until its driver levels have decoded motion (see <see cref="IsLevelReady"/>).</para>
/// <para><b>Splat pose.</b> A static node uses its grid position and its stored rotation. A bound node uses
/// <see cref="TriangleFrame"/> on its posed driver triangle. When that triangle is degenerate, the node uses the frame of
/// the triangle at frame 0 (or the identity when that is degenerate too) and moves with corner a.</para>
/// </remarks>
public static class GaussianCachePoseReference
{
    /// <summary>The extent of a splat, in standard deviations, for the projected radius of the LOD test.</summary>
    public const float LodExtentSigma = 3.0f;

    /// <summary>The smallest camera distance for the projected radius. Nodes nearer than this distance use it.</summary>
    public const float MinLodDistance = 1e-6f;

    /// <summary>
    /// Computes the chosen nodes of a cache and their poses at a time, for a camera. The output is sorted by level, then
    /// by node. The method allocates; it is a reference for tests and tools, not for per-frame use.
    /// </summary>
    /// <param name="cache">The cache. Its decoded data selects the loaded nodes and the decoded frames.</param>
    /// <param name="timeSeconds">The playback time.</param>
    /// <param name="view">The transform from the space of the cache to view space (the model-view transform).</param>
    /// <param name="projection">The projection transform.</param>
    /// <param name="viewportSize">The size of the viewport in pixels. The LOD test uses the height.</param>
    /// <param name="lodThresholdPixels">A node refines when its projected 3σ radius is at least this number of pixels.</param>
    /// <param name="output">Receives the poses. The method clears it first.</param>
    public static void Compute(
        GaussianCache cache,
        float timeSeconds,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector2 viewportSize,
        float lodThresholdPixels,
        List<GaussianSplatPose> output)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();

        GaussianCacheHeader header = cache.Header;
        GaussianCacheRuntimeData runtime = cache.Runtime;
        Vector3[] driverPose = new Vector3[runtime.Frames.VertexCount];
        PoseDrivers(cache, timeSeconds, driverPose);

        int levelCount = runtime.LevelRecords.Length;
        bool[] ready = new bool[levelCount];
        for (int level = 0; level < levelCount; level++)
            ready[level] = IsLevelReady(runtime, level);

        Cut cut = new(
            runtime,
            ready,
            driverPose,
            new GridQuantizer(header.Bounds, header.GridBits),
            view,
            FocalPixels(projection, viewportSize.Y),
            SigmaScale(view),
            IsPerspective(projection),
            lodThresholdPixels);

        for (int level = 0; level < levelCount; level++)
        {
            if (!ready[level])
                continue;

            GaussianCacheSplatRecord[] records = runtime.LevelRecords[level];
            bool[] loaded = runtime.LevelNodeLoaded[level];
            for (int node = 0; node < records.Length; node++)
            {
                if (!loaded[node])
                    continue;

                ref readonly GaussianCacheSplatRecord record = ref records[node];
                cut.Pose(record, level, out Vector3 position, out Quaternion rotation);
                if (cut.Refines(record, level, position) || !cut.AncestorsRefine(level, node))
                    continue;

                output.Add(new GaussianSplatPose(
                    level,
                    node,
                    position,
                    rotation,
                    new Vector3(record.ScaleX, record.ScaleY, record.ScaleZ),
                    LinearColor(record.ColorRgba8, header.ColorScale)));
            }
        }
    }

    /// <summary>
    /// Computes the position of each driver vertex at a time, as the driver pose pass does. Vertices of a driver level
    /// whose motion is not decoded get NaN.
    /// </summary>
    /// <param name="cache">The cache.</param>
    /// <param name="timeSeconds">The playback time.</param>
    /// <param name="positions">Receives one position for each driver vertex.</param>
    /// <exception cref="ArgumentException"><paramref name="positions"/> is shorter than the driver vertex count.</exception>
    public static void PoseDrivers(GaussianCache cache, float timeSeconds, Span<Vector3> positions)
    {
        ArgumentNullException.ThrowIfNull(cache);
        GaussianCacheHeader header = cache.Header;
        DriverFrameStore frames = cache.Runtime.Frames;
        if (positions.Length < frames.VertexCount)
            throw new ArgumentException($"The span has {positions.Length} elements for {frames.VertexCount} driver vertices.", nameof(positions));

        GridQuantizer grid = new(header.Bounds, header.GridBits);
        float frame = FractionalFrame(header, timeSeconds);
        for (int level = 0; level < frames.DriverLevelCount; level++)
        {
            int first = frames.GetLevelFirstVertex(level);
            int count = frames.GetLevelVertexCount(level);
            int step = DriverStep(frames, level);
            if (step == 0)
            {
                positions.Slice(first, count).Fill(new Vector3(float.NaN));
                continue;
            }

            int a = Math.Clamp((int)MathF.Floor(frame / step) * step, 0, frames.FrameCount - 1);
            int b = TemporalLevels.NextNeighbor(a, step, frames.FrameCount, header.LoopMode);
            if (b < 0 || b >= frames.FrameCount)
                b = a;
            float weight = Math.Clamp((frame - a) / step, 0.0f, 1.0f);
            for (int vertex = first; vertex < first + count; vertex++)
                positions[vertex] = (StoredPosition(frames, grid, a, vertex) * (1.0f - weight)) + (StoredPosition(frames, grid, b, vertex) * weight);
        }
    }

    /// <summary>Maps a playback time to a fractional frame with the loop mode of the cache. A still cache gives 0.</summary>
    public static float FractionalFrame(GaussianCacheHeader header, float timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(header);
        int frameCount = header.FrameCount;
        float rate = header.FrameRate;
        if (frameCount <= 1 || !(rate > 0.0f) || !float.IsFinite(rate) || !float.IsFinite(timeSeconds))
            return 0.0f;

        switch (header.LoopMode)
        {
            case GaussianCacheLoopMode.Loop:
            {
                double period = frameCount / (double)rate;
                double wrapped = PositiveModulo(timeSeconds, period);
                float frame = (float)(wrapped * rate);
                // Rounding can give the frame count, which is frame 0 of the next cycle.
                return frame < frameCount ? frame : 0.0f;
            }

            case GaussianCacheLoopMode.PingPong:
            {
                double period = (frameCount - 1) / (double)rate;
                double folded = period - Math.Abs(PositiveModulo(timeSeconds, 2.0 * period) - period);
                return Math.Clamp((float)(folded * rate), 0.0f, frameCount - 1);
            }

            default:
                return Math.Clamp(timeSeconds * rate, 0.0f, frameCount - 1);
        }
    }

    /// <summary>
    /// Returns the frame step of a driver level: <c>8 >> DecodedTemporalLevel</c>. A level with no vertices of its own
    /// returns 1, because it has no motion to decode. A level whose T0 motion is not decoded returns 0.
    /// </summary>
    public static int DriverStep(DriverFrameStore frames, int driverLevel)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.GetLevelVertexCount(driverLevel) == 0)
            return 1;
        sbyte decoded = frames.DecodedTemporalLevel[driverLevel];
        return decoded < 0 ? 0 : TemporalLevels.Step((byte)decoded);
    }

    /// <summary>
    /// Returns true when a splat level can be drawn: every driver level up to it has decoded motion. Splat level k binds to
    /// driver level k, whose triangles use the vertices of driver levels 0 to k.
    /// </summary>
    public static bool IsLevelReady(GaussianCacheRuntimeData runtime, int level)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        DriverFrameStore frames = runtime.Frames;
        int last = Math.Min(level, frames.DriverLevelCount - 1);
        for (int driverLevel = 0; driverLevel <= last; driverLevel++)
        {
            if (DriverStep(frames, driverLevel) == 0)
                return false;
        }

        return true;
    }

    /// <summary>Returns the number of pixels that one unit covers at a distance of one unit: the Y scale of the projection times half the viewport height.</summary>
    public static float FocalPixels(in Matrix4x4 projection, float viewportHeight)
        => projection.M22 * viewportHeight * 0.5f;

    /// <summary>Returns the largest axis scale of a model-view transform. The LOD test scales the splat sigma by it.</summary>
    public static float SigmaScale(in Matrix4x4 view)
    {
        float x = new Vector3(view.M11, view.M12, view.M13).Length();
        float y = new Vector3(view.M21, view.M22, view.M23).Length();
        float z = new Vector3(view.M31, view.M32, view.M33).Length();
        return MathF.Max(x, MathF.Max(y, z));
    }

    /// <summary>Returns true for a perspective projection, whose W output depends on the view depth.</summary>
    public static bool IsPerspective(in Matrix4x4 projection)
        => projection.M34 != 0.0f;

    /// <summary>Returns the linear color times the color scale (XYZ) and the opacity (W) of an RGBA8 record color.</summary>
    public static Vector4 LinearColor(uint colorRgba8, float colorScale)
        => new(
            Srgb8.ToLinear((byte)colorRgba8) * colorScale,
            Srgb8.ToLinear((byte)(colorRgba8 >> 8)) * colorScale,
            Srgb8.ToLinear((byte)(colorRgba8 >> 16)) * colorScale,
            (byte)(colorRgba8 >> 24) / 255.0f);

    private static double PositiveModulo(double value, double period)
    {
        double result = value % period;
        return result < 0.0 ? result + period : result;
    }

    private static Vector3 StoredPosition(DriverFrameStore frames, GridQuantizer grid, int frame, int vertex)
    {
        int offset = ((frame * frames.VertexCount) + vertex) * 3;
        ushort[] positions = frames.Positions;
        return grid.Dequantize(positions[offset], positions[offset + 1], positions[offset + 2]);
    }

    /// <summary>The state of one cut: the posed drivers and the camera terms of the LOD test.</summary>
    private readonly struct Cut(
        GaussianCacheRuntimeData runtime,
        bool[] ready,
        Vector3[] driverPose,
        GridQuantizer grid,
        Matrix4x4 view,
        float focalPixels,
        float sigmaScale,
        bool perspective,
        float lodThresholdPixels)
    {
        public bool Refines(in GaussianCacheSplatRecord record, int level, Vector3 center)
        {
            if (level + 1 >= ready.Length || record.ChildCount == 0 || !ready[level + 1])
                return false;

            bool[] childLoaded = runtime.LevelNodeLoaded[level + 1];
            for (uint child = record.ChildStart; child < record.ChildStart + record.ChildCount; child++)
            {
                if (!childLoaded[child])
                    return false;
            }

            return ProjectedRadius(record, center) >= lodThresholdPixels;
        }

        public bool AncestorsRefine(int level, int node)
        {
            while (level > 0)
            {
                int parent = runtime.ParentIndex[level][node];
                if (parent < 0)
                    return false;
                level--;
                node = parent;
                ref readonly GaussianCacheSplatRecord record = ref runtime.LevelRecords[level][node];
                Pose(record, level, out Vector3 position, out _);
                if (!Refines(record, level, position))
                    return false;
            }

            return true;
        }

        public void Pose(in GaussianCacheSplatRecord record, int level, out Vector3 position, out Quaternion rotation)
        {
            Quaternion local = LocalRotation(record);
            if ((record.Flags & GaussianCacheSplatRecord.StaticFlag) != 0)
            {
                position = grid.Dequantize(record.A, record.B, record.C);
                rotation = local;
                return;
            }

            uint[] indices = runtime.DriverLevelIndices[level];
            uint ia = indices[record.Triangle * 3];
            uint ib = indices[record.Triangle * 3 + 1];
            uint ic = indices[record.Triangle * 3 + 2];
            Vector3 a = driverPose[ia];
            Vector3 b = driverPose[ib];
            Vector3 c = driverPose[ic];
            float u = PlaneCoordinate.Unpack(record.A);
            float v = PlaneCoordinate.Unpack(record.B);
            float h = (float)BitConverter.UInt16BitsToHalf(record.C);

            if (TriangleFrame.TryCompute(a, b, c, out Quaternion frame))
                position = TriangleFrame.Reconstruct(a, b, c, u, v, h);
            else
            {
                // The triangle is degenerate now. Use its rest frame and move with corner a.
                DriverFrameStore frames = runtime.Frames;
                Vector3 a0 = StoredPosition(frames, grid, 0, (int)ia);
                Vector3 b0 = StoredPosition(frames, grid, 0, (int)ib);
                Vector3 c0 = StoredPosition(frames, grid, 0, (int)ic);
                Vector3 offset = TriangleFrame.TryCompute(a0, b0, c0, out frame)
                    ? TriangleFrame.Reconstruct(a0, b0, c0, u, v, h) - a0
                    : (u * (b0 - a0)) + (v * (c0 - a0));
                position = a + offset;
            }

            rotation = Quaternion.Normalize(frame * local);
        }

        private float ProjectedRadius(in GaussianCacheSplatRecord record, Vector3 center)
        {
            float sigma = MathF.Max(record.ScaleX, MathF.Max(record.ScaleY, record.ScaleZ)) * sigmaScale;
            float radius = LodExtentSigma * sigma * focalPixels;
            if (perspective)
                radius /= MathF.Max(Vector3.Transform(center, view).Length(), MinLodDistance);
            return radius;
        }

        private static Quaternion LocalRotation(in GaussianCacheSplatRecord record)
        {
            Quaternion q = new(record.RotX, record.RotY, record.RotZ, record.RotW);
            return q.LengthSquared() > 0.0f ? Quaternion.Normalize(q) : Quaternion.Identity;
        }
    }
}
