using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.OpenGL;

namespace XREngine.Editor.ComponentEditors;

public sealed class ModelComponentEditor : IXRComponentEditor
{
    private sealed class AdvancedToggleState
    {
        public bool Enabled;
    }

    private static readonly ConditionalWeakTable<XRComponent, AdvancedToggleState> _advancedPropertiesState = new();
    private static readonly Vector4 ActiveLodHighlight = new(0.20f, 0.50f, 0.90f, 0.18f);
    private const float TexturePreviewMaxEdge = 96.0f;
    private const float TexturePreviewFallbackEdge = 64.0f;

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not ModelComponent modelComponent)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            return;
        }

        bool advanced = GetAdvancedPropertiesState(modelComponent);

        ImGui.PushID(modelComponent.GetHashCode());
        if (ImGui.Checkbox("Advanced Properties", ref advanced))
            SetAdvancedPropertiesState(modelComponent, advanced);
        ImGui.PopID();

        if (advanced)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            return;
        }

        DrawModelOverview(modelComponent);
    }

    private static bool GetAdvancedPropertiesState(ModelComponent component)
        => _advancedPropertiesState.TryGetValue(component, out var state) && state.Enabled;

    private static void SetAdvancedPropertiesState(ModelComponent component, bool enabled)
    {
        if (enabled)
            _advancedPropertiesState.GetValue(component, _ => new AdvancedToggleState()).Enabled = true;
        else
            _advancedPropertiesState.Remove(component);
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
    }

    private static void DrawSubmeshSection(ModelComponent modelComponent, int index, SubMesh subMesh, RenderableMesh? runtimeMesh)
    {
        string submeshName = string.IsNullOrEmpty(subMesh.Name) ? "<unnamed>" : subMesh.Name;
        string headerLabel = $"Submesh {index}: {submeshName} ({subMesh.LODs.Count} LOD{(subMesh.LODs.Count == 1 ? string.Empty : "s")})";

        if (!ImGui.TreeNodeEx(headerLabel, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var bounds = subMesh.Bounds;
        ImGui.TextUnformatted($"Bounds Min: ({bounds.Min.X:F2}, {bounds.Min.Y:F2}, {bounds.Min.Z:F2})");
        ImGui.TextUnformatted($"Bounds Max: ({bounds.Max.X:F2}, {bounds.Max.Y:F2}, {bounds.Max.Z:F2})");

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp
                                          | ImGuiTableFlags.RowBg
                                          | ImGuiTableFlags.BordersOuter
                                          | ImGuiTableFlags.BordersInnerV
                                          | ImGuiTableFlags.NoSavedSettings;

        if (ImGui.BeginTable($"Submesh{index}_LODs", 6, tableFlags))
        {
            ImGui.TableSetupColumn("LOD", ImGuiTableColumnFlags.WidthFixed, 30.0f);
            ImGui.TableSetupColumn("Distance", ImGuiTableColumnFlags.WidthFixed, 30.0f);
            ImGui.TableSetupColumn("Data", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableHeadersRow();

            var runtimeLodNode = runtimeMesh?.LODs.First;
            int lodIndex = 0;

            foreach (var lod in subMesh.LODs)
            {
                var runtimeLod = runtimeLodNode?.Value;
                runtimeLodNode = runtimeLodNode?.Next;

                ImGui.TableNextRow();

                if (runtimeMesh is not null && runtimeMesh.CurrentLOD?.Value == runtimeLod)
                {
                    uint highlight = ImGui.ColorConvertFloat4ToU32(ActiveLodHighlight);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, highlight);
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, highlight);
                }

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"#{lodIndex}");
                
                ImGui.TableSetColumnIndex(1);
                float maxDistance = lod.MaxVisibleDistance;
                ImGui.DragFloat($"##Distance", ref maxDistance, 0.1f, 0.0f, float.MaxValue, "%.2f");
                lod.MaxVisibleDistance = maxDistance;

                ImGui.TableSetColumnIndex(2);

                if (runtimeLod?.Renderer?.Mesh?.HasBlendshapes == true)
                    DrawBlendshapeControls(modelComponent, subMesh, runtimeLod);

                DrawMaterialControls(subMesh, runtimeLod);

                lodIndex++;
            }

            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private static void DrawBlendshapeControls(ModelComponent modelComponent, SubMesh subMesh, RenderableMesh.RenderableLOD? runtimeLOD)
    {
        XRMeshRenderer? renderer = runtimeLOD?.Renderer;
        if (renderer?.Mesh is null || !renderer.Mesh.HasBlendshapes)
            return;

        if (!ImGui.CollapsingHeader($"Blendshapes##Blend_{subMesh.GetHashCode()}", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var mesh = renderer.Mesh;
        uint blendshapeCount = mesh.BlendshapeCount;
        if (blendshapeCount == 0)
        {
            ImGui.TextDisabled("No blendshapes.");
            return;
        }

        if (ImGui.BeginTable($"Blendshapes_{subMesh.GetHashCode()}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Blendshape", ImGuiTableColumnFlags.WidthStretch, 0.4f);
            ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthStretch, 0.4f);
            ImGui.TableSetupColumn("Normalized", ImGuiTableColumnFlags.WidthStretch, 0.2f);
            ImGui.TableHeadersRow();

            for (uint i = 0; i < blendshapeCount; i++)
            {
                string name = mesh.BlendshapeNames[(int)i];

                float percent = renderer.GetBlendshapeWeight(i);
                float normalized = renderer.GetBlendshapeWeightNormalized(i);

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(name);

                ImGui.TableSetColumnIndex(1);
                ImGui.PushID($"BlendshapePct_{subMesh.GetHashCode()}_{i}");
                float pctValue = percent;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.SliderFloat("##Pct", ref pctValue, 0.0f, 100.0f, "%.1f%%"))
                {
                    modelComponent.SetBlendShapeWeight(name, pctValue);
                    renderer.PushBlendshapeWeightsToGPU();
                }
                ImGui.PopID();

                ImGui.TableSetColumnIndex(2);
                ImGui.PushID($"BlendshapeNorm_{subMesh.GetHashCode()}_{i}");
                float normValue = normalized;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.SliderFloat("##Norm", ref normValue, 0.0f, 1.0f, "%.3f"))
                {
                    modelComponent.SetBlendShapeWeightNormalized(name, normValue);
                    renderer.PushBlendshapeWeightsToGPU();
                }
                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private static void DrawMaterialControls(SubMesh subMesh, RenderableMesh.RenderableLOD? runtimeLOD)
    {
        XRMeshRenderer? renderer = runtimeLOD?.Renderer;
        XRMaterial? material = renderer?.Material ?? subMesh.LODs.FirstOrDefault()?.Material;

        if (material is null)
            return;

        if (!ImGui.CollapsingHeader($"Material##Mat_{subMesh.GetHashCode()}", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextUnformatted(material.Name ?? "<unnamed material>");

        var parameters = material.Parameters;
        if (parameters is null || parameters.Length == 0)
        {
            ImGui.TextDisabled("No uniform parameters.");
        }
        else if (ImGui.BeginTable($"MaterialParams_{subMesh.GetHashCode()}", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.4f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < parameters.Length; i++)
            {
                ShaderVar param = parameters[i];
                string name = param.Name ?? $"Param{i}";

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(name);

                ImGui.TableSetColumnIndex(1);
                ImGui.PushID($"MatParam_{subMesh.GetHashCode()}_{i}");
                DrawShaderParameterControl(renderer, param);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        var textures = material.Textures;
        if (textures is null || textures.Count == 0)
        {
            ImGui.TextDisabled("No textures.");
            return;
        }
        if (ImGui.BeginTable($"MaterialTextures_{subMesh.GetHashCode()}", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.4f);
            ImGui.TableSetupColumn("Texture", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < textures.Count; i++)
            {
                XRTexture? tex = textures[i];
                string name = tex?.Name ?? $"Texture{i}";
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(name);
                ImGui.TableSetColumnIndex(1);
                ImGui.PushID($"MatTex_{subMesh.GetHashCode()}_{i}");
                DrawTexturePreviewCell(tex);
                ImGui.PopID();
            }

            ImGui.EndTable();
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
                        if (renderer is not null)
                            renderer.Material?.MarkDirty();
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
                        if (renderer is not null)
                            renderer.Material?.MarkDirty();
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
                        if (renderer is not null)
                            renderer.Material?.MarkDirty();
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
                        if (renderer is not null)
                            renderer.Material?.MarkDirty();
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
                        if (renderer is not null)
                            renderer.Material?.MarkDirty();
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
    {
        if (texture is null)
        {
            ImGui.TextDisabled("<null>");
            return;
        }

        bool hasPreview = TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason);

        if (hasPreview)
        {
            ImGui.Image(handle, displaySize);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(FormatAssetLabel(texture.Name, texture));
                if (pixelSize.X > 0f && pixelSize.Y > 0f)
                    ImGui.TextUnformatted($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
                ImGui.EndTooltip();
            }
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
}
