using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Lowers the read-only Advanced global SSBO tables (bindings 0-29) into one
/// raw uint arena. The arena begins with 32 uvec4 table headers; writable
/// diagnostics and all local/native buffers deliberately remain untouched.
/// </summary>
internal static class OpenGLAdvancedSceneShaderLowering
{
    private static readonly Regex Define = new(@"(?m)^\s*#define\s+(?<name>XR_ADV_BINDING_[A-Za-z0-9_]+)\s+(?<value>\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex Struct = new(@"struct\s+(?<name>[A-Za-z_]\w*)\s*\{(?<body>[^{}]*)\}\s*;", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TableDeclaration = new(@"XR_ADV_TABLE_LAYOUT\s*\(\s*(?<binding>XR_ADV_BINDING_\w+)\s*\)\s*readonly\s+buffer\s+\w+\s*\{\s*(?<type>\w+)\s+(?<field>\w+)\s*\[\s*\]\s*;\s*\}\s*(?<instance>\w+)\s*;", RegexOptions.Compiled);

    /// <summary>Returns a GL-specialized source with global table loads decoded from
    /// one std430 uint arena. Unsupported declaration syntax fails closed.</summary>
    internal static string Lower(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Dictionary<string, uint> macros = ParseBindingMacros(source);
        Dictionary<string, StructLayout> structs = ParseStructs(source);
        List<Table> tables = FindTables(source, macros, structs);
        if (tables.Count == 0) return source;

        StringBuilder stripped = new(source);
        for (int index = tables.Count - 1; index >= 0; --index)
        {
            Table table = tables[index];
            // Each table's element type is declared before its original block.
            // Keep its decoder here so GLSL declaration order remains valid.
            stripped.Remove(table.Start, table.Length);
            stripped.Insert(table.Start, BuildLoader(table));
        }
        stripped.Insert(tables[0].Start, BuildArena());
        return ReplaceTableUses(stripped.ToString(), tables);
    }

    private static Dictionary<string, uint> ParseBindingMacros(string source)
    {
        Dictionary<string, uint> result = new(StringComparer.Ordinal);
        foreach (Match match in Define.Matches(source))
            result[match.Groups["name"].Value] = uint.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }

    private static Dictionary<string, StructLayout> ParseStructs(string source)
    {
        Dictionary<string, StructLayout> result = new(StringComparer.Ordinal);
        foreach (Match match in Struct.Matches(source))
        {
            List<Field> fields = [];
            string body = Regex.Replace(match.Groups["body"].Value, @"//[^\r\n]*|/\*[\s\S]*?\*/", string.Empty);
            foreach (string statement in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] tokens = statement.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                fields.Add(tokens.Length == 2 && !tokens[1].Contains('[')
                    ? new(tokens[0], tokens[1]) : new("<unsupported>", statement));
            }
            result[match.Groups["name"].Value] = new(match.Groups["name"].Value, fields);
        }
        return result;
    }

    private static List<Table> FindTables(string source, Dictionary<string, uint> macros, Dictionary<string, StructLayout> structs)
    {
        List<Table> tables = [];
        foreach (Match match in TableDeclaration.Matches(source))
        {
            if (!macros.TryGetValue(match.Groups["binding"].Value, out uint binding) || binding > 29u || binding == 10u)
                continue;
            string type = match.Groups["type"].Value;
            if (!TryGetTypeLayout(type, structs, out TypeLayout layout))
                throw new NotSupportedException($"Advanced GL arena cannot decode global table element '{type}'.");
            if (layout is StructLayout record)
                ComputeLayout(record, structs, new HashSet<string>(StringComparer.Ordinal));
            uint expectedSize = binding switch
            {
                0u or 17u => 80u,
                1u => 224u,
                2u => 320u,
                3u or 7u or 8u or 21u => 64u,
                4u => 928u,
                5u or 14u => 128u,
                6u => 272u,
                9u or 22u => 48u,
                11u => 4u,
                12u or 18u or 23u => 32u,
                13u => 176u,
                15u => 192u,
                16u => 208u,
                19u or 20u => 16u,
                27u => 8u,
                _ => throw new NotSupportedException($"Advanced GL arena has no record ABI for binding {binding}."),
            };
            if (layout.Size != expectedSize)
                throw new InvalidOperationException($"Advanced GL arena record {type} has std430 size {layout.Size}; its CPU ABI requires {expectedSize} bytes.");
            tables.Add(new(match.Index, match.Length, match.Groups["instance"].Value,
                match.Groups["field"].Value, type, binding, layout));
        }
        return tables;
    }

    private static string ReplaceTableUses(string source, List<Table> tables)
    {
        foreach (Table table in tables.DistinctBy(static value => value.Instance))
        {
            source = source.Replace($"uint({table.Instance}.{table.Field}.length())", $"XR_ADV_ArenaCount({table.Binding}u)", StringComparison.Ordinal);
            source = source.Replace($"{table.Instance}.{table.Field}.length()", $"int(XR_ADV_ArenaCount({table.Binding}u))", StringComparison.Ordinal);
            source = ReplaceIndexedLoads(source, table);
        }
        return source;
    }

    private static string ReplaceIndexedLoads(string source, Table table)
    {
        Regex indexed = new(@"\b" + Regex.Escape(table.Instance) + @"\s*\.\s*" + Regex.Escape(table.Field) + @"\s*\[");
        StringBuilder result = new(source.Length);
        int copied = 0, search = 0;
        while (indexed.Match(source, search) is { Success: true } match)
        {
            search = match.Index;
            int expressionStart = match.Index + match.Length;
            int end = FindBalanced(source, expressionStart - 1, '[', ']');
            result.Append(source, copied, search - copied);
            result.Append("XR_ADV_Load_").Append(table.Instance).Append('(')
                .Append(source, expressionStart, end - expressionStart).Append(')');
            copied = end + 1;
            search = copied;
        }
        if (copied == 0) return source;
        result.Append(source, copied, source.Length - copied);
        return result.ToString();
    }

    private static string BuildArena()
    {
        StringBuilder output = new();
        output.AppendLine("\n// GL packed Advanced global arena: 32 x uvec4 = 512-byte header.");
        output.AppendLine("layout(std430, binding = 0) readonly buffer XRAdvancedPackedSceneArena { uint words[]; } XR_ADV_PackedScene;");
        output.AppendLine("uvec4 XR_ADV_ArenaHeader(uint table) { uint i = table * 4u; return uvec4(XR_ADV_PackedScene.words[i], XR_ADV_PackedScene.words[i + 1u], XR_ADV_PackedScene.words[i + 2u], XR_ADV_PackedScene.words[i + 3u]); }");
        output.AppendLine("uint XR_ADV_ArenaCount(uint table) { return XR_ADV_ArenaHeader(table).y; }");
        return output.ToString();
    }

    private static string BuildLoader(Table table)
        => $"\n{table.Type} XR_ADV_Load_{table.Instance}(uint index) {{\n" +
           $"    uvec4 h = XR_ADV_ArenaHeader({table.Binding}u);\n" +
           "    uint byteOffset = (h.x + index * h.z) * 4u;\n" +
           $"    return {DecodeExpression(table.Layout, "byteOffset")};\n}}\n";

    private static string DecodeExpression(TypeLayout layout, string offset)
    {
        if (layout.IsScalar) return DecodeScalar(layout.Name, offset);
        if (layout.IsVector)
        {
            string scalar = layout.Name[0] is 'u' ? "uint" : layout.Name[0] is 'i' ? "int" : "float";
            string ctor = layout.Name;
            return ctor + "(" + string.Join(", ", Enumerable.Range(0, layout.Components).Select(index => DecodeScalar(scalar, Offset(offset, index * 4)))) + ")";
        }
        if (layout.Name == "mat4")
            return "transpose(mat4(" + string.Join(", ", Enumerable.Range(0, 4).Select(index => "vec4(" + string.Join(", ", Enumerable.Range(0, 4).Select(component => DecodeScalar("float", Offset(offset, index * 16 + component * 4)))) + ")")) + "))";
        return layout.Name + "(" + string.Join(", ", ((StructLayout)layout).Fields.Select(field => DecodeExpression(field.Layout!, Offset(offset, checked((int)field.Offset))))) + ")";
    }

    private static string DecodeScalar(string type, string offset) => type switch
    {
        "uint" => $"XR_ADV_PackedScene.words[{offset} / 4u]",
        "int" => $"int(XR_ADV_PackedScene.words[{offset} / 4u])",
        "bool" => $"XR_ADV_PackedScene.words[{offset} / 4u] != 0u",
        "float" => $"uintBitsToFloat(XR_ADV_PackedScene.words[{offset} / 4u])",
        _ => throw new NotSupportedException($"Advanced GL arena lowering does not support scalar '{type}'."),
    };

    private static string Offset(string basis, int bytes) => bytes == 0 ? basis : $"({basis} + {bytes}u)";

    private static void ComputeLayout(StructLayout layout, Dictionary<string, StructLayout> all, HashSet<string> visiting)
    {
        if (layout.Computed) return;
        if (!visiting.Add(layout.Name)) throw new NotSupportedException($"Recursive GLSL struct '{layout.Name}' is unsupported.");
        uint offset = 0, alignment = 4;
        foreach (Field field in layout.Fields)
        {
            if (!TryGetTypeLayout(field.Type, all, out TypeLayout type))
                throw new NotSupportedException($"Advanced GL arena lowering does not support type '{field.Type}' in '{layout.Name}'.");
            if (type is StructLayout nested) ComputeLayout(nested, all, visiting);
            field.Layout = type;
            offset = Align(offset, type.Alignment);
            field.Offset = offset;
            offset += type.Size;
            alignment = Math.Max(alignment, type.Alignment);
        }
        layout.Alignment = alignment;
        layout.Size = Align(offset, alignment);
        layout.Computed = true;
        visiting.Remove(layout.Name);
    }

    private static bool TryGetTypeLayout(string type, Dictionary<string, StructLayout> structs, out TypeLayout layout)
    {
        layout = type switch
        {
            "uint" or "int" or "float" or "bool" => new TypeLayout(type, 4u, 4u, true, false, 1),
            "uvec2" or "ivec2" or "vec2" => new TypeLayout(type, 8u, 8u, false, true, 2),
            "uvec3" or "ivec3" or "vec3" => new TypeLayout(type, 16u, 12u, false, true, 3),
            "uvec4" or "ivec4" or "vec4" => new TypeLayout(type, 16u, 16u, false, true, 4),
            "mat4" => new TypeLayout(type, 16u, 64u, false, false, 0),
            _ when structs.TryGetValue(type, out StructLayout? nested) => nested,
            _ => null!,
        };
        return layout is not null;
    }

    private static int FindBalanced(string source, int open, char opening, char closing)
    {
        int depth = 0;
        for (int index = open; index < source.Length; ++index)
        {
            if (source[index] == opening) ++depth;
            else if (source[index] == closing && --depth == 0) return index;
        }
        throw new InvalidOperationException($"Unbalanced GLSL {opening}{closing} expression.");
    }

    private static uint Align(uint value, uint alignment) => (value + alignment - 1u) / alignment * alignment;

    private sealed record Table(int Start, int Length, string Instance, string Field, string Type, uint Binding, TypeLayout Layout);
    private class TypeLayout(string name, uint alignment, uint size, bool isScalar, bool isVector, int components)
    {
        internal string Name { get; } = name;
        internal uint Alignment { get; set; } = alignment;
        internal uint Size { get; set; } = size;
        internal bool IsScalar { get; } = isScalar;
        internal bool IsVector { get; } = isVector;
        internal int Components { get; } = components;
    }
    private sealed class StructLayout(string name, List<Field> fields) : TypeLayout(name, 16u, 0u, false, false, 0)
    {
        internal List<Field> Fields { get; } = fields;
        internal bool Computed { get; set; }
    }
    private sealed class Field(string type, string name)
    {
        internal string Type { get; } = type;
        internal string Name { get; } = name;
        internal uint Offset { get; set; }
        internal TypeLayout? Layout { get; set; }
    }
}
