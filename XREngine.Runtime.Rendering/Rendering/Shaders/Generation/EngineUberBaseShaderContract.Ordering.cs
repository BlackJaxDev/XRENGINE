using System.Text.Json.Nodes;

namespace XREngine.Rendering.Shaders.Generation;

public static partial class EngineUberBaseShaderContract
{
    private static void AddOrderGateBindings(JsonArray bindings)
    {
        bindings.Add(new JsonObject
        {
            ["name"] = "AuthoredSourceRanks", ["physicalName"] = "authoredSourceRanks_0", ["group"] = 0, ["binding"] = 2,
            ["kind"] = "read-only-storage", ["visibility"] = new JsonArray("vertex"), ["owner"] = "Engine", ["frequency"] = "Frame",
            ["bytes"] = 4, ["dynamic"] = false, ["members"] = new JsonArray(), ["runtimeArray"] = true,
        });
        string[] names = ["sourceIndex", "activeRank", "sourceCount", "enabled"];
        string[] providers = ["AuthoredSourceIndex", "AuthoredActiveRank", "AuthoredSourceCount", "AuthoredOrderEnabled"];
        JsonArray members = [];
        for (int i = 0; i < names.Length; i++)
            members.Add(new JsonObject { ["name"] = names[i] + "_0", ["provider"] = providers[i], ["offset"] = i * 4, ["bytes"] = 4, ["type"] = "u32" });
        bindings.Add(new JsonObject
        {
            ["name"] = "AuthoredOrderGate", ["physicalName"] = "authoredOrderGate_0", ["group"] = 0, ["binding"] = 3,
            ["kind"] = "uniform", ["visibility"] = new JsonArray("vertex"), ["owner"] = "Engine", ["frequency"] = "Object",
            ["bytes"] = 16, ["dynamic"] = true, ["members"] = members,
        });
    }
}
