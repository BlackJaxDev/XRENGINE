using ImGuiNET;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using SimpleScene.Util.ssBVH;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Tools;
using AssetFieldOptions = XREngine.Editor.ImGuiAssetUtilities.AssetFieldOptions;
using XREngine.Rendering.Info;

namespace XREngine.Editor.ComponentEditors;

public sealed class ModelComponentEditor : IXRComponentEditor
{
    private static readonly Vector4 ActiveLodHighlight = new(0.20f, 0.50f, 0.90f, 0.18f);
    private const float TexturePreviewMaxEdge = 96.0f;
    private const float TexturePreviewFallbackEdge = 64.0f;

    private const ImGuiColorEditFlags ColorPickerFlags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.HDR | ImGuiColorEditFlags.NoOptions;
    
    private static readonly Vector4 ActiveLodTextColor = new(0.35f, 0.75f, 1.00f, 1.00f);
    private static readonly string[] TextureAssetPickerExtensions =
    [
        ".asset",
        ".png",
        ".jpg",
        ".jpeg",
        ".tga",
        ".tif",
        ".tiff",
        ".exr",
        ".hdr"
    ];
    private static readonly HashSet<string> TextureImportExtensionSet = new(TextureAssetPickerExtensions.Skip(1), StringComparer.OrdinalIgnoreCase);
    private static readonly AssetFieldOptions TextureAssetFieldOptions = AssetFieldOptions.WithExtensions(TextureAssetPickerExtensions);
    private const string TextureImportDialogFilter = "Image Files (*.png;*.jpg;*.jpeg;*.tga;*.tif;*.tiff;*.exr;*.hdr)|*.png;*.jpg;*.jpeg;*.tga;*.tif;*.tiff;*.exr;*.hdr|XRTexture2D Asset (*.asset)|*.asset";
    private const string ImportedTextureFolderName = "Textures";
    private const string MissingAssetCategoryTexture = "Texture2D";

    private sealed class ImpostorState
    {
        public uint SheetSize = 1024;
        public bool CaptureDepth = true;
        public OctahedralImposterGenerator.Result? LastResult;
    }

    private sealed class BvhPreviewState
    {
        public bool Enabled;
        public ColorF4 InternalNodeColor = ColorF4.Cyan;
        public ColorF4 LeafNodeColor = ColorF4.LightGreen;
        public bool HighlightLeafNodes = true;
        public bool CullNodesAgainstCamera = true;

        public readonly HashSet<XRMesh> AttemptedBvhBuilds = new();
        public readonly Dictionary<RenderableMesh, RenderInfo.DelPreRenderCallback> MeshHandlers = new();
        private readonly Dictionary<RenderableMesh, SkinnedBvhCacheEntry> _skinnedBvhCache = new();
        private readonly object _handlersLock = new();
        private readonly object _skinnedCacheLock = new();

        public void LockHandlers(Action<Dictionary<RenderableMesh, RenderInfo.DelPreRenderCallback>> action)
        {
            lock (_handlersLock)
                action(MeshHandlers);
        }

        public void UpdateSkinnedBvhCache(RenderableMesh mesh, BVH<Triangle> tree, Matrix4x4 localToWorld)
        {
            lock (_skinnedCacheLock)
                _skinnedBvhCache[mesh] = new SkinnedBvhCacheEntry(tree, localToWorld);
        }

        public bool TryGetSkinnedBvhCache(RenderableMesh mesh, out SkinnedBvhCacheEntry entry)
        {
            lock (_skinnedCacheLock)
                return _skinnedBvhCache.TryGetValue(mesh, out entry);
        }

        public void RemoveSkinnedBvhCache(RenderableMesh mesh)
        {
            lock (_skinnedCacheLock)
                _skinnedBvhCache.Remove(mesh);
        }

        public void ClearSkinnedBvhCache()
        {
            lock (_skinnedCacheLock)
                _skinnedBvhCache.Clear();
        }
    }

    private readonly record struct SkinnedBvhCacheEntry(BVH<Triangle> Tree, Matrix4x4 LocalToWorld);

    private static readonly ConditionalWeakTable<ModelComponent, ImpostorState> s_impostorStates = new();
    private static readonly ConditionalWeakTable<ModelComponent, BvhPreviewState> s_bvhPreviewStates = new();
    private static readonly ConditionalWeakTable<SubMesh, SubMeshMaterialState> s_submeshMaterialStates = new();

    private sealed class SubMeshMaterialState
    {
        public bool LinkMaterialsAcrossLods = true;
    }

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not ModelComponent modelComponent)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(modelComponent, visited, "Model Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        DrawComponentProperties(modelComponent);
        DrawModelOverview(modelComponent);
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawComponentProperties(ModelComponent modelComponent)
    {
        if (!ImGui.CollapsingHeader("Component", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;

        if (ImGui.BeginTable("ComponentProperties", 2, tableFlags))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Model");
            ImGui.TableSetColumnIndex(1);
            ImGuiAssetUtilities.DrawAssetField("ComponentModel", modelComponent.Model, asset =>
            {
                if (!ReferenceEquals(modelComponent.Model, asset))
                    modelComponent.Model = asset;
            });

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Render Bounds");
            ImGui.TableSetColumnIndex(1);
            bool renderBounds = modelComponent.RenderBounds;
            if (ImGui.Checkbox("##RenderBounds", ref renderBounds))
                modelComponent.RenderBounds = renderBounds;

            ImGui.EndTable();
        }

        ImGui.Spacing();
    }

    private static void DrawModelOverview(ModelComponent modelComponent)
    {
        Model? model = modelComponent.Model;
        if (model is null)
        {
            ImGui.TextDisabled("No model assigned.");
            return;
        }

        string displayName = string.IsNullOrEmpty(model.Name) ? "<unnamed model>" : model.Name;
        ImGui.TextUnformatted($"Model: {displayName}");
        ImGui.TextUnformatted("Submeshes: " + model.Meshes.Count.ToString(CultureInfo.InvariantCulture));

        if (model.Meshes.Count == 0)
            return;

        DrawUnifiedBlendshapeControls(modelComponent);

        if (!ImGui.CollapsingHeader("Submeshes", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var runtimeMeshes = modelComponent.Meshes.ToArray();
        int submeshIndex = 0;
        foreach (SubMesh subMesh in model.Meshes)
        {
            RenderableMesh? runtimeMesh = submeshIndex < runtimeMeshes.Length ? runtimeMeshes[submeshIndex] : null;
            DrawSubmeshSection(modelComponent, submeshIndex, subMesh, runtimeMesh);
            submeshIndex++;
        }
        DrawImpostorUtilities(modelComponent, model);
        DrawBvhPreviewUtilities(modelComponent);
    }

    private static void DrawUnifiedBlendshapeControls(ModelComponent modelComponent)
    {
        var runtimeMeshes = modelComponent.Meshes;
        if (runtimeMeshes.Count == 0)
            return;

        var blendshapeValues = new Dictionary<string, List<float>>(StringComparer.InvariantCultureIgnoreCase);
        var displayNames = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

        foreach (RenderableMesh runtimeMesh in runtimeMeshes)
        {
            foreach (var lod in runtimeMesh.LODs)
            {
                XRMeshRenderer? renderer = lod.Renderer;
                XRMesh? mesh = renderer?.Mesh;
                if (mesh is null || !mesh.HasBlendshapes)
                    continue;

                uint blendshapeCount = mesh.BlendshapeCount;
                for (uint i = 0; i < blendshapeCount; i++)
                {
                    string name = mesh.BlendshapeNames[(int)i];
                    if (!blendshapeValues.TryGetValue(name, out var values))
                    {
                        values = new List<float>();
                        blendshapeValues[name] = values;
                        displayNames[name] = name;
                    }

                    values.Add(renderer.GetBlendshapeWeight(i));
                }
            }
        }

        if (blendshapeValues.Count == 0)
            return;

        if (!ImGui.CollapsingHeader("Blendshapes (All LODs)", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.BeginTable("Blendshapes_All", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Blendshape", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("Weight (%)", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableHeadersRow();

            foreach (var entry in blendshapeValues.OrderBy(pair => displayNames.TryGetValue(pair.Key, out var name) ? name : pair.Key, StringComparer.InvariantCultureIgnoreCase))
            {
                string key = entry.Key;
                List<float> values = entry.Value;
                if (values.Count == 0)
                    continue;

                string displayName = displayNames.TryGetValue(key, out var name) ? name : key;

                float firstValue = values[0];
                bool hasMixedValues = values.Skip(1).Any(value => MathF.Abs(value - firstValue) > 0.0001f);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(displayName);

                ImGui.TableSetColumnIndex(1);
                ImGui.PushID($"BlendGlobal_{key}");
                float pctValue = hasMixedValues ? values.Average() : firstValue;
                ImGui.SetNextItemWidth(-1f);
                if (hasMixedValues)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled("Mixed");
                    ImGui.SameLine();
                }

                if (ImGui.SliderFloat("##Pct", ref pctValue, 0.0f, 100.0f, "%.1f%%"))
                {
                    modelComponent.SetBlendShapeWeight(key, pctValue);
                    PushBlendshapeWeightsToGpu(modelComponent, key);
                }
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
    }

    private static void DrawBvhPreviewUtilities(ModelComponent modelComponent)
    {
        var state = s_bvhPreviewStates.GetValue(modelComponent, _ => new BvhPreviewState());

        if (ImGui.CollapsingHeader("BVH Preview"))
        {
            bool enabled = state.Enabled;
            if (ImGui.Checkbox("Enable", ref enabled))
                state.Enabled = enabled;

            bool highlightLeafNodes = state.HighlightLeafNodes;
            if (ImGui.Checkbox("Highlight Leaf Nodes", ref highlightLeafNodes))
                state.HighlightLeafNodes = highlightLeafNodes;

            bool cullNodes = state.CullNodesAgainstCamera;
            if (ImGui.Checkbox("Cull Nodes Against Camera", ref cullNodes))
                state.CullNodesAgainstCamera = cullNodes;

            Vector4 internalColor = new(state.InternalNodeColor.R, state.InternalNodeColor.G, state.InternalNodeColor.B, state.InternalNodeColor.A);
            if (ImGui.ColorEdit4("Internal Node Color", ref internalColor, ColorPickerFlags))
                state.InternalNodeColor = new ColorF4(internalColor.X, internalColor.Y, internalColor.Z, internalColor.W);

            Vector4 leafColor = new(state.LeafNodeColor.R, state.LeafNodeColor.G, state.LeafNodeColor.B, state.LeafNodeColor.A);
            if (ImGui.ColorEdit4("Leaf Node Color", ref leafColor, ColorPickerFlags))
                state.LeafNodeColor = new ColorF4(leafColor.X, leafColor.Y, leafColor.Z, leafColor.W);

            ImGui.Spacing();
            ImGui.TextDisabled("Draws BVH bounds for visible meshes in the current camera view.");
        }

        if (state.Enabled)
            EnsureBvhPreviewHooks(modelComponent, state);
        else
            DisableBvhPreviewHooks(state);
    }

    private static void EnsureBvhPreviewHooks(ModelComponent modelComponent, BvhPreviewState state)
    {
        state.LockHandlers(handlers =>
        {
            foreach (RenderableMesh mesh in modelComponent.Meshes)
            {
                if (handlers.ContainsKey(mesh))
                    continue;

                RenderInfo.DelPreRenderCallback handler = (info, command, camera) => RenderBvhPreviewForMesh(mesh, command, camera, state);
                mesh.RenderInfo.CollectedForRenderCallback += handler;
                handlers.Add(mesh, handler);
            }

            if (handlers.Count == modelComponent.Meshes.Count)
                return;

            var currentMeshes = modelComponent.Meshes;
            var removed = new List<RenderableMesh>();
            foreach (var kvp in handlers)
            {
                if (!currentMeshes.Contains(kvp.Key))
                    removed.Add(kvp.Key);
            }

            for (int i = 0; i < removed.Count; i++)
            {
                var mesh = removed[i];
                if (handlers.TryGetValue(mesh, out var handler))
                {
                    mesh.RenderInfo.CollectedForRenderCallback -= handler;
                    handlers.Remove(mesh);
                    state.RemoveSkinnedBvhCache(mesh);
                }
            }
        });
    }

    private static void DisableBvhPreviewHooks(BvhPreviewState state)
    {
        state.LockHandlers(handlers =>
        {
            if (handlers.Count == 0)
                return;

            foreach (var kvp in handlers)
                kvp.Key.RenderInfo.CollectedForRenderCallback -= kvp.Value;

            handlers.Clear();
        });

        state.ClearSkinnedBvhCache();
    }

    private static void RenderBvhPreviewForMesh(RenderableMesh mesh, RenderCommand command, XRCamera? camera, BvhPreviewState state)
    {
        if (!state.Enabled)
            return;

        if (Engine.Rendering.State.IsShadowPass)
            return;

        if (command is not RenderCommandMesh3D)
            return;

        IVolume? frustum = camera?.WorldFrustum();
        RenderMeshTree(mesh, frustum, state);
    }

    [ThreadStatic]
    private static Stack<BVHNode<Triangle>>? t_nodeStack;

    private static void RenderMeshTree(RenderableMesh mesh, IVolume? frustum, BvhPreviewState state)
    {
        bool skinned = mesh.IsSkinned;
        BVH<Triangle>? tree = null;
        Matrix4x4 meshMatrix = Matrix4x4.Identity;

        if (skinned)
        {
            tree = mesh.GetSkinnedBvh();
            if (tree is not null)
            {
                meshMatrix = mesh.SkinnedBvhLocalToWorldMatrix;
                state.UpdateSkinnedBvhCache(mesh, tree, meshMatrix);
            }
            else if (state.TryGetSkinnedBvhCache(mesh, out var cached))
            {
                tree = cached.Tree;
                meshMatrix = cached.LocalToWorld;
            }
        }
        else
        {
            var renderer = mesh.CurrentLODRenderer;
            var xrMesh = renderer?.Mesh;
            tree = xrMesh?.BVHTree;
            if (tree?._rootBVH is null && xrMesh is not null && state.AttemptedBvhBuilds.Add(xrMesh))
            {
                xrMesh.GenerateBVH();
                tree = xrMesh.BVHTree;
            }

            meshMatrix = mesh.Component.Transform.RenderMatrix;
        }

        var root = tree?._rootBVH;
        if (root is null)
            return;

        var nodeStack = t_nodeStack ??= new Stack<BVHNode<Triangle>>();
        nodeStack.Clear();
        nodeStack.Push(root);

        Matrix4x4 rotationScaleMatrix = meshMatrix;
        rotationScaleMatrix.Translation = Vector3.Zero;

        while (nodeStack.Count > 0)
        {
            var node = nodeStack.Pop();
            if (node is null)
                continue;

            AABB nodeBounds = node.box;
            AABB worldBounds = TransformAabb(nodeBounds, meshMatrix);
            if (state.CullNodesAgainstCamera && frustum is not null)
            {
                EContainment containment = frustum.ContainsAABB(worldBounds);
                if (containment == EContainment.Disjoint)
                    continue;
            }

            ColorF4 color = node.IsLeaf && state.HighlightLeafNodes ? state.LeafNodeColor : state.InternalNodeColor;
            Vector3 worldCenter = Vector3.Transform(nodeBounds.Center, meshMatrix);
            Engine.Rendering.Debug.RenderBox(nodeBounds.HalfExtents, worldCenter, rotationScaleMatrix, false, color);

            if (node.left is not null)
                nodeStack.Push(node.left);
            if (node.right is not null)
                nodeStack.Push(node.right);
        }
    }

    private static AABB TransformAabb(AABB localBounds, Matrix4x4 transform)
        => localBounds.Transformed(point => Vector3.Transform(point, transform));

    private static void DrawImpostorUtilities(ModelComponent modelComponent, Model model)
    {
        if (!ImGui.CollapsingHeader("Impostor Utilities"))
            return;

        var state = s_impostorStates.GetValue(modelComponent, _ => new ImpostorState());

        int sheetSize = (int)state.SheetSize;
        if (ImGui.InputInt("Sheet Size (px)", ref sheetSize))
            state.SheetSize = (uint)Math.Max(128, sheetSize);

        bool captureDepth = state.CaptureDepth;
        if (ImGui.Checkbox("Capture Depth", ref captureDepth))
            state.CaptureDepth = captureDepth;

        ImGui.TextDisabled("26 directional captures (axes, edges, and diagonals) are blended into the sheet.");

        if (ImGui.Button("Generate Octahedral Impostor", new Vector2(-1f, 0f)))
        {
            state.LastResult = OctahedralImposterGenerator.Generate(modelComponent, new OctahedralImposterGenerator.Settings(state.SheetSize, 1.15f, state.CaptureDepth));
            if (state.LastResult is null)
                Debug.LogWarning("Impostor generation failed. See console for details.");
        }

        if (state.LastResult is { } result)
        {
            Vector3 size = result.LocalBounds.Size;
            ImGui.Separator();
            ImGui.TextUnformatted("Last Generation:");
            ImGui.TextDisabled($"Views: {result.Views.Width} x {result.Views.Height} x {result.Views.Depth}");
            ImGui.TextDisabled($"Bounds: {size.X:0.##}, {size.Y:0.##}, {size.Z:0.##}");
            ImGui.TextDisabled($"Views Captured: {result.Views.Depth} (dirs: {result.CaptureDirections.Count})");

            ImGui.Spacing();
            ImGui.TextUnformatted("View Previews:");
            
            // Display all 26 views in a grid
            float availWidth = ImGui.GetContentRegionAvail().X;
            int columns = Math.Max(1, (int)(availWidth / 110f)); // ~100px per thumbnail + spacing
            
            if (ImGui.BeginTable("ImpostorViewsGrid", columns, ImGuiTableFlags.None))
            {
                for (int i = 0; i < result.Views.Textures.Length; i++)
                {
                    ImGui.TableNextColumn();
                    ImGui.PushID($"ImpostorView_{i}");
                    ImGui.TextDisabled(GetImpostorViewLabel(i));
                    DrawTexturePreviewCell(result.Views.Textures[i], 100f);
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }

            if (ImGui.Button("Create Billboard Impostor", new Vector2(-1f, 0f)))
                TryCreateBillboardFromImpostor(modelComponent, result);
            
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Disable this ModelComponent and attach an octahedral billboard imposter using the generated captures.");
        }

        ImGui.Spacing();
    }

    /// <summary>
    /// Returns a descriptive label for the impostor view at the given index.
    /// Order matches OctahedralImposterGenerator.BuildCaptureDirections().
    /// </summary>
    private static string GetImpostorViewLabel(int index) => index switch
    {
        // Axis-aligned (6)
        0 => "0: +X (Right)",
        1 => "1: -X (Left)",
        2 => "2: +Y (Top)",
        3 => "3: -Y (Bottom)",
        4 => "4: +Z (Front)",
        5 => "5: -Z (Back)",
        // Edge midpoints - XY plane (4)
        6 => "6: +X+Y",
        7 => "7: +X-Y",
        8 => "8: -X+Y",
        9 => "9: -X-Y",
        // Edge midpoints - XZ plane (4)
        10 => "10: +X+Z",
        11 => "11: +X-Z",
        12 => "12: -X+Z",
        13 => "13: -X-Z",
        // Edge midpoints - YZ plane (4)
        14 => "14: +Y+Z",
        15 => "15: +Y-Z",
        16 => "16: -Y+Z",
        17 => "17: -Y-Z",
        // Elevated diagonals (8)
        18 => "18: -X-Y-Z",
        19 => "19: -X-Y+Z",
        20 => "20: -X+Y-Z",
        21 => "21: -X+Y+Z",
        22 => "22: +X-Y-Z",
        23 => "23: +X-Y+Z",
        24 => "24: +X+Y-Z",
        25 => "25: +X+Y+Z",
        _ => $"{index}: Unknown"
    };

    private static void TryCreateBillboardFromImpostor(ModelComponent modelComponent, OctahedralImposterGenerator.Result result)
    {
        OctahedralBillboardComponent? billboard = modelComponent.SceneNode.GetOrAddComponent<OctahedralBillboardComponent>(out bool wasAdded);
        if (billboard is null)
        {
            Debug.LogWarning("Failed to add an octahedral billboard component to the scene node.");
            return;
        }

        billboard.ApplyCaptureResult(result, matchBounds: true);
        modelComponent.IsActive = false;

        if (wasAdded)
            Debug.Out("Created octahedral impostor billboard and disabled the original model component.");
        else
            Debug.Out("Updated existing octahedral impostor billboard and disabled the original model component.");
    }

    private static void DrawSubmeshSection(ModelComponent modelComponent, int index, SubMesh subMesh, RenderableMesh? runtimeMesh)
    {
        ImGui.PushID($"Submesh{index}");

        string submeshName = string.IsNullOrWhiteSpace(subMesh.Name) ? "<unnamed>" : subMesh.Name;
        string headerLabel = $"Submesh {index}: {submeshName} ({subMesh.LODs.Count} LOD{(subMesh.LODs.Count == 1 ? string.Empty : "s")})##Submesh{index}";

        if (!ImGui.TreeNodeEx(headerLabel, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            ImGui.PopID();
            return;
        }

        var bounds = subMesh.Bounds;
        ImGui.TextUnformatted($"Bounds Min: ({bounds.Min.X:F2}, {bounds.Min.Y:F2}, {bounds.Min.Z:F2})");
        ImGui.TextUnformatted($"Bounds Max: ({bounds.Max.X:F2}, {bounds.Max.Y:F2}, {bounds.Max.Z:F2})");

        if (subMesh.CullingBounds is { } cullingBounds)
            ImGui.TextDisabled($"Custom Culling Bounds Size: {cullingBounds.Size.X:F2} x {cullingBounds.Size.Y:F2} x {cullingBounds.Size.Z:F2}");

        if (runtimeMesh is not null)
        {
            bool renderBounds = runtimeMesh.RenderBounds;
            if (ImGui.Checkbox("Render Bounds##SubmeshRenderBounds", ref renderBounds))
                runtimeMesh.RenderBounds = renderBounds;

            ImGui.SameLine();
            ImGui.TextDisabled(FormatRenderCommandLabel(runtimeMesh));
        }

        var lodEntries = BuildLodEntries(subMesh, runtimeMesh);

        DrawLodSummaryTable(index, subMesh, lodEntries, runtimeMesh);

        ImGui.Spacing();
        DrawLodPropertyEditors(modelComponent, index, subMesh, lodEntries, runtimeMesh);

        ImGui.TreePop();
        ImGui.PopID();
    }

    private static void DrawBlendshapeControls(ModelComponent modelComponent, int submeshIndex, int lodIndex, RenderableMesh.RenderableLOD? runtimeLOD)
    {
        XRMeshRenderer? renderer = runtimeLOD?.Renderer;
        if (renderer?.Mesh is null || !renderer.Mesh.HasBlendshapes)
            return;

        if (!ImGui.CollapsingHeader($"Blendshapes##Blend_{submeshIndex}_{lodIndex}", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var mesh = renderer.Mesh;
        uint blendshapeCount = mesh.BlendshapeCount;
        if (blendshapeCount == 0)
        {
            ImGui.TextDisabled("No blendshapes.");
            return;
        }

        if (ImGui.BeginTable($"Blendshapes_{submeshIndex}_{lodIndex}", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Blendshape", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("Weight (%)", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableHeadersRow();

            for (uint i = 0; i < blendshapeCount; i++)
            {
                string name = mesh.BlendshapeNames[(int)i];

                float percent = renderer.GetBlendshapeWeight(i);

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(name);

                ImGui.TableSetColumnIndex(1);
                ImGui.PushID($"BlendPct_{submeshIndex}_{lodIndex}_{i}");
                float pctValue = percent;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.SliderFloat("##Pct", ref pctValue, 0.0f, 100.0f, "%.1f%%"))
                {
                    modelComponent.SetBlendShapeWeight(name, pctValue);
                    renderer.PushBlendshapeWeightsToGPU();
                }
                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private static void PushBlendshapeWeightsToGpu(ModelComponent modelComponent, string blendshapeName, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
    {
        bool HasMatchingBlendshape(XRMeshRenderer x)
            => (x?.Mesh?.HasBlendshapes ?? false) && Array.Exists(x.Mesh!.BlendshapeNames, name => string.Equals(name, blendshapeName, comp));

        foreach (var renderer in modelComponent.GetAllRenderersWhere(HasMatchingBlendshape))
            renderer.PushBlendshapeWeightsToGPU();
    }

    private static void DrawMaterialControls(int submeshIndex, int lodIndex, SubMeshLOD lod, RenderableMesh.RenderableLOD? runtimeLOD)
    {
        XRMeshRenderer? renderer = runtimeLOD?.Renderer;
        XRMaterialBase? assetMaterial = lod.Material;
        XRMaterialBase? runtimeMaterial = renderer?.Material;
        XRMaterialBase? effectiveMaterial = runtimeMaterial ?? assetMaterial;

        if (effectiveMaterial is null)
        {
            ImGui.TextDisabled("Material not assigned.");
            return;
        }

        if (!ImGui.CollapsingHeader($"Material##Mat_{submeshIndex}_{lodIndex}", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (assetMaterial is not null)
            ImGui.TextUnformatted($"Asset: {FormatAssetLabel(assetMaterial.Name, assetMaterial)}");
        if (runtimeMaterial is not null && !ReferenceEquals(runtimeMaterial, assetMaterial))
            ImGui.TextDisabled($"Runtime: {FormatAssetLabel(runtimeMaterial.Name, runtimeMaterial)}");

        ImGui.TextDisabled($"Render Pass: {DescribeRenderPass(effectiveMaterial.RenderPass)}");

        var parameters = effectiveMaterial.Parameters;
        if (parameters is not null && parameters.Length > 0)
        {
            if (ImGui.BeginTable($"MaterialParams_{submeshIndex}_{lodIndex}", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.45f);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.55f);
                ImGui.TableHeadersRow();

                for (int i = 0; i < parameters.Length; i++)
                {
                    ShaderVar param = parameters[i];
                    string name = param.Name ?? $"Param{i}";

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(name);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.PushID($"MatParam_{submeshIndex}_{lodIndex}_{i}");
                    DrawShaderParameterControl(renderer, param);
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }
        else
        {
            ImGui.TextDisabled("No uniform parameters.");
        }

        ImGui.SeparatorText("Textures");

        if (runtimeMaterial is not null && !ReferenceEquals(runtimeMaterial, assetMaterial))
        {
            ImGui.TextDisabled("Runtime Overrides:");
            DrawMaterialTextureTable($"MaterialTextures_{submeshIndex}_{lodIndex}_Runtime", runtimeMaterial);
            if (assetMaterial is not null)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Asset Textures:");
                DrawMaterialTextureTable($"MaterialTextures_{submeshIndex}_{lodIndex}_Asset", assetMaterial);
            }
        }
        else
        {
            DrawMaterialTextureTable($"MaterialTextures_{submeshIndex}_{lodIndex}", effectiveMaterial);
        }
    }

    private static void DrawShaderParameterControl(XRMeshRenderer? renderer, ShaderVar param)
    {
        switch (param)
        {
            case ShaderFloat f:
                {
                    float value = f.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat("##Float", ref value, 0.01f))
                    {
                        f.SetValue(value);
                        renderer?.Material?.MarkDirty();
                    }
                    break;
                }
            case ShaderArrayBase a:
                {
                    ImGui.TextDisabled($"Array ({a.Length} elements)");
                    break;
                }
            case ShaderInt i:
                {
                    int value = i.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragInt("##Int", ref value))
                    {
                        i.SetValue(value);
                        renderer?.Material?.MarkDirty();
                    }
                    break;
                }
            case ShaderUInt ui:
                {
                    uint value = ui.Value;
                    ImGui.SetNextItemWidth(-1f);
                    int intValue = (int)value;
                    if (ImGui.DragInt("##UInt", ref intValue, 1.0f, 0, int.MaxValue))
                    {
                        ui.SetValue((uint)intValue);
                        renderer?.Material?.MarkDirty();
                    }
                    break;
                }
            case ShaderBool b:
                {
                    bool value = b.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.Checkbox("##Bool", ref value))
                    {
                        b.SetValue(value);
                        renderer?.Material?.MarkDirty();
                    }
                    break;
                }
            case ShaderVector2 v2:
                {
                    Vector2 value = v2.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Vec2", ref value, 0.01f))
                    {
                        v2.SetValue(value);
                        renderer?.Material?.MarkDirty();
                    }
                    break;
                }
            case ShaderVector3 v3:
                {
                    Vector3 value = v3.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3("##Vec3", ref value, 0.01f))
                    {
                        v3.SetValue(value);
                        renderer?.Material?.MarkDirty();
                    }
                    break;
                }
            case ShaderVector4 v4:
                {
                    Vector4 value = v4.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4("##Vec4", ref value, 0.01f))
                    {
                        v4.SetValue(value);
                        renderer?.Material?.MarkDirty();
                    }
                    break;
                }
            case ShaderMat4 m4:
                {
                    // Matrix editing is verbose; show as read-only for now.
                    ImGui.TextDisabled("Matrix4x4 (edit not implemented)");
                    break;
                }
            default:
                ImGui.TextDisabled(param.GetType().Name);
                break;
        }
    }

    private static string FormatAssetLabel(string? preferredName, object? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
            return preferredName!;

        return fallback?.GetType().Name ?? "<none>";
    }

    private static void DrawTexturePreviewCell(XRTexture? texture)
        => DrawTexturePreviewCell(texture, null);

    private static void DrawTexturePreviewCell(XRTexture? texture, float? maxSize)
    {
        if (texture is null)
        {
            ImGui.TextDisabled("<null>");
            return;
        }

        bool hasPreview = TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason);
        
        // Apply max size constraint if specified
        if (maxSize.HasValue && hasPreview)
        {
            float scale = Math.Min(maxSize.Value / displaySize.X, maxSize.Value / displaySize.Y);
            if (scale < 1f)
                displaySize *= scale;
        }
        
        string previewLabel = FormatAssetLabel(texture.Name, texture);

        if (hasPreview)
        {
            bool openDialog = false;
            ImGui.Image(handle, displaySize);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(previewLabel);
                if (pixelSize.X > 0f && pixelSize.Y > 0f)
                    ImGui.TextUnformatted($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
                ImGui.EndTooltip();
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    openDialog = true;
            }

            if (ImGui.SmallButton("View Larger"))
                openDialog = true;

            if (openDialog)
                ComponentEditorLayout.RequestPreviewDialog($"{previewLabel} Texture", handle, pixelSize, flipVertically: false);
        }
        else
        {
            ImGui.TextDisabled(failureReason ?? "Preview unavailable");
        }

        if (pixelSize.X > 0f && pixelSize.Y > 0f)
            ImGui.TextDisabled($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
    }

    private static bool TryGetTexturePreviewData(
        XRTexture texture,
        out nint handle,
        out Vector2 displaySize,
        out Vector2 pixelSize,
        out string? failureReason)
    {
        pixelSize = GetTexturePixelSize(texture);
        displaySize = GetPreviewSize(pixelSize);
        handle = nint.Zero;
        failureReason = null;

        if (!Engine.IsRenderThread)
        {
            failureReason = "Preview unavailable off render thread";
            return false;
        }

        OpenGLRenderer? renderer = TryGetOpenGLRenderer();
        if (renderer is null)
        {
            failureReason = "Preview requires OpenGL renderer";
            return false;
        }

        switch (texture)
        {
            case XRTexture2D tex2D:
                var apiTexture = renderer.GenericToAPI<GLTexture2D>(tex2D);
                if (apiTexture is null)
                {
                    failureReason = "Texture not uploaded";
                    return false;
                }

                uint binding = apiTexture.BindingId;
                if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
                {
                    failureReason = "Texture not ready";
                    return false;
                }

                handle = (nint)binding;
                return true;

            default:
                failureReason = $"{texture.GetType().Name} preview not supported";
                return false;
        }
    }

    private static OpenGLRenderer? TryGetOpenGLRenderer()
    {
        if (AbstractRenderer.Current is OpenGLRenderer current)
            return current;

        foreach (var window in Engine.Windows)
            if (window.Renderer is OpenGLRenderer renderer)
                return renderer;

        return null;
    }

    private static Vector2 GetTexturePixelSize(XRTexture texture)
    {
        return texture switch
        {
            XRTexture2D tex2D => new Vector2(tex2D.Width, tex2D.Height),
            _ => new Vector2(texture.WidthHeightDepth.X, texture.WidthHeightDepth.Y),
        };
    }

    private static Vector2 GetPreviewSize(Vector2 pixelSize)
    {
        float width = pixelSize.X;
        float height = pixelSize.Y;

        if (width <= 0f || height <= 0f)
            return new Vector2(TexturePreviewFallbackEdge, TexturePreviewFallbackEdge);

        float maxDimension = MathF.Max(width, height);
        if (maxDimension <= TexturePreviewMaxEdge)
            return new Vector2(width, height);

        float scale = TexturePreviewMaxEdge / maxDimension;
        return new Vector2(width * scale, height * scale);
    }

    private static IReadOnlyList<(int Index, SubMeshLOD Lod, LinkedListNode<RenderableMesh.RenderableLOD>? RuntimeNode)> BuildLodEntries(SubMesh subMesh, RenderableMesh? runtimeMesh)
    {
        List<(int, SubMeshLOD, LinkedListNode<RenderableMesh.RenderableLOD>?)> entries = new();
        var runtimeNode = runtimeMesh?.LODs.First;
        int lodIndex = 0;
        foreach (SubMeshLOD lod in subMesh.LODs)
        {
            var currentNode = runtimeNode;
            runtimeNode = runtimeNode?.Next;
            entries.Add((lodIndex, lod, currentNode));
            lodIndex++;
        }

        return entries;
    }

    private static void DrawLodSummaryTable(
        int submeshIndex,
        SubMesh subMesh,
        IReadOnlyList<(int Index, SubMeshLOD Lod, LinkedListNode<RenderableMesh.RenderableLOD>? RuntimeNode)> lodEntries,
        RenderableMesh? runtimeMesh)
    {
        if (lodEntries.Count == 0)
        {
            ImGui.TextDisabled("No LODs defined.");
            return;
        }

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp
                                           | ImGuiTableFlags.RowBg
                                           | ImGuiTableFlags.BordersOuter
                                           | ImGuiTableFlags.BordersInnerV
                                           | ImGuiTableFlags.Resizable
                                           | ImGuiTableFlags.NoSavedSettings;

        if (!ImGui.BeginTable($"Submesh{submeshIndex}_LODs", 6, tableFlags))
            return;

        ImGui.TableSetupColumn("LOD", ImGuiTableColumnFlags.WidthFixed, 80.0f);
        ImGui.TableSetupColumn("Max Distance", ImGuiTableColumnFlags.WidthFixed, 140.0f);
        ImGui.TableSetupColumn("Mesh", ImGuiTableColumnFlags.WidthStretch, 0.0f);
        ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch, 0.0f);
        ImGui.TableSetupColumn("Textures", ImGuiTableColumnFlags.WidthFixed, 170.0f);
        ImGui.TableSetupColumn("Runtime", ImGuiTableColumnFlags.WidthStretch, 0.0f);
        ImGui.TableHeadersRow();

        foreach (var entry in lodEntries)
        {
            var lod = entry.Lod;
            var runtimeNode = entry.RuntimeNode;
            var runtimeLod = runtimeNode?.Value;

            bool isActive = runtimeMesh is not null && runtimeMesh.CurrentLOD == runtimeNode;

            ImGui.TableNextRow();

            if (isActive)
            {
                uint highlight = ImGui.ColorConvertFloat4ToU32(ActiveLodHighlight);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, highlight);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, highlight);
            }

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(isActive ? $"#{entry.Index} (Active)" : $"#{entry.Index}");

            ImGui.TableSetColumnIndex(1);
            float maxDistance = lod.MaxVisibleDistance;
            ImGui.PushID($"LOD{submeshIndex}_{entry.Index}_Distance");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.DragFloat("##MaxDistance", ref maxDistance, 0.25f, 0.0f, float.MaxValue, "%.2f"))
            {
                maxDistance = MathF.Max(0.0f, maxDistance);
                if (!subMesh.LODs.Any(other => !ReferenceEquals(other, lod) && MathF.Abs(other.MaxVisibleDistance - maxDistance) < 0.0001f))
                    UpdateLodDistance(subMesh, lod, maxDistance, runtimeNode);
            }
            ImGui.PopID();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Camera distance threshold where this LOD becomes active.");

            ImGui.TableSetColumnIndex(2);
            DrawMeshSummaryCell(lod.Mesh, runtimeLod?.Renderer?.Mesh);

            ImGui.TableSetColumnIndex(3);
            DrawMaterialSummaryCell(lod.Material, runtimeLod?.Renderer?.Material);

            ImGui.TableSetColumnIndex(4);
            DrawTextureSummaryCell(submeshIndex, entry.Index, lod.Material, runtimeLod?.Renderer?.Material);

            ImGui.TableSetColumnIndex(5);
            DrawRuntimeSummaryCell(entry.Index, isActive, runtimeMesh, runtimeNode);
        }

        ImGui.EndTable();
    }

    private static void DrawMeshSummaryCell(XRMesh? assetMesh, XRMesh? runtimeMesh)
    {
        if (assetMesh is null && runtimeMesh is null)
        {
            ImGui.TextDisabled("--");
            return;
        }

        if (assetMesh is not null)
        {
            ImGui.TextUnformatted($"Asset: {FormatAssetLabel(assetMesh.Name, assetMesh)}");
            ImGui.TextDisabled(FormatMeshStats(assetMesh));
        }

        if (runtimeMesh is not null && !ReferenceEquals(runtimeMesh, assetMesh))
        {
            ImGui.TextDisabled($"Runtime: {FormatAssetLabel(runtimeMesh.Name, runtimeMesh)}");
            ImGui.TextDisabled(FormatMeshStats(runtimeMesh));
        }
    }

    private static void DrawMaterialSummaryCell(XRMaterialBase? assetMaterial, XRMaterialBase? runtimeMaterial)
    {
        if (assetMaterial is null && runtimeMaterial is null)
        {
            ImGui.TextDisabled("--");
            return;
        }

        if (assetMaterial is not null)
        {
            int parameterCount = assetMaterial.Parameters?.Length ?? 0;
            ImGui.TextUnformatted($"Asset: {FormatAssetLabel(assetMaterial.Name, assetMaterial)}");
            ImGui.TextDisabled($"{parameterCount} parameter{(parameterCount == 1 ? string.Empty : "s")}");
        }

        if (runtimeMaterial is not null && !ReferenceEquals(runtimeMaterial, assetMaterial))
        {
            int runtimeParameterCount = runtimeMaterial.Parameters?.Length ?? 0;
            ImGui.TextDisabled($"Runtime: {FormatAssetLabel(runtimeMaterial.Name, runtimeMaterial)}");
            ImGui.TextDisabled($"{runtimeParameterCount} parameter{(runtimeParameterCount == 1 ? string.Empty : "s")}");
        }
    }

    private static void DrawTextureSummaryCell(int submeshIndex, int lodIndex, XRMaterialBase? assetMaterial, XRMaterialBase? runtimeMaterial)
    {
        XRMaterialBase? effectiveMaterial = runtimeMaterial ?? assetMaterial;
        if (effectiveMaterial is null)
        {
            ImGui.TextDisabled("--");
            return;
        }

        int assetTextureCount = assetMaterial?.Textures?.Count ?? 0;
        int runtimeTextureCount = runtimeMaterial?.Textures?.Count ?? assetTextureCount;

        if (runtimeMaterial is not null && !ReferenceEquals(runtimeMaterial, assetMaterial))
            ImGui.TextUnformatted($"Textures: {assetTextureCount} asset / {runtimeTextureCount} runtime");
        else
            ImGui.TextUnformatted($"Textures: {runtimeTextureCount}");

        if (runtimeTextureCount == 0 && assetTextureCount == 0)
            return;

        ImGui.PushID($"TextureSummary_{submeshIndex}_{lodIndex}");
        if (ImGui.SmallButton("Preview##Textures"))
            ImGui.OpenPopup("TexturePreviewPopup");

        if (ImGui.BeginPopup("TexturePreviewPopup"))
        {
            if (assetMaterial is not null)
            {
                ImGui.TextUnformatted("Asset");
                ImGui.Separator();
                DrawMaterialTextureTable($"AssetTextures_{submeshIndex}_{lodIndex}", assetMaterial);
            }

            if (runtimeMaterial is not null && !ReferenceEquals(runtimeMaterial, assetMaterial))
            {
                if (assetMaterial is not null)
                    ImGui.Separator();
                ImGui.TextUnformatted("Runtime");
                ImGui.Separator();
                DrawMaterialTextureTable($"RuntimeTextures_{submeshIndex}_{lodIndex}", runtimeMaterial);
            }
            else
            {
                DrawMaterialTextureTable($"Textures_{submeshIndex}_{lodIndex}", effectiveMaterial);
            }

            ImGui.EndPopup();
        }
        ImGui.PopID();
    }

    private static void DrawRuntimeSummaryCell(int entryIndex, bool isActive, RenderableMesh? runtimeMesh, LinkedListNode<RenderableMesh.RenderableLOD>? runtimeNode)
    {
        if (runtimeMesh is null)
        {
            ImGui.TextDisabled("Runtime mesh not generated.");
            return;
        }

        if (runtimeNode is null)
        {
            ImGui.TextDisabled("LOD renderer not created.");
            return;
        }

        if (entryIndex == 0)
            ImGui.TextDisabled(FormatRenderCommandLabel(runtimeMesh));

        var renderer = runtimeNode.Value.Renderer;
        if (renderer is null)
        {
            ImGui.TextDisabled("Renderer unavailable.");
            return;
        }

        ImGui.TextUnformatted($"Generate Async: {(renderer.GenerateAsync ? "Yes" : "No")}");

        if (renderer.Material is not null)
            ImGui.TextDisabled($"Render Pass: {DescribeRenderPass(renderer.Material.RenderPass)}");

        if (isActive && renderer.Mesh is not null)
            ImGui.TextDisabled($"Active Mesh: {FormatAssetLabel(renderer.Mesh.Name, renderer.Mesh)}");
    }

    private static void DrawMaterialTextureTable(string tableId, XRMaterialBase material)
    {
        var textures = material.Textures;
        if (textures is null || textures.Count == 0)
        {
            ImGui.TextDisabled("No textures assigned.");
            return;
        }

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV;
        if (!ImGui.BeginTable(tableId, 3, tableFlags))
            return;

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableHeadersRow();

        for (int i = 0; i < textures.Count; i++)
        {
            XRTexture? tex = textures[i];

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            string label = $"{i}: {FormatAssetLabel(tex?.Name, tex)}";
            ImGui.TextUnformatted(label);
            if (tex is not null)
            {
                string? location = tex.FilePath ?? tex.OriginalPath;
                if (!string.IsNullOrWhiteSpace(location) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(location);
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID($"{tableId}_Preview{i}");
            DrawTexturePreviewCell(tex);
            ImGui.PopID();

            ImGui.TableSetColumnIndex(2);
            ImGui.PushID($"{tableId}_Actions{i}");
            DrawTextureSlotControls(material, i, tex);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void DrawTextureSlotControls(XRMaterialBase material, int slotIndex, XRTexture? currentTexture)
    {
        XRTexture2D? current2D = currentTexture as XRTexture2D;

        ImGuiAssetUtilities.DrawAssetField("TextureAsset", current2D, asset => ApplyTextureSelection(material, slotIndex, asset), TextureAssetFieldOptions);

        float available = ImGui.GetContentRegionAvail().X;
        if (available > 0.0f)
            ImGui.SameLine();

        if (ImGui.SmallButton("Import"))
        {
            OpenTextureImportDialog(material, slotIndex);
        }
    }

    private static void OpenTextureImportDialog(XRMaterialBase material, int slotIndex)
    {
        string dialogId = $"TextureImport_{material.GetHashCode()}_{slotIndex}";
        ImGuiFileBrowser.OpenFile(
            dialogId,
            "Select Texture",
            result =>
            {
                if (result.Success && !string.IsNullOrEmpty(result.SelectedPath))
                {
                    XRTexture2D? imported = LoadTextureFromAnyPath(result.SelectedPath);
                    if (imported is not null)
                        ApplyTextureSelection(material, slotIndex, imported);
                }
            },
            TextureImportDialogFilter
        );
    }

    private static void ApplyTextureSelection(XRMaterialBase material, int slotIndex, XRTexture2D? newTexture)
    {
        var textures = material.Textures;
        if (textures is null || slotIndex < 0 || slotIndex >= textures.Count)
            return;

        XRTexture? current = textures[slotIndex];

        if (newTexture is not null)
        {
            if (!EnsureTextureImported(newTexture))
                return;
        }

        if (ReferenceEquals(current, newTexture))
            return;

        textures[slotIndex] = newTexture;
        material.MarkDirty();
    }

    private static bool EnsureTextureImported(XRTexture2D texture)
    {
        string? filePath = texture.FilePath;
        if (!string.IsNullOrWhiteSpace(filePath) && filePath.EndsWith($".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return true;

        return PromoteTextureToGameAsset(texture);
    }

    private static bool PromoteTextureToGameAsset(XRTexture2D texture)
    {
        var assets = Engine.Assets;
        if (assets is null)
        {
            Debug.LogWarning("Cannot import textures because the asset manager is unavailable.");
            return false;
        }

        string? sourcePath = texture.OriginalPath ?? texture.FilePath;
        string assetName = string.IsNullOrWhiteSpace(texture.Name)
            ? GenerateTextureAssetName(sourcePath)
            : SanitizeAssetName(texture.Name!);
        texture.Name = assetName;
        ConfigureImportedTextureDefaults(texture);

        IEnumerable SaveRoutine()
        {
            assets.SaveGameAssetTo(texture, ImportedTextureFolderName);
            yield break;
        }

        void OnCompleted()
            => Engine.EnqueueMainThreadTask(texture.ClearDirty);

        void OnError(Exception ex)
            => Debug.LogException(ex, $"Failed to import texture '{assetName}'.");

        try
        {
            Engine.Jobs.Schedule(
                SaveRoutine(),
                progress: null,
                completed: OnCompleted,
                error: OnError);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"Failed to schedule texture import for '{assetName}'.");
            return false;
        }
    }

    private static string GenerateTextureAssetName(string? sourcePath)
    {
        string baseName = string.IsNullOrWhiteSpace(sourcePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(sourcePath) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(baseName))
            baseName = $"ImportedTexture_{Guid.NewGuid():N}";

        return SanitizeAssetName(baseName);
    }

    private static string SanitizeAssetName(string name)
    {
        string sanitized = name;
        foreach (char c in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(c, '_');

        return string.IsNullOrWhiteSpace(sanitized)
            ? $"ImportedTexture_{Guid.NewGuid():N}"
            : sanitized;
    }

    private static void ConfigureImportedTextureDefaults(XRTexture2D texture)
    {
        texture.MagFilter = ETexMagFilter.Linear;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.UWrap = ETexWrapMode.Repeat;
        texture.VWrap = ETexWrapMode.Repeat;
        texture.AutoGenerateMipmaps = true;
        texture.AlphaAsTransparency = true;
    }

    private static XRTexture2D? LoadTextureFromAnyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path))
        {
            Debug.LogWarning($"Texture file '{path}' does not exist.");
            AssetDiagnostics.RecordMissingAsset(path, MissingAssetCategoryTexture, $"{nameof(ModelComponentEditor)}.{nameof(LoadTextureFromAnyPath)}");
            return null;
        }

        string extension = Path.GetExtension(path);
        if (extension.Equals($".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
        {
            var assets = Engine.Assets;
            if (assets is null)
            {
                Debug.LogWarning("Cannot load texture assets because the asset manager is unavailable.");
                return null;
            }

            return assets.Load<XRTexture2D>(path);
        }

        if (!TextureImportExtensionSet.Contains(extension))
        {
            Debug.LogWarning($"Unsupported texture extension '{extension}'.");
            return null;
        }

        return ImportTextureFromPath(path);
    }

    private static XRTexture2D? ImportTextureFromPath(string path)
    {
        var assets = Engine.Assets;
        if (assets is null)
        {
            Debug.LogWarning("Cannot import textures because the asset manager is unavailable.");
            return null;
        }

        XRTexture2D? texture = assets.Load<XRTexture2D>(path);
        if (texture is null)
        {
            Debug.LogWarning($"Failed to load texture from '{path}'.");
            AssetDiagnostics.RecordMissingAsset(path, MissingAssetCategoryTexture, $"{nameof(ModelComponentEditor)}.{nameof(ImportTextureFromPath)}");
            return null;
        }

        return EnsureTextureImported(texture) ? texture : null;
    }

    private static void DrawMeshDiagnostics(int submeshIndex, int lodIndex, SubMeshLOD lod, RenderableMesh.RenderableLOD? runtimeLod)
    {
        XRMesh? assetMesh = lod.Mesh;
        XRMesh? runtimeMesh = runtimeLod?.Renderer?.Mesh;
        if (assetMesh is null && runtimeMesh is null)
            return;

        ImGui.SeparatorText("Mesh Diagnostics");

        if (ImGui.BeginTable($"MeshDiagnostics_{submeshIndex}_{lodIndex}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 130.0f);
            ImGui.TableSetupColumn("Geometry", ImGuiTableColumnFlags.WidthStretch, 0.4f);
            ImGui.TableSetupColumn("Attributes", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableHeadersRow();

            if (assetMesh is not null)
                DrawMeshDiagnosticsRow("Asset", assetMesh);

            if (runtimeMesh is not null && !ReferenceEquals(runtimeMesh, assetMesh))
                DrawMeshDiagnosticsRow("Runtime", runtimeMesh);

            ImGui.EndTable();
        }
    }

    private static void DrawMeshDiagnosticsRow(string sourceLabel, XRMesh mesh)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted($"{sourceLabel}: {FormatAssetLabel(mesh.Name, mesh)}");

        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(FormatMeshStats(mesh));

        ImGui.TableSetColumnIndex(2);
        ImGui.TextDisabled(FormatMeshAttributes(mesh));
    }

    private static string FormatMeshStats(XRMesh mesh)
    {
        return $"{mesh.VertexCount} verts, {FormatMeshTopology(mesh)}";
    }

    private static string FormatMeshTopology(XRMesh mesh)
    {
        return mesh.Type switch
        {
            EPrimitiveType.Triangles => $"{mesh.IndexCount / 3} tris ({mesh.IndexCount} idx)",
            EPrimitiveType.Lines => $"{mesh.IndexCount / 2} lines ({mesh.IndexCount} idx)",
            EPrimitiveType.Points => $"{mesh.IndexCount} points",
            _ => $"{mesh.IndexCount} indices ({mesh.Type})",
        };
    }

    private static string FormatMeshAttributes(XRMesh mesh)
    {
        var attributes = new List<string>();

        if (mesh.HasNormals)
            attributes.Add("Normals");
        if (mesh.HasTangents)
            attributes.Add("Tangents");
        if (mesh.HasTexCoords)
            attributes.Add($"UV Sets {mesh.TexCoordCount}");
        if (mesh.HasColors)
            attributes.Add($"Colors {mesh.ColorCount}");
        if (mesh.HasSkinning)
            attributes.Add($"Skinning ({mesh.UtilizedBones.Length} bones)");
        if (mesh.HasBlendshapes)
            attributes.Add($"Blendshapes ({mesh.BlendshapeCount})");
        if (mesh.MaxBlendshapeAccumulation)
            attributes.Add("Blendshape Accumulation");

        return attributes.Count > 0 ? string.Join(", ", attributes) : "No additional attributes";
    }

    private static string DescribeRenderPass(int renderPass)
    {
        if (Enum.IsDefined(typeof(EDefaultRenderPass), renderPass))
            return ((EDefaultRenderPass)renderPass).ToString();
        return renderPass.ToString(CultureInfo.InvariantCulture);
    }

    private static void DrawSubmeshMaterialControls(int submeshIndex, SubMesh subMesh)
    {
        var state = s_submeshMaterialStates.GetValue(subMesh, _ => new SubMeshMaterialState());
        var sharedMaterial = GetSharedMaterialCandidate(subMesh, out bool isMixed);

        ImGui.SeparatorText($"Material Defaults (Submesh {submeshIndex})");

        bool linkMaterials = state.LinkMaterialsAcrossLods;
        if (ImGui.Checkbox("Link materials across LODs", ref linkMaterials))
            state.LinkMaterialsAcrossLods = linkMaterials;

        if (!linkMaterials)
        {
            ImGui.TextDisabled("Per-LOD materials enabled.");
            return;
        }

        ImGui.TextUnformatted("Shared Material");
        ImGuiAssetUtilities.DrawAssetField("SharedMaterial", sharedMaterial, asset => ApplySharedMaterial(subMesh, asset), AssetFieldOptions.ForMaterials());

        if (isMixed)
            ImGui.TextDisabled("LOD materials differ. Assign a shared material to sync them.");
    }

    private static XRMaterialBase? GetSharedMaterialCandidate(SubMesh subMesh, out bool isMixed)
    {
        isMixed = false;
        XRMaterialBase? shared = null;
        bool hasValue = false;

        foreach (var lod in subMesh.LODs)
        {
            if (!hasValue)
            {
                shared = lod.Material;
                hasValue = true;
                continue;
            }

            if (!ReferenceEquals(shared, lod.Material))
            {
                isMixed = true;
                break;
            }
        }

        return shared;
    }

    private static void ApplySharedMaterial(SubMesh subMesh, XRMaterialBase? material)
    {
        foreach (var lod in subMesh.LODs)
            lod.Material = material;
    }

    private static string FormatRenderCommandLabel(RenderableMesh? runtimeMesh)
    {
        if (runtimeMesh is null)
            return "Runtime mesh not initialised";

        int commandCount = runtimeMesh.RenderInfo.RenderCommands.Count;
        var renderer = runtimeMesh.CurrentLODRenderer;
        string pass = renderer?.Material is not null ? DescribeRenderPass(renderer.Material.RenderPass) : "n/a";
        return commandCount > 0
            ? $"{commandCount} render cmd(s), pass {pass}"
            : $"0 render cmd(s), pass {pass}";
    }

    private static void DrawLodPropertyEditors(
        ModelComponent modelComponent,
        int submeshIndex,
        SubMesh subMesh,
        IReadOnlyList<(int Index, SubMeshLOD Lod, LinkedListNode<RenderableMesh.RenderableLOD>? RuntimeNode)> lodEntries,
        RenderableMesh? runtimeMesh)
    {
        if (lodEntries.Count == 0)
            return;

        ImGui.SeparatorText($"LOD Details (Submesh {submeshIndex})");
        DrawSubmeshMaterialControls(submeshIndex, subMesh);

        foreach (var entry in lodEntries)
            DrawLodEditor(modelComponent, submeshIndex, subMesh, entry, runtimeMesh);
    }

    private static void DrawLodEditor(
        ModelComponent modelComponent,
        int submeshIndex,
        SubMesh subMesh,
        (int Index, SubMeshLOD Lod, LinkedListNode<RenderableMesh.RenderableLOD>? RuntimeNode) entry,
        RenderableMesh? runtimeMesh)
    {
        var lod = entry.Lod;
        var runtimeNode = entry.RuntimeNode;
        var runtimeLod = runtimeNode?.Value;
        bool isActive = runtimeMesh is not null && runtimeMesh.CurrentLOD == runtimeNode;

        ImGui.PushID($"Submesh{submeshIndex}_LOD{entry.Index}");

        string displayLabel = $"LOD #{entry.Index} ({lod.MaxVisibleDistance.ToString("F2", CultureInfo.InvariantCulture)}m)";
        if (isActive)
            displayLabel += " [Active]";
        string idLabel = $"{displayLabel}##Submesh{submeshIndex}_LOD{entry.Index}";

        if (isActive)
            ImGui.PushStyleColor(ImGuiCol.Text, ActiveLodTextColor);

        bool open = ImGui.TreeNodeEx(idLabel, ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.SpanFullWidth);

        if (isActive)
            ImGui.PopStyleColor();

        if (open)
        {
            DrawLodEditorContent(modelComponent, submeshIndex, subMesh, runtimeMesh, lod, entry.Index, runtimeNode, runtimeLod);
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void DrawLodEditorContent(
        ModelComponent modelComponent,
        int submeshIndex,
        SubMesh subMesh,
        RenderableMesh? runtimeMesh,
        SubMeshLOD lod,
        int lodIndex,
        LinkedListNode<RenderableMesh.RenderableLOD>? runtimeNode,
        RenderableMesh.RenderableLOD? runtimeLod)
    {
        const ImGuiTableFlags propertyTableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;

        if (ImGui.BeginTable($"AssetProperties_{submeshIndex}_{lodIndex}", 2, propertyTableFlags))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Max Visible Distance");
            ImGui.TableSetColumnIndex(1);
            float maxDistance = lod.MaxVisibleDistance;
            if (ImGui.InputFloat("##MaxDistance", ref maxDistance, 0.0f, 0.0f, "%.2f"))
            {
                maxDistance = MathF.Max(0.0f, maxDistance);
                if (!subMesh.LODs.Any(other => !ReferenceEquals(other, lod) && MathF.Abs(other.MaxVisibleDistance - maxDistance) < 0.0001f))
                    UpdateLodDistance(subMesh, lod, maxDistance, runtimeNode);
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Asset Mesh");
            ImGui.TableSetColumnIndex(1);
            ImGuiAssetUtilities.DrawAssetField("AssetMesh", lod.Mesh, asset =>
            {
                lod.Mesh = asset;
                subMesh.Bounds = subMesh.CalculateBoundingBox();
            }, AssetFieldOptions.ForMeshes());

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Asset Material");
            ImGui.TableSetColumnIndex(1);
            var materialState = s_submeshMaterialStates.GetValue(subMesh, _ => new SubMeshMaterialState());
            if (materialState.LinkMaterialsAcrossLods)
            {
                XRMaterialBase? sharedMaterial = GetSharedMaterialCandidate(subMesh, out _);
                bool hasOverride = !ReferenceEquals(lod.Material, sharedMaterial);
                bool overrideMaterial = hasOverride;
                if (ImGui.Checkbox("Override##AssetMaterial", ref overrideMaterial))
                {
                    if (!overrideMaterial && !ReferenceEquals(lod.Material, sharedMaterial))
                        lod.Material = sharedMaterial;
                }

                ImGui.SameLine();
                if (overrideMaterial)
                {
                    ImGuiAssetUtilities.DrawAssetField("AssetMaterial", lod.Material, asset => lod.Material = asset, AssetFieldOptions.ForMaterials());
                }
                else
                {
                    ImGui.BeginDisabled();
                    ImGuiAssetUtilities.DrawAssetField("SharedMaterial", sharedMaterial, _ => { }, AssetFieldOptions.ForMaterials(), allowClear: false, allowCreateOrReplace: false);
                    ImGui.EndDisabled();
                }
            }
            else
            {
                ImGuiAssetUtilities.DrawAssetField("AssetMaterial", lod.Material, asset => lod.Material = asset, AssetFieldOptions.ForMaterials());
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();

        DrawMeshDiagnostics(submeshIndex, lodIndex, lod, runtimeLod);

        DrawBlendshapeControls(modelComponent, submeshIndex, lodIndex, runtimeLod);

        DrawMaterialControls(submeshIndex, lodIndex, lod, runtimeLod);

        ImGui.SeparatorText("Runtime Renderer");

        var renderer = runtimeLod?.Renderer;
        if (renderer is null)
        {
            ImGui.TextDisabled("Runtime renderer not available.");
        }
        else
        {
            if (ImGui.BeginTable($"RuntimeProperties_{submeshIndex}_{lodIndex}", 2, propertyTableFlags))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Generate Async");
                ImGui.TableSetColumnIndex(1);
                bool generateAsync = renderer.GenerateAsync;
                if (ImGui.Checkbox("##GenerateAsync", ref generateAsync))
                    renderer.GenerateAsync = generateAsync;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Runtime Mesh");
                ImGui.TableSetColumnIndex(1);
                ImGuiAssetUtilities.DrawAssetField("RuntimeMesh", renderer.Mesh, asset => renderer.Mesh = asset, AssetFieldOptions.ForMeshes());

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Runtime Material");
                ImGui.TableSetColumnIndex(1);
                ImGuiAssetUtilities.DrawAssetField("RuntimeMaterial", renderer.Material, asset => renderer.Material = asset, AssetFieldOptions.ForMaterials());

                ImGui.EndTable();
            }

            if (renderer.Mesh is not null)
                ImGui.TextDisabled($"Runtime Mesh: {FormatAssetLabel(renderer.Mesh.Name, renderer.Mesh)}");
            if (renderer.Material is not null)
                ImGui.TextDisabled($"Runtime Material Pass: {DescribeRenderPass(renderer.Material.RenderPass)}");
        }

        runtimeMesh?.RenderInfo.LocalCullingVolume = subMesh.CullingBounds ?? subMesh.Bounds;
    }

    private static void UpdateLodDistance(
        SubMesh subMesh,
        SubMeshLOD lod,
        float newDistance,
        LinkedListNode<RenderableMesh.RenderableLOD>? runtimeNode)
    {
        if (MathF.Abs(lod.MaxVisibleDistance - newDistance) < 0.0001f)
            return;

        lod.MaxVisibleDistance = newDistance;

        var resorted = subMesh.LODs.ToList();
        subMesh.LODs.Clear();
        foreach (var entry in resorted.OrderBy(x => x.MaxVisibleDistance))
            subMesh.LODs.Add(entry);

        if (runtimeNode is not null)
        {
            var current = runtimeNode.Value;
            runtimeNode.Value = current with { MaxVisibleDistance = newDistance };
        }
    }
}
