using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Tools.ShaderCooker;

internal sealed partial class WgslAbiParser
{
    private readonly Dictionary<string, (string Type, int Offset)> _workgroupVariables = new(StringComparer.Ordinal);

    private void WorkgroupDeclaration(List<WgslAbiAttribute> attributes)
    {
        if (_expected?.ComputeEntryPoint is null || attributes.Count != 0 || _workgroupVariables.Count >= 32)
            Fail(Current.Offset, "workgroup storage requires a bounded, unbound compute declaration");
        Expect("<");
        Expect("workgroup");
        Expect(">");
        WgslAbiToken name = Identifier();
        Expect(":");
        string type = TypeUntil(";");
        Expect(";");
        if (!_workgroupVariables.TryAdd(name.Text, (type, name.Offset)))
            Fail(name.Offset, "duplicate workgroup variable");
    }

    private void ValidateWorkgroupStorage()
    {
        if (_expected is not { ComputeEntryPoint: { } entry } expected)
            return;
        // Preserve the already validated legacy luminance descriptor, whose
        // fixed 256-vec2 scratch predates explicit workgroup-memory metadata.
        int bytes = _luminanceScratchDeclarations * 2048;
        foreach ((string name, (string type, int offset)) in _workgroupVariables)
        {
            (int _, int size) = ResolveWorkgroupType(type, offset, 0);
            if (References(entry, name, new HashSet<string>(StringComparer.Ordinal)))
                bytes = checked(bytes + RoundUp(size, 16, offset));
        }
        if (bytes > 65536)
            Fail(0, "workgroup storage exceeds the verifier's 64 KiB bound");
        bool declared = expected.RequiredLimits.TryGetValue("maxComputeWorkgroupStorageSize", out int required);
        if (declared ? required < bytes : _workgroupVariables.Count != 0)
            Fail(0, "required maxComputeWorkgroupStorageSize does not cover actual statically used workgroup memory");
    }

    private (int Alignment, int Size) ResolveWorkgroupType(string type, int offset, int depth)
    {
        if (depth >= 16)
            Fail(offset, "workgroup storage type nesting exceeds verifier bounds");
        type = ResolveAlias(type, new HashSet<string>(StringComparer.Ordinal), offset);
        if (type.StartsWith("array<", StringComparison.Ordinal) && type.EndsWith('>'))
        {
            string contents = type[6..^1];
            int separator = contents.LastIndexOf(',');
            if (separator < 0)
                Fail(offset, "workgroup arrays require an explicit bounded literal count");
            string literal = contents[(separator + 1)..];
            if (literal.EndsWith('u') || literal.EndsWith('i'))
                literal = literal[..^1];
            int count = 0;
            if (literal.Length is < 1 or > 5 || !literal.All(static character => character is >= '0' and <= '9') ||
                !int.TryParse(literal, out count) || count is < 1 or > 16384)
                Fail(offset, "workgroup array count must be a positive bounded decimal literal");
            (int alignment, int size) = ResolveWorkgroupType(contents[..separator], offset, depth + 1);
            int total = checked(RoundUp(size, alignment, offset) * count);
            if (total > 65536)
                Fail(offset, "workgroup array exceeds the verifier's 64 KiB bound");
            return (alignment, total);
        }
        if (type is "atomic<u32>" or "atomic<i32>")
            return (4, 4);
        // Struct layout, overrides, f16, and opaque types need their own exact
        // verifier rules rather than sharing uniform-buffer alignment rules.
        if (_structs.ContainsKey(type))
            Fail(offset, "structured workgroup storage has no admitted physical layout");
        WgslAbiShape shape = Resolve(type, new HashSet<string>(StringComparer.Ordinal), offset);
        return (shape.Alignment, shape.Size);
    }
}
