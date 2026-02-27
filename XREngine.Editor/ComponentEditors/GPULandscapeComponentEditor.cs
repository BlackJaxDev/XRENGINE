using ImGuiNET;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Scene.Components.Landscape;
using XREngine.Scene.Components.Landscape.Interfaces;
using XREngine.Scene.Components.Landscape.TerrainModules;

namespace XREngine.Editor.ComponentEditors;

/// <summary>
/// Custom ImGui editor for GPULandscapeComponent.
/// Provides a visual interface for configuring terrain, LOD, and material layers.
/// </summary>
public sealed class GPULandscapeComponentEditor : IXRComponentEditor
{
    private const ImGuiColorEditFlags ColorPickerFlags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoOptions;

    private sealed class EditorState
    {
        public int SelectedLayerIndex = -1;
        public int SelectedModuleIndex = -1;
        public bool ShowAddLayerPopup = false;
        public bool ShowAddModulePopup = false;
        public string NewItemFilter = string.Empty;
        public ETerrainBrushMode BrushMode = ETerrainBrushMode.Raise;
        public float BrushRadius = 10.0f;
        public float BrushStrength = 1.0f;
        public float BrushFalloff = 0.5f;
    }

    private enum ETerrainBrushMode
    {
        Raise,
        Lower,
        Flatten,
        Smooth,
        Paint
    }

    private static readonly ConditionalWeakTable<LandscapeComponent, EditorState> s_states = new();

    // Available module types
    private static readonly (string Name, Type Type)[] AvailableHeightModules =
    [
        ("Heightmap", typeof(HeightmapModule)),
        ("Procedural Noise", typeof(ProceduralNoiseModule)),
    ];

    private static readonly (string Name, Type Type)[] AvailableSplatModules =
    [
        ("Slope-Based Splat", typeof(SlopeSplatModule)),
        ("Height-Based Splat", typeof(HeightSplatModule)),
    ];

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not LandscapeComponent landscape)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(landscape, visited, "Landscape Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        var state = s_states.GetValue(landscape, _ => new EditorState());

        ImGui.PushID(landscape.GetHashCode());

        DrawTerrainSizeSection(landscape);
        DrawHeightmapSection(landscape);
        DrawLODSection(landscape);
        DrawLayersSection(landscape, state);
        DrawModulesSection(landscape, state);
        DrawBrushSection(landscape, state);
        DrawStatisticsSection(landscape);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawTerrainSizeSection(LandscapeComponent landscape)
    {
        if (!ImGui.CollapsingHeader("Terrain Size", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        float terrainSize = landscape.TerrainSize;
        if (ImGui.DragFloat("Terrain Size", ref terrainSize, 10.0f, 1.0f, 100000.0f, "%.1f"))
            landscape.TerrainSize = MathF.Max(1.0f, terrainSize);

        float minHeight = landscape.MinHeight;
        if (ImGui.DragFloat("Min Height", ref minHeight, 0.1f, -10000.0f, landscape.MaxHeight, "%.1f"))
            landscape.MinHeight = MathF.Min(minHeight, landscape.MaxHeight);

        float maxHeight = landscape.MaxHeight;
        if (ImGui.DragFloat("Max Height", ref maxHeight, 0.1f, landscape.MinHeight, 10000.0f, "%.1f"))
            landscape.MaxHeight = MathF.Max(maxHeight, landscape.MinHeight);

        ImGui.Spacing();

        // Quick terrain size presets
        ImGui.TextUnformatted("Presets:");
        if (ImGui.Button("256"))
            landscape.TerrainSize = 256.0f;
        ImGui.SameLine();
        if (ImGui.Button("512"))
            landscape.TerrainSize = 512.0f;
        ImGui.SameLine();
        if (ImGui.Button("1024"))
            landscape.TerrainSize = 1024.0f;
        ImGui.SameLine();
        if (ImGui.Button("4096"))
            landscape.TerrainSize = 4096.0f;
    }

    private static void DrawHeightmapSection(LandscapeComponent landscape)
    {
        if (!ImGui.CollapsingHeader("Heightmap", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        int heightmapRes = (int)landscape.HeightmapResolution;
        if (ImGui.DragInt("Resolution", ref heightmapRes, 16, 17, 8193, "%d"))
        {
            // Ensure power of 2 + 1
            int nearestPow2 = (int)Math.Pow(2, Math.Round(Math.Log2(heightmapRes - 1)));
            landscape.HeightmapResolution = (uint)(nearestPow2 + 1);
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"(Actual: {landscape.HeightmapResolution})");

        // Resolution presets
        if (ImGui.Button("129##res"))
            landscape.HeightmapResolution = 129;
        ImGui.SameLine();
        if (ImGui.Button("257##res"))
            landscape.HeightmapResolution = 257;
        ImGui.SameLine();
        if (ImGui.Button("513##res"))
            landscape.HeightmapResolution = 513;
        ImGui.SameLine();
        if (ImGui.Button("1025##res"))
            landscape.HeightmapResolution = 1025;
        ImGui.SameLine();
        if (ImGui.Button("2049##res"))
            landscape.HeightmapResolution = 2049;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Import/Export buttons
        if (ImGui.Button("Import Heightmap..."))
        {
            // TODO: File dialog to import heightmap
        }
        ImGui.SameLine();
        if (ImGui.Button("Export Heightmap..."))
        {
            // TODO: File dialog to export heightmap
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            // TODO: Clear heightmap to flat
        }
    }

    private static void DrawLODSection(LandscapeComponent landscape)
    {
        if (!ImGui.CollapsingHeader("Level of Detail"))
            return;

        float lodDistance = landscape.LOD0Distance;
        if (ImGui.DragFloat("LOD 0 Distance", ref lodDistance, 1.0f, 1.0f, 1000.0f, "%.1f"))
            landscape.LOD0Distance = MathF.Max(1.0f, lodDistance);

        float lodMultiplier = landscape.LODDistanceMultiplier;
        if (ImGui.DragFloat("LOD Distance Multiplier", ref lodMultiplier, 0.1f, 1.0f, 10.0f, "%.2f"))
            landscape.LODDistanceMultiplier = MathF.Max(1.0f, lodMultiplier);

        int chunkCount = (int)landscape.ChunkCount;
        if (ImGui.DragInt("Chunk Count", ref chunkCount, 1, 1, 64))
            landscape.ChunkCount = (uint)Math.Max(1, chunkCount);

        bool enableMorphing = landscape.EnableMorphing;
        if (ImGui.Checkbox("Enable Morphing", ref enableMorphing))
            landscape.EnableMorphing = enableMorphing;

        if (enableMorphing)
        {
            float morphStart = landscape.MorphStartRatio;
            if (ImGui.SliderFloat("Morph Start Ratio", ref morphStart, 0.0f, 1.0f, "%.2f"))
                landscape.MorphStartRatio = morphStart;
        }

        ImGui.Spacing();

        // LOD visualization
        ImGui.TextUnformatted("LOD Distances:");
        for (int i = 0; i < 6; i++)
        {
            float distance = lodDistance * MathF.Pow(lodMultiplier, i);
            ImGui.TextDisabled($"  LOD {i}: < {distance:F0}m");
        }
    }

    private static void DrawLayersSection(LandscapeComponent landscape, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Material Layers", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var layers = landscape.Layers;

        // Layer list
        ImGui.BeginChild("LayerList", new Vector2(0, 150), ImGuiChildFlags.Border);

        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            ImGui.PushID(i);

            bool isSelected = state.SelectedLayerIndex == i;

            // Layer color preview
            Vector4 tint = new(layer.Tint.R, layer.Tint.G, layer.Tint.B, 1.0f);
            ImGui.ColorButton("##tint", tint, ImGuiColorEditFlags.NoTooltip, new Vector2(16, 16));
            ImGui.SameLine();

            // Selectable layer name
            if (ImGui.Selectable(layer.Name ?? $"Layer {i}", isSelected))
                state.SelectedLayerIndex = i;

            // Context menu
            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem("Move Up") && i > 0)
                {
                    landscape.MoveLayerUp(i);
                    state.SelectedLayerIndex = i - 1;
                }
                if (ImGui.MenuItem("Move Down") && i < layers.Count - 1)
                {
                    landscape.MoveLayerDown(i);
                    state.SelectedLayerIndex = i + 1;
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Remove"))
                {
                    landscape.RemoveLayer(layer);
                    if (state.SelectedLayerIndex >= layers.Count)
                        state.SelectedLayerIndex = layers.Count - 1;
                }
                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        ImGui.EndChild();

        // Add layer button
        if (ImGui.Button("Add Layer"))
        {
            var newLayer = new TerrainLayer { Name = $"Layer {layers.Count}" };
            landscape.AddLayer(newLayer);
            state.SelectedLayerIndex = layers.Count - 1;
        }

        // Selected layer properties
        if (state.SelectedLayerIndex >= 0 && state.SelectedLayerIndex < layers.Count)
        {
            ImGui.Separator();
            DrawLayerProperties(layers[state.SelectedLayerIndex]);
        }
    }

    private static void DrawLayerProperties(TerrainLayer layer)
    {
        ImGui.TextUnformatted($"Layer: {layer.Name}");
        ImGui.Spacing();

        // Name
        string name = layer.Name ?? string.Empty;
        if (ImGui.InputText("Name", ref name, 256))
            layer.Name = name;
        ImGuiUndoHelper.TrackDragUndo("Layer Name", layer);

        // Tint
        Vector3 tint = new(layer.Tint.R, layer.Tint.G, layer.Tint.B);
        if (ImGui.ColorEdit3("Tint", ref tint, ColorPickerFlags))
            layer.Tint = new ColorF4(tint.X, tint.Y, tint.Z, 1.0f);
        ImGuiUndoHelper.TrackDragUndo("Layer Tint", layer);

        // UV Tiling and Offset
        Vector2 tiling = layer.Tiling;
        if (ImGui.DragFloat2("UV Tiling", ref tiling, 0.01f, 0.001f, 100.0f, "%.3f"))
            layer.Tiling = Vector2.Max(new Vector2(0.001f), tiling);
        ImGuiUndoHelper.TrackDragUndo("Layer UV Tiling", layer);

        Vector2 offset = layer.Offset;
        if (ImGui.DragFloat2("UV Offset", ref offset, 0.01f, -100.0f, 100.0f, "%.3f"))
            layer.Offset = offset;
        ImGuiUndoHelper.TrackDragUndo("Layer UV Offset", layer);

        // Metallic/Roughness
        float metallic = layer.Metallic;
        if (ImGui.SliderFloat("Metallic", ref metallic, 0.0f, 1.0f, "%.2f"))
            layer.Metallic = metallic;
        ImGuiUndoHelper.TrackDragUndo("Layer Metallic", layer);

        float roughness = layer.Roughness;
        if (ImGui.SliderFloat("Roughness", ref roughness, 0.0f, 1.0f, "%.2f"))
            layer.Roughness = roughness;
        ImGuiUndoHelper.TrackDragUndo("Layer Roughness", layer);

        // Normal Scale
        float normalStrength = layer.NormalStrength;
        if (ImGui.DragFloat("Normal Strength", ref normalStrength, 0.01f, 0.0f, 2.0f, "%.2f"))
            layer.NormalStrength = MathF.Max(0.0f, normalStrength);
        ImGuiUndoHelper.TrackDragUndo("Layer Normal Strength", layer);

        // Height/Parallax strength
        float heightStrength = layer.HeightStrength;
        if (ImGui.DragFloat("Height Strength", ref heightStrength, 0.001f, 0.0f, 0.5f, "%.3f"))
            layer.HeightStrength = MathF.Max(0.0f, heightStrength);
        ImGuiUndoHelper.TrackDragUndo("Layer Height Strength", layer);

        ImGui.Spacing();
        ImGui.TextDisabled("Textures:");

        // Texture slots (display only for now)
        DrawTextureSlot("Diffuse", layer.DiffuseTexture);
        DrawTextureSlot("Normal", layer.NormalTexture);
        DrawTextureSlot("Roughness", layer.RoughnessTexture);
        DrawTextureSlot("Height", layer.HeightTexture);
        DrawTextureSlot("Metallic", layer.MetallicTexture);
        DrawTextureSlot("AO", layer.AOTexture);
    }

    private static void DrawTextureSlot(string label, XREngine.Rendering.XRTexture2D? texture)
    {
        ImGui.TextUnformatted($"  {label}: ");
        ImGui.SameLine();
        if (texture != null)
            ImGui.TextUnformatted(texture.Name ?? "<unnamed>");
        else
            ImGui.TextDisabled("<none>");
    }

    private static void DrawModulesSection(LandscapeComponent landscape, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Procedural Modules"))
            return;

        var modules = landscape.Modules;

        // Module list
        ImGui.BeginChild("ModuleList", new Vector2(0, 150), ImGuiChildFlags.Border);

        for (int i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            ImGui.PushID($"mod{i}");

            bool isSelected = state.SelectedModuleIndex == i;

            // Module enable checkbox
            bool enabled = module.Enabled;
            if (ImGui.Checkbox("##ModEnabled", ref enabled))
            {
                using var _ = Undo.TrackChange("Module Enabled", module);
                module.Enabled = enabled;
            }

            ImGui.SameLine();

            // Icon based on type
            string icon = module switch
            {
                ITerrainHeightModule => "[H]",
                ITerrainSplatModule => "[S]",
                ITerrainDetailModule => "[D]",
                _ => "[?]"
            };
            ImGui.TextDisabled(icon);
            ImGui.SameLine();

            // Selectable module name
            if (ImGui.Selectable(module.ModuleName, isSelected))
                state.SelectedModuleIndex = i;

            // Context menu
            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem("Remove"))
                {
                    landscape.RemoveModule(module);
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
            ImGui.OpenPopup("AddTerrainModulePopup");
        }

        DrawAddModulePopup(landscape, state);

        // Selected module properties
        if (state.SelectedModuleIndex >= 0 && state.SelectedModuleIndex < modules.Count)
        {
            ImGui.Separator();
            DrawModuleProperties(modules[state.SelectedModuleIndex]);
        }
    }

    private static void DrawAddModulePopup(LandscapeComponent landscape, EditorState state)
    {
        if (!ImGui.BeginPopup("AddTerrainModulePopup"))
            return;

        ImGui.TextUnformatted("Add Module");
        ImGui.Separator();

        ImGui.InputTextWithHint("##Filter", "Search...", ref state.NewItemFilter, 256);

        string filter = state.NewItemFilter.ToLowerInvariant();

        // Height modules
        ImGui.TextDisabled("Height Modules");
        foreach (var (name, type) in AvailableHeightModules)
        {
            if (!string.IsNullOrEmpty(filter) && !name.ToLowerInvariant().Contains(filter))
                continue;

            if (ImGui.MenuItem(name))
            {
                if (Activator.CreateInstance(type) is ITerrainModule newModule)
                    landscape.AddModule(newModule);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.Separator();

        // Splat modules
        ImGui.TextDisabled("Splat Modules");
        foreach (var (name, type) in AvailableSplatModules)
        {
            if (!string.IsNullOrEmpty(filter) && !name.ToLowerInvariant().Contains(filter))
                continue;

            if (ImGui.MenuItem(name))
            {
                if (Activator.CreateInstance(type) is ITerrainModule newModule)
                    landscape.AddModule(newModule);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.EndPopup();
    }

    private static void DrawModuleProperties(ITerrainModule module)
    {
        ImGui.TextUnformatted($"Module: {module.ModuleName}");
        ImGui.TextDisabled($"Priority: {module.Priority}");
        ImGui.Spacing();

        switch (module)
        {
            case ProceduralNoiseModule noise:
                DrawProceduralNoiseProperties(noise);
                break;
            case SlopeSplatModule slopeSplat:
                DrawSlopeSplatProperties(slopeSplat);
                break;
            case HeightSplatModule heightSplat:
                DrawHeightSplatProperties(heightSplat);
                break;
            case HeightmapModule:
                ImGui.TextDisabled("Uses external heightmap texture.");
                break;
            default:
                ImGui.TextDisabled("Custom module - use default property editor.");
                break;
        }
    }

    private static void DrawProceduralNoiseProperties(ProceduralNoiseModule module)
    {
        float frequency = module.Frequency;
        if (ImGui.DragFloat("Frequency", ref frequency, 0.001f, 0.0001f, 1.0f, "%.4f"))
            module.Frequency = MathF.Max(0.0001f, frequency);

        int octaves = module.Octaves;
        if (ImGui.SliderInt("Octaves", ref octaves, 1, 16))
            module.Octaves = octaves;

        float persistence = module.Persistence;
        if (ImGui.SliderFloat("Persistence", ref persistence, 0.0f, 1.0f, "%.3f"))
            module.Persistence = persistence;

        float lacunarity = module.Lacunarity;
        if (ImGui.DragFloat("Lacunarity", ref lacunarity, 0.01f, 1.0f, 4.0f, "%.3f"))
            module.Lacunarity = MathF.Max(1.0f, lacunarity);
    }

    private static void DrawSlopeSplatProperties(SlopeSplatModule module)
    {
        float cliffAngle = module.CliffAngle;
        if (ImGui.SliderFloat("Cliff Angle", ref cliffAngle, 0.0f, 90.0f, "%.1f°"))
            module.CliffAngle = cliffAngle;

        float blendRange = module.BlendRange;
        if (ImGui.DragFloat("Blend Range", ref blendRange, 0.1f, 0.0f, 45.0f, "%.1f°"))
            module.BlendRange = MathF.Max(0.0f, blendRange);
    }

    private static void DrawHeightSplatProperties(HeightSplatModule module)
    {
        var thresholds = module.HeightThresholds;

        ImGui.TextUnformatted("Height Thresholds (normalized 0-1):");

        bool changed = false;
        for (int i = 0; i < thresholds.Length; i++)
        {
            float threshold = thresholds[i];
            if (ImGui.SliderFloat($"Threshold {i}", ref threshold, 0.0f, 1.0f, "%.3f"))
            {
                thresholds[i] = threshold;
                changed = true;
            }
        }

        if (changed)
            module.HeightThresholds = thresholds;

        float blendRange = module.BlendRange;
        if (ImGui.DragFloat("Blend Range", ref blendRange, 0.01f, 0.0f, 0.5f, "%.3f"))
            module.BlendRange = MathF.Max(0.0f, blendRange);
    }

    private static void DrawBrushSection(LandscapeComponent landscape, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Sculpting Brush"))
            return;

        // Brush mode
        string[] modeNames = ["Raise", "Lower", "Flatten", "Smooth", "Paint"];
        int currentMode = (int)state.BrushMode;
        if (ImGui.Combo("Mode", ref currentMode, modeNames, modeNames.Length))
            state.BrushMode = (ETerrainBrushMode)currentMode;

        // Brush parameters
        if (ImGui.DragFloat("Radius", ref state.BrushRadius, 0.1f, 0.1f, 1000.0f, "%.1f"))
            state.BrushRadius = MathF.Max(0.1f, state.BrushRadius);

        if (ImGui.SliderFloat("Strength", ref state.BrushStrength, 0.0f, 1.0f, "%.3f"))
            state.BrushStrength = state.BrushStrength;

        if (ImGui.SliderFloat("Falloff", ref state.BrushFalloff, 0.0f, 1.0f, "%.3f"))
            state.BrushFalloff = state.BrushFalloff;

        // Brush preview
        ImGui.Spacing();
        ImGui.TextUnformatted("Brush Preview:");

        var drawList = ImGui.GetWindowDrawList();
        Vector2 center = ImGui.GetCursorScreenPos() + new Vector2(50, 50);

        // Draw brush falloff visualization
        float maxRadius = 40.0f;
        float innerRadius = maxRadius * (1.0f - state.BrushFalloff);

        drawList.AddCircleFilled(center, maxRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.5f, 1.0f, 0.2f)), 32);
        drawList.AddCircleFilled(center, innerRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.5f, 1.0f, 0.5f)), 32);
        drawList.AddCircle(center, maxRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.5f, 1.0f, 1.0f)), 32, 2.0f);

        ImGui.Dummy(new Vector2(100, 100));

        ImGui.TextDisabled("Click and drag on terrain to sculpt.");
    }

    private static void DrawStatisticsSection(LandscapeComponent landscape)
    {
        if (!ImGui.CollapsingHeader("Statistics"))
            return;

        // Calculate stats
        uint vertexCount = landscape.HeightmapResolution * landscape.HeightmapResolution;
        uint chunkCount = landscape.ChunkCount * landscape.ChunkCount;

        ImGui.TextUnformatted($"Heightmap Resolution: {landscape.HeightmapResolution}x{landscape.HeightmapResolution}");
        ImGui.TextUnformatted($"Vertex Count: {vertexCount:N0}");
        ImGui.TextUnformatted($"Chunk Count: {chunkCount:N0}");
        ImGui.TextUnformatted($"Terrain Size: {landscape.TerrainSize:F0}m");
        ImGui.TextUnformatted($"Height Range: {landscape.MinHeight:F1}m to {landscape.MaxHeight:F1}m");
        ImGui.TextUnformatted($"Material Layers: {landscape.Layers.Count}");
        ImGui.TextUnformatted($"Modules: {landscape.Modules.Count}");

        // Memory estimate (very rough)
        long heightmapMemory = vertexCount * sizeof(float);
        long splatmapMemory = vertexCount * 4 * sizeof(byte) * Math.Max(1, (landscape.Layers.Count - 1) / 4 + 1);
        long totalMemory = heightmapMemory + splatmapMemory;

        ImGui.Spacing();
        ImGui.TextUnformatted("Estimated GPU Memory:");
        ImGui.TextUnformatted($"  Heightmap: {FormatBytes(heightmapMemory)}");
        ImGui.TextUnformatted($"  Splatmaps: {FormatBytes(splatmapMemory)}");
        ImGui.TextUnformatted($"  Total: {FormatBytes(totalMemory)}");
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
