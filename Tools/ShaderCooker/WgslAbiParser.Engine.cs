using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Tools.ShaderCooker;

internal sealed partial class WgslAbiParser
{
    private readonly ShaderProgramArtifact? _expected;
    private readonly Dictionary<(int Group, int Binding), string> _bindingNames = [];
    private readonly Dictionary<string, HashSet<string>> _functionReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShaderStageVisibility> _stages = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _vertexInputs = [];

    private void EngineFunctionDeclaration(WgslAbiToken name, List<WgslAbiAttribute> attributes)
    {
        ShaderStageVisibility stage = ShaderStageVisibility.None;
        foreach (WgslAbiAttribute attribute in attributes)
        {
            ShaderStageVisibility flag = attribute.Name switch
            {
                "vertex" => ShaderStageVisibility.Vertex,
                "fragment" => ShaderStageVisibility.Fragment,
                "compute" => ShaderStageVisibility.Compute,
                _ => ShaderStageVisibility.None,
            };
            if (flag != ShaderStageVisibility.None)
            {
                if (stage != ShaderStageVisibility.None || attribute.Argument is not null)
                    Fail(attribute.Offset, "duplicate or malformed stage attribute");
                stage = flag;
            }
        }
        if (stage != ShaderStageVisibility.None && !_stages.TryAdd(name.Text, stage))
            Fail(name.Offset, "duplicate stage entry point");
        Expect("(");
        while (!Take(")"))
        {
            List<WgslAbiAttribute> inputAttributes = Attributes();
            WgslAbiToken input = Identifier();
            Expect(":");
            string type = TypeUntil(",", ")");
            if (stage == ShaderStageVisibility.Vertex)
                CollectVertexInput(type, inputAttributes, input.Offset);
            if (Take(")")) break;
            Expect(",");
        }
        while (!End && !Peek("{")) _index++;
        if (End) Fail(name.Offset, "function body is missing");
        int start = _index;
        SkipBlock();
        HashSet<string> references = new(StringComparer.Ordinal);
        for (int index = start; index < _index; index++) references.Add(_tokens[index].Text);
        if (!_functionReferences.TryAdd(name.Text, references)) Fail(name.Offset, "duplicate function");
    }

    private void CollectVertexInput(string type, List<WgslAbiAttribute> attributes, int offset)
    {
        List<WgslAbiAttribute> locations = attributes.Where(attribute => attribute.Name == "location").ToList();
        if (locations.Count > 1) Fail(offset, "duplicate vertex location attribute");
        if (locations.Count == 1)
        {
            int value = Literal(locations[0]);
            if (!_vertexInputs.TryAdd(value, CanonicalVector(ResolveAlias(type, new HashSet<string>(StringComparer.Ordinal), offset))))
                Fail(offset, "duplicate vertex input location");
            return;
        }
        if (attributes.Any(attribute => attribute.Name == "builtin")) return;
        string resolved = ResolveAlias(type, new HashSet<string>(StringComparer.Ordinal), offset);
        if (!_structs.TryGetValue(resolved, out WgslAbiStructure? structure))
            Fail(offset, "vertex input must declare locations or builtins");
        foreach (WgslAbiMember member in structure.Members)
            CollectVertexInput(member.Type, member.Attributes, member.Offset);
    }

    private void ValidateEngineLayout()
    {
        ShaderProgramArtifact expected = _expected!;
        CheckEntry(expected.VertexEntryPoint, ShaderStageVisibility.Vertex);
        CheckEntry(expected.FragmentEntryPoint, ShaderStageVisibility.Fragment);
        CheckEntry(expected.ComputeEntryPoint, ShaderStageVisibility.Compute);
        int entryCount = (expected.VertexEntryPoint is null ? 0 : 1) + (expected.FragmentEntryPoint is null ? 0 : 1) + (expected.ComputeEntryPoint is null ? 0 : 1);
        if (_stages.Count != entryCount) Fail(0, "WGSL contains an undeclared stage entry point");
        int attributeCount = 0;
        foreach (ShaderVertexBufferLayout buffer in expected.VertexBuffers)
        foreach (ShaderVertexAttribute attribute in buffer.Attributes)
        {
            attributeCount++;
            string type = attribute.Format switch
            {
                "float32" => "f32", "float32x2" => "vec2<f32>", "float32x3" => "vec3<f32>", "float32x4" => "vec4<f32>",
                _ => "unsupported",
            };
            if (!_vertexInputs.TryGetValue(attribute.Location, out string? found) || found != type)
                Fail(0, $"vertex location {attribute.Location} must have type {type}");
        }
        if (_vertexInputs.Count != attributeCount) Fail(0, "WGSL vertex inputs differ from the declared layout");
        if (_bindings.Count != expected.Resources.Length) Fail(0, "WGSL resources differ from the declared binding layout");
        foreach (ShaderStageResourceLayout resource in expected.Resources)
        {
            ShaderAbiResourceContract contract = resource.Contract;
            var key = (checked((int)contract.Set), checked((int)contract.Binding));
            if (!_bindings.TryGetValue(key, out var found)) Fail(0, $"missing resource group {key.Item1} binding {key.Item2}");
            if (_bindingNames[key] != contract.PhysicalName) Fail(found.Offset, $"resource physical name must be '{contract.PhysicalName}'");
            string kind = resource.BindingType switch
            {
                "uniform" => "uniform", "read-only-storage" => "storage,read", "storage" => "storage,read_write",
                "filtering-sampler" or "comparison-sampler" => "sampler", _ => "texture",
            };
            if (found.Kind != kind) Fail(found.Offset, "resource address space does not match its declared binding kind");
            if (contract.Kind is ShaderAbiResourceKind.UniformBuffer or ShaderAbiResourceKind.StorageBuffer)
                CheckEngineBuffer(found.Type, found.Offset, contract);
            else
            {
                string type = resource.BindingType switch
                {
                    "texture-2d-float" => "texture_2d<f32>", "texture-2d-array-float" => "texture_2d_array<f32>",
                    "texture-cube-float" => "texture_cube<f32>", "texture-depth-2d" => "texture_depth_2d",
                    "texture-depth-2d-array" => "texture_depth_2d_array", "filtering-sampler" => "sampler", "comparison-sampler" => "sampler_comparison",
                    _ => "unsupported",
                };
                if (ResolveAlias(found.Type, new HashSet<string>(StringComparer.Ordinal), found.Offset) != type)
                    Fail(found.Offset, "resource type does not match its declared binding kind");
            }
            foreach ((string entry, ShaderStageVisibility stage) in _stages)
                if ((resource.Visibility & stage) == 0 && References(entry, contract.PhysicalName, new HashSet<string>(StringComparer.Ordinal)))
                    Fail(found.Offset, $"entry point '{entry}' uses a resource outside its declared stage visibility");
        }
    }

    private void CheckEntry(string? entry, ShaderStageVisibility stage)
    {
        if (entry is not null && (!_stages.TryGetValue(entry, out ShaderStageVisibility actual) || actual != stage))
            Fail(0, $"entry point '{entry}' does not declare stage {stage}");
    }

    private bool References(string function, string resource, HashSet<string> visiting)
    {
        if (!visiting.Add(function) || !_functionReferences.TryGetValue(function, out HashSet<string>? references)) return false;
        if (references.Contains(resource)) return true;
        foreach (string reference in references)
            if (_functionReferences.ContainsKey(reference) && References(reference, resource, visiting)) return true;
        return false;
    }

    private void CheckEngineBuffer(string type, int offset, ShaderAbiResourceContract expected)
    {
        string resolved = ResolveAlias(type, new HashSet<string>(StringComparer.Ordinal), offset);
        if (expected.Kind == ShaderAbiResourceKind.StorageBuffer && expected.ByteSize == 4 && expected.Members.IsEmpty)
        {
            // The descriptor's empty-member storage cohort is explicitly a raw
            // runtime u32 array. Four bytes is its minimum binding size, not its
            // runtime capacity. Fixed arrays and padded/vector elements are not
            // interchangeable with the scalar-packed engine payload.
            if (!resolved.StartsWith("array<", StringComparison.Ordinal) || !resolved.EndsWith('>'))
                Fail(offset, "raw storage must use a runtime array<u32>");
            string element = resolved[6..^1];
            if (element.Contains(',') ||
                ResolveAlias(element, new HashSet<string>(StringComparer.Ordinal), offset) != "u32")
                Fail(offset, "raw storage must use a runtime array<u32> with four-byte scalar stride");
            return;
        }
        if (!_structs.TryGetValue(resolved, out WgslAbiStructure? structure)) Fail(offset, "buffer must use a named physical structure");
        if (structure.Members.Count != expected.Members.Length) Fail(offset, "buffer member count differs from declared ABI");
        int cursor = 0, alignment = 16;
        for (int index = 0; index < structure.Members.Count; index++)
        {
            WgslAbiMember member = structure.Members[index];
            ShaderAbiMemberContract declared = expected.Members[index];
            WgslAbiShape shape = Resolve(member.Type, new HashSet<string>(StringComparer.Ordinal), member.Offset);
            int memberAlignment = shape.Alignment, memberSize = shape.Size;
            foreach (WgslAbiAttribute attribute in member.Attributes)
            {
                int value = Literal(attribute);
                if (attribute.Name == "align" && value >= memberAlignment && (value & (value - 1)) == 0) memberAlignment = value;
                else if (attribute.Name == "size" && value >= memberSize) memberSize = value;
                else Fail(attribute.Offset, "unsupported or invalid physical member attribute");
            }
            cursor = RoundUp(cursor, memberAlignment, member.Offset);
            if (member.Name != declared.PhysicalName || cursor != declared.Offset || memberSize != declared.Size ||
                shape.Leaf != declared.PhysicalType || shape.LeafOffset != 0 || shape.Stride != declared.MatrixStride)
                Fail(member.Offset, $"member '{member.Name}' differs from declared name, offset, type, size, or matrix stride");
            cursor = checked(cursor + memberSize);
            alignment = Math.Max(alignment, memberAlignment);
        }
        if (RoundUp(cursor, alignment, offset) != expected.ByteSize) Fail(offset, "buffer byte size differs from declared ABI");
    }
}
