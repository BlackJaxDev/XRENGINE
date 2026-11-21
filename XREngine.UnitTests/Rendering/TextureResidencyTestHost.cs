using System.IO;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// An isolated imported texture streaming manager for residency tests. It installs an OpenGL host,
/// registers the OpenGL streaming provider, and restores the host and the budget settings at dispose.
/// </summary>
internal sealed class TextureResidencyTestHost : IDisposable
{
    private readonly IRuntimeRenderingHostServices _previousServices;
    private readonly IDisposable _providerLease;
    private readonly RuntimeEngine.Rendering.EngineSettings _settings;
    private readonly int _maxTransitions;
    private readonly int _maxPromotions;
    private readonly int _maxPromotionMegabytes;
    private readonly int _promotionCooldownFrames;
    private readonly int _promotionFadeFrames;
    private readonly int _profileFadeFrames;

    public TextureResidencyTestHost()
    {
        _previousServices = RuntimeRenderingHostServices.Current;
        RuntimeRenderingHostServices.Current = TextureResidencyHostServicesProxy.Create(
            RuntimeRenderingBootstrap.CreateEngineHostServices(),
            out TextureResidencyHostServicesProxy host);
        Host = host;
        _providerLease = TextureStreamingBackendRegistry.Register(
            RuntimeGraphicsApiKind.OpenGL,
            OpenGlTextureStreamingBackendProvider.Instance);

        _settings = RuntimeEngine.Rendering.Settings;
        _maxTransitions = _settings.TextureStreamingCutsceneMaxTransitionsPerFrame;
        _maxPromotions = _settings.TextureStreamingCutsceneMaxPromotionsPerFrame;
        _maxPromotionMegabytes = _settings.TextureStreamingCutsceneMaxPromotionMegabytesPerFrame;
        _promotionCooldownFrames = _settings.TextureStreamingCutscenePromotionCooldownFrames;
        _promotionFadeFrames = _settings.TextureStreamingCutscenePromotionFadeFrames;
        _profileFadeFrames = _settings.TextureStreamingBudgetProfileFadeFrames;

        Manager = new ImportedTextureStreamingManager();
    }

    /// <summary>The host that controls the VRAM budget.</summary>
    public TextureResidencyHostServicesProxy Host { get; }

    /// <summary>The manager under test. It is not the shared instance.</summary>
    public ImportedTextureStreamingManager Manager { get; }

    /// <summary>The backend that records the scheduled loads.</summary>
    public FakeTextureStreamingResidencyBackend Backend { get; } = new();

    /// <summary>The engine rendering settings. Dispose restores the budget values.</summary>
    public RuntimeEngine.Rendering.EngineSettings Settings => _settings;

    /// <summary>
    /// Tracks a texture with a known source size and a published preview, and with no render usage.
    /// </summary>
    public (XRTexture2D Texture, ImportedTextureStreamingRecord Record) AddTexture(string name, uint sourceSize = 1024u, uint residentSize = 64u)
    {
        XRTexture2D texture = new()
        {
            Name = name,
            FilePath = Path.Combine(Path.GetTempPath(), $"texture-residency-{name}.png"),
        };
        ImportedTextureStreamingRecord record = Manager.GetOrCreateRecord(texture, texture.FilePath, deferAuthorityResolution: true);
        lock (record.Sync)
        {
            record.SourceWidth = sourceSize;
            record.SourceHeight = sourceSize;
            record.ResidentMaxDimension = residentSize;
            record.PreviewReady = true;
            record.Backend = Backend;
        }

        return (texture, record);
    }

    public void Dispose()
    {
        _settings.TextureStreamingCutsceneMaxTransitionsPerFrame = _maxTransitions;
        _settings.TextureStreamingCutsceneMaxPromotionsPerFrame = _maxPromotions;
        _settings.TextureStreamingCutsceneMaxPromotionMegabytesPerFrame = _maxPromotionMegabytes;
        _settings.TextureStreamingCutscenePromotionCooldownFrames = _promotionCooldownFrames;
        _settings.TextureStreamingCutscenePromotionFadeFrames = _promotionFadeFrames;
        _settings.TextureStreamingBudgetProfileFadeFrames = _profileFadeFrames;
        _providerLease.Dispose();
        RuntimeRenderingHostServices.Current = _previousServices;
    }
}
