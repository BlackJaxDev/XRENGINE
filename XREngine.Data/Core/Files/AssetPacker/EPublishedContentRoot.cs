namespace XREngine.Core.Files;

/// <summary>The three archives a published runtime reads. Each root opens its archive once for its lifetime.</summary>
public enum EPublishedContentRoot
{
    /// <summary>Startup, user, editor-preference, build settings, and runtime metadata.</summary>
    Config = 0,
    /// <summary>Cooked game content.</summary>
    GameContent = 1,
    /// <summary>Cooked engine common assets such as shaders and default resources.</summary>
    CommonAssets = 2,
}
