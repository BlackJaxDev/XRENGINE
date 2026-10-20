using System.Text.Json.Nodes;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

public static partial class EngineUberBaseShaderContract
{
    /// <summary>Builds a schema-three recipe without changing the existing material-recipe grammar.</summary>
    public static JsonObject CreateRecipe(string name, string sourceFile, uint features, string pass)
    {
        bool forward = pass is Pass or OrderPass;
        bool orderGate = pass == OrderPass;
        if ((features & ~31u) != 0 || pass is not (Pass or OrderPass or "depth-normal" or "depth" or "point-shadow-depth" or "spot-shadow-depth"))
            throw new ArgumentException("Unsupported Uber base recipe profile.");
        JsonArray bindings = (JsonArray)JsonNode.Parse(ForwardBindings)!;
        JsonObject viewBlock = (JsonObject)bindings[0]!;
        viewBlock["bytes"] = 96;
        ((JsonArray)viewBlock["members"]!).Add(new JsonObject
            { ["name"] = "RenderTime_0", ["provider"] = "RenderTime", ["offset"] = 80, ["bytes"] = 4, ["type"] = "f32" });
        if (forward) AddEnvironmentBindings(bindings);
        if (orderGate) AddOrderGateBindings(bindings);
        if (!forward)
        {
            for (int index = bindings.Count - 1; index >= 0; index--)
                if (bindings[index]!["group"]!.GetValue<int>() >= 2) bindings.RemoveAt(index);
            JsonObject objectBlock = (JsonObject)bindings[1]!;
            objectBlock["bytes"] = 64;
            ((JsonArray)objectBlock["members"]!).RemoveAt(1);
        }
        JsonArray members = [];
        foreach (ShaderAbiMemberContract member in UberBaseParameterSchema.Members)
            members.Add(new JsonObject { ["name"] = member.PhysicalName, ["provider"] = member.ProviderName,
                ["offset"] = member.Offset, ["bytes"] = member.Size, ["type"] = member.PhysicalType });
        bindings.Add(new JsonObject
        {
            ["name"] = "UberBaseMaterial", ["physicalName"] = "uberMaterial_0", ["group"] = 1, ["binding"] = 15,
            ["kind"] = "uniform", ["visibility"] = new JsonArray("fragment"), ["owner"] = "Material", ["frequency"] = "Material",
            ["bytes"] = UberBaseParameterSchema.ByteSize, ["dynamic"] = true, ["members"] = members,
        });
        int textureCount = 0;
        for (int role = 0; role < 7; role++)
        {
            bool active = role switch { 0 => true, 1 => forward && (features & 1u) != 0,
                2 => (features & 2u) != 0, 3 or 4 or 5 => forward && (features & 4u) != 0,
                6 => forward && (features & 8u) != 0, _ => false };
            if (!active) continue;
            textureCount++;
            string sampler = role switch { 0 => "_MainTex", 1 => "_BumpMap", 2 => "_AlphaMask", 3 => "_PBRMetallicMaps",
                4 => "_PBRSmoothnessMaps", 5 => "_SpecularMap", _ => "_EmissionMap" };
            string physical = role == 5 ? "U_SpecularMap" : sampler;
            bindings.Add(new JsonObject { ["name"] = sampler, ["physicalName"] = physical + "_0", ["group"] = 1,
                ["binding"] = role * 2 + 1, ["kind"] = "texture-2d-float", ["visibility"] = new JsonArray("fragment"),
                ["owner"] = "Material", ["frequency"] = "Material", ["bytes"] = 0, ["dynamic"] = false, ["members"] = new JsonArray() });
            bindings.Add(new JsonObject { ["name"] = sampler, ["physicalName"] = physical + "Sampler_0", ["group"] = 1,
                ["binding"] = role * 2 + 2, ["kind"] = "filtering-sampler", ["visibility"] = new JsonArray("fragment"),
                ["owner"] = "Material", ["frequency"] = "Material", ["bytes"] = 0, ["dynamic"] = false, ["members"] = new JsonArray() });
        }
        if (pass == "point-shadow-depth")
            bindings.Add(new JsonObject
            {
                ["name"] = "PointShadowLight", ["physicalName"] = "pointDepth_0", ["group"] = 0, ["binding"] = 3,
                ["kind"] = "uniform", ["visibility"] = new JsonArray("fragment"), ["owner"] = "Engine", ["frequency"] = "View",
                ["bytes"] = 16, ["dynamic"] = true,
                ["members"] = new JsonArray(
                    new JsonObject { ["name"] = "lightPosition_0", ["provider"] = "LightPos", ["offset"] = 0, ["bytes"] = 12, ["type"] = "vec3<f32>" },
                    new JsonObject { ["name"] = "farPlaneDistance_0", ["provider"] = "FarPlaneDist", ["offset"] = 12, ["bytes"] = 4, ["type"] = "f32" }),
            });
        JsonArray attributes = [];
        string[] semantics = ["position", "normal", "tangent-or-zero", "uv0-or-zero", "uv1-or-zero", "uv2-or-zero", "uv3-or-zero", "color0-or-default"];
        string[] formats = ["float32x3", "float32x3", "float32x4", "float32x2", "float32x2", "float32x2", "float32x2", "float32x4"];
        int[] offsets = [0, 12, 24, 40, 48, 56, 64, 72];
        for (int index = 0; index < semantics.Length; index++)
            attributes.Add(new JsonObject { ["location"] = index, ["offset"] = offsets[index], ["format"] = formats[index], ["semantic"] = semantics[index] });
        JsonObject recipe = new()
        {
            ["schemaVersion"] = 3, ["name"] = name, ["pass"] = pass, ["source"] = sourceFile,
            ["sourceLanguage"] = "Slang", ["target"] = "WebGPUWgsl",
            ["entryPoints"] = new JsonObject { ["vertex"] = "uberBaseVertex", ["fragment"] = "uberBaseFragment" },
            ["defines"] = new JsonArray(), ["includes"] = new JsonArray(), ["specialization"] = new JsonObject(),
            ["requiredFeatures"] = new JsonArray(), ["requiredLimits"] = new JsonObject
            {
                ["maxVertexAttributes"] = 8, ["maxVertexBuffers"] = 8, ["maxVertexBufferArrayStride"] = 88,
                ["maxBindGroups"] = forward ? 4 : 2, ["maxBindingsPerBindGroup"] = Math.Max(textureCount * 2 + 1, forward ? 15 : 3),
                ["maxUniformBufferBindingSize"] = forward ? 1088 : 352,
                ["maxDynamicUniformBuffersPerPipelineLayout"] = forward ? (orderGate ? 8 : 7) : pass == "point-shadow-depth" ? 4 : 3,
                ["maxUniformBuffersPerShaderStage"] = forward ? 6 : pass == "point-shadow-depth" ? 3 : 2,
                ["maxSampledTexturesPerShaderStage"] = textureCount + (forward ? 7 : 0),
                ["maxSamplersPerShaderStage"] = textureCount + (forward ? 7 : 0),
                ["maxStorageBuffersPerShaderStage"] = forward ? 5 : 0,
                ["maxStorageBufferBindingSize"] = forward ? 8 * 1024 * 1024 : 0,
            },
            ["matrixLayout"] = "column-major", ["semanticSchemaIdentity"] = orderGate ? OrderGateSchema : Schema,
            ["coordinates"] = "xrengine.webgpu.coordinates.v1",
            ["layout"] = new JsonObject { ["vertexBuffers"] = new JsonArray(new JsonObject
                { ["slot"] = 0, ["stride"] = 88, ["stepMode"] = "vertex", ["attributes"] = attributes }), ["bindings"] = bindings },
            ["pipeline"] = new JsonObject(),
        };
        if (!forward)
        {
            JsonObject limits = (JsonObject)recipe["requiredLimits"]!;
            limits.Remove("maxStorageBuffersPerShaderStage");
            limits.Remove("maxStorageBufferBindingSize");
        }
        return recipe;
    }

    /// <summary>Requires the complete physical layout generated for this exact feature/pass profile.</summary>
    public static bool TryValidateLayout(ShaderProgramArtifact artifact, uint features, string pass, out string reason)
    {
        reason = "UberBase.ProgramAbiMismatch: the exact prepared Uber feature/pass ABI is required.";
        JsonObject recipe = CreateRecipe(artifact.Name, "expected.slang", features, pass);
        if (artifact.DescriptorBytes.IsDefaultOrEmpty) return false;
        using JsonDocument descriptor = JsonDocument.Parse(artifact.DescriptorBytes.AsMemory());
        if (!descriptor.RootElement.TryGetProperty("layout", out JsonElement layout) ||
            !JsonNode.DeepEquals(recipe["layout"], JsonNode.Parse(layout.GetRawText()))) return false;
        JsonObject limits = (JsonObject)recipe["requiredLimits"]!;
        if (artifact.RequiredLimits.Count != limits.Count) return false;
        foreach ((string name, JsonNode? value) in limits)
            if (!artifact.RequiredLimits.TryGetValue(name, out int actual) || actual != value!.GetValue<int>()) return false;
        reason = string.Empty;
        return true;
    }

    private const string ForwardBindings = """
        [
          {
            "name": "View",
            "physicalName": "view_0",
            "group": 0,
            "binding": 0,
            "kind": "uniform",
            "visibility": [
              "vertex",
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 80,
            "dynamic": true,
            "members": [
              {
                "name": "viewProjection_0",
                "provider": "ViewProjection",
                "offset": 0,
                "bytes": 64,
                "type": "mat4x4<f32>"
              },
              {
                "name": "cameraPosition_0",
                "provider": "CameraPosition",
                "offset": 64,
                "bytes": 16,
                "type": "vec4<f32>"
              }
            ]
          },
          {
            "name": "Object",
            "physicalName": "object_0",
            "group": 0,
            "binding": 1,
            "kind": "uniform",
            "visibility": [
              "vertex"
            ],
            "owner": "Engine",
            "frequency": "Object",
            "bytes": 128,
            "dynamic": true,
            "members": [
              {
                "name": "modelMatrix_0",
                "provider": "ModelMatrix",
                "offset": 0,
                "bytes": 64,
                "type": "mat4x4<f32>"
              },
              {
                "name": "normalMatrix_0",
                "provider": "NormalMatrix",
                "offset": 64,
                "bytes": 64,
                "type": "mat4x4<f32>"
              }
            ]
          },
          {
            "name": "ForwardLighting",
            "physicalName": "lighting_0",
            "group": 2,
            "binding": 0,
            "kind": "uniform",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 1088,
            "dynamic": true,
            "members": [
              {
                "name": "lightCounts_0",
                "provider": "ForwardLightCounts",
                "offset": 0,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "globalAmbient_0",
                "provider": "GlobalAmbient",
                "offset": 16,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "directional0Direction_0",
                "provider": "ForwardDirectional0Direction",
                "offset": 32,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "directional0ColorIntensity_0",
                "provider": "ForwardDirectional0ColorIntensity",
                "offset": 48,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "directional1Direction_0",
                "provider": "ForwardDirectional1Direction",
                "offset": 64,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "directional1ColorIntensity_0",
                "provider": "ForwardDirectional1ColorIntensity",
                "offset": 80,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "directional2Direction_0",
                "provider": "ForwardDirectional2Direction",
                "offset": 96,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "directional2ColorIntensity_0",
                "provider": "ForwardDirectional2ColorIntensity",
                "offset": 112,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "directional3Direction_0",
                "provider": "ForwardDirectional3Direction",
                "offset": 128,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "directional3ColorIntensity_0",
                "provider": "ForwardDirectional3ColorIntensity",
                "offset": 144,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point0PositionRadius_0",
                "provider": "ForwardPoint0PositionRadius",
                "offset": 160,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point0ColorIntensity_0",
                "provider": "ForwardPoint0ColorIntensity",
                "offset": 176,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point0Brightness_0",
                "provider": "ForwardPoint0Brightness",
                "offset": 192,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point1PositionRadius_0",
                "provider": "ForwardPoint1PositionRadius",
                "offset": 208,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point1ColorIntensity_0",
                "provider": "ForwardPoint1ColorIntensity",
                "offset": 224,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point1Brightness_0",
                "provider": "ForwardPoint1Brightness",
                "offset": 240,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point2PositionRadius_0",
                "provider": "ForwardPoint2PositionRadius",
                "offset": 256,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point2ColorIntensity_0",
                "provider": "ForwardPoint2ColorIntensity",
                "offset": 272,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point2Brightness_0",
                "provider": "ForwardPoint2Brightness",
                "offset": 288,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point3PositionRadius_0",
                "provider": "ForwardPoint3PositionRadius",
                "offset": 304,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point3ColorIntensity_0",
                "provider": "ForwardPoint3ColorIntensity",
                "offset": 320,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point3Brightness_0",
                "provider": "ForwardPoint3Brightness",
                "offset": 336,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point4PositionRadius_0",
                "provider": "ForwardPoint4PositionRadius",
                "offset": 352,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point4ColorIntensity_0",
                "provider": "ForwardPoint4ColorIntensity",
                "offset": 368,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point4Brightness_0",
                "provider": "ForwardPoint4Brightness",
                "offset": 384,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point5PositionRadius_0",
                "provider": "ForwardPoint5PositionRadius",
                "offset": 400,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point5ColorIntensity_0",
                "provider": "ForwardPoint5ColorIntensity",
                "offset": 416,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point5Brightness_0",
                "provider": "ForwardPoint5Brightness",
                "offset": 432,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point6PositionRadius_0",
                "provider": "ForwardPoint6PositionRadius",
                "offset": 448,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point6ColorIntensity_0",
                "provider": "ForwardPoint6ColorIntensity",
                "offset": 464,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point6Brightness_0",
                "provider": "ForwardPoint6Brightness",
                "offset": 480,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point7PositionRadius_0",
                "provider": "ForwardPoint7PositionRadius",
                "offset": 496,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point7ColorIntensity_0",
                "provider": "ForwardPoint7ColorIntensity",
                "offset": 512,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "point7Brightness_0",
                "provider": "ForwardPoint7Brightness",
                "offset": 528,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot0PositionRadius_0",
                "provider": "ForwardSpot0PositionRadius",
                "offset": 544,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot0ColorIntensity_0",
                "provider": "ForwardSpot0ColorIntensity",
                "offset": 560,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot0DirectionExponent_0",
                "provider": "ForwardSpot0DirectionExponent",
                "offset": 576,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot0CutoffsBrightness_0",
                "provider": "ForwardSpot0CutoffsBrightness",
                "offset": 592,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot1PositionRadius_0",
                "provider": "ForwardSpot1PositionRadius",
                "offset": 608,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot1ColorIntensity_0",
                "provider": "ForwardSpot1ColorIntensity",
                "offset": 624,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot1DirectionExponent_0",
                "provider": "ForwardSpot1DirectionExponent",
                "offset": 640,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot1CutoffsBrightness_0",
                "provider": "ForwardSpot1CutoffsBrightness",
                "offset": 656,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot2PositionRadius_0",
                "provider": "ForwardSpot2PositionRadius",
                "offset": 672,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot2ColorIntensity_0",
                "provider": "ForwardSpot2ColorIntensity",
                "offset": 688,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot2DirectionExponent_0",
                "provider": "ForwardSpot2DirectionExponent",
                "offset": 704,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot2CutoffsBrightness_0",
                "provider": "ForwardSpot2CutoffsBrightness",
                "offset": 720,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot3PositionRadius_0",
                "provider": "ForwardSpot3PositionRadius",
                "offset": 736,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot3ColorIntensity_0",
                "provider": "ForwardSpot3ColorIntensity",
                "offset": 752,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot3DirectionExponent_0",
                "provider": "ForwardSpot3DirectionExponent",
                "offset": 768,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot3CutoffsBrightness_0",
                "provider": "ForwardSpot3CutoffsBrightness",
                "offset": 784,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot4PositionRadius_0",
                "provider": "ForwardSpot4PositionRadius",
                "offset": 800,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot4ColorIntensity_0",
                "provider": "ForwardSpot4ColorIntensity",
                "offset": 816,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot4DirectionExponent_0",
                "provider": "ForwardSpot4DirectionExponent",
                "offset": 832,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot4CutoffsBrightness_0",
                "provider": "ForwardSpot4CutoffsBrightness",
                "offset": 848,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot5PositionRadius_0",
                "provider": "ForwardSpot5PositionRadius",
                "offset": 864,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot5ColorIntensity_0",
                "provider": "ForwardSpot5ColorIntensity",
                "offset": 880,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot5DirectionExponent_0",
                "provider": "ForwardSpot5DirectionExponent",
                "offset": 896,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot5CutoffsBrightness_0",
                "provider": "ForwardSpot5CutoffsBrightness",
                "offset": 912,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot6PositionRadius_0",
                "provider": "ForwardSpot6PositionRadius",
                "offset": 928,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot6ColorIntensity_0",
                "provider": "ForwardSpot6ColorIntensity",
                "offset": 944,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot6DirectionExponent_0",
                "provider": "ForwardSpot6DirectionExponent",
                "offset": 960,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot6CutoffsBrightness_0",
                "provider": "ForwardSpot6CutoffsBrightness",
                "offset": 976,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot7PositionRadius_0",
                "provider": "ForwardSpot7PositionRadius",
                "offset": 992,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot7ColorIntensity_0",
                "provider": "ForwardSpot7ColorIntensity",
                "offset": 1008,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot7DirectionExponent_0",
                "provider": "ForwardSpot7DirectionExponent",
                "offset": 1024,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spot7CutoffsBrightness_0",
                "provider": "ForwardSpot7CutoffsBrightness",
                "offset": 1040,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "ambientOcclusionControls_0",
                "provider": "AmbientOcclusionControls",
                "offset": 1056,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "ambientOcclusionViewport_0",
                "provider": "UberAmbientOcclusionViewport",
                "offset": 1072,
                "bytes": 16,
                "type": "vec4<f32>"
              }
            ]
          },
          {
            "name": "AmbientOcclusionTexture",
            "physicalName": "ambientOcclusionTexture_0",
            "group": 2,
            "binding": 1,
            "kind": "texture-2d-float",
            "visibility": [
              "fragment"
            ],
            "owner": "Material",
            "frequency": "Material",
            "bytes": 0,
            "dynamic": false,
            "members": []
          },
          {
            "name": "AmbientOcclusionTexture",
            "physicalName": "ambientOcclusionSampler_0",
            "group": 2,
            "binding": 2,
            "kind": "filtering-sampler",
            "visibility": [
              "fragment"
            ],
            "owner": "Material",
            "frequency": "Material",
            "bytes": 0,
            "dynamic": false,
            "members": []
          },
          {
            "name": "DirectionalShadow",
            "physicalName": "directionalShadow_0",
            "group": 3,
            "binding": 0,
            "kind": "uniform",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 144,
            "dynamic": true,
            "members": [
              {
                "name": "lightViewProjection_0",
                "provider": "DirectionalShadowViewProjection",
                "offset": 0,
                "bytes": 64,
                "type": "mat4x4<f32>"
              },
              {
                "name": "control_0",
                "provider": "DirectionalShadowControl",
                "offset": 64,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "biasProjection_0",
                "provider": "DirectionalShadowBiasProjection",
                "offset": 80,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "biasParams_0",
                "provider": "DirectionalShadowBiasParams",
                "offset": 96,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "filterParams_0",
                "provider": "DirectionalShadowFilterParams",
                "offset": 112,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "sourceParams_0",
                "provider": "DirectionalShadowSourceParams",
                "offset": 128,
                "bytes": 16,
                "type": "vec4<f32>"
              }
            ]
          },
          {
            "name": "DirectionalShadowMap",
            "physicalName": "directionalShadowMap_0",
            "group": 3,
            "binding": 1,
            "kind": "texture-depth-2d",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 0,
            "dynamic": false,
            "members": []
          },
          {
            "name": "DirectionalShadowMap",
            "physicalName": "directionalShadowSampler_0",
            "group": 3,
            "binding": 2,
            "kind": "comparison-sampler",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 0,
            "dynamic": false,
            "members": []
          },
          {
            "name": "LocalShadows",
            "physicalName": "localShadow_0",
            "group": 3,
            "binding": 3,
            "kind": "uniform",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 240,
            "dynamic": true,
            "members": [
              {
                "name": "spotViewProjection_0",
                "provider": "SpotShadowViewProjection",
                "offset": 0,
                "bytes": 64,
                "type": "mat4x4<f32>"
              },
              {
                "name": "spotControl_0",
                "provider": "SpotShadowControl",
                "offset": 64,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spotPosition_0",
                "provider": "SpotShadowPosition",
                "offset": 80,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spotDirection_0",
                "provider": "SpotShadowDirection",
                "offset": 96,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spotBias_0",
                "provider": "SpotShadowBias",
                "offset": 112,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spotFilter_0",
                "provider": "SpotShadowFilter",
                "offset": 128,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "spotSource_0",
                "provider": "SpotShadowSource",
                "offset": 144,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "pointControl_0",
                "provider": "PointShadowControl",
                "offset": 160,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "pointPosition_0",
                "provider": "PointShadowPosition",
                "offset": 176,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "pointBias_0",
                "provider": "PointShadowBias",
                "offset": 192,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "pointFilter_0",
                "provider": "PointShadowFilter",
                "offset": 208,
                "bytes": 16,
                "type": "vec4<f32>"
              },
              {
                "name": "pointSource_0",
                "provider": "PointShadowSource",
                "offset": 224,
                "bytes": 16,
                "type": "vec4<f32>"
              }
            ]
          },
          {
            "name": "SpotShadowMap",
            "physicalName": "spotShadowMap_0",
            "group": 3,
            "binding": 4,
            "kind": "texture-2d-float",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 0,
            "dynamic": false,
            "members": []
          },
          {
            "name": "SpotShadowMap",
            "physicalName": "spotShadowSampler_0",
            "group": 3,
            "binding": 5,
            "kind": "filtering-sampler",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 0,
            "dynamic": false,
            "members": []
          },
          {
            "name": "PointShadowMap",
            "physicalName": "pointShadowMap_0",
            "group": 3,
            "binding": 6,
            "kind": "texture-cube-float",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 0,
            "dynamic": false,
            "members": []
          },
          {
            "name": "PointShadowMap",
            "physicalName": "pointShadowSampler_0",
            "group": 3,
            "binding": 7,
            "kind": "filtering-sampler",
            "visibility": [
              "fragment"
            ],
            "owner": "Engine",
            "frequency": "View",
            "bytes": 0,
            "dynamic": false,
            "members": []
          }
        ]
        """;
}
