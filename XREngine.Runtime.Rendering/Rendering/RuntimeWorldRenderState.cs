using XREngine.Components;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Owns render publication state for the production world instance without
/// introducing a second public world-instance identity.
/// </summary>
public sealed class RuntimeWorldRenderState
{
    private readonly WorldAmbientSettingsAdapter _ambientSettings = new();

    public RuntimeWorldRenderState(IRuntimeRenderWorld world, VisualScene3D visualScene)
    {
        VisualScene = visualScene ?? throw new ArgumentNullException(nameof(visualScene));
        Lights = new Lights3DCollection(world);
    }

    public VisualScene3D VisualScene { get; }
    public Lights3DCollection Lights { get; }
    public IRuntimeAmbientSettings? AmbientSettings
        => _ambientSettings.Settings is null ? null : _ambientSettings;

    public void BindSettings(WorldSettings? settings)
        => _ambientSettings.Settings = settings;

    public void AddRenderable(IRuntimeRenderInfo3DRegistrationItem renderable)
    {
        if (renderable is RenderInfo3D renderInfo)
            VisualScene.AddRenderable(renderInfo);
    }

    public void RemoveRenderable(IRuntimeRenderInfo3DRegistrationItem renderable)
    {
        if (renderable is RenderInfo3D renderInfo)
            VisualScene.RemoveRenderable(renderInfo);
    }

    public void AddWorldObject(RuntimeWorldObjectBase worldObject)
    {
        if (worldObject is not IRenderable renderable)
            return;

        foreach (RenderInfo renderInfo in renderable.RenderedObjects)
            if (renderInfo is RenderInfo3D renderInfo3D)
                VisualScene.AddRenderable(renderInfo3D);
    }

    public void RemoveWorldObject(RuntimeWorldObjectBase worldObject)
    {
        if (worldObject is not IRenderable renderable)
            return;

        foreach (RenderInfo renderInfo in renderable.RenderedObjects)
            if (renderInfo is RenderInfo3D renderInfo3D)
                VisualScene.RemoveRenderable(renderInfo3D);
    }

    private sealed class WorldAmbientSettingsAdapter : IRuntimeAmbientSettings
    {
        public WorldSettings? Settings { get; set; }

        public ColorF3 AmbientLightColor
        {
            get => Settings?.AmbientLightColor ?? default;
            set
            {
                if (Settings is not null)
                    Settings.AmbientLightColor = value;
            }
        }

        public float AmbientLightIntensity
        {
            get => Settings?.AmbientLightIntensity ?? 0.0f;
            set
            {
                if (Settings is not null)
                    Settings.AmbientLightIntensity = value;
            }
        }
    }
}
