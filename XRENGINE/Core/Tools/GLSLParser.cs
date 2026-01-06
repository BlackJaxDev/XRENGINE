using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;

namespace XREngine.Core.Tools;

public class GLSLManager
{
    public sealed class Variable
    {
        public int? LayoutLocation { get; set; }
        public EShaderVarType? Type { get; set; }
        public string TypeName { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsArray { get; set; }
        public string? DefaultValue { get; set; }
    }

    public sealed class Parameter
    {
        public string Name { get; set; } = "";
        public string TypeName { get; set; } = "";
        public EShaderVarType? Type { get; set; }
    }

    public sealed class Method
    {
        public string ReturnTypeName { get; set; } = "";
        public EShaderVarType? ReturnType { get; set; }
        public string Name { get; set; } = "";
        public List<Parameter> Parameters { get; init; } = [];
        public string Body { get; set; } = "";
        public bool IsMain => string.Equals(Name, "main", StringComparison.Ordinal);
    }

    public sealed record ParsedInvocation(string? DeclaredType, string OutputName, string MethodName, IReadOnlyList<string> Arguments);

    public EGLSLVersion Version { get; private set; } = EGLSLVersion.Ver_460;
    public string RawSource { get; private set; } = "";
    public List<Variable> Uniforms { get; private set; } = [];
    public List<Variable> In { get; private set; } = [];
    public List<Variable> Out { get; private set; } = [];
    public List<Variable> Consts { get; private set; } = [];
    public List<Method> Methods { get; private set; } = [];
    public IReadOnlyList<ParsedInvocation> MainInvocations { get; private set; } = Array.Empty<ParsedInvocation>();
    public Method? MainMethod => Methods.FirstOrDefault(m => m.IsMain);

    private static readonly Regex VersionRegex = new(@"#version\s+(?<ver>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariableRegex = new(@"(?:layout\s*\(\s*location\s*=\s*(?<loc>\d+)\s*\)\s*)?(?<qualifier>in|out|uniform)\s+(?<type>\w+)\s+(?<name>\w+)(?<array>\s*\[\s*\d*\s*\])?(?:\s*=\s*(?<default>[^;]+))?\s*;", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex ConstRegex = new(@"const\s+(?<type>\w+)\s+(?<name>\w+)\s*=\s*(?<value>[^;]+);", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex MethodRegex = new(@"(?<ret>\w[\w\d]*)\s+(?<name>\w[\w\d]*)\s*\((?<args>[^)]*)\)\s*\{(?<body>.*?)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex InvocationRegex = new(@"(?:(?<decl>\w+)\s+)?(?<out>\w+)\s*=\s*(?<method>\w+)\s*\((?<args>[^)]*)\)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);

    public void Parse(string text)
    {
        RawSource = text ?? string.Empty;
        Uniforms = [];
        In = [];
        Out = [];
        Consts = [];
        Methods = [];

        ParseVersion(text);
        ParseVariables(text);
        ParseConsts(text);
        ParseMethods(text);
        ParseMainInvocations();
    }

    private void ParseVersion(string text)
    {
        var match = VersionRegex.Match(text);
        if (!match.Success)
            return;

        string versionToken = match.Groups["ver"].Value;
        if (int.TryParse(versionToken, NumberStyles.Any, CultureInfo.InvariantCulture, out int ver))
        {
            string enumName = $"Ver_{ver}";
            if (Enum.TryParse(enumName, out EGLSLVersion parsed))
                Version = parsed;
        }
    }

    private void ParseVariables(string text)
    {
        foreach (Match match in VariableRegex.Matches(text))
        {
            var variable = new Variable
            {
                Name = match.Groups["name"].Value,
                TypeName = match.Groups["type"].Value,
                Type = ParseType(match.Groups["type"].Value),
                DefaultValue = match.Groups["default"].Success ? match.Groups["default"].Value : null,
                IsArray = match.Groups["array"].Success
            };

            if (int.TryParse(match.Groups["loc"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out int layout))
                variable.LayoutLocation = layout;

            string qualifier = match.Groups["qualifier"].Value.ToLowerInvariant();
            switch (qualifier)
            {
                case "uniform":
                    Uniforms.Add(variable);
                    break;
                case "in":
                    In.Add(variable);
                    break;
                case "out":
                    Out.Add(variable);
                    break;
            }
        }
    }

    private void ParseConsts(string text)
    {
        foreach (Match match in ConstRegex.Matches(text))
        {
            Consts.Add(new Variable
            {
                Name = match.Groups["name"].Value,
                TypeName = match.Groups["type"].Value,
                Type = ParseType(match.Groups["type"].Value),
                DefaultValue = match.Groups["value"].Value
            });
        }
    }

    private void ParseMethods(string text)
    {
        foreach (Match match in MethodRegex.Matches(text))
        {
            string methodName = match.Groups["name"].Value;
            string retType = match.Groups["ret"].Value;
            string argText = match.Groups["args"].Value;

            var method = new Method
            {
                Name = methodName,
                ReturnTypeName = retType,
                ReturnType = string.Equals(retType, "void", StringComparison.OrdinalIgnoreCase) ? null : ParseType(retType),
                Body = match.Groups["body"].Value.Trim()
            };

            var args = argText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var arg in args)
            {
                var pieces = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length >= 2)
                {
                    string typeName = pieces[0];
                    string paramName = pieces[1];
                    method.Parameters.Add(new Parameter
                    {
                        Name = paramName,
                        TypeName = typeName,
                        Type = ParseType(typeName)
                    });
                }
            }

            Methods.Add(method);
        }
    }

    private void ParseMainInvocations()
    {
        if (MainMethod is null)
        {
            MainInvocations = Array.Empty<ParsedInvocation>();
            return;
        }

        List<ParsedInvocation> invocations = [];
        foreach (Match match in InvocationRegex.Matches(MainMethod.Body))
        {
            string? decl = match.Groups["decl"].Success ? match.Groups["decl"].Value : null;
            string output = match.Groups["out"].Value;
            string method = match.Groups["method"].Value;
            string args = match.Groups["args"].Value;
            var argList = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            invocations.Add(new ParsedInvocation(
                decl,
                output,
                method,
                argList));
        }

        MainInvocations = invocations;
    }

    private static EShaderVarType? ParseType(string typeText)
    {
        string enumName = typeText.StartsWith("_", StringComparison.Ordinal) ? typeText : $"_{typeText}";
        if (Enum.TryParse(enumName, true, out EShaderVarType parsed))
            return parsed;

        return null;
    }
}
