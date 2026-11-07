using System.Text;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Overlays;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering;

// Patch statements of derived scripts: after, before, replace, remove, wrap, append, set, and condition, and the
// patch forms of the resources and schema blocks.
public sealed partial class RenderPipelineScript
{
    /// <summary>The source location of a patch. <paramref name="BodyLine"/> is the line of the first character of a command body, or 0.</summary>
    internal readonly record struct ScriptPatchLocation(int Line, int Column, int BodyLine);

    private static readonly string[] s_commandPatchWords = ["after", "before", "replace", "remove", "wrap", "append", "set", "condition"];

    private sealed partial class Parser
    {
        /// <summary>
        /// Parses a patch statement when the next tokens are one. A word that is also a command name stays a command,
        /// so a command named <c>remove</c> still parses.
        /// </summary>
        private bool TryParseCommandPatch(RenderPipelineScriptDocument document)
        {
            Token word = Peek();
            if (word.Kind is not (TokenKind.Identifier or TokenKind.Keyword) ||
                Peek(1).Kind != TokenKind.OpenParen ||
                Array.IndexOf(s_commandPatchWords, word.Text) < 0 ||
                CommandTypes.ContainsKey(word.Text))
            {
                return false;
            }

            if (!document.IsDerived)
                throw Error(word, "A patch statement needs a derive header.");

            Advance();
            Consume(TokenKind.OpenParen, "Expected '(' after the patch word.");
            string target = ParseTarget();
            Consume(TokenKind.CloseParen, "Expected ')' after the patch target.");

            RenderCommandPatch patch = new() { Target = target };
            int bodyLine = 0;
            switch (word.Text)
            {
                case "after":
                case "before":
                case "replace":
                case "append":
                    patch.Kind = word.Text switch
                    {
                        "after" => ERenderCommandPatchKind.InsertAfter,
                        "before" => ERenderCommandPatchKind.InsertBefore,
                        "replace" => ERenderCommandPatchKind.Replace,
                        _ => ERenderCommandPatchKind.AppendTo,
                    };
                    patch.CommandText = ParseBodyText(out bodyLine);
                    break;
                case "remove":
                    patch.Kind = ERenderCommandPatchKind.Remove;
                    Consume(TokenKind.Semicolon, "Expected ';' after the remove statement.");
                    break;
                case "wrap":
                    patch.Kind = ERenderCommandPatchKind.Wrap;
                    if (MatchKeyword("if"))
                    {
                        patch.Condition = ParseParenthesizedDataCondition();
                        patch.WrapLabel = ParseBranchLabel();
                        Consume(TokenKind.Semicolon, "Expected ';' after the wrap statement.");
                    }
                    else
                    {
                        patch.WrapScopeText = ParseBodyText(out bodyLine);
                    }
                    break;
                case "set":
                {
                    patch.Kind = ERenderCommandPatchKind.SetProperty;
                    Consume(TokenKind.Dot, "Expected '.' and a property name after set(...).");
                    patch.PropertyName = Consume(TokenKind.Identifier, "Expected a property name.").Text;
                    Token equals = Consume(TokenKind.Equals, "Expected '=' after the property name.");
                    ParseValue();
                    Token end = Consume(TokenKind.Semicolon, "Expected ';' after the value.");
                    patch.ValueText = _script[(equals.Offset + 1)..end.Offset].Trim();
                    break;
                }
                default:
                    patch.Kind = ERenderCommandPatchKind.SetCondition;
                    Consume(TokenKind.Equals, "Expected '=' after condition(...).");
                    patch.Condition = ParseParenthesizedDataCondition();
                    Consume(TokenKind.Semicolon, "Expected ';' after the condition.");
                    break;
            }

            document.CommandPatches.Add(patch);
            document.CommandPatchLocations.Add(new ScriptPatchLocation(word.Line, word.Column, bodyLine));
            return true;
        }

        /// <summary>Parses a target: a dotted anchor id such as <c>post.bloom</c>, or a quoted string such as a structural path.</summary>
        private string ParseTarget()
        {
            Token first = Peek();
            if (first.Kind == TokenKind.String)
                return Advance().Text;
            if (first.Kind is not (TokenKind.Identifier or TokenKind.Keyword))
                throw Error(first, "Expected an anchor id or a quoted target.");

            StringBuilder builder = new(Advance().Text);
            while (Match(TokenKind.Dot))
            {
                Token part = Peek();
                if (part.Kind is not (TokenKind.Identifier or TokenKind.Keyword or TokenKind.Number))
                    throw Error(part, "Expected a name after '.' in the anchor id.");
                builder.Append('.').Append(Advance().Text);
            }

            return builder.ToString();
        }

        /// <summary>Parses <c>{ statements }</c>, checks the statements, and returns the text between the braces.</summary>
        private string ParseBodyText(out int bodyLine)
        {
            Token open = Consume(TokenKind.OpenBrace, "Expected '{' to begin the patch body.");
            bodyLine = open.Line;
            while (!Is(TokenKind.CloseBrace))
            {
                if (Is(TokenKind.EndOfFile))
                    throw Error(open, "Expected '}' to end the patch body.");
                ParseStatement();
            }

            Token close = Advance();
            return _script[(open.Offset + 1)..close.Offset];
        }

        /// <summary>
        /// Parses the <c>resources</c> block of a derived script: a declaration adds a resource, <c>override</c> before a
        /// declaration replaces a base resource, and <c>remove Name;</c> removes one.
        /// </summary>
        private void ParseResourcePatchBlock(RenderPipelineScriptDocument document)
        {
            while (!Match(TokenKind.CloseBrace))
            {
                Token start = Peek();
                if (IsContextual("remove") && Peek(1).Kind is TokenKind.Identifier or TokenKind.String)
                {
                    Advance();
                    Token removed = ConsumeName("Expected a resource name after 'remove'.");
                    Consume(TokenKind.Semicolon, "Expected ';' after the remove statement.");
                    AddResourcePatch(document, new RenderResourcePatch { Kind = ERenderResourcePatchKind.Remove, Name = removed.Text }, start);
                    continue;
                }

                ERenderResourcePatchKind kind = ERenderResourcePatchKind.Add;
                if (IsContextual("override") && Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind is TokenKind.Identifier or TokenKind.String)
                {
                    Advance();
                    kind = ERenderResourcePatchKind.Override;
                }

                if (ParseResourceDeclaration(document) is { } resource)
                    AddResourcePatch(document, new RenderResourcePatch { Kind = kind, Name = resource.Name, Resource = resource }, start);
            }
        }

        private static void AddResourcePatch(RenderPipelineScriptDocument document, RenderResourcePatch patch, Token start)
        {
            document.ResourcePatches.Add(patch);
            document.ResourcePatchLocations.Add(new ScriptPatchLocation(start.Line, start.Column, 0));
        }

        /// <summary>
        /// Parses the <c>schema</c> block of a derived script: a stage declaration adds a stage, and <c>hide key;</c>,
        /// <c>default key.Param = value;</c>, and <c>category key = "Category";</c> change base stages.
        /// </summary>
        private void ParseSchemaPatchBlock(RenderPipelineScriptDocument document)
        {
            while (!Match(TokenKind.CloseBrace))
            {
                Token statement = Peek();
                if (statement.Kind == TokenKind.Identifier && statement.Text is "hide" or "category")
                {
                    Advance();
                    Token key = ConsumeName("Expected a stage key.");
                    PostProcessSchemaPatch patch = new() { StageKey = key.Text };
                    if (statement.Text == "hide")
                    {
                        patch.Kind = EPostProcessSchemaPatchKind.HideStage;
                    }
                    else
                    {
                        patch.Kind = EPostProcessSchemaPatchKind.MoveToCategory;
                        Consume(TokenKind.Equals, "Expected '=' after the stage key.");
                        patch.CategoryKey = ConsumeName("Expected a category key.").Text;
                    }

                    Consume(TokenKind.Semicolon, "Expected ';' after the schema statement.");
                    AddSchemaPatch(document, patch, statement);
                    continue;
                }

                if (statement.Kind == TokenKind.Keyword && statement.Text == "default")
                {
                    Advance();
                    Token key = ConsumeName("Expected a stage key.");
                    Consume(TokenKind.Dot, "Expected '.' and a parameter name after the stage key.");
                    Token parameter = ConsumeName("Expected a parameter name.");
                    Token equals = Consume(TokenKind.Equals, "Expected '=' after the parameter name.");
                    ParseValue();
                    Token end = Consume(TokenKind.Semicolon, "Expected ';' after the value.");
                    AddSchemaPatch(document, new PostProcessSchemaPatch
                    {
                        Kind = EPostProcessSchemaPatchKind.OverrideDefault,
                        StageKey = key.Text,
                        ParameterName = parameter.Text,
                        ValueText = _script[(equals.Offset + 1)..end.Offset].Trim(),
                    }, statement);
                    continue;
                }

                if (ParseStageDeclaration(document, document.SchemaPatches.Count) is { } stage)
                {
                    AddSchemaPatch(document, new PostProcessSchemaPatch
                    {
                        Kind = EPostProcessSchemaPatchKind.AddStage,
                        StageKey = stage.Key,
                        Stage = stage,
                    }, statement);
                }
            }
        }

        private static void AddSchemaPatch(RenderPipelineScriptDocument document, PostProcessSchemaPatch patch, Token start)
        {
            document.SchemaPatches.Add(patch);
            document.SchemaPatchLocations.Add(new ScriptPatchLocation(start.Line, start.Column, 0));
        }
    }
}
