using System.Globalization;
using XREngine.Rendering.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering;

// Document blocks: derive header, settings, passes, resources, and schema.
public sealed partial class RenderPipelineScript
{
    /// <summary>A base constructor argument from <c>derive Name(...)</c>. A positional argument has no name.</summary>
    internal sealed record ScriptArgument(string? Name, IValueNode Value, int Line, int Column);

    /// <summary>An assignment in the <c>settings</c> block.</summary>
    internal sealed record ScriptSetting(string Name, IValueNode Value, int Line, int Column);

    /// <summary>Parses a full document. Script errors go to the problems of the document.</summary>
    internal static RenderPipelineScriptDocument ParseDocument(string text)
    {
        RenderPipelineScriptDocument document = new();
        Parser parser;
        try
        {
            parser = new Parser(text);
        }
        catch (ScriptSyntaxException ex)
        {
            document.AddError(ex.Line, ex.Column, ex.Reason);
            return document;
        }

        try
        {
            parser.ParseDocument(document);
        }
        catch (ScriptSyntaxException ex)
        {
            document.AddError(ex.Line, ex.Column, ex.Reason);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or ArgumentException or InvalidCastException)
        {
            (int line, int column) = parser.CurrentLocation;
            document.AddError(line, column, ex.Message);
        }

        IReadOnlyList<ScriptComment> markers = parser.MarkerComments;
        for (int i = 0; i < markers.Count; i++)
        {
            ScriptComment marker = markers[i];
            if (marker.Prefix == BaseHashCommentPrefix)
                document.BaseHash ??= marker.Text;
            else
                document.AddUnconverted(marker.Line, marker.Column, string.Concat("Unconverted: ", marker.Text));
        }

        return document;
    }

    private sealed partial class Parser
    {
        private static readonly string[] s_blockNames = ["settings", "passes", "resources", "schema"];

        /// <summary>Gets the location of the last consumed token, for errors that carry no location.</summary>
        public (int Line, int Column) CurrentLocation
        {
            get
            {
                Token token = _tokens[Math.Clamp(_index - 1, 0, _tokens.Count - 1)];
                return (token.Line, token.Column);
            }
        }

        /// <summary>Parses the whole token stream into <paramref name="document"/>.</summary>
        public void ParseDocument(RenderPipelineScriptDocument document)
        {
            bool seenContent = false;
            while (!Is(TokenKind.EndOfFile))
            {
                if (IsContextual("derive") && Peek(1).Kind is TokenKind.Identifier or TokenKind.String)
                {
                    if (seenContent || document.IsDerived)
                        throw Error(Peek(), "The 'derive' header must be the first statement and appear once.");

                    Advance();
                    ParseDeriveHeader(document);
                    continue;
                }

                seenContent = true;
                if (TryParseBlock(document))
                    continue;
                if (TryParseCommandPatch(document))
                    continue;

                document.Steps.Add(ParseStatement());
            }
        }

        private void ParseDeriveHeader(RenderPipelineScriptDocument document)
        {
            Token target = Advance();
            if (target.Kind == TokenKind.String)
            {
                document.ParentPath = target.Text;
                document.BaseLine = target.Line;
                document.BaseColumn = target.Column;
                Consume(TokenKind.Semicolon, "Expected ';' after the derive header.");
                return;
            }

            document.BaseName = target.Text;
            document.BaseLine = target.Line;
            document.BaseColumn = target.Column;
            if (Match(TokenKind.OpenParen) && !Match(TokenKind.CloseParen))
            {
                do
                {
                    Token start = Peek();
                    string? name = null;
                    if (start.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
                    {
                        name = Advance().Text;
                        Advance();
                    }

                    document.BaseArguments.Add(new ScriptArgument(name, ParseValue(), start.Line, start.Column));
                }
                while (Match(TokenKind.Comma));
                Consume(TokenKind.CloseParen, "Expected ')' after the base arguments.");
            }

            Consume(TokenKind.Semicolon, "Expected ';' after the derive header.");
        }

        private bool TryParseBlock(RenderPipelineScriptDocument document)
        {
            if (Peek().Kind != TokenKind.Identifier || Peek(1).Kind != TokenKind.OpenBrace)
                return false;

            string name = Peek().Text;
            if (Array.IndexOf(s_blockNames, name) < 0)
                return false;

            Advance();
            Advance();
            switch (name)
            {
                case "settings":
                    ParseSettingsBlock(document);
                    break;
                case "passes":
                    document.HasPassesBlock = true;
                    ParsePassesBlock(document);
                    break;
                case "resources":
                    document.HasResourcesBlock = true;
                    if (document.IsDerived)
                        ParseResourcePatchBlock(document);
                    else
                        ParseResourcesBlock(document);
                    break;
                default:
                    document.HasSchemaBlock = true;
                    if (document.IsDerived)
                        ParseSchemaPatchBlock(document);
                    else
                        ParseSchemaBlock(document);
                    break;
            }

            return true;
        }

        private void ParseSettingsBlock(RenderPipelineScriptDocument document)
        {
            while (!Match(TokenKind.CloseBrace))
            {
                Token name = Consume(TokenKind.Identifier, "Expected a setting name.");
                Consume(TokenKind.Equals, "Expected '=' after the setting name.");
                IValueNode value = ParseValue();
                Consume(TokenKind.Semicolon, "Expected ';' after the setting value.");
                document.Settings.Add(new ScriptSetting(name.Text, value, name.Line, name.Column));
            }
        }

        private void ParsePassesBlock(RenderPipelineScriptDocument document)
        {
            while (!Match(TokenKind.CloseBrace))
            {
                Token statement = Consume(TokenKind.Identifier, "Expected 'scene' or 'depends' in the passes block.");
                switch (statement.Text)
                {
                    case "scene":
                    {
                        int passIndex = ParseInteger("Expected a pass index after 'scene'.");
                        IComparer<RenderCommand>? sorter = null;
                        if (IsContextual("sorter"))
                        {
                            Advance();
                            Token sorterName = Consume(TokenKind.Identifier, "Expected a sorter name after 'sorter'.");
                            if (!RenderPipelineScriptSorters.TryCreate(sorterName.Text, out sorter))
                                document.AddError(sorterName.Line, sorterName.Column, $"Unknown sorter '{sorterName.Text}'. Known sorters: {string.Join(", ", RenderPipelineScriptSorters.Names)}.");
                        }

                        if (!document.RenderPasses.TryAdd(passIndex, sorter))
                            document.AddError(statement.Line, statement.Column, $"Scene pass {passIndex} is declared twice.");
                        break;
                    }
                    case "depends":
                    {
                        int passIndex = ParseInteger("Expected a pass index after 'depends'.");
                        if (!IsContextual("on"))
                            throw Error(Peek(), "Expected 'on' in the depends statement.");
                        Advance();
                        int dependsOn = ParseInteger("Expected a pass index after 'on'.");
                        document.PassDependencies.Add(new AuthoredPassDependency { PassIndex = passIndex, DependsOnPassIndex = dependsOn });
                        break;
                    }
                    default:
                        throw Error(statement, "Expected 'scene' or 'depends' in the passes block.");
                }

                Consume(TokenKind.Semicolon, "Expected ';' after the passes statement.");
            }
        }

        private void ParseResourcesBlock(RenderPipelineScriptDocument document)
        {
            while (!Match(TokenKind.CloseBrace))
            {
                if (ParseResourceDeclaration(document) is { } resource)
                    document.Resources.Add(resource);
            }
        }

        /// <summary>Parses <c>Kind Name(Properties);</c>. A declaration with errors adds them and returns null.</summary>
        private AuthoredRenderResource? ParseResourceDeclaration(RenderPipelineScriptDocument document)
        {
            Token kindToken = Consume(TokenKind.Identifier, "Expected a resource kind, such as Texture or FrameBuffer.");
            Token nameToken = ConsumeName("Expected a resource name.");
            List<PropertyAssignment> properties = ParsePropertyAssignments();
            Consume(TokenKind.Semicolon, "Expected ';' after the resource declaration.");

            if (!Enum.TryParse(kindToken.Text, ignoreCase: true, out EAuthoredRenderResourceKind kind))
            {
                document.AddError(kindToken.Line, kindToken.Column, $"Unknown resource kind '{kindToken.Text}'. Known kinds: {string.Join(", ", Enum.GetNames<EAuthoredRenderResourceKind>())}.");
                return null;
            }

            AuthoredRenderResource resource = new() { Name = nameToken.Text, Kind = kind };
            return ApplyProperties(document, resource, properties) ? resource : null;
        }

        private void ParseSchemaBlock(RenderPipelineScriptDocument document)
        {
            while (!Match(TokenKind.CloseBrace))
            {
                if (ParseStageDeclaration(document, document.Stages.Count) is { } stage)
                    document.Stages.Add(stage);
            }
        }

        /// <summary>
        /// Parses <c>stage key(Properties) { param ...; }</c> or <c>library key;</c>. The order defaults to
        /// <paramref name="defaultOrder"/>. A declaration with errors adds them and returns null.
        /// </summary>
        private AuthoredPostProcessStage? ParseStageDeclaration(RenderPipelineScriptDocument document, int defaultOrder)
        {
            Token statement = Consume(TokenKind.Identifier, "Expected 'stage' or 'library' in the schema block.");
            bool isLibrary = statement.Text switch
            {
                "library" => true,
                "stage" => false,
                _ => throw Error(statement, "Expected 'stage' or 'library' in the schema block."),
            };

            Token keyToken = ConsumeName("Expected a stage key.");
            List<PropertyAssignment> properties = ParsePropertyAssignments();
            AuthoredPostProcessStage stage = new()
            {
                Key = keyToken.Text,
                IsLibraryStage = isLibrary,
                Order = defaultOrder,
            };

            bool valid = ApplyProperties(document, stage, properties);
            if (Match(TokenKind.OpenBrace))
            {
                while (!Match(TokenKind.CloseBrace))
                {
                    Token paramToken = Consume(TokenKind.Identifier, "Expected 'param' in the stage body.");
                    if (paramToken.Text != "param")
                        throw Error(paramToken, "Expected 'param' in the stage body.");

                    Token paramName = ConsumeName("Expected a parameter name.");
                    List<PropertyAssignment> paramProperties = ParsePropertyAssignments();
                    Consume(TokenKind.Semicolon, "Expected ';' after the parameter declaration.");
                    AuthoredPostProcessParameter parameter = new() { Name = paramName.Text };
                    if (ApplyProperties(document, parameter, paramProperties))
                        stage.Parameters.Add(parameter);
                }
            }
            else
            {
                Consume(TokenKind.Semicolon, "Expected ';' or '{' after the stage declaration.");
            }

            return valid ? stage : null;
        }

        /// <summary>Sets each assignment on <paramref name="target"/>. Each failed assignment adds an error.</summary>
        /// <returns>True when every assignment succeeded.</returns>
        private static bool ApplyProperties(RenderPipelineScriptDocument document, object target, List<PropertyAssignment> properties)
        {
            bool valid = true;
            for (int i = 0; i < properties.Count; i++)
            {
                PropertyAssignment assignment = properties[i];
                try
                {
                    ScriptValueConverter.SetObjectProperty(target, assignment.PropertyName, assignment.Value);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or InvalidCastException or OverflowException)
                {
                    document.AddError(assignment.Line, assignment.Column, ex.Message);
                    valid = false;
                }
            }

            return valid;
        }

        private int ParseInteger(string message)
        {
            bool negative = Match(TokenKind.Minus);
            Token number = Consume(TokenKind.Number, message);
            if (!int.TryParse(number.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw Error(number, message);
            return negative ? -value : value;
        }

        /// <summary>Consumes a name, which is an identifier or a quoted string.</summary>
        private Token ConsumeName(string message)
        {
            Token token = Peek();
            if (token.Kind is TokenKind.Identifier or TokenKind.String)
                return Advance();

            throw Error(token, message);
        }
    }
}
