namespace XREngine.Tools.ShaderCooker;

internal sealed class WgslAbiParser
{
    private readonly string _source;
    private readonly string _context;
    private readonly List<WgslAbiToken> _tokens = [];
    private readonly Dictionary<string, WgslAbiStructure> _structs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Group, int Binding), (string Kind, string Type, int Offset)> _bindings = [];
    private int _index;
    private bool _vertex;
    private bool _fragment;

    internal WgslAbiParser(string source, string context)
    {
        _source = source;
        _context = context;
        Lex();
    }

    internal void Validate()
    {
        while (!End)
        {
            List<WgslAbiAttribute> attributes = Attributes();
            if (Take("struct")) { StructureDeclaration(attributes); continue; }
            if (Take("alias")) { AliasDeclaration(attributes); continue; }
            if (Take("var")) { ResourceDeclaration(attributes); continue; }
            if (Take("fn")) { FunctionDeclaration(attributes); continue; }
            if (attributes.Count != 0) Fail(attributes[0].Offset, "unsupported attributed declaration");
            // Other module declarations (enable, diagnostic, const, override) cannot declare a resource.
            while (!End && !Take(";"))
            {
                if (Peek("{")) SkipBlock();
                else _index++;
            }
        }

        if (!_vertex || !_fragment) Fail(0, "@vertex vertexMain and @fragment fragmentMain are required");
        if (_bindings.Count != 4) Fail(0, "exactly four global resource bindings are required");
        Check(0, 0, "uniform", "mat4x4<f32>", 64, 16);
        Check(1, 0, "uniform", "vec4<f32>", 16, 0);
        Check(1, 1, "texture", "texture_2d<f32>", 0, 0);
        Check(1, 2, "sampler", "sampler", 0, 0);
    }

    private void Check(int group, int binding, string kind, string leaf, int bytes, int stride)
    {
        if (!_bindings.TryGetValue((group, binding), out var found) || found.Kind != kind)
            Fail(0, $"@group({group}) @binding({binding}) must be {kind}");
        if (kind == "uniform")
        {
            WgslAbiShape shape = Resolve(found.Type, new HashSet<string>(StringComparer.Ordinal), found.Offset);
            if (shape.Alignment != 16 || shape.Leaf != leaf || shape.LeafOffset != 0 || shape.Size != bytes || shape.Stride != stride)
                Fail(found.Offset, $"@group({group}) @binding({binding}) must contain one {leaf} at offset 0, with size {bytes}" +
                    (stride == 0 ? "" : $" and matrix stride {stride}"));
        }
        else if (ResolveAlias(found.Type, new HashSet<string>(StringComparer.Ordinal), found.Offset) != leaf)
            Fail(found.Offset, $"@group({group}) @binding({binding}) must be {leaf}");
    }

    private void StructureDeclaration(List<WgslAbiAttribute> attributes)
    {
        if (attributes.Count != 0) Fail(attributes[0].Offset, "attributes on a struct are unsupported");
        WgslAbiToken name = Identifier();
        Expect("{");
        List<WgslAbiMember> members = [];
        while (!Take("}"))
        {
            if (End) Fail(name.Offset, "unterminated struct");
            List<WgslAbiAttribute> memberAttributes = Attributes();
            WgslAbiToken field = Identifier();
            Expect(":");
            string type = TypeUntil(",", ";", "}");
            members.Add(new WgslAbiMember(field.Text, type, memberAttributes, field.Offset));
            if (Take("}")) break;
            _index++;
        }
        Take(";");
        if (!_structs.TryAdd(name.Text, new WgslAbiStructure(members, name.Offset)) || _aliases.ContainsKey(name.Text))
            Fail(name.Offset, "duplicate type name");
    }

    private void AliasDeclaration(List<WgslAbiAttribute> attributes)
    {
        if (attributes.Count != 0) Fail(attributes[0].Offset, "attributes on an alias are unsupported");
        WgslAbiToken name = Identifier();
        Expect("=");
        string target = TypeUntil(";");
        Expect(";");
        if (!_aliases.TryAdd(name.Text, target) || _structs.ContainsKey(name.Text)) Fail(name.Offset, "duplicate type name");
    }

    private void ResourceDeclaration(List<WgslAbiAttribute> attributes)
    {
        int offset = Current.Offset;
        string address = "";
        if (Take("<"))
        {
            address = Identifier().Text;
            // Access modes and other address-space qualifiers are outside this profile.
            Expect(">");
        }
        Identifier();
        Expect(":");
        string type = TypeUntil(";");
        Expect(";");
        if (attributes.Count != 2 || attributes.Count(a => a.Name == "group") != 1 || attributes.Count(a => a.Name == "binding") != 1)
            Fail(offset, "every global var must have one @group and one @binding attribute");
        int group = Literal(attributes.Single(a => a.Name == "group"));
        int binding = Literal(attributes.Single(a => a.Name == "binding"));
        string kind = address == "uniform" ? "uniform" : address == "" && type.StartsWith("texture_", StringComparison.Ordinal) ? "texture" :
            address == "" && type == "sampler" ? "sampler" : "unsupported";
        if (kind == "unsupported") Fail(offset, "unsupported global resource address space or type");
        if (!_bindings.TryAdd((group, binding), (kind, type, offset))) Fail(offset, "duplicate resource binding");
    }

    private void FunctionDeclaration(List<WgslAbiAttribute> attributes)
    {
        WgslAbiToken name = Identifier();
        bool vertex = attributes.Any(a => a.Name == "vertex" && a.Argument is null);
        bool fragment = attributes.Any(a => a.Name == "fragment" && a.Argument is null);
        if (attributes.Any(a => a.Name == "compute")) Fail(name.Offset, "compute entry points are outside the selected profile");
        if (name.Text == "vertexMain")
        {
            if (!vertex || _vertex) Fail(name.Offset, "vertexMain requires exactly one @vertex declaration");
            _vertex = true;
        }
        if (name.Text == "fragmentMain")
        {
            if (!fragment || _fragment) Fail(name.Offset, "fragmentMain requires exactly one @fragment declaration");
            _fragment = true;
        }
        if ((vertex && name.Text != "vertexMain") || (fragment && name.Text != "fragmentMain"))
            Fail(name.Offset, "unexpected shader stage entry point");
        while (!End && !Peek("{")) _index++;
        if (End) Fail(name.Offset, "function body is missing");
        SkipBlock();
    }

    private WgslAbiShape Resolve(string type, HashSet<string> visiting, int offset)
    {
        type = ResolveAlias(type, visiting, offset);
        if (type is "f32" or "i32" or "u32") return new WgslAbiShape(4, 4, type, 0, 0);
        if (type is "vec2f" or "vec2<f32>" or "vec2<i32>" or "vec2<u32>") return new WgslAbiShape(8, 8, CanonicalVector(type), 0, 0);
        if (type is "vec3f" or "vec3<f32>" or "vec3<i32>" or "vec3<u32>") return new WgslAbiShape(16, 12, CanonicalVector(type), 0, 0);
        if (type is "vec4f" or "vec4<f32>" or "vec4<i32>" or "vec4<u32>") return new WgslAbiShape(16, 16, CanonicalVector(type), 0, 0);
        if (type is "mat4x4f" or "mat4x4<f32>") return new WgslAbiShape(16, 64, "mat4x4<f32>", 0, 16);
        if (!_structs.TryGetValue(type, out WgslAbiStructure? structure)) Fail(offset, $"unsupported or unknown uniform type '{type}'");
        if (visiting.Count >= 64) Fail(offset, "uniform type nesting limit exceeded");
        if (!visiting.Add(type)) Fail(offset, "recursive uniform structure");
        int cursor = 0, alignment = 1, leafOffset = 0, stride = 0;
        string leaf = "";
        foreach (WgslAbiMember member in structure.Members)
        {
            WgslAbiShape field = Resolve(member.Type, visiting, member.Offset);
            int fieldAlign = _structs.ContainsKey(ResolveAlias(member.Type, new HashSet<string>(StringComparer.Ordinal), member.Offset))
                ? Math.Max(16, field.Alignment) : field.Alignment;
            int fieldSize = field.Size;
            foreach (WgslAbiAttribute attribute in member.Attributes)
            {
                int value = Literal(attribute);
                if (attribute.Name == "align")
                {
                    if (value < fieldAlign || (value & (value - 1)) != 0) Fail(attribute.Offset, "invalid @align");
                    fieldAlign = value;
                }
                else if (attribute.Name == "size")
                {
                    if (value < fieldSize) Fail(attribute.Offset, "invalid @size");
                    fieldSize = value;
                }
                else Fail(attribute.Offset, "unsupported uniform member attribute");
            }
            cursor = RoundUp(cursor, fieldAlign, member.Offset);
            if (leaf.Length != 0 || field.Leaf.Length == 0) Fail(member.Offset, "uniform must contain exactly one physical field");
            leaf = field.Leaf;
            leafOffset = checked(cursor + field.LeafOffset);
            stride = field.Stride;
            cursor = checked(cursor + fieldSize);
            alignment = Math.Max(alignment, fieldAlign);
        }
        visiting.Remove(type);
        if (leaf.Length == 0) Fail(structure.Offset, "empty uniform structure");
        alignment = Math.Max(alignment, 16); // RequiredAlignOf(struct, uniform).
        return new WgslAbiShape(alignment, RoundUp(cursor, alignment, offset), leaf, leafOffset, stride);
    }

    private string ResolveAlias(string type, HashSet<string> visiting, int offset)
    {
        while (_aliases.TryGetValue(type, out string? target))
        {
            if (visiting.Count >= 64) Fail(offset, "type alias nesting limit exceeded");
            if (!visiting.Add(type)) Fail(offset, "recursive type alias");
            type = target;
        }
        return type;
    }

    private static string CanonicalVector(string type) => type.EndsWith('f') ? $"{type[..4]}<f32>" : type;

    private int RoundUp(int value, int alignment, int offset)
    {
        if (alignment <= 0 || alignment > 1024 * 1024 || value > 1024 * 1024) Fail(offset, "uniform layout exceeds verifier bounds");
        return checked((value + alignment - 1) / alignment * alignment);
    }

    private List<WgslAbiAttribute> Attributes()
    {
        List<WgslAbiAttribute> result = [];
        while (Take("@"))
        {
            WgslAbiToken name = Identifier();
            string? argument = null;
            if (Take("("))
            {
                int start = _index, depth = 1;
                while (!End && depth != 0)
                {
                    if (Take("(")) { if (++depth > 64) Fail(name.Offset, "attribute nesting limit exceeded"); }
                    else if (Take(")")) depth--;
                    else _index++;
                }
                if (depth != 0) Fail(name.Offset, "unterminated attribute");
                argument = string.Concat(_tokens.GetRange(start, _index - start - 1).Select(t => t.Text));
            }
            result.Add(new WgslAbiAttribute(name.Text, argument, name.Offset));
        }
        return result;
    }

    private int Literal(WgslAbiAttribute attribute)
    {
        int value = 0;
        if (attribute.Argument is null || attribute.Argument.Length > 7 ||
            !attribute.Argument.All(c => c is >= '0' and <= '9') ||
            !int.TryParse(attribute.Argument, out value) || value > 1024 * 1024)
            Fail(attribute.Offset, $"@{attribute.Name} requires a bounded decimal literal");
        return value;
    }

    private string TypeUntil(params string[] delimiters)
    {
        int start = _index, depth = 0;
        while (!End)
        {
            if (depth == 0 && delimiters.Contains(Current.Text)) break;
            if (Take("<")) { if (++depth > 64) Fail(Current.Offset, "type nesting limit exceeded"); }
            else if (Take(">")) { if (--depth < 0) Fail(Current.Offset, "unbalanced type brackets"); }
            else _index++;
        }
        if (depth != 0 || start == _index || End) Fail(Current.Offset, "malformed type");
        return string.Concat(_tokens.GetRange(start, _index - start).Select(t => t.Text));
    }

    private void SkipBlock()
    {
        Expect("{");
        int depth = 1;
        while (!End && depth > 0)
        {
            if (Take("{")) { if (++depth > 64) Fail(Current.Offset, "block nesting limit exceeded"); }
            else if (Take("}")) depth--;
            else _index++;
        }
        if (depth != 0) Fail(Current.Offset, "unterminated block");
    }

    private WgslAbiToken Identifier()
    {
        WgslAbiToken token = Current;
        if (token.Text.Length == 0 || !(token.Text[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_'))
            Fail(token.Offset, "identifier expected");
        _index++;
        return token;
    }

    private bool End => _index >= _tokens.Count;
    private WgslAbiToken Current => End ? new WgslAbiToken("", _source.Length) : _tokens[_index];
    private bool Peek(string value) => !End && Current.Text == value;
    private bool Take(string value) { if (!Peek(value)) return false; _index++; return true; }
    private void Expect(string value) { if (!Take(value)) Fail(Current.Offset, $"expected '{value}'"); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(int offset, string message) => throw new InvalidDataException($"{_context}: WGSL ABI at offset {offset}: {message}.");

    private void Lex()
    {
        for (int i = 0; i < _source.Length;)
        {
            char c = _source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < _source.Length && _source[i + 1] == '/')
            {
                i += 2;
                while (i < _source.Length && _source[i] is not ('\r' or '\n')) i++;
                continue;
            }
            if (c == '/' && i + 1 < _source.Length && _source[i + 1] == '*')
            {
                int start = i, depth = 1;
                i += 2;
                while (i + 1 < _source.Length && depth != 0)
                {
                    if (_source[i] == '/' && _source[i + 1] == '*') { if (++depth > 64) Fail(i, "comment nesting limit exceeded"); i += 2; }
                    else if (_source[i] == '*' && _source[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) Fail(start, "unterminated block comment");
                continue;
            }
            int begin = i;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')
            {
                while (i < _source.Length && (_source[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) i++;
            }
            else if (c is >= '0' and <= '9')
            {
                while (i < _source.Length && _source[i] is >= '0' and <= '9') i++;
            }
            else if (c is >= '!' and <= '~') i++;
            else Fail(i, "unsupported source character");
            _tokens.Add(new WgslAbiToken(_source[begin..i], begin));
            if (_tokens.Count > 262144) Fail(begin, "token limit exceeded");
        }
    }
}
