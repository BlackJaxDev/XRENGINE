using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Editor;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.OpenGL;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector : IXRAssetInspector
{
    private const float TexturePreviewMaxEdge = 96.0f;
    private const float TexturePreviewFallbackEdge = 64.0f;

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        if (targets.Targets.Count != 1 || targets.PrimaryTarget is not XRMaterial material)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(targets, visitedObjects);
            return;
        }

        DrawHeader(material);
        DrawShaderList(material);
        DrawRenderOptions(material, visitedObjects);
        DrawUniforms(material);
        DrawUniformBlocks(material);
        DrawSamplerList(material);
        DrawDefaultInspector(material, visitedObjects);
    }

    private static void DrawHeader(XRMaterial material)
    {
        ImGui.TextUnformatted(material.Name ?? "<unnamed material>");
        ImGui.TextDisabled($"Render Pass: {DescribeRenderPass(material.RenderPass)}");
    }

    private static void DrawShaderList(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Shaders", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (material.Shaders.Count == 0)
        {
            ImGui.TextDisabled("No shaders assigned.");
            return;
        }

        for (int i = 0; i < material.Shaders.Count; i++)
        {
            XRShader shader = material.Shaders[i];
            string name = shader?.Name ?? shader?.Source?.FilePath ?? $"Shader {i}";
            string type = shader?.Type.ToString() ?? "Unknown";
            ImGui.BulletText($"{name} ({type})");
        }
    }

    private static void DrawRenderOptions(XRMaterial material, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Render Options", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        int renderPass = material.RenderPass;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputInt("Render Pass", ref renderPass))
        {
            material.RenderPass = renderPass;
            material.MarkDirty();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Render pass bucket used by the active pipeline.");

        EditorImGuiUI.DrawRuntimeObjectInspector("Render Option Settings", material.RenderOptions, visitedObjects, defaultOpen: true);
    }

    private static void DrawUniforms(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Uniforms", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var parameterLookup = BuildParameterLookup(material);
        bool anyUniforms = false;

        for (int shaderIndex = 0; shaderIndex < material.Shaders.Count; shaderIndex++)
        {
            XRShader shader = material.Shaders[shaderIndex];
            if (shader?.Source?.Text is not { } text)
                continue;

            var parsed = ParseShaderSource(text);
            if (parsed.Uniforms.Count == 0)
                continue;

            anyUniforms = true;
            string header = FormatShaderHeader(shader, shaderIndex, "Uniforms");
            if (!ImGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            DrawUniformTable(parsed.Uniforms, parameterLookup, material);
            ImGui.TreePop();
        }

        if (!anyUniforms)
            ImGui.TextDisabled("No uniform declarations found in assigned shaders.");
    }

    private static void DrawUniformBlocks(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Uniform Blocks", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var parameterLookup = BuildParameterLookup(material);
        bool anyBlocks = false;

        for (int shaderIndex = 0; shaderIndex < material.Shaders.Count; shaderIndex++)
        {
            XRShader shader = material.Shaders[shaderIndex];
            if (shader?.Source?.Text is not { } text)
                continue;

            var parsed = ParseShaderSource(text);
            if (parsed.Blocks.Count == 0)
                continue;

            anyBlocks = true;
            string header = FormatShaderHeader(shader, shaderIndex, "Blocks");
            if (!ImGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            foreach (var block in parsed.Blocks)
            {
                string blockLabel = string.IsNullOrWhiteSpace(block.InstanceName)
                    ? block.BlockName
                    : $"{block.BlockName} ({block.InstanceName})";

                if (!ImGui.TreeNodeEx(blockLabel, ImGuiTreeNodeFlags.DefaultOpen))
                    continue;

                DrawUniformBlockMembers(block, parameterLookup, material);
                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        if (!anyBlocks)
            ImGui.TextDisabled("No uniform blocks found in assigned shaders.");
    }

    private static void DrawSamplerList(XRMaterial material)
    {
        if (!ImGui.CollapsingHeader("Texture Samplers", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var samplers = CollectSamplerDefinitions(material);
        if (samplers.Count == 0)
        {
            ImGui.TextDisabled("No sampler uniforms found in assigned shaders.");
            return;
        }

        if (ImGui.BeginTable("SamplerTable", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 50.0f);
            ImGui.TableSetupColumn("Sampler", ImGuiTableColumnFlags.WidthStretch, 0.25f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120.0f);
            ImGui.TableSetupColumn("Texture", ImGuiTableColumnFlags.WidthStretch, 0.35f);
            ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthFixed, 140.0f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < samplers.Count; i++)
            {
                var sampler = samplers[i];
                XRTexture? texture = i < material.Textures.Count ? material.Textures[i] : null;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(i.ToString(CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(sampler.Name);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(sampler.TypeLabel);

                ImGui.TableSetColumnIndex(3);
                ImGui.PushID($"SamplerTexture_{i}");
                DrawSamplerTextureField(material, sampler, i, texture);
                ImGui.PopID();

                ImGui.TableSetColumnIndex(4);
                DrawTexturePreviewCell(texture, TexturePreviewMaxEdge);
            }

            ImGui.EndTable();
        }
    }

    private static void DrawDefaultInspector(XRMaterial material, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Advanced Properties"))
            return;

        EditorImGuiUI.DrawDefaultAssetInspector(material, visitedObjects);
    }

    private static string DescribeRenderPass(int renderPass)
    {
        if (Enum.IsDefined(typeof(EDefaultRenderPass), renderPass))
            return ((EDefaultRenderPass)renderPass).ToString();
        return renderPass.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, ShaderVar> BuildParameterLookup(XRMaterial material)
    {
        var lookup = new Dictionary<string, ShaderVar>(StringComparer.Ordinal);
        foreach (var param in material.Parameters)
        {
            if (param?.Name is { Length: > 0 } name)
                lookup[name] = param;
        }

        return lookup;
    }

    private static void DrawUniformTable(IReadOnlyList<UniformEntry> uniforms, Dictionary<string, ShaderVar> parameters, XRMaterial material)
    {
        if (!ImGui.BeginTable("UniformTable", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableHeadersRow();

        foreach (var uniform in uniforms)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(uniform.Name);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(uniform.TypeLabel);

            ImGui.TableSetColumnIndex(2);
            ShaderVar? param = FindParameter(parameters, uniform.Name);
            if (param is null)
            {
                ImGui.TextDisabled("<missing parameter>");
            }
            else
            {
                ImGui.PushID(param.GetHashCode());
                DrawShaderParameterControl(material, param);
                ImGui.PopID();
            }
        }

        ImGui.EndTable();
    }

    private static void DrawUniformBlockMembers(UniformBlockEntry block, Dictionary<string, ShaderVar> parameters, XRMaterial material)
    {
        if (!ImGui.BeginTable($"Block_{block.BlockName}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Member", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableHeadersRow();

        foreach (var member in block.Members)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(member.Name);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(member.TypeLabel);

            ImGui.TableSetColumnIndex(2);
            ShaderVar? param = FindParameter(parameters, member.Name, block.BlockName, block.InstanceName);
            if (param is null)
            {
                ImGui.TextDisabled("<missing parameter>");
            }
            else
            {
                ImGui.PushID(param.GetHashCode());
                DrawShaderParameterControl(material, param);
                ImGui.PopID();
            }
        }

        ImGui.EndTable();
    }

    private static ShaderVar? FindParameter(Dictionary<string, ShaderVar> parameters, string memberName, string? blockName = null, string? instanceName = null)
    {
        if (parameters.TryGetValue(memberName, out var param))
            return param;

        if (!string.IsNullOrWhiteSpace(blockName) && parameters.TryGetValue($"{blockName}.{memberName}", out param))
            return param;

        if (!string.IsNullOrWhiteSpace(instanceName) && parameters.TryGetValue($"{instanceName}.{memberName}", out param))
            return param;

        return null;
    }

    private static void DrawShaderParameterControl(XRMaterial material, ShaderVar param)
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
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderArrayBase a:
                ImGui.TextDisabled($"Array ({a.Length} elements)");
                break;
            case ShaderInt i:
                {
                    int value = i.Value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragInt("##Int", ref value))
                    {
                        i.SetValue(value);
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderUInt ui:
                {
                    uint value = ui.Value;
                    int intValue = (int)value;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragInt("##UInt", ref intValue, 1.0f, 0, int.MaxValue))
                    {
                        ui.SetValue((uint)intValue);
                        material.MarkDirty();
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
                        material.MarkDirty();
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
                        material.MarkDirty();
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
                        material.MarkDirty();
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
                        material.MarkDirty();
                    }
                    break;
                }
            case ShaderMat4:
                ImGui.TextDisabled("Matrix4x4 (edit not implemented)");
                break;
            default:
                ImGui.TextDisabled(param.GetType().Name);
                break;
        }
    }

    private static void DrawSamplerTextureField(XRMaterial material, SamplerEntry sampler, int slotIndex, XRTexture? currentTexture)
    {
        void Assign(XRTexture? tex)
        {
            EnsureTextureSlots(material, slotIndex + 1);
            material.Textures[slotIndex] = tex;
            material.MarkDirty();
        }

        switch (sampler.Kind)
        {
            case SamplerKind.Texture1D:
                ImGuiAssetUtilities.DrawAssetField("Texture1D", currentTexture as XRTexture1D, asset => Assign(asset));
                break;
            case SamplerKind.Texture1DArray:
                ImGuiAssetUtilities.DrawAssetField("Texture1DArray", currentTexture as XRTexture1DArray, asset => Assign(asset));
                break;
            case SamplerKind.Texture2D:
                ImGuiAssetUtilities.DrawAssetField("Texture2D", currentTexture as XRTexture2D, asset => Assign(asset));
                break;
            case SamplerKind.Texture2DArray:
                ImGuiAssetUtilities.DrawAssetField("Texture2DArray", currentTexture as XRTexture2DArray, asset => Assign(asset));
                break;
            case SamplerKind.Texture3D:
                ImGuiAssetUtilities.DrawAssetField("Texture3D", currentTexture as XRTexture3D, asset => Assign(asset));
                break;
            case SamplerKind.TextureCube:
                ImGuiAssetUtilities.DrawAssetField("TextureCube", currentTexture as XRTextureCube, asset => Assign(asset));
                break;
            case SamplerKind.TextureCubeArray:
                ImGuiAssetUtilities.DrawAssetField("TextureCubeArray", currentTexture as XRTextureCubeArray, asset => Assign(asset));
                break;
            case SamplerKind.TextureRectangle:
                ImGuiAssetUtilities.DrawAssetField("TextureRectangle", currentTexture as XRTextureRectangle, asset => Assign(asset));
                break;
            case SamplerKind.TextureBuffer:
                ImGuiAssetUtilities.DrawAssetField("TextureBuffer", currentTexture as XRTextureBuffer, asset => Assign(asset));
                break;
            default:
                ImGui.TextDisabled("Unsupported sampler type");
                break;
        }
    }

    private static void EnsureTextureSlots(XRMaterial material, int count)
    {
        while (material.Textures.Count < count)
            material.Textures.Add(null);
    }

    private static void DrawTexturePreviewCell(XRTexture? texture, float maxSize)
    {
        if (texture is null)
        {
            ImGui.TextDisabled("<none>");
            return;
        }

        if (!TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? reason))
        {
            ImGui.TextDisabled(reason ?? "Preview unavailable");
            return;
        }

        float scale = MathF.Min(maxSize / displaySize.X, maxSize / displaySize.Y);
        if (scale < 1f)
            displaySize *= scale;

        ImGui.Image(handle, displaySize);
        if (pixelSize.X > 0 && pixelSize.Y > 0)
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

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            failureReason = "Preview requires OpenGL renderer";
            return false;
        }

        if (texture is not XRTexture2D tex2D)
        {
            failureReason = $"{texture.GetType().Name} preview not supported";
            return false;
        }

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

    private static ParsedShaderData ParseShaderSource(string source)
    {
        string cleaned = StripComments(source);
        var blocks = ParseUniformBlocks(cleaned);
        string sourceWithoutBlocks = UniformBlockRegex().Replace(cleaned, string.Empty);
        var uniforms = ParseUniforms(sourceWithoutBlocks);

        return new ParsedShaderData(uniforms, blocks);
    }

    private static IReadOnlyList<UniformEntry> ParseUniforms(string source)
    {
        var results = new List<UniformEntry>();
        foreach (Match match in UniformRegex().Matches(source))
        {
            string type = match.Groups["type"].Value;
            if (IsSamplerType(type))
                continue;

            string name = match.Groups["name"].Value;
            int arraySize = ParseArraySize(match.Groups["array"].Value);
            results.Add(new UniformEntry(name, type, arraySize));
        }

        return results;
    }

    private static IReadOnlyList<UniformBlockEntry> ParseUniformBlocks(string source)
    {
        var results = new List<UniformBlockEntry>();
        foreach (Match match in UniformBlockRegex().Matches(source))
        {
            string blockName = match.Groups["block"].Value;
            string body = match.Groups["body"].Value;
            string instanceName = match.Groups["instance"].Value;

            var members = new List<UniformEntry>();
            foreach (Match memberMatch in BlockMemberRegex().Matches(body))
            {
                string type = memberMatch.Groups["type"].Value;
                string name = memberMatch.Groups["name"].Value;
                int arraySize = ParseArraySize(memberMatch.Groups["array"].Value);
                members.Add(new UniformEntry(name, type, arraySize));
            }

            results.Add(new UniformBlockEntry(blockName, instanceName, members));
        }

        return results;
    }

    private static List<SamplerEntry> CollectSamplerDefinitions(XRMaterial material)
    {
        var samplers = new List<SamplerEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int shaderIndex = 0; shaderIndex < material.Shaders.Count; shaderIndex++)
        {
            XRShader shader = material.Shaders[shaderIndex];
            if (shader?.Source?.Text is not { } text)
                continue;

            string cleaned = StripComments(text);
            foreach (Match match in UniformRegex().Matches(cleaned))
            {
                string type = match.Groups["type"].Value;
                if (!IsSamplerType(type))
                    continue;

                string name = match.Groups["name"].Value;
                int arraySize = ParseArraySize(match.Groups["array"].Value);

                if (!seen.Add(name))
                    continue;

                var kind = MapSamplerKind(type);
                if (arraySize > 0)
                {
                    for (int i = 0; i < arraySize; i++)
                    {
                        string indexedName = $"{name}[{i}]";
                        string typeLabel = $"{type}[{arraySize}]";
                        samplers.Add(new SamplerEntry(indexedName, typeLabel, kind, arraySize));
                    }
                }
                else
                {
                    string typeLabel = type;
                    samplers.Add(new SamplerEntry(name, typeLabel, kind, arraySize));
                }
            }
        }

        return samplers;
    }

    private static bool IsSamplerType(string type)
        => type.StartsWith("sampler", StringComparison.OrdinalIgnoreCase);

    private static SamplerKind MapSamplerKind(string samplerType)
    {
        return samplerType switch
        {
            "sampler1D" => SamplerKind.Texture1D,
            "sampler1DArray" => SamplerKind.Texture1DArray,
            "sampler2D" => SamplerKind.Texture2D,
            "sampler2DShadow" => SamplerKind.Texture2D,
            "sampler2DArray" => SamplerKind.Texture2DArray,
            "sampler2DArrayShadow" => SamplerKind.Texture2DArray,
            "sampler3D" => SamplerKind.Texture3D,
            "samplerCube" => SamplerKind.TextureCube,
            "samplerCubeShadow" => SamplerKind.TextureCube,
            "samplerCubeArray" => SamplerKind.TextureCubeArray,
            "sampler2DRect" => SamplerKind.TextureRectangle,
            "samplerBuffer" => SamplerKind.TextureBuffer,
            _ => SamplerKind.Unknown,
        };
    }

    private static int ParseArraySize(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    private static string StripComments(string source)
    {
        string withoutBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string FormatShaderHeader(XRShader shader, int index, string suffix)
    {
        string name = shader?.Name ?? shader?.Source?.FilePath ?? $"Shader {index}";
        return $"{name}##{suffix}_{index}";
    }

    private sealed record ParsedShaderData(
        IReadOnlyList<UniformEntry> Uniforms,
        IReadOnlyList<UniformBlockEntry> Blocks);

    private sealed record UniformEntry(string Name, string Type, int ArraySize)
    {
        public string TypeLabel => ArraySize > 0 ? $"{Type}[{ArraySize}]" : Type;
    }

    private sealed record UniformBlockEntry(string BlockName, string InstanceName, List<UniformEntry> Members);

    private sealed record SamplerEntry(string Name, string TypeLabel, SamplerKind Kind, int ArraySize);

    private enum SamplerKind
    {
        Unknown,
        Texture1D,
        Texture1DArray,
        Texture2D,
        Texture2DArray,
        Texture3D,
        TextureCube,
        TextureCubeArray,
        TextureRectangle,
        TextureBuffer,
    }

    [GeneratedRegex(@"(?:layout\s*\([^\)]*\)\s*)?uniform\s+(?<block>\w+)\s*\{(?<body>.*?)\}\s*(?<instance>\w+)?\s*;", RegexOptions.Singleline)]
    private static partial Regex UniformBlockRegex();

    [GeneratedRegex(@"(?<type>\w+)\s+(?<name>\w+)\s*(?:\[(?<array>\d+)\])?\s*;")]
    private static partial Regex BlockMemberRegex();

    [GeneratedRegex(@"(?:layout\s*\([^\)]*\)\s*)?uniform\s+(?<type>\w+)\s+(?<name>\w+)\s*(?:\[(?<array>\d+)\])?\s*;", RegexOptions.Singleline)]
    private static partial Regex UniformRegex();
}
