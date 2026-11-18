using System.Globalization;

namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Parses ShaderLab structure text into a <see cref="ShaderLabDocument"/>.
/// </summary>
/// <remarks>
/// The parser reads structure only. Program text stays raw for the compiler frontend.
/// Keywords compare without case, as ShaderLab does. A render state command takes the
/// tokens on its own line as arguments; a following <c>{</c> starts nested commands.
/// </remarks>
public sealed class ShaderLabParser
{
    private const int MaxNestingDepth = 32;

    private readonly List<ShaderLabToken> _tokens;
    private readonly string _text;
    private readonly string _sourcePath;
    private int _index;
    private int _depth;

    private ShaderLabParser(string text, string sourcePath)
    {
        _text = text;
        _sourcePath = sourcePath;
        _tokens = ShaderLabTokenizer.Tokenize(text, sourcePath);
    }

    /// <summary>Parses the text of one <c>.shader</c> file.</summary>
    /// <param name="text">The file text.</param>
    /// <param name="sourcePath">The path used in diagnostics and line directives.</param>
    /// <exception cref="ShaderLabParseException">The structure text is not valid ShaderLab.</exception>
    public static ShaderLabDocument Parse(string text, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourcePath);
        return new ShaderLabParser(text, sourcePath).ParseDocument();
    }

    private ShaderLabToken Current => _tokens[_index];

    private ShaderLabDocument ParseDocument()
    {
        ExpectKeyword("Shader");
        string name = ExpectString();
        ExpectSymbol('{');

        List<ShaderLabProperty> properties = [];
        List<ShaderLabProgramBlock> includes = [];
        List<ShaderLabSubShader> subShaders = [];
        string? fallback = null;
        string? customEditor = null;
        ParseShaderBody(properties, includes, subShaders, [], ref fallback, ref customEditor);
        ExpectSymbol('}');

        return new ShaderLabDocument
        {
            Name = name,
            SourcePath = _sourcePath,
            Properties = properties,
            IncludeBlocks = includes,
            SubShaders = subShaders,
            Fallback = fallback,
            CustomEditor = customEditor,
        };
    }

    private void ParseShaderBody(
        List<ShaderLabProperty> properties,
        List<ShaderLabProgramBlock> includes,
        List<ShaderLabSubShader> subShaders,
        List<ShaderLabCommand> inheritedCommands,
        ref string? fallback,
        ref string? customEditor)
    {
        while (!Current.IsSymbol('}'))
        {
            ShaderLabToken token = Current;
            if (token.Kind == ShaderLabTokenKind.End)
                throw Error(token, "The shader body has no closing brace.");

            if (token.Kind == ShaderLabTokenKind.ProgramBlock)
            {
                _index++;
                if (!token.Program!.IsInclude)
                    throw Error(token, $"'{token.Text}' is not valid at shader level.");
                includes.Add(token.Program);
                continue;
            }

            if (token.IsKeyword("Properties"))
            {
                _index++;
                ParseProperties(properties);
            }
            else if (token.IsKeyword("SubShader"))
            {
                _index++;
                subShaders.Add(ParseSubShader(token.Line, inheritedCommands));
            }
            else if (token.IsKeyword("Category"))
            {
                // A category applies its render state to the subshaders inside it.
                _index++;
                EnterBlock(token);
                List<ShaderLabCommand> categoryCommands = [.. inheritedCommands];
                ParseCategoryBody(properties, includes, subShaders, categoryCommands, ref fallback, ref customEditor);
                LeaveBlock();
            }
            else if (token.IsKeyword("Fallback"))
            {
                _index++;
                if (Current.Kind == ShaderLabTokenKind.String)
                    fallback = ExpectString();
                else if (Current.IsKeyword("Off"))
                    _index++;
                else
                    throw Error(Current, "Fallback needs a shader name or 'Off'.");
            }
            else if (token.IsKeyword("CustomEditor"))
            {
                _index++;
                customEditor = ExpectString();
            }
            else if (token.IsKeyword("CustomEditorForRenderPipeline"))
            {
                _index++;
                ExpectString();
                ExpectString();
            }
            else if (token.IsKeyword("Dependency"))
            {
                _index++;
                ExpectString();
                ExpectSymbol('=');
                ExpectString();
            }
            else
            {
                throw Error(token, $"Unexpected '{token.Text}' at shader level.");
            }
        }
    }

    private void ParseCategoryBody(
        List<ShaderLabProperty> properties,
        List<ShaderLabProgramBlock> includes,
        List<ShaderLabSubShader> subShaders,
        List<ShaderLabCommand> categoryCommands,
        ref string? fallback,
        ref string? customEditor)
    {
        while (!Current.IsSymbol('}'))
        {
            ShaderLabToken token = Current;
            if (token.Kind == ShaderLabTokenKind.End)
                throw Error(token, "The category has no closing brace.");

            if (token.Kind == ShaderLabTokenKind.Identifier &&
                !token.IsKeyword("SubShader") && !token.IsKeyword("Properties") && !token.IsKeyword("Fallback") &&
                !token.IsKeyword("Category") && !token.IsKeyword("CustomEditor") && !token.IsKeyword("Dependency"))
            {
                categoryCommands.Add(ParseCommand());
                continue;
            }

            // Parse exactly one shader-level item with the category state.
            int before = _index;
            ParseOneShaderItem(properties, includes, subShaders, categoryCommands, ref fallback, ref customEditor);
            if (_index == before)
                throw Error(token, $"Unexpected '{token.Text}' in a category.");
        }
    }

    private void ParseOneShaderItem(
        List<ShaderLabProperty> properties,
        List<ShaderLabProgramBlock> includes,
        List<ShaderLabSubShader> subShaders,
        List<ShaderLabCommand> inheritedCommands,
        ref string? fallback,
        ref string? customEditor)
    {
        ShaderLabToken token = Current;
        if (token.Kind == ShaderLabTokenKind.ProgramBlock && token.Program!.IsInclude)
        {
            _index++;
            includes.Add(token.Program);
        }
        else if (token.IsKeyword("SubShader"))
        {
            _index++;
            subShaders.Add(ParseSubShader(token.Line, inheritedCommands));
        }
        else if (token.IsKeyword("Properties"))
        {
            _index++;
            ParseProperties(properties);
        }
        else if (token.IsKeyword("Fallback"))
        {
            _index++;
            if (Current.Kind == ShaderLabTokenKind.String)
                fallback = ExpectString();
            else
                ExpectKeyword("Off");
        }
        else if (token.IsKeyword("CustomEditor"))
        {
            _index++;
            customEditor = ExpectString();
        }
        else if (token.IsKeyword("Category"))
        {
            _index++;
            EnterBlock(token);
            List<ShaderLabCommand> nested = [.. inheritedCommands];
            ParseCategoryBody(properties, includes, subShaders, nested, ref fallback, ref customEditor);
            LeaveBlock();
        }
    }

    private void ParseProperties(List<ShaderLabProperty> properties)
    {
        EnterBlock(Current);
        while (!Current.IsSymbol('}'))
        {
            if (Current.Kind == ShaderLabTokenKind.End)
                throw Error(Current, "The Properties block has no closing brace.");
            properties.Add(ParseProperty());
        }

        LeaveBlock();
    }

    private ShaderLabProperty ParseProperty()
    {
        List<string> attributes = [];
        while (Current.IsSymbol('['))
            attributes.Add(ReadBracketText());

        ShaderLabToken nameToken = Current;
        string name = ExpectIdentifier();
        ExpectSymbol('(');
        string displayName = ExpectString();
        ExpectSymbol(',');
        ShaderLabToken typeToken = Current;
        string typeName = ExpectIdentifier();
        float rangeMin = 0.0f;
        float rangeMax = 0.0f;
        ShaderLabPropertyKind kind = typeName.ToLowerInvariant() switch
        {
            "float" => ShaderLabPropertyKind.Float,
            "int" or "integer" => ShaderLabPropertyKind.Int,
            "range" => ShaderLabPropertyKind.Range,
            "color" => ShaderLabPropertyKind.Color,
            "vector" => ShaderLabPropertyKind.Vector,
            "2d" => ShaderLabPropertyKind.Texture2D,
            "3d" => ShaderLabPropertyKind.Texture3D,
            "cube" => ShaderLabPropertyKind.TextureCube,
            "2darray" => ShaderLabPropertyKind.Texture2DArray,
            "cubearray" => ShaderLabPropertyKind.TextureCubeArray,
            "any" => ShaderLabPropertyKind.TextureAny,
            _ => throw Error(typeToken, $"Unknown property type '{typeName}' for '{name}'."),
        };
        if (kind == ShaderLabPropertyKind.Range)
        {
            ExpectSymbol('(');
            rangeMin = ExpectNumber();
            ExpectSymbol(',');
            rangeMax = ExpectNumber();
            ExpectSymbol(')');
        }

        ExpectSymbol(')');
        ExpectSymbol('=');

        float[] defaultValue = [];
        string? defaultTexture = null;
        if (IsTextureKind(kind))
        {
            defaultTexture = Current.Kind == ShaderLabTokenKind.String ? ExpectString() : ExpectIdentifier();
            if (Current.IsSymbol('{'))
                SkipBlock();
        }
        else if (Current.IsSymbol('('))
        {
            _index++;
            List<float> values = new(4);
            while (true)
            {
                values.Add(ExpectNumber());
                if (Current.IsSymbol(','))
                {
                    _index++;
                    continue;
                }

                ExpectSymbol(')');
                break;
            }

            defaultValue = [.. values];
        }
        else
        {
            defaultValue = [ExpectNumber()];
        }

        return new ShaderLabProperty
        {
            Name = name,
            DisplayName = displayName,
            Kind = kind,
            Attributes = attributes,
            RangeMin = rangeMin,
            RangeMax = rangeMax,
            DefaultValue = defaultValue,
            DefaultTexture = defaultTexture,
            Line = nameToken.Line,
        };
    }

    private ShaderLabSubShader ParseSubShader(int line, List<ShaderLabCommand> inheritedCommands)
    {
        EnterBlock(Current);
        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        List<ShaderLabCommand> commands = [.. inheritedCommands];
        List<ShaderLabProgramBlock> includes = [];
        List<ShaderLabPass> passes = [];
        ShaderLabProgramBlock? surfaceProgram = null;
        int? lod = null;
        while (!Current.IsSymbol('}'))
        {
            ShaderLabToken token = Current;
            if (token.Kind == ShaderLabTokenKind.End)
                throw Error(token, "The SubShader block has no closing brace.");

            if (token.Kind == ShaderLabTokenKind.ProgramBlock)
            {
                _index++;
                if (token.Program!.IsInclude)
                    includes.Add(token.Program);
                else if (surfaceProgram is null)
                    surfaceProgram = token.Program;
                else
                    throw Error(token, "A SubShader can contain only one program block outside a pass.");
                continue;
            }

            if (token.IsKeyword("Tags"))
            {
                _index++;
                ParseTags(tags);
            }
            else if (token.IsKeyword("LOD"))
            {
                _index++;
                lod = (int)ExpectNumber();
            }
            else if (token.IsKeyword("Pass"))
            {
                _index++;
                passes.Add(ParsePass(token.Line));
            }
            else if (token.IsKeyword("GrabPass"))
            {
                _index++;
                passes.Add(ParseGrabPass(token.Line));
            }
            else if (token.IsKeyword("UsePass"))
            {
                _index++;
                string reference = ExpectString();
                passes.Add(new ShaderLabPass
                {
                    Kind = ShaderLabPassKind.Use,
                    Name = reference,
                    Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Commands = [],
                    IncludeBlocks = [],
                    Line = token.Line,
                });
            }
            else if (token.IsKeyword("PackageRequirements"))
            {
                _index++;
                SkipBlock();
            }
            else if (token.Kind == ShaderLabTokenKind.Identifier)
            {
                commands.Add(ParseCommand());
            }
            else
            {
                throw Error(token, $"Unexpected '{token.Text}' in a SubShader.");
            }
        }

        LeaveBlock();
        return new ShaderLabSubShader
        {
            Tags = tags,
            Lod = lod,
            Commands = commands,
            IncludeBlocks = includes,
            SurfaceProgram = surfaceProgram,
            Passes = passes,
            Line = line,
        };
    }

    private ShaderLabPass ParsePass(int line)
    {
        EnterBlock(Current);
        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        List<ShaderLabCommand> commands = [];
        List<ShaderLabProgramBlock> includes = [];
        ShaderLabProgramBlock? program = null;
        string? name = null;
        while (!Current.IsSymbol('}'))
        {
            ShaderLabToken token = Current;
            if (token.Kind == ShaderLabTokenKind.End)
                throw Error(token, "The Pass block has no closing brace.");

            if (token.Kind == ShaderLabTokenKind.ProgramBlock)
            {
                _index++;
                if (token.Program!.IsInclude)
                    includes.Add(token.Program);
                else if (program is null)
                    program = token.Program;
                else
                    throw Error(token, "A Pass can contain only one program block.");
                continue;
            }

            if (token.IsKeyword("Name"))
            {
                _index++;
                name = ExpectString();
            }
            else if (token.IsKeyword("Tags"))
            {
                _index++;
                ParseTags(tags);
            }
            else if (token.IsKeyword("PackageRequirements"))
            {
                _index++;
                SkipBlock();
            }
            else if (token.Kind == ShaderLabTokenKind.Identifier)
            {
                commands.Add(ParseCommand());
            }
            else
            {
                throw Error(token, $"Unexpected '{token.Text}' in a Pass.");
            }
        }

        LeaveBlock();
        return new ShaderLabPass
        {
            Kind = ShaderLabPassKind.Normal,
            Name = name,
            Tags = tags,
            Commands = commands,
            Program = program,
            IncludeBlocks = includes,
            Line = line,
        };
    }

    private ShaderLabPass ParseGrabPass(int line)
    {
        EnterBlock(Current);
        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        string? textureName = null;
        while (!Current.IsSymbol('}'))
        {
            ShaderLabToken token = Current;
            if (token.Kind == ShaderLabTokenKind.String)
            {
                textureName = ExpectString();
            }
            else if (token.IsKeyword("Tags"))
            {
                _index++;
                ParseTags(tags);
            }
            else if (token.IsKeyword("Name"))
            {
                _index++;
                ExpectString();
            }
            else
            {
                throw Error(token, $"Unexpected '{token.Text}' in a GrabPass.");
            }
        }

        LeaveBlock();
        return new ShaderLabPass
        {
            Kind = ShaderLabPassKind.Grab,
            Name = textureName,
            Tags = tags,
            Commands = [],
            IncludeBlocks = [],
            Line = line,
        };
    }

    private void ParseTags(Dictionary<string, string> tags)
    {
        EnterBlock(Current);
        while (!Current.IsSymbol('}'))
        {
            string key = ExpectString();
            ExpectSymbol('=');
            tags[key] = ExpectString();
        }

        LeaveBlock();
    }

    private ShaderLabCommand ParseCommand()
    {
        ShaderLabToken nameToken = Current;
        _index++;
        List<string> arguments = [];
        while (Current.Line == nameToken.Line &&
               Current.Kind is not (ShaderLabTokenKind.End or ShaderLabTokenKind.ProgramBlock) &&
               !Current.IsSymbol('{') && !Current.IsSymbol('}'))
        {
            if (Current.IsSymbol('['))
            {
                arguments.Add("[" + ReadBracketText() + "]");
                continue;
            }

            arguments.Add(Current.Text);
            _index++;
        }

        List<ShaderLabCommand> children = [];
        if (Current.IsSymbol('{'))
        {
            EnterBlock(Current);
            while (!Current.IsSymbol('}'))
            {
                if (Current.Kind != ShaderLabTokenKind.Identifier)
                    throw Error(Current, $"Unexpected '{Current.Text}' in the '{nameToken.Text}' block.");
                children.Add(ParseCommand());
            }

            LeaveBlock();
        }

        return new ShaderLabCommand(nameToken.Text, arguments, children, nameToken.Line);
    }

    private string ReadBracketText()
    {
        ShaderLabToken open = Current;
        _index++;
        int nesting = 1;
        int start = open.End;
        while (true)
        {
            ShaderLabToken token = Current;
            if (token.Kind == ShaderLabTokenKind.End)
                throw Error(open, "An attribute or property reference has no closing bracket.");
            _index++;
            if (token.IsSymbol('['))
                nesting++;
            else if (token.IsSymbol(']') && --nesting == 0)
                return _text[start..token.Start].Trim();
        }
    }

    private void SkipBlock()
    {
        EnterBlock(Current);
        int nesting = 1;
        while (nesting > 0)
        {
            ShaderLabToken token = Current;
            if (token.Kind == ShaderLabTokenKind.End)
                throw Error(token, "A block has no closing brace.");
            _index++;
            if (token.IsSymbol('{'))
                nesting++;
            else if (token.IsSymbol('}'))
                nesting--;
        }

        _depth--;
    }

    private void EnterBlock(ShaderLabToken owner)
    {
        ExpectSymbol('{');
        if (++_depth > MaxNestingDepth)
            throw Error(owner, $"Blocks are nested deeper than {MaxNestingDepth} levels.");
    }

    private void LeaveBlock()
    {
        ExpectSymbol('}');
        _depth--;
    }

    private void ExpectKeyword(string keyword)
    {
        if (!Current.IsKeyword(keyword))
            throw Error(Current, $"Expected '{keyword}' but found '{Current.Text}'.");
        _index++;
    }

    private void ExpectSymbol(char symbol)
    {
        if (!Current.IsSymbol(symbol))
            throw Error(Current, $"Expected '{symbol}' but found '{Current.Text}'.");
        _index++;
    }

    private string ExpectString()
    {
        if (Current.Kind != ShaderLabTokenKind.String)
            throw Error(Current, $"Expected a string but found '{Current.Text}'.");
        return _tokens[_index++].Text;
    }

    private string ExpectIdentifier()
    {
        if (Current.Kind != ShaderLabTokenKind.Identifier)
            throw Error(Current, $"Expected a name but found '{Current.Text}'.");
        return _tokens[_index++].Text;
    }

    private float ExpectNumber()
    {
        ShaderLabToken token = Current;
        if (token.Kind != ShaderLabTokenKind.Number ||
            !float.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            throw Error(token, $"Expected a number but found '{token.Text}'.");
        _index++;
        return value;
    }

    private static bool IsTextureKind(ShaderLabPropertyKind kind)
        => kind is ShaderLabPropertyKind.Texture2D or ShaderLabPropertyKind.Texture3D or ShaderLabPropertyKind.TextureCube or
            ShaderLabPropertyKind.Texture2DArray or ShaderLabPropertyKind.TextureCubeArray or ShaderLabPropertyKind.TextureAny;

    private ShaderLabParseException Error(ShaderLabToken token, string message)
        => new(_sourcePath, token.Line, message);
}
