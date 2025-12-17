using ImGuiNET;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Components.ParticleModules;
using XREngine.Data.Colors;
using XREngine.Scene.Components.Particles;
using XREngine.Scene.Components.Particles.Enums;
using XREngine.Scene.Components.Particles.Interfaces;

namespace XREngine.Editor.ComponentEditors;

/// <summary>
/// Custom ImGui editor for GPUParticleEmitterComponent.
/// Provides a visual interface for configuring particle emitters and their modules.
/// </summary>
public sealed class GPUParticleEmitterComponentEditor : IXRComponentEditor
{
    private const ImGuiColorEditFlags ColorPickerFlags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.HDR | ImGuiColorEditFlags.NoOptions;

    private sealed class EditorState
    {
        public int SelectedModuleIndex = -1;
        public bool ShowAddModulePopup = false;
        public string NewModuleFilter = string.Empty;
    }

    private static readonly ConditionalWeakTable<ParticleEmitterComponent, EditorState> s_states = new();

    // Available module types for the add module popup
    private static readonly (string Name, Type Type)[] AvailableSpawnModules =
    [
        ("Point Spawn", typeof(PointSpawnModule)),
        ("Sphere Spawn", typeof(SphereSpawnModule)),
        ("Box Spawn", typeof(BoxSpawnModule)),
        ("Cone Velocity", typeof(ConeVelocityModule)),
    ];

    private static readonly (string Name, Type Type)[] AvailableUpdateModules =
    [
        ("Gravity", typeof(GravityModule)),
        ("Drag", typeof(DragModule)),
        ("Color Over Lifetime", typeof(ColorOverLifetimeModule)),
        ("Size Over Lifetime", typeof(SizeOverLifetimeModule)),
    ];

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not ParticleEmitterComponent emitter)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(emitter, visited, "Particle Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        var state = s_states.GetValue(emitter, _ => new EditorState());

        ImGui.PushID(emitter.GetHashCode());

        DrawEmitterSection(emitter);
        DrawParticleDefaultsSection(emitter);
        DrawPhysicsSection(emitter);
        DrawRenderingSection(emitter);
        DrawModulesSection(emitter, state);
        DrawStatisticsSection(emitter);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawEmitterSection(ParticleEmitterComponent emitter)
    {
        if (!ImGui.CollapsingHeader("Emitter", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Max Particles
        int maxParticles = (int)emitter.MaxParticles;
        if (ImGui.DragInt("Max Particles", ref maxParticles, 100, 1, 10000000))
            emitter.MaxParticles = (uint)Math.Max(1, maxParticles);

        // Emission Rate
        float emissionRate = emitter.EmissionRate;
        if (ImGui.DragFloat("Emission Rate", ref emissionRate, 1.0f, 0.0f, 100000.0f, "%.1f particles/sec"))
            emitter.EmissionRate = MathF.Max(0.0f, emissionRate);

        // Is Emitting
        bool isEmitting = emitter.IsEmitting;
        if (ImGui.Checkbox("Is Emitting", ref isEmitting))
            emitter.IsEmitting = isEmitting;

        ImGui.SameLine();

        // Is Simulating
        bool isSimulating = emitter.IsSimulating;
        if (ImGui.Checkbox("Is Simulating", ref isSimulating))
            emitter.IsSimulating = isSimulating;

        ImGui.Spacing();

        // Control buttons
        if (ImGui.Button("Play"))
        {
            emitter.IsEmitting = true;
            emitter.IsSimulating = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Pause"))
        {
            emitter.IsSimulating = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            emitter.IsEmitting = false;
            emitter.IsSimulating = false;
        }
    }

    private static void DrawParticleDefaultsSection(ParticleEmitterComponent emitter)
    {
        if (!ImGui.CollapsingHeader("Particle Defaults", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Lifetime
        float lifetimeMin = emitter.LifetimeMin;
        float lifetimeMax = emitter.LifetimeMax;

        ImGui.TextUnformatted("Lifetime (seconds)");
        if (ImGui.DragFloatRange2("##Lifetime", ref lifetimeMin, ref lifetimeMax, 0.1f, 0.01f, 100.0f, "Min: %.2f", "Max: %.2f"))
        {
            emitter.LifetimeMin = MathF.Max(0.01f, lifetimeMin);
            emitter.LifetimeMax = MathF.Max(emitter.LifetimeMin, lifetimeMax);
        }

        // Initial Color
        Vector4 initialColor = new(emitter.InitialColor.R, emitter.InitialColor.G, emitter.InitialColor.B, emitter.InitialColor.A);
        if (ImGui.ColorEdit4("Initial Color", ref initialColor, ColorPickerFlags))
            emitter.InitialColor = new ColorF4(initialColor.X, initialColor.Y, initialColor.Z, initialColor.W);

        // Scale
        Vector3 scaleMin = emitter.ScaleMin;
        Vector3 scaleMax = emitter.ScaleMax;

        ImGui.TextUnformatted("Scale Range");
        if (ImGui.DragFloat3("Scale Min", ref scaleMin, 0.01f, 0.001f, 100.0f, "%.3f"))
            emitter.ScaleMin = Vector3.Max(new Vector3(0.001f), scaleMin);

        if (ImGui.DragFloat3("Scale Max", ref scaleMax, 0.01f, 0.001f, 100.0f, "%.3f"))
            emitter.ScaleMax = Vector3.Max(emitter.ScaleMin, scaleMax);
    }

    private static void DrawPhysicsSection(ParticleEmitterComponent emitter)
    {
        if (!ImGui.CollapsingHeader("Physics"))
            return;

        Vector3 gravity = emitter.Gravity;
        if (ImGui.DragFloat3("Gravity", ref gravity, 0.1f, -1000.0f, 1000.0f, "%.2f"))
            emitter.Gravity = gravity;

        // Preset gravity buttons
        if (ImGui.Button("Earth"))
            emitter.Gravity = new Vector3(0, -9.81f, 0);
        ImGui.SameLine();
        if (ImGui.Button("Moon"))
            emitter.Gravity = new Vector3(0, -1.62f, 0);
        ImGui.SameLine();
        if (ImGui.Button("Zero-G"))
            emitter.Gravity = Vector3.Zero;
        ImGui.SameLine();
        if (ImGui.Button("Up"))
            emitter.Gravity = new Vector3(0, 9.81f, 0);
    }

    private static void DrawRenderingSection(ParticleEmitterComponent emitter)
    {
        if (!ImGui.CollapsingHeader("Rendering"))
            return;

        // Billboard Mode
        int billboardMode = (int)emitter.BillboardMode;
        string[] billboardModes = ["View Facing", "View Facing Vertical", "Velocity Aligned", "Stretched Billboard", "World Space", "Local Space"];
        if (ImGui.Combo("Billboard Mode", ref billboardMode, billboardModes, billboardModes.Length))
            emitter.BillboardMode = (EParticleBillboardMode)billboardMode;

        // Blend Mode
        int blendMode = (int)emitter.BlendMode;
        string[] blendModes = ["Alpha Blend", "Additive", "Soft Additive", "Multiply", "Premultiplied"];
        if (ImGui.Combo("Blend Mode", ref blendMode, blendModes, blendModes.Length))
            emitter.BlendMode = (EParticleBlendMode)blendMode;

        // Local Bounds
        var bounds = emitter.LocalBounds;
        Vector3 boundsMin = bounds.Min;
        Vector3 boundsMax = bounds.Max;

        ImGui.TextUnformatted("Culling Bounds");
        bool boundsChanged = false;
        if (ImGui.DragFloat3("Bounds Min", ref boundsMin, 1.0f, -10000.0f, 10000.0f, "%.1f"))
            boundsChanged = true;
        if (ImGui.DragFloat3("Bounds Max", ref boundsMax, 1.0f, -10000.0f, 10000.0f, "%.1f"))
            boundsChanged = true;

        if (boundsChanged)
            emitter.LocalBounds = new Data.Geometry.AABB(boundsMin, boundsMax);

        // Material preview would go here
        ImGui.Spacing();
        if (emitter.Material != null)
        {
            ImGui.TextUnformatted($"Material: {emitter.Material.Name ?? "<unnamed>"}");
        }
        else
        {
            ImGui.TextDisabled("No material assigned (using default)");
        }
    }

    private static void DrawModulesSection(ParticleEmitterComponent emitter, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Modules", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var modules = emitter.Modules;

        // Module list
        ImGui.BeginChild("ModuleList", new Vector2(0, 200), ImGuiChildFlags.Border);

        for (int i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            ImGui.PushID(i);

            bool isSelected = state.SelectedModuleIndex == i;

            // Module header with enable checkbox
            bool enabled = module.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled))
                module.Enabled = enabled;

            ImGui.SameLine();

            // Selectable module name
            string moduleLabel = $"{module.ModuleName} ({module.GetType().Name})";
            if (ImGui.Selectable(moduleLabel, isSelected))
                state.SelectedModuleIndex = i;

            // Context menu
            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem("Remove"))
                {
                    emitter.RemoveModule(module);
                    if (state.SelectedModuleIndex >= modules.Count)
                        state.SelectedModuleIndex = modules.Count - 1;
                }
                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        ImGui.EndChild();

        // Add module button
        if (ImGui.Button("Add Module..."))
        {
            state.ShowAddModulePopup = true;
            ImGui.OpenPopup("AddModulePopup");
        }

        DrawAddModulePopup(emitter, state);

        // Selected module properties
        if (state.SelectedModuleIndex >= 0 && state.SelectedModuleIndex < modules.Count)
        {
            ImGui.Separator();
            DrawModuleProperties(modules[state.SelectedModuleIndex]);
        }
    }

    private static void DrawAddModulePopup(ParticleEmitterComponent emitter, EditorState state)
    {
        if (!ImGui.BeginPopup("AddModulePopup"))
            return;

        ImGui.TextUnformatted("Add Module");
        ImGui.Separator();

        // Filter
        ImGui.InputTextWithHint("##Filter", "Search...", ref state.NewModuleFilter, 256);

        string filter = state.NewModuleFilter.ToLowerInvariant();

        // Spawn modules
        ImGui.TextDisabled("Spawn Modules");
        foreach (var (name, type) in AvailableSpawnModules)
        {
            if (!string.IsNullOrEmpty(filter) && !name.ToLowerInvariant().Contains(filter))
                continue;

            if (ImGui.MenuItem(name))
            {
                if (Activator.CreateInstance(type) is IParticleModule newModule)
                    emitter.AddModule(newModule);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.Separator();

        // Update modules
        ImGui.TextDisabled("Update Modules");
        foreach (var (name, type) in AvailableUpdateModules)
        {
            if (!string.IsNullOrEmpty(filter) && !name.ToLowerInvariant().Contains(filter))
                continue;

            if (ImGui.MenuItem(name))
            {
                if (Activator.CreateInstance(type) is IParticleModule newModule)
                    emitter.AddModule(newModule);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.EndPopup();
    }

    private static void DrawModuleProperties(IParticleModule module)
    {
        ImGui.TextUnformatted($"Module: {module.ModuleName}");
        ImGui.TextDisabled($"Priority: {module.Priority}");
        ImGui.Spacing();

        switch (module)
        {
            case GravityModule gravity:
                DrawGravityModuleProperties(gravity);
                break;
            case DragModule drag:
                DrawDragModuleProperties(drag);
                break;
            case ColorOverLifetimeModule colorModule:
                DrawColorOverLifetimeProperties(colorModule);
                break;
            case SizeOverLifetimeModule sizeModule:
                DrawSizeOverLifetimeProperties(sizeModule);
                break;
            case SphereSpawnModule sphere:
                DrawSphereSpawnProperties(sphere);
                break;
            case BoxSpawnModule box:
                DrawBoxSpawnProperties(box);
                break;
            case ConeVelocityModule cone:
                DrawConeVelocityProperties(cone);
                break;
            case PointSpawnModule:
                ImGui.TextDisabled("No configurable properties.");
                break;
            default:
                ImGui.TextDisabled("Custom module - use default property editor.");
                break;
        }
    }

    private static void DrawGravityModuleProperties(GravityModule module)
    {
        Vector3 gravity = module.Gravity;
        if (ImGui.DragFloat3("Gravity", ref gravity, 0.1f, -100.0f, 100.0f, "%.2f"))
            module.Gravity = gravity;
    }

    private static void DrawDragModuleProperties(DragModule module)
    {
        float drag = module.Drag;
        if (ImGui.SliderFloat("Drag", ref drag, 0.0f, 1.0f, "%.3f"))
            module.Drag = drag;
    }

    private static void DrawColorOverLifetimeProperties(ColorOverLifetimeModule module)
    {
        Vector4 startColor = new(module.StartColor.R, module.StartColor.G, module.StartColor.B, module.StartColor.A);
        if (ImGui.ColorEdit4("Start Color", ref startColor, ColorPickerFlags))
            module.StartColor = new ColorF4(startColor.X, startColor.Y, startColor.Z, startColor.W);

        Vector4 endColor = new(module.EndColor.R, module.EndColor.G, module.EndColor.B, module.EndColor.A);
        if (ImGui.ColorEdit4("End Color", ref endColor, ColorPickerFlags))
            module.EndColor = new ColorF4(endColor.X, endColor.Y, endColor.Z, endColor.W);

        // Preview gradient
        ImGui.TextUnformatted("Preview:");
        var drawList = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetCursorScreenPos();
        Vector2 size = new(ImGui.GetContentRegionAvail().X, 20);

        for (int i = 0; i < (int)size.X; i++)
        {
            float t = i / size.X;
            Vector4 c = Vector4.Lerp(startColor, endColor, t);
            uint color = ImGui.ColorConvertFloat4ToU32(c);
            drawList.AddLine(new Vector2(pos.X + i, pos.Y), new Vector2(pos.X + i, pos.Y + size.Y), color);
        }
        ImGui.Dummy(size);
    }

    private static void DrawSizeOverLifetimeProperties(SizeOverLifetimeModule module)
    {
        float startSize = module.StartSize;
        if (ImGui.DragFloat("Start Size", ref startSize, 0.01f, 0.0f, 10.0f, "%.3f"))
            module.StartSize = MathF.Max(0.0f, startSize);

        float endSize = module.EndSize;
        if (ImGui.DragFloat("End Size", ref endSize, 0.01f, 0.0f, 10.0f, "%.3f"))
            module.EndSize = MathF.Max(0.0f, endSize);
    }

    private static void DrawSphereSpawnProperties(SphereSpawnModule module)
    {
        float radius = module.Radius;
        if (ImGui.DragFloat("Radius", ref radius, 0.1f, 0.0f, 1000.0f, "%.2f"))
            module.Radius = MathF.Max(0.0f, radius);

        bool surfaceOnly = module.SurfaceOnly;
        if (ImGui.Checkbox("Surface Only", ref surfaceOnly))
            module.SurfaceOnly = surfaceOnly;
    }

    private static void DrawBoxSpawnProperties(BoxSpawnModule module)
    {
        Vector3 extents = module.HalfExtents;
        if (ImGui.DragFloat3("Half Extents", ref extents, 0.1f, 0.0f, 1000.0f, "%.2f"))
            module.HalfExtents = Vector3.Max(Vector3.Zero, extents);
    }

    private static void DrawConeVelocityProperties(ConeVelocityModule module)
    {
        float angle = module.ConeAngle;
        if (ImGui.SliderFloat("Cone Angle", ref angle, 0.0f, 180.0f, "%.1f°"))
            module.ConeAngle = angle;

        Vector2 speedRange = module.SpeedRange;
        if (ImGui.DragFloatRange2("Speed", ref speedRange.X, ref speedRange.Y, 0.1f, 0.0f, 1000.0f, "Min: %.1f", "Max: %.1f"))
            module.SpeedRange = new Vector2(MathF.Max(0, speedRange.X), MathF.Max(speedRange.X, speedRange.Y));
    }

    private static void DrawStatisticsSection(ParticleEmitterComponent emitter)
    {
        if (!ImGui.CollapsingHeader("Statistics"))
            return;

        ImGui.TextUnformatted($"Alive Particles: {emitter.AliveParticleCount:N0}");
        ImGui.TextUnformatted($"Max Particles: {emitter.MaxParticles:N0}");

        float usage = emitter.MaxParticles > 0 ? (float)emitter.AliveParticleCount / emitter.MaxParticles : 0;
        ImGui.ProgressBar(usage, new Vector2(-1, 0), $"{usage:P1}");

        ImGui.TextDisabled($"Modules: {emitter.Modules.Count}");
    }
}
