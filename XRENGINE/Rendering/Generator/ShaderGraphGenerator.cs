using System;
using System.Linq;
using XREngine.Core.Tools;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders.Generator;

public sealed class ShaderGraphGenerator : ShaderGeneratorBase
{
    public ShaderGraphGenerator(ShaderGraph graph, XRMesh? mesh = null)
        : base(mesh ?? new XRMesh())
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        ShaderVersion = graph.Version;
        WriteGLPerVertexOutStruct = false;
        ApplyInterface();
        ApplyConstants();
        ApplyMethods();
    }

    public ShaderGraph Graph { get; }

    protected override void WriteMain()
    {
        var declaredSymbols = Graph.GetDeclaredSymbolNames();

        foreach (var node in Graph.GetInvocationOrder())
        {
            string call = $"{node.MethodName}({string.Join(", ", node.Inputs.Select(GetInputBinding))})";

            if (node.OutputType is null || string.IsNullOrWhiteSpace(node.OutputName))
            {
                Line($"{call};");
                continue;
            }

            string resultName = node.OutputName!;
            string typeName = node.OutputType.Value.ToString()[1..];
            bool declare = !declaredSymbols.Contains(resultName);

            Line(declare
                ? $"{typeName} {resultName} = {call};"
                : $"{resultName} = {call};");

            declaredSymbols.Add(resultName);
        }
    }

    private string GetInputBinding(ShaderGraphInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.SourceVariable))
            return input.SourceVariable!;
        return input.Name;
    }

    private void ApplyInterface()
    {
        foreach (var uniform in Graph.Uniforms)
        {
            UniformNames[uniform.Name] = (uniform.Type ?? EShaderVarType._float, uniform.IsArray);
        }

        foreach (var attribute in Graph.Attributes)
        {
            InputVars[attribute.Name] = (attribute.LayoutLocation, attribute.Type ?? EShaderVarType._float);
        }

        foreach (var output in Graph.Outputs)
        {
            OutputVars[output.Name] = (output.LayoutLocation, output.Type ?? EShaderVarType._float);
        }
    }

    private void ApplyConstants()
    {
        foreach (var constant in Graph.Consts)
        {
            HelperMethodWriters.Add(() =>
            {
                string typeName = constant.TypeName;
                string arraySuffix = constant.IsArray ? "[]" : string.Empty;
                string defaultValue = string.IsNullOrWhiteSpace(constant.DefaultValue) ? string.Empty : $" = {constant.DefaultValue}";
                Line($"const {typeName} {constant.Name}{arraySuffix}{defaultValue};");
            });
        }
    }

    private void ApplyMethods()
    {
        foreach (var method in Graph.Methods.Where(m => !m.IsMain))
        {
            HelperMethodWriters.Add(() =>
            {
                string parameters = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
                Line($"{method.ReturnTypeName} {method.Name}({parameters})");
                using (OpenBracketState())
                {
                    foreach (var line in method.Body.Split('\n'))
                        Line(line.TrimEnd());
                }
                Line();
            });
        }
    }
}
