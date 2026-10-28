using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Cache;

namespace XREngine.Rendering.Models.Gaussian.Baking.Views;

/// <summary>
/// Plans the camera of a preview loop: one pose for each video frame. The camera motion repeats with the loop, so the
/// pose after the last frame is the pose of frame 0 and the loop has no jump when it starts again.
/// </summary>
public static class LoopCameraPlanner
{
    /// <summary>The vertical field of view of every loop pose, in degrees.</summary>
    public const float VerticalFovDegrees = 40.0f;

    /// <summary>The loop length of a still avatar or item, in seconds.</summary>
    public const float StillPrefabSeconds = 8.0f;

    /// <summary>The loop length of a still world, in seconds.</summary>
    public const float StillWorldSeconds = 10.0f;

    /// <summary>The video frame rate of a still loop, in frames per second.</summary>
    public const float StillFramesPerSecond = 30.0f;

    /// <summary>
    /// The shortest loop of an animated avatar or item, in seconds. The loop repeats the clip a whole number of times
    /// until it is at least this long, and the camera orbits one time over the full loop.
    /// </summary>
    public const float MinTurntableSeconds = 6.0f;

    /// <summary>
    /// Chooses the length and the frame rate of the loop of a cache. The video frame rate is the stored frame rate of
    /// the cache, so video frame <c>i</c> shows the cache at <c>i / FrameRate</c> seconds.
    /// <list type="bullet">
    /// <item>A still cache (one frame) loops for <see cref="StillPrefabSeconds"/> (avatar or item) or
    /// <see cref="StillWorldSeconds"/> (world) at <see cref="StillFramesPerSecond"/>.</item>
    /// <item>The playback period of an animated <see cref="GaussianCacheLoopMode.Loop"/> or
    /// <see cref="GaussianCacheLoopMode.Once"/> cache is its clip: <see cref="GaussianCacheHeader.FrameCount"/> frames.
    /// An animated <see cref="GaussianCacheLoopMode.PingPong"/> cache plays forward and then back, because only that is
    /// a loop with no jump: 2 × (<see cref="GaussianCacheHeader.FrameCount"/> − 1) frames.</item>
    /// <item>The loop of an animated avatar or item repeats the playback period <c>k</c> times, with
    /// <c>k = ceil(MinTurntableSeconds / period)</c> (see <see cref="MinTurntableSeconds"/>). The loop of an animated
    /// world is one period. The loop is a whole number of periods, so the frame after the last frame is frame 0 of the
    /// clip again.</item>
    /// </list>
    /// </summary>
    /// <param name="kind">The kind of content.</param>
    /// <param name="header">The header of the cache.</param>
    public static LoopTiming ChooseTiming(GaussianBakeKind kind, GaussianCacheHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (header.FrameCount <= 1)
        {
            float seconds = kind switch
            {
                GaussianBakeKind.Avatar or GaussianBakeKind.Item => StillPrefabSeconds,
                GaussianBakeKind.World => StillWorldSeconds,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The bake kind is not defined."),
            };
            return new LoopTiming(seconds, StillFramesPerSecond, (int)MathF.Round(seconds * StillFramesPerSecond));
        }

        float rate = header.FrameRate;
        if (!float.IsFinite(rate) || rate <= 0.0f)
            throw new ArgumentException($"The frame rate of an animated cache must be more than 0, not {rate}.", nameof(header));

        int period = header.LoopMode == GaussianCacheLoopMode.PingPong
            ? 2 * (header.FrameCount - 1)
            : header.FrameCount;
        int repeats = kind is GaussianBakeKind.Avatar or GaussianBakeKind.Item
            ? Math.Max(1, (int)Math.Ceiling(MinTurntableSeconds * (double)rate / period))
            : 1;
        int frames = checked(period * repeats);
        return new LoopTiming((float)(frames / (double)rate), rate, frames);
    }

    /// <summary>
    /// Returns one camera pose for each video frame of a loop. Frame <c>i</c> is at <c>i × durationSeconds / frameCount</c>
    /// seconds. Every pose has a vertical field of view of <see cref="VerticalFovDegrees"/>.
    /// <list type="bullet">
    /// <item>With a path (<see cref="GaussianCacheCameraHints.Path"/>), the position is a linear interpolation and the
    /// rotation is a spherical interpolation between the two keys around the frame time. The time wraps over
    /// <paramref name="durationSeconds"/>: after the last key, the camera goes back to the first key, which it reaches at
    /// the first key time plus <paramref name="durationSeconds"/>.</item>
    /// <item>With no path, the camera orbits <see cref="GaussianCacheCameraHints.OrbitTarget"/> one time, at
    /// <see cref="GaussianCacheCameraHints.DefaultRadius"/> and <see cref="GaussianCacheCameraHints.DefaultPitchDegrees"/>,
    /// with yaw = <see cref="GaussianCacheCameraHints.DefaultYawDegrees"/> + 360° × i / <paramref name="frameCount"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="hints">The camera hints of the cache.</param>
    /// <param name="durationSeconds">The length of the loop, in seconds.</param>
    /// <param name="frameCount">The number of video frames in the loop.</param>
    public static BakeCameraPose[] Plan(GaussianCacheCameraHints hints, float durationSeconds, int frameCount)
    {
        Validate(hints, durationSeconds, frameCount);

        GaussianCacheCameraKey[] keys = SortKeys(hints.Path);
        BakeCameraPose[] poses = new BakeCameraPose[frameCount];
        for (int i = 0; i < frameCount; i++)
            poses[i] = Evaluate(hints, keys, durationSeconds, frameCount, i);
        return poses;
    }

    /// <summary>
    /// Returns the pose of one frame, with the rules of <see cref="Plan"/>. The frame index can be outside the loop:
    /// frame <paramref name="frameCount"/> gives the pose of frame 0.
    /// </summary>
    /// <param name="hints">The camera hints of the cache.</param>
    /// <param name="durationSeconds">The length of the loop, in seconds.</param>
    /// <param name="frameCount">The number of video frames in the loop.</param>
    /// <param name="frameIndex">The index of the frame.</param>
    public static BakeCameraPose PoseAt(GaussianCacheCameraHints hints, float durationSeconds, int frameCount, int frameIndex)
    {
        Validate(hints, durationSeconds, frameCount);
        return Evaluate(hints, SortKeys(hints.Path), durationSeconds, frameCount, frameIndex);
    }

    private static BakeCameraPose Evaluate(GaussianCacheCameraHints hints, GaussianCacheCameraKey[] keys, float durationSeconds, int frameCount, int frameIndex)
        => keys.Length > 0
            ? PathPose(keys, durationSeconds, frameIndex * (double)durationSeconds / frameCount)
            : OrbitPose(hints, frameIndex, frameCount);

    /// <summary>
    /// Returns an orbit pose. Yaw 0 puts the camera on the +Z side of the target, and a positive yaw turns it toward +X.
    /// A positive pitch puts the camera above the target. The camera looks at the target with +Y up.
    /// </summary>
    private static BakeCameraPose OrbitPose(GaussianCacheCameraHints hints, int frameIndex, int frameCount)
    {
        // The turn uses the frame index modulo the frame count, so frame frameCount is frame 0 exactly.
        double turn = PositiveModulo(frameIndex, frameCount) / (double)frameCount;
        double yawDegrees = PositiveModulo(hints.DefaultYawDegrees + (360.0 * turn), 360.0);
        float yaw = (float)(yawDegrees * Math.PI / 180.0);
        float pitch = hints.DefaultPitchDegrees * MathF.PI / 180.0f;

        // The pitch turns around the camera X axis first, and then the yaw turns around the world Y axis.
        // A negative angle around X tilts the −Z view direction down.
        Quaternion rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(yaw, -pitch, 0.0f));
        Vector3 offset = Vector3.Transform(new Vector3(0.0f, 0.0f, hints.DefaultRadius), rotation);
        return new BakeCameraPose(hints.OrbitTarget + offset, rotation, VerticalFovDegrees);
    }

    private static BakeCameraPose PathPose(GaussianCacheCameraKey[] keys, double durationSeconds, double timeSeconds)
    {
        if (keys.Length == 1)
            return new BakeCameraPose(keys[0].Position, Quaternion.Normalize(keys[0].Rotation), VerticalFovDegrees);

        double time = PositiveModulo(timeSeconds, durationSeconds);
        int last = keys.Length - 1;
        int from;
        int to;
        double fromTime;
        double toTime;
        if (time < keys[0].TimeSeconds)
        {
            // Before the first key: the segment from the last key of the previous loop.
            from = last;
            to = 0;
            fromTime = keys[last].TimeSeconds - durationSeconds;
            toTime = keys[0].TimeSeconds;
        }
        else if (time >= keys[last].TimeSeconds)
        {
            // After the last key: the segment to the first key of the next loop.
            from = last;
            to = 0;
            fromTime = keys[last].TimeSeconds;
            toTime = keys[0].TimeSeconds + durationSeconds;
        }
        else
        {
            from = 0;
            while (time >= keys[from + 1].TimeSeconds)
                from++;
            to = from + 1;
            fromTime = keys[from].TimeSeconds;
            toTime = keys[to].TimeSeconds;
        }

        double span = toTime - fromTime;
        float weight = span > 0.0 ? (float)Math.Clamp((time - fromTime) / span, 0.0, 1.0) : 1.0f;
        Vector3 position = Vector3.Lerp(keys[from].Position, keys[to].Position, weight);
        Quaternion rotation = Quaternion.Normalize(Quaternion.Slerp(
            Quaternion.Normalize(keys[from].Rotation),
            Quaternion.Normalize(keys[to].Rotation),
            weight));
        return new BakeCameraPose(position, rotation, VerticalFovDegrees);
    }

    /// <summary>Returns the keys in time order. Keys with the same time keep their order.</summary>
    private static GaussianCacheCameraKey[] SortKeys(GaussianCacheCameraKey[]? keys)
        => keys is null || keys.Length == 0 ? [] : [.. keys.OrderBy(key => key.TimeSeconds)];

    private static double PositiveModulo(double value, double period)
    {
        double result = value % period;
        return result < 0.0 ? result + period : result;
    }

    private static void Validate(GaussianCacheCameraHints hints, float durationSeconds, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(hints);
        if (!float.IsFinite(durationSeconds) || durationSeconds <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "The loop length must be more than 0 seconds.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        GaussianCacheCameraKey[]? keys = hints.Path;
        if (keys is not null && keys.Length > 0)
        {
            foreach (GaussianCacheCameraKey key in keys)
            {
                if (!float.IsFinite(key.TimeSeconds) || !IsFinite(key.Position) || !IsFinite(key.Rotation) || key.Rotation.LengthSquared() == 0.0f)
                    throw new ArgumentException("Each camera path key must have a finite time, a finite position, and a rotation that is not zero.", nameof(hints));
            }

            return;
        }

        if (!IsFinite(hints.OrbitTarget) || !float.IsFinite(hints.DefaultRadius) || hints.DefaultRadius <= 0.0f
            || !float.IsFinite(hints.DefaultYawDegrees) || !float.IsFinite(hints.DefaultPitchDegrees))
        {
            throw new ArgumentException("The orbit target, yaw, and pitch must be finite, and the default radius must be more than 0.", nameof(hints));
        }
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
