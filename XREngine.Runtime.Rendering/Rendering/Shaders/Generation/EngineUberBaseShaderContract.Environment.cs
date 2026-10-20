using System.Text.Json.Nodes;

namespace XREngine.Rendering.Shaders.Generation;

public static partial class EngineUberBaseShaderContract
{
    private static void AddEnvironmentBindings(JsonArray bindings)
    {
        JsonArray fields = [];
        AddField("ProbeCount", 0, 4, "i32");
        AddField("TetraCount", 4, 4, "i32");
        AddField("ForwardPbrResourcesEnabled", 8, 4, "u32");
        AddField("UseProbeGrid", 12, 4, "u32");
        AddField("ProbeGridOrigin", 16, 12, "vec3<f32>");
        AddField("ProbeGridCellSize", 28, 4, "f32");
        AddField("ProbeGridDims", 32, 12, "vec3<i32>");
        AddField("SpecularOcclusionEnabled", 44, 4, "u32");
        bindings.Add(new JsonObject
        {
            ["name"] = "UberForwardPbr", ["physicalName"] = "uberPbr_0", ["group"] = 2, ["binding"] = 3,
            ["kind"] = "uniform", ["visibility"] = new JsonArray("fragment"), ["owner"] = "Engine", ["frequency"] = "View",
            ["bytes"] = 48, ["dynamic"] = true, ["members"] = fields,
        });
        string[] textures = ["BRDF", "IrradianceArray", "PrefilterArray"];
        for (int index = 0; index < textures.Length; index++)
        {
            string name = textures[index];
            bindings.Add(Resource(name, name + "_0", 4 + index * 2, index == 0 ? "texture-2d-float" : "texture-2d-array-float", 0, false));
            bindings.Add(Resource(name, name + "Sampler_0", 5 + index * 2, "filtering-sampler", 0, false));
        }
        string[] buffers = ["uberPositions", "uberTetrahedra", "uberProbeParameters", "uberGridCells", "uberGridIndices"];
        for (int index = 0; index < buffers.Length; index++)
        {
            JsonObject resource = Resource(buffers[index], buffers[index] + "_0", 20 + index, "read-only-storage", 4, false);
            resource["runtimeArray"] = true;
            bindings.Add(resource);
        }
        return;

        void AddField(string name, int offset, int bytes, string type)
            => fields.Add(new JsonObject { ["name"] = name + "_0", ["provider"] = name, ["offset"] = offset, ["bytes"] = bytes, ["type"] = type });
        static JsonObject Resource(string name, string physicalName, int binding, string kind, int bytes, bool dynamic)
            => new()
            {
                ["name"] = name, ["physicalName"] = physicalName, ["group"] = 2, ["binding"] = binding, ["kind"] = kind,
                ["visibility"] = new JsonArray("fragment"), ["owner"] = "Engine", ["frequency"] = "View",
                ["bytes"] = bytes, ["dynamic"] = dynamic, ["members"] = new JsonArray(),
            };
    }
}
