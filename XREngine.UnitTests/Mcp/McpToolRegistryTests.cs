using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using XREngine.Editor.Mcp;

namespace XREngine.UnitTests.Mcp;

[TestFixture]
public class McpToolRegistryTests
{
    [Test]
    public void Registry_ContainsWorkflowParityTools()
    {
        string[] expected =
        [
            "undo",
            "redo",
            "clear_selection",
            "create_primitive_shape",
            "enter_play_mode",
            "exit_play_mode",
            "save_world",
            "load_world"
        ];

        var names = McpToolRegistry.Tools.Select(t => t.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        foreach (var name in expected)
            names.Contains(name).ShouldBeTrue($"Expected MCP tool '{name}' to be registered.");
    }

    [Test]
    public void CreateSceneNodeSchema_MapsRequiredAndOptionalArguments()
    {
        var tool = McpToolRegistry.Tools.First(t => t.Name == "create_scene_node");
        var schema = JsonSerializer.SerializeToElement(tool.InputSchema);

        schema.GetProperty("type").GetString().ShouldBe("object");
        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();

        var required = schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        required.ShouldContain("name");
        required.ShouldNotContain("parent_id");
        required.ShouldNotContain("scene_name");

        var properties = schema.GetProperty("properties");
        properties.TryGetProperty("name", out _).ShouldBeTrue();
        properties.TryGetProperty("parent_id", out _).ShouldBeTrue();
        properties.TryGetProperty("scene_name", out _).ShouldBeTrue();
    }

    [Test]
    public void CreatePrimitiveShapeSchema_UsesNumberForSizeAndBooleanForAppendLikeFlags()
    {
        var primitive = McpToolRegistry.Tools.First(t => t.Name == "create_primitive_shape");
        var primitiveSchema = JsonSerializer.SerializeToElement(primitive.InputSchema);
        primitiveSchema.GetProperty("properties").GetProperty("size").GetProperty("type").GetString().ShouldBe("number");

        var selector = McpToolRegistry.Tools.First(t => t.Name == "select_node_by_name");
        var selectorSchema = JsonSerializer.SerializeToElement(selector.InputSchema);
        selectorSchema.GetProperty("properties").GetProperty("append").GetProperty("type").GetString().ShouldBe("boolean");
    }
}
