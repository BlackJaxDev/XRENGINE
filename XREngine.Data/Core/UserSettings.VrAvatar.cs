using System.ComponentModel;

namespace XREngine;

public partial class UserSettings
{
    private string? _vrAvatarPrefabPath;
    private bool _vrSpectatorEnabled;
    private int? _vrFirstPersonAvatarLayer;

    [Category("VR Avatar")]
    [Description("Optional configured humanoid prefab for a newly created VR pawn. No existing scene avatar is cloned or taken implicitly.")]
    public string? VrAvatarPrefabPath { get => _vrAvatarPrefabPath; set => SetField(ref _vrAvatarPrefabPath, value); }
    [Category("VR Spectator")]
    public bool VrSpectatorEnabled { get => _vrSpectatorEnabled; set => SetField(ref _vrSpectatorEnabled, value); }
    [Category("VR Spectator")]
    [Description("Optional render layer reserved exclusively for the local avatar, hidden only from headset eyes. Leave unset if the scene has no reserved layer.")]
    public int? VrFirstPersonAvatarLayer { get => _vrFirstPersonAvatarLayer; set => SetField(ref _vrFirstPersonAvatarLayer, value); }
}
