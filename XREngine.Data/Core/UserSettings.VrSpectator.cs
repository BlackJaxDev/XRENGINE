using System.ComponentModel;

namespace XREngine;

public partial class UserSettings
{
    private bool _vrSpectatorEnabled;
    private float _vrSpectatorDistance = 3.0f;
    private float _vrSpectatorHeight = 1.6f;
    private float _vrSpectatorShoulderOffset;
    private float _vrSpectatorAimHeight = 1.4f;
    private float _vrSpectatorFieldOfView = 60.0f;
    private float _vrSpectatorFollowSpeed = 12.0f;
    private uint _vrSpectatorWidth = 1920u;
    private uint _vrSpectatorHeightPixels = 1080u;
    private uint _vrSpectatorFramesPerSecond = 30u;

    [Category("VR Spectator")]
    public bool VrSpectatorEnabled
    {
        get => _vrSpectatorEnabled;
        set { if (SetField(ref _vrSpectatorEnabled, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public float VrSpectatorDistance
    {
        get => _vrSpectatorDistance;
        set { ValidateSpectator(value, 0.0f, 20.0f); if (SetField(ref _vrSpectatorDistance, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public float VrSpectatorHeight
    {
        get => _vrSpectatorHeight;
        set { ValidateSpectator(value, 0.0f, 10.0f); if (SetField(ref _vrSpectatorHeight, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public float VrSpectatorShoulderOffset
    {
        get => _vrSpectatorShoulderOffset;
        set { ValidateSpectator(value, -3.0f, 3.0f); if (SetField(ref _vrSpectatorShoulderOffset, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public float VrSpectatorAimHeight
    {
        get => _vrSpectatorAimHeight;
        set { ValidateSpectator(value, 0.0f, 10.0f); if (SetField(ref _vrSpectatorAimHeight, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public float VrSpectatorFieldOfView
    {
        get => _vrSpectatorFieldOfView;
        set { ValidateSpectator(value, 25.0f, 120.0f); if (SetField(ref _vrSpectatorFieldOfView, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public float VrSpectatorFollowSpeed
    {
        get => _vrSpectatorFollowSpeed;
        set { ValidateSpectator(value, 0.0f, 60.0f); if (SetField(ref _vrSpectatorFollowSpeed, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public uint VrSpectatorWidth
    {
        get => _vrSpectatorWidth;
        set { ValidateSpectatorPixels(value); if (SetField(ref _vrSpectatorWidth, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public uint VrSpectatorHeightPixels
    {
        get => _vrSpectatorHeightPixels;
        set { ValidateSpectatorPixels(value); if (SetField(ref _vrSpectatorHeightPixels, value)) MarkDirty(); }
    }

    [Category("VR Spectator")]
    public uint VrSpectatorFramesPerSecond
    {
        get => _vrSpectatorFramesPerSecond;
        set { if (value is < 1u or > 120u) throw new ArgumentOutOfRangeException(nameof(value)); if (SetField(ref _vrSpectatorFramesPerSecond, value)) MarkDirty(); }
    }

    private static void ValidateSpectator(float value, float minimum, float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static void ValidateSpectatorPixels(uint value)
    {
        if (value is < 64u or > 8192u)
            throw new ArgumentOutOfRangeException(nameof(value));
    }
}
