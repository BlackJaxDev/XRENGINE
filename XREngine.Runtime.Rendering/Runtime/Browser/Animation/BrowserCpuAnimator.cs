using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Reference-fixture idle/movement clips; this is not a second production animation state system.</summary>
public sealed class BrowserCpuAnimator
{
    private readonly BrowserSkeleton _skeleton;
    private readonly BrowserAnimationClip _idle;
    private readonly BrowserAnimationClip _moving;
    private readonly BrowserBoneTransform[] _pose;
    private readonly BrowserBoneTransform[] _movingPose;
    private readonly Matrix4x4[] _world;
    private readonly Matrix4x4[] _palette;
    private float _idleTime;
    private float _movingTime;

    public BrowserCpuAnimator(BrowserSkeleton skeleton, BrowserAnimationClip idle, BrowserAnimationClip moving)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(idle);
        ArgumentNullException.ThrowIfNull(moving);
        if (idle.BoneCount != skeleton.BoneCount || moving.BoneCount != skeleton.BoneCount)
            throw new ArgumentException("Animation clips must match the selected skeleton bone order and count.");
        _skeleton = skeleton;
        _idle = idle;
        _moving = moving;
        _pose = new BrowserBoneTransform[skeleton.BoneCount];
        _movingPose = new BrowserBoneTransform[skeleton.BoneCount];
        _world = new Matrix4x4[skeleton.BoneCount];
        _palette = new Matrix4x4[skeleton.BoneCount];
        Evaluate();
    }

    public BrowserSkeleton Skeleton => _skeleton;
    public float MovementBlend { get; private set; }
    public ReadOnlySpan<Matrix4x4> Palette => _palette;

    /// <summary>Advances after collision resolves movement. Pausing means no call; resumed clocks do not catch up stale time.</summary>
    public void Advance(float deltaSeconds, float movementSpeed)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds is < 0 or > 0.1f ||
            !float.IsFinite(movementSpeed) || movementSpeed is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "Animation requires a bounded simulation step and nonnegative movement speed.");
        float target = Math.Clamp(movementSpeed / 2.5f, 0, 1);
        MovementBlend += Math.Clamp(target - MovementBlend, -deltaSeconds * 6, deltaSeconds * 6);
        _idleTime = (_idleTime + deltaSeconds) % _idle.DurationSeconds;
        _movingTime = (_movingTime + deltaSeconds * Math.Clamp(movementSpeed / 2.5f, 0.25f, 2)) % _moving.DurationSeconds;
        Evaluate();
    }

    private void Evaluate()
    {
        _idle.Sample(_idleTime, _pose);
        _moving.Sample(_movingTime, _movingPose);
        for (int bone = 0; bone < _pose.Length; bone++)
            _pose[bone] = BrowserBoneTransform.Interpolate(_pose[bone], _movingPose[bone], MovementBlend);
        _skeleton.Evaluate(_pose, _world, _palette);
    }
}
