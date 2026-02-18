using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Core.Tools;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders.Generator;

public enum ShaderGraphNodeKind
{
    Attribute,
    Uniform,
    Constant,
    Output,
    MethodDefinition,
    MethodInvocation
}

public sealed class ShaderGraphInput(string name, EShaderVarType? type, string? source)
{
    public string Name { get; set; } = name;
    public EShaderVarType? Type { get; set; } = type;
    public string? SourceVariable { get; set; } = source;
}

public sealed class ShaderGraphNode(int id, string name, ShaderGraphNodeKind kind)
{
    public int Id { get; } = id;
    public string Name { get; set; } = name;
    public ShaderGraphNodeKind Kind { get; set; } = kind;
    public string? OutputName { get; set; }
    public EShaderVarType? OutputType { get; set; }
    public string? MethodName { get; set; }
    public GLSLManager.Method? MethodDefinition { get; set; }
    public List<ShaderGraphInput> Inputs { get; } = [];
    public Vector2 Position { get; set; }
}

public readonly record struct ShaderGraphEdge(int FromId, int ToId, string InputName, string FromVariable);

public sealed class ShaderGraph
{
    private int _nextId = 1;

    public ShaderGraph(GLSLManager manager)
    {
        Source = manager.RawSource;
        Version = manager.Version;
        Attributes = manager.In.AsReadOnly();
        Uniforms = manager.Uniforms.AsReadOnly();
        Outputs = manager.Out.AsReadOnly();
        Consts = manager.Consts.AsReadOnly();
        Methods = manager.Methods.AsReadOnly();
        Invocations = manager.MainInvocations;

        BuildInterfaceNodes();
        BuildMethodDefinitionNodes();
        BuildInvocationNodes();
    }

    public ShaderGraph()
    {
        Attributes = [];
        Uniforms = [];
        Outputs = [];
        Consts = [];
        Methods = [];
        Invocations = [];
    }

    public string Source { get; } = string.Empty;
    public EGLSLVersion Version { get; init; } = EGLSLVersion.Ver_460;
    public IReadOnlyList<GLSLManager.Variable> Attributes { get; }
    public IReadOnlyList<GLSLManager.Variable> Uniforms { get; }
    public IReadOnlyList<GLSLManager.Variable> Outputs { get; }
    public IReadOnlyList<GLSLManager.Variable> Consts { get; }
    public IReadOnlyList<GLSLManager.Method> Methods { get; }
    public IReadOnlyList<GLSLManager.ParsedInvocation> Invocations { get; }

    public List<ShaderGraphNode> Nodes { get; } = [];

    public static ShaderGraph FromGlsl(string source)
    {
        var parser = new GLSLManager();
        parser.Parse(source);
        return new ShaderGraph(parser);
    }

    public ShaderGraphNode AddMethodInvocationNode(GLSLManager.Method method)
    {
        var node = new ShaderGraphNode(NewId(), method.Name, ShaderGraphNodeKind.MethodInvocation)
        {
            MethodDefinition = method,
            MethodName = method.Name,
            OutputName = $"{method.Name}_out{_nextId}",
            OutputType = method.ReturnType,
            Position = Vector2.Zero
        };

        foreach (var param in method.Parameters)
            node.Inputs.Add(new ShaderGraphInput(param.Name, param.Type, null));

        Nodes.Add(node);
        return node;
    }

    public ShaderGraphNode? FindNode(int id)
        => Nodes.FirstOrDefault(n => n.Id == id);

    public IEnumerable<ShaderGraphEdge> BuildEdges()
    {
        var lookup = Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.OutputName))
            .ToDictionary(n => n.OutputName!, n => n);

        foreach (var node in Nodes)
        {
            foreach (var input in node.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.SourceVariable))
                    continue;

                if (lookup.TryGetValue(input.SourceVariable!, out var from))
                    yield return new ShaderGraphEdge(from.Id, node.Id, input.Name, input.SourceVariable!);
            }
        }
    }

    public IEnumerable<string> GetAvailableValueNames(ShaderGraphNode? exclude = null)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void TryAdd(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name);
        }

        foreach (var input in Attributes)
            TryAdd(input.Name);
        foreach (var u in Uniforms)
            TryAdd(u.Name);
        foreach (var c in Consts)
            TryAdd(c.Name);
        foreach (var o in Outputs)
            TryAdd(o.Name);
        foreach (var node in Nodes)
        {
            if (exclude is not null && node.Id == exclude.Id)
                continue;
            TryAdd(node.OutputName);
        }
        return set;
    }

    public IEnumerable<ShaderGraphNode> GetInvocationOrder()
    {
        var invocationNodes = Nodes.Where(n => n.Kind == ShaderGraphNodeKind.MethodInvocation).ToDictionary(n => n.Id);
        Dictionary<int, int> inDegree = invocationNodes.Values.ToDictionary(n => n.Id, _ => 0);

        foreach (var edge in BuildEdges())
        {
            if (!invocationNodes.ContainsKey(edge.ToId) || !invocationNodes.ContainsKey(edge.FromId))
                continue;
            inDegree[edge.ToId]++;
        }

        Queue<int> queue = new(invocationNodes.Values.Where(n => inDegree[n.Id] == 0).Select(n => n.Id));
        List<ShaderGraphNode> ordered = [];

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            ordered.Add(invocationNodes[id]);

            foreach (var edge in BuildEdges().Where(e => e.FromId == id))
            {
                if (!invocationNodes.ContainsKey(edge.ToId))
                    continue;
                inDegree[edge.ToId]--;
                if (inDegree[edge.ToId] == 0)
                    queue.Enqueue(edge.ToId);
            }
        }

        return ordered;
    }

    public HashSet<string> GetDeclaredSymbolNames()
    {
        HashSet<string> symbols = new(StringComparer.Ordinal);
        foreach (var v in Attributes)
            symbols.Add(v.Name);
        foreach (var v in Uniforms)
            symbols.Add(v.Name);
        foreach (var v in Outputs)
            symbols.Add(v.Name);
        foreach (var v in Consts)
            symbols.Add(v.Name);
        return symbols;
    }

    private int NewId() => _nextId++;

    private void BuildInterfaceNodes()
    {
        foreach (var attrib in Attributes)
            Nodes.Add(CreateInterfaceNode(attrib, ShaderGraphNodeKind.Attribute));

        foreach (var uniform in Uniforms)
            Nodes.Add(CreateInterfaceNode(uniform, ShaderGraphNodeKind.Uniform));

        foreach (var constant in Consts)
            Nodes.Add(CreateInterfaceNode(constant, ShaderGraphNodeKind.Constant));

        foreach (var output in Outputs)
            Nodes.Add(CreateInterfaceNode(output, ShaderGraphNodeKind.Output));
    }

    private ShaderGraphNode CreateInterfaceNode(GLSLManager.Variable variable, ShaderGraphNodeKind kind)
    {
        return new ShaderGraphNode(NewId(), variable.Name, kind)
        {
            OutputName = variable.Name,
            OutputType = variable.Type,
            Position = Vector2.Zero
        };
    }

    private void BuildMethodDefinitionNodes()
    {
        foreach (var method in Methods.Where(m => !m.IsMain))
        {
            var node = new ShaderGraphNode(NewId(), method.Name, ShaderGraphNodeKind.MethodDefinition)
            {
                MethodName = method.Name,
                OutputName = method.Name,
                OutputType = method.ReturnType,
                MethodDefinition = method
            };
            foreach (var param in method.Parameters)
                node.Inputs.Add(new ShaderGraphInput(param.Name, param.Type, null));
            Nodes.Add(node);
        }
    }

    private void BuildInvocationNodes()
    {
        foreach (var invocation in Invocations)
        {
            GLSLManager.Method? method = Methods.FirstOrDefault(m => string.Equals(m.Name, invocation.MethodName, StringComparison.Ordinal));
            var node = new ShaderGraphNode(NewId(), invocation.MethodName, ShaderGraphNodeKind.MethodInvocation)
            {
                MethodName = invocation.MethodName,
                MethodDefinition = method,
                OutputName = invocation.OutputName,
                OutputType = method?.ReturnType ?? TryInferInvocationType(invocation),
            };

            var arguments = invocation.Arguments.ToArray();
            for (int i = 0; i < arguments.Length; i++)
            {
                string inputName = method?.Parameters.ElementAtOrDefault(i)?.Name ?? $"arg{i}";
                EShaderVarType? inputType = method?.Parameters.ElementAtOrDefault(i)?.Type;
                node.Inputs.Add(new ShaderGraphInput(inputName, inputType, arguments[i]));
            }

            Nodes.Add(node);
        }
    }

    private EShaderVarType? TryInferInvocationType(GLSLManager.ParsedInvocation invocation)
    {
        if (!string.IsNullOrWhiteSpace(invocation.DeclaredType))
            return ParseEnum(invocation.DeclaredType);
        return null;
    }

    private static EShaderVarType? ParseEnum(string typeName)
    {
        string enumName = typeName.StartsWith("_", StringComparison.Ordinal) ? typeName : $"_{typeName}";
        if (Enum.TryParse(enumName, true, out EShaderVarType parsed))
            return parsed;
        return null;
    }
}
