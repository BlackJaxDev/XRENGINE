using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using XREngine.Editor;

var options = GeneratorOptions.Parse(args);
string repoRoot = FindRepositoryRoot(options.RepositoryRoot);
string sourceFilePath = Path.GetFullPath(Path.Combine(repoRoot, options.SourceFile));
string schemaPath = Path.GetFullPath(Path.Combine(repoRoot, options.SchemaPath));
string[] settingsPaths = options.SettingsPaths
    .Select(path => Path.GetFullPath(Path.Combine(repoRoot, path)))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (!File.Exists(sourceFilePath))
{
    Console.Error.WriteLine($"Settings source file not found: {sourceFilePath}");
    return 2;
}

var sourceMetadata = SourceMetadataParser.Parse(File.ReadAllText(sourceFilePath));
var metadataBuilder = new SettingsMetadataBuilder(sourceMetadata);
TypeMetadata rootMetadata = metadataBuilder.Build(typeof(EditorUnitTests.Settings));
JObject? existingSchema = LoadJsonObjectIfExists(schemaPath);

JObject schemaDocument = SchemaGenerator.Build(rootMetadata, existingSchema);
Directory.CreateDirectory(Path.GetDirectoryName(schemaPath)!);
File.WriteAllText(schemaPath, schemaDocument.ToString(Formatting.Indented) + Environment.NewLine);
Console.WriteLine($"Wrote schema: {MakeRelativePath(repoRoot, schemaPath)}");

foreach (string settingsPath in settingsPaths)
{
    JObject? existingSettings = LoadJsonObjectIfExists(settingsPath);
    JObject mergedSettings = SettingsDocumentGenerator.Build(rootMetadata, existingSettings);
    string schemaRelativePath = NormalizePath(Path.GetRelativePath(Path.GetDirectoryName(settingsPath)!, schemaPath));
    string jsonc = JsoncWriter.Write(rootMetadata, mergedSettings, schemaDocument, schemaRelativePath);

    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
    File.WriteAllText(settingsPath, jsonc);
    Console.WriteLine($"Wrote settings: {MakeRelativePath(repoRoot, settingsPath)}");
}

return 0;

static JObject? LoadJsonObjectIfExists(string path)
{
    if (!File.Exists(path))
        return null;

    var settings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, LineInfoHandling = LineInfoHandling.Ignore };
    return JObject.Parse(File.ReadAllText(path), settings);
}

static string FindRepositoryRoot(string? explicitRoot)
{
    if (!string.IsNullOrWhiteSpace(explicitRoot))
        return Path.GetFullPath(explicitRoot);

    string current = Path.GetFullPath(Directory.GetCurrentDirectory());
    while (true)
    {
        if (File.Exists(Path.Combine(current, "XRENGINE.sln")))
            return current;

        string? parent = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
            throw new DirectoryNotFoundException("Unable to locate repository root containing XRENGINE.sln.");

        current = parent;
    }
}

static string NormalizePath(string path) => path.Replace('\\', '/');

static string MakeRelativePath(string repoRoot, string absolutePath)
    => NormalizePath(Path.GetRelativePath(repoRoot, absolutePath));

sealed class GeneratorOptions
{
    public string? RepositoryRoot { get; private set; }
    public string SchemaPath { get; private set; } = Path.Combine(".vscode", "schemas", "unit-testing-world-settings.schema.json");
    public string SourceFile { get; private set; } = Path.Combine("XREngine.Editor", "Unit Tests", "Default", "UnitTestingWorld.Toggles.cs");
    public List<string> SettingsPaths { get; } =
    [
        Path.Combine("Assets", "UnitTestingWorldSettings.jsonc"),
        Path.Combine("XREngine.Server", "Assets", "UnitTestingWorldSettings.jsonc")
    ];

    public static GeneratorOptions Parse(string[] args)
    {
        var options = new GeneratorOptions();

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--repo-root":
                    options.RepositoryRoot = RequireValue(args, ref index, arg);
                    break;
                case "--schema-path":
                    options.SchemaPath = RequireValue(args, ref index, arg);
                    break;
                case "--source-file":
                    options.SourceFile = RequireValue(args, ref index, arg);
                    break;
                case "--settings-path":
                    options.SettingsPaths.Add(RequireValue(args, ref index, arg));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {option}.");

        index++;
        return args[index];
    }
}

sealed class SourceMetadata
{
    public Dictionary<string, List<string>> MemberOrderByType { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> MemberDescriptionsByType { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> MemberSectionsByType { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> GetOrder(string typeName)
        => MemberOrderByType.TryGetValue(typeName, out var members) ? members : [];

    public string? GetDescription(string typeName, string memberName)
        => MemberDescriptionsByType.TryGetValue(typeName, out var members) && members.TryGetValue(memberName, out var description)
            ? description
            : null;

    public string? GetSection(string typeName, string memberName)
        => MemberSectionsByType.TryGetValue(typeName, out var members) && members.TryGetValue(memberName, out var section)
            ? section
            : null;
}

static class SourceMetadataParser
{
    private static readonly Regex MemberRegex = new(
        @"^public\s+(?<type>.+?)\s+(?<name>\w+)\s*(?:\{\s*get;\s*set;\s*\}\s*(?:=\s*[^;]*)?;?|=\s*[^;]+;)(?<comment>\s*//.*)?$",
        RegexOptions.Compiled);

    public static SourceMetadata Parse(string source)
    {
        var metadata = new SourceMetadata();
        ParseClass(source, "Settings", metadata);
        ParseClass(source, "ModelImportSettings", metadata);
        ParseClass(source, "YawPitchRollDegrees", metadata);
        ParseClass(source, "TranslationXYZ", metadata);
        ParseClass(source, "ProbeGridCounts", metadata);
        return metadata;
    }

    private static void ParseClass(string source, string className, SourceMetadata metadata)
    {
        string? body = ExtractTypeBody(source, $"public class {className}");
        if (body is null)
            return;

        var order = new List<string>();
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        metadata.MemberOrderByType[className] = order;
        metadata.MemberDescriptionsByType[className] = descriptions;
        metadata.MemberSectionsByType[className] = sections;

        int depth = 0;
        bool summaryActive = false;
        var summaryLines = new List<string>();
        string? pendingSummary = null;
        string? currentSection = null;
        string[] lines = body.Replace("\r\n", "\n").Split('\n');

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (depth == 0 && (summaryActive || line.StartsWith("///", StringComparison.Ordinal)))
            {
                string docText = line.StartsWith("///", StringComparison.Ordinal)
                    ? line[3..].Trim()
                    : line;

                if (docText.Contains("<summary>", StringComparison.Ordinal))
                    summaryActive = true;

                string cleaned = Regex.Replace(docText, "<.*?>", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                    summaryLines.Add(cleaned);

                if (docText.Contains("</summary>", StringComparison.Ordinal))
                {
                    summaryActive = false;
                    pendingSummary = string.Join(' ', summaryLines).Trim();
                    summaryLines.Clear();
                }

                continue;
            }

            if (depth == 0)
            {
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    string commentText = line[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(commentText))
                        currentSection = commentText;
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("public class ", StringComparison.Ordinal) || line.StartsWith("public enum ", StringComparison.Ordinal))
                {
                    pendingSummary = null;
                    continue;
                }

                Match memberMatch = MemberRegex.Match(line);
                if (memberMatch.Success && !line.Contains("=>", StringComparison.Ordinal))
                {
                    string memberName = memberMatch.Groups["name"].Value;
                    order.Add(memberName);

                    if (!string.IsNullOrWhiteSpace(currentSection))
                        sections[memberName] = currentSection;

                    string? description = null;
                    string trailingComment = memberMatch.Groups["comment"].Value;
                    if (!string.IsNullOrWhiteSpace(trailingComment))
                        description = trailingComment.Trim()[2..].Trim();
                    else if (!string.IsNullOrWhiteSpace(pendingSummary))
                        description = pendingSummary;

                    if (!string.IsNullOrWhiteSpace(description))
                        descriptions[memberName] = description;

                    pendingSummary = null;
                }
            }

            depth += Count(rawLine, '{');
            depth -= Count(rawLine, '}');
        }
    }

    private static string? ExtractTypeBody(string source, string declaration)
    {
        int declarationIndex = source.IndexOf(declaration, StringComparison.Ordinal);
        if (declarationIndex < 0)
            return null;

        int bodyStart = source.IndexOf('{', declarationIndex);
        if (bodyStart < 0)
            return null;

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            char current = source[index];
            if (current == '{')
                depth++;
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                    return source[(bodyStart + 1)..index];
            }
        }

        return null;
    }

    private static int Count(string text, char value)
    {
        int total = 0;
        foreach (char current in text)
            if (current == value)
                total++;
        return total;
    }
}

sealed class SettingsMetadataBuilder
{
    private static readonly NullabilityInfoContext NullabilityContext = new();
    private readonly SourceMetadata _sourceMetadata;
    private readonly Dictionary<Type, TypeMetadata> _cache = [];

    public SettingsMetadataBuilder(SourceMetadata sourceMetadata)
    {
        _sourceMetadata = sourceMetadata;
    }

    public TypeMetadata Build(Type type)
    {
        if (_cache.TryGetValue(type, out var existing))
            return existing;

        var metadata = new TypeMetadata(type.Name, type);
        _cache[type] = metadata;

        IReadOnlyList<string> sourceOrder = _sourceMetadata.GetOrder(type.Name);
        var members = GetSerializableMembers(type)
            .Select(member => BuildMember(type, member, sourceOrder))
            .OrderBy(member => member.Order)
            .ThenBy(member => member.Name, StringComparer.Ordinal)
            .ToArray();

        metadata.SetMembers(members);
        return metadata;
    }

    private MemberMetadata BuildMember(Type declaringType, MemberInfo member, IReadOnlyList<string> sourceOrder)
    {
        Type declaredType = GetMemberType(member);
        Type nonNullableType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        bool allowsNull = AllowsNull(member, declaredType);
        bool isCollection = TryGetCollectionElementType(nonNullableType, out var elementType);
        bool isEnum = nonNullableType.IsEnum;
        bool isFlagsEnum = isEnum && nonNullableType.GetCustomAttribute<FlagsAttribute>() is not null;

        TypeMetadata? objectMetadata = null;
        if (isCollection && elementType is not null && IsComplexObject(elementType))
            objectMetadata = Build(elementType);
        else if (!isCollection && IsComplexObject(nonNullableType))
            objectMetadata = Build(nonNullableType);

        int order = IndexOf(sourceOrder, member.Name);
        if (order < 0)
            order = 1000000 + member.MetadataToken;

        return new MemberMetadata(
            member.Name,
            member,
            declaredType,
            nonNullableType,
            allowsNull,
            isCollection,
            elementType,
            isEnum,
            isFlagsEnum,
            objectMetadata,
            _sourceMetadata.GetDescription(declaringType.Name, member.Name),
            _sourceMetadata.GetSection(declaringType.Name, member.Name),
            order);
    }

    private static IEnumerable<MemberInfo> GetSerializableMembers(Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;

        var members = new List<MemberInfo>();

        foreach (PropertyInfo property in type.GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            if (property.GetMethod is null || property.SetMethod is null)
                continue;
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;

            members.Add(property);
        }

        foreach (FieldInfo field in type.GetFields(Flags))
        {
            if (field.IsStatic)
                continue;
            if (field.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;

            members.Add(field);
        }

        return members.OrderBy(member => member.MetadataToken).ToArray();
    }

    private static Type GetMemberType(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException($"Unsupported member type {member.GetType().Name}.")
        };

    private static bool AllowsNull(MemberInfo member, Type declaredType)
    {
        if (Nullable.GetUnderlyingType(declaredType) is not null)
            return true;

        if (declaredType.IsValueType)
            return false;

        return member switch
        {
            PropertyInfo property => NullabilityContext.Create(property).WriteState == NullabilityState.Nullable,
            FieldInfo field => NullabilityContext.Create(field).WriteState == NullabilityState.Nullable,
            _ => false
        };
    }

    private static bool TryGetCollectionElementType(Type type, out Type? elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return true;
        }

        if (type.IsGenericType)
        {
            Type genericDefinition = type.GetGenericTypeDefinition();
            if (genericDefinition == typeof(List<>) || genericDefinition == typeof(IList<>) || genericDefinition == typeof(IEnumerable<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null;
        return false;
    }

    private static bool IsComplexObject(Type type)
        => type.IsClass && type != typeof(string);

    private static int IndexOf(IReadOnlyList<string> values, string target)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], target, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }
}

sealed class TypeMetadata
{
    private IReadOnlyList<MemberMetadata> _members = [];

    public TypeMetadata(string name, Type clrType)
    {
        Name = name;
        ClrType = clrType;
    }

    public string Name { get; }
    public Type ClrType { get; }
    public IReadOnlyList<MemberMetadata> Members => _members;

    public void SetMembers(IReadOnlyList<MemberMetadata> members) => _members = members;

    public MemberMetadata? FindMember(string name)
        => _members.FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.Ordinal));
}

sealed record MemberMetadata(
    string Name,
    MemberInfo ReflectionMember,
    Type DeclaredType,
    Type NonNullableType,
    bool AllowsNull,
    bool IsCollection,
    Type? ElementType,
    bool IsEnum,
    bool IsFlagsEnum,
    TypeMetadata? ObjectMetadata,
    string? SourceDescription,
    string? Section,
    int Order)
{
    public string[] EnumNames => IsEnum ? Enum.GetNames(NonNullableType) : [];
}

static class SchemaGenerator
{
    public static JObject Build(TypeMetadata rootMetadata, JObject? existingSchema)
    {
        JObject? existingDefinitions = existingSchema?["definitions"] as JObject;
        var definitions = new JObject();
        var rootProperties = new JObject
        {
            ["$schema"] = MergeNode(
                new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional schema pointer used by editors. Ignored by the runtime loader.",
                    ["default"] = "../.vscode/schemas/unit-testing-world-settings.schema.json"
                },
                existingSchema?["properties"]?["$schema"]),
            ["$id"] = MergeNode(
                new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional document identifier. Not consumed by the runtime settings loader."
                },
                existingSchema?["properties"]?["$id"])
        };

        foreach (MemberMetadata member in rootMetadata.Members)
        {
            rootProperties[member.Name] = BuildMemberNode(member, existingSchema?["properties"]?[member.Name], definitions, existingDefinitions);
        }

        return new JObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["$id"] = "./.vscode/schemas/unit-testing-world-settings.schema.json",
            ["title"] = "Unit Testing World Settings",
            ["type"] = "object",
            ["description"] = "Startup settings for the XREngine Unit Testing World. Use this schema with the JSONC settings files. Hover descriptions explain what each property controls, and enum-backed properties expose selectable values in VS Code.",
            ["additionalProperties"] = false,
            ["properties"] = rootProperties,
            ["required"] = new JArray(),
            ["definitions"] = definitions
        };
    }

    private static JToken BuildMemberNode(MemberMetadata member, JToken? existingNode, JObject definitions, JObject? existingDefinitions)
    {
        JObject generated = BuildTypeNode(member.NonNullableType, member.AllowsNull, member.IsCollection, member.ElementType, member.IsFlagsEnum, member.ObjectMetadata, definitions, existingDefinitions);

        if (!string.IsNullOrWhiteSpace(member.SourceDescription) && generated["description"] is null)
            generated["description"] = member.SourceDescription;

        if (member.IsEnum && !member.IsFlagsEnum && generated["markdownDescription"] is null)
            generated["markdownDescription"] = $"Supported values: `{string.Join("`, `", member.EnumNames)}`.";

        if (generated["description"] is null)
            generated["description"] = GetFallbackDescription(member);

        return MergeNode(generated, existingNode);
    }

    private static JObject BuildTypeNode(Type type, bool allowsNull, bool isCollection, Type? elementType, bool isFlagsEnum, TypeMetadata? objectMetadata, JObject definitions, JObject? existingDefinitions)
    {
        if (isCollection && elementType is not null)
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = BuildArrayItemNode(elementType, objectMetadata, definitions, existingDefinitions)
            };
        }

        if (type.IsEnum && !isFlagsEnum)
        {
            EnsureEnumDefinition(type, definitions, existingDefinitions?[type.Name]);
            if (allowsNull)
            {
                return new JObject
                {
                    ["oneOf"] = new JArray
                    {
                        new JObject { ["$ref"] = $"#/definitions/{type.Name}" },
                        new JObject { ["type"] = "null" }
                    }
                };
            }

            return new JObject { ["$ref"] = $"#/definitions/{type.Name}" };
        }

        if (type.IsEnum && isFlagsEnum)
        {
            return allowsNull
                ? new JObject { ["type"] = new JArray("string", "null") }
                : new JObject { ["type"] = "string" };
        }

        if (objectMetadata is not null)
        {
            EnsureObjectDefinition(objectMetadata, definitions, existingDefinitions);
            if (allowsNull)
            {
                return new JObject
                {
                    ["oneOf"] = new JArray
                    {
                        new JObject { ["$ref"] = $"#/definitions/{objectMetadata.Name}" },
                        new JObject { ["type"] = "null" }
                    }
                };
            }

            return new JObject { ["$ref"] = $"#/definitions/{objectMetadata.Name}" };
        }

        return allowsNull
            ? new JObject { ["type"] = new JArray(MapPrimitiveType(type), "null") }
            : new JObject { ["type"] = MapPrimitiveType(type) };
    }

    private static JToken BuildArrayItemNode(Type elementType, TypeMetadata? objectMetadata, JObject definitions, JObject? existingDefinitions)
    {
        Type nonNullable = Nullable.GetUnderlyingType(elementType) ?? elementType;
        if (nonNullable.IsEnum && nonNullable.GetCustomAttribute<FlagsAttribute>() is null)
        {
            EnsureEnumDefinition(nonNullable, definitions, existingDefinitions?[nonNullable.Name]);
            return new JObject { ["$ref"] = $"#/definitions/{nonNullable.Name}" };
        }

        if (nonNullable.IsEnum)
            return new JObject { ["type"] = "string" };

        if (objectMetadata is not null)
        {
            EnsureObjectDefinition(objectMetadata, definitions, existingDefinitions);
            return new JObject { ["$ref"] = $"#/definitions/{objectMetadata.Name}" };
        }

        return new JObject { ["type"] = MapPrimitiveType(nonNullable) };
    }

    private static void EnsureObjectDefinition(TypeMetadata metadata, JObject definitions, JObject? existingDefinitions)
    {
        if (definitions.Property(metadata.Name) is not null)
            return;

        var properties = new JObject();
        var definition = new JObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties
        };

        definitions[metadata.Name] = definition;
        foreach (MemberMetadata member in metadata.Members)
            properties[member.Name] = BuildMemberNode(member, existingDefinitions?[metadata.Name]?["properties"]?[member.Name], definitions, existingDefinitions);
    }

    private static void EnsureEnumDefinition(Type enumType, JObject definitions, JToken? existingDefinition)
    {
        if (definitions.Property(enumType.Name) is not null)
            return;

        string[] names = Enum.GetNames(enumType);
        definitions[enumType.Name] = MergeNode(new JObject
        {
            ["type"] = "string",
            ["enum"] = new JArray(names),
            ["markdownDescription"] = $"Supported values: `{string.Join("`, `", names)}`."
        }, existingDefinition);
    }

    private static JObject MergeNode(JObject generated, JToken? existingNode)
    {
        if (existingNode is not JObject existing)
            return generated;

        string[] copyKeys =
        [
            "description",
            "markdownDescription",
            "examples",
            "defaultSnippets",
            "default",
            "minimum",
            "maximum",
            "minLength",
            "maxLength",
            "pattern",
            "format"
        ];

        foreach (string key in copyKeys)
        {
            if (generated[key] is null && existing[key] is not null)
                generated[key] = existing[key]!.DeepClone();
        }

        return generated;
    }

    private static string GetFallbackDescription(MemberMetadata member)
    {
        if (member.IsCollection)
            return $"Generated from {member.Name} on {member.ReflectionMember.DeclaringType?.Name}.";
        if (member.IsEnum && !member.IsFlagsEnum)
            return $"Selects the {member.Name} enum value.";
        if (member.IsFlagsEnum)
            return $"Comma-separated {member.NonNullableType.Name} flag names.";
        if (member.NonNullableType == typeof(bool))
            return $"Enables or disables {member.Name}.";
        return $"Generated from {member.Name} on {member.ReflectionMember.DeclaringType?.Name}.";
    }

    private static string MapPrimitiveType(Type type)
    {
        Type nonNullable = Nullable.GetUnderlyingType(type) ?? type;
        if (nonNullable == typeof(string))
            return "string";
        if (nonNullable == typeof(bool))
            return "boolean";
        if (nonNullable == typeof(int) || nonNullable == typeof(long) || nonNullable == typeof(short) || nonNullable == typeof(byte) ||
            nonNullable == typeof(uint) || nonNullable == typeof(ulong) || nonNullable == typeof(ushort) || nonNullable == typeof(sbyte))
            return "integer";
        if (nonNullable == typeof(float) || nonNullable == typeof(double) || nonNullable == typeof(decimal))
            return "number";

        return "string";
    }
}

static class SettingsDocumentGenerator
{
    private static readonly JsonSerializer Serializer = JsonSerializer.CreateDefault(new JsonSerializerSettings
    {
        Converters = [new StringEnumConverter()]
    });

    public static JObject Build(TypeMetadata rootMetadata, JObject? existingSettings)
    {
        var generated = (JObject)JToken.FromObject(Activator.CreateInstance(rootMetadata.ClrType)!, Serializer);
        JObject merged = (JObject)MergeObject(rootMetadata, generated, existingSettings);

        string id = existingSettings?["$id"]?.Value<string>() ?? "1";
        var document = new JObject
        {
            ["$id"] = id
        };

        foreach (MemberMetadata member in rootMetadata.Members)
            document[member.Name] = merged[member.Name]?.DeepClone();

        return document;
    }

    private static JToken MergeObject(TypeMetadata metadata, JObject generated, JObject? existing)
    {
        if (existing is null)
            return generated;

        foreach (MemberMetadata member in metadata.Members)
        {
            if (existing.TryGetValue(member.Name, StringComparison.Ordinal, out JToken? existingValue))
            {
                generated[member.Name] = MergeValue(member, generated[member.Name], existingValue);
            }
        }

        return generated;
    }

    private static JToken MergeValue(MemberMetadata member, JToken? generatedValue, JToken existingValue)
    {
        if (member.IsEnum && !member.IsFlagsEnum)
        {
            string? canonicalExisting = NormalizeEnumValue(member, existingValue);
            if (!string.IsNullOrWhiteSpace(canonicalExisting))
                return canonicalExisting;

            string? canonicalGenerated = NormalizeEnumValue(member, generatedValue);
            if (!string.IsNullOrWhiteSpace(canonicalGenerated))
                return canonicalGenerated;
        }

        if (member.IsCollection && member.ElementType is not null && existingValue is JArray existingArray)
        {
            var mergedArray = new JArray();
            foreach (JToken item in existingArray)
            {
                if (member.ObjectMetadata is not null && item is JObject existingObject)
                {
                    var defaultObject = (JObject)JToken.FromObject(Activator.CreateInstance(member.ElementType)!, Serializer);
                    mergedArray.Add(MergeObject(member.ObjectMetadata, defaultObject, existingObject));
                }
                else
                {
                    mergedArray.Add(item.DeepClone());
                }
            }

            return mergedArray;
        }

        if (member.ObjectMetadata is not null && generatedValue is JObject generatedObject && existingValue is JObject existingObjectValue)
            return MergeObject(member.ObjectMetadata, generatedObject, existingObjectValue);

        return existingValue.DeepClone();
    }

    public static string NormalizeEnumForWrite(MemberMetadata member, JToken? value)
        => NormalizeEnumValue(member, value) ?? member.EnumNames.First();

    private static string? NormalizeEnumValue(MemberMetadata member, JToken? value)
    {
        if (value is null)
            return null;

        if (value.Type == JTokenType.String)
        {
            string? text = value.Value<string>();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return member.EnumNames.FirstOrDefault(name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
        }

        if (value.Type == JTokenType.Integer)
        {
            int rawValue = value.Value<int>();
            if (Enum.IsDefined(member.NonNullableType, rawValue))
                return Enum.GetName(member.NonNullableType, rawValue);
        }

        return null;
    }
}

static class JsoncWriter
{
    public static string Write(TypeMetadata rootMetadata, JObject document, JObject schemaDocument, string schemaRelativePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // Generated from XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Toggles.cs.");
        sb.AppendLine("  // Re-run Tools/Generate-UnitTestingWorldSettings.ps1 after changing the settings type.");
        sb.AppendLine($"  \"$schema\": {JsonConvert.ToString(schemaRelativePath)},");
        sb.AppendLine($"  \"$id\": {JsonConvert.ToString(document["$id"]?.Value<string>() ?? "1")},");

        var propertyLines = BuildObjectBody(rootMetadata, document, schemaDocument["properties"] as JObject, indentLevel: 1, includeRootPreamble: true);
        AppendBodyLines(sb, propertyLines);

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static List<string> BuildObjectBody(TypeMetadata metadata, JObject document, JObject? schemaProperties, int indentLevel, bool includeRootPreamble = false)
    {
        var lines = new List<string>();
        string indent = new(' ', indentLevel * 2);
        string? previousSection = includeRootPreamble ? string.Empty : null;

        foreach (MemberMetadata member in metadata.Members)
        {
            if (!document.TryGetValue(member.Name, StringComparison.Ordinal, out JToken? value) || value is null)
                continue;

            if (!string.Equals(previousSection, member.Section, StringComparison.Ordinal))
            {
                if (lines.Count > 0)
                    lines.Add(string.Empty);

                if (!string.IsNullOrWhiteSpace(member.Section))
                    lines.Add($"{indent}// {member.Section}.");

                previousSection = member.Section;
            }

            foreach (string comment in BuildCommentLines(member, schemaProperties?[member.Name]))
                lines.Add($"{indent}// {comment}");

            lines.Add($"{indent}\"{member.Name}\": {WriteValue(member, value, schemaProperties?[member.Name], indentLevel)}");
        }

        return lines;
    }

    private static IEnumerable<string> BuildCommentLines(MemberMetadata member, JToken? schemaProperty)
    {
        string? description = schemaProperty?["description"]?.Value<string>() ?? member.SourceDescription;
        if (!string.IsNullOrWhiteSpace(description))
            yield return description!;

        if (member.IsEnum && !member.IsFlagsEnum)
            yield return $"{member.Name}: {string.Join(", ", member.EnumNames)}";
    }

    private static string WriteValue(MemberMetadata member, JToken value, JToken? schemaProperty, int indentLevel)
    {
        if (member.IsCollection && value is JArray array)
            return WriteArray(member, array, schemaProperty, indentLevel);

        if (member.ObjectMetadata is not null && value is JObject obj)
            return WriteObject(member.ObjectMetadata, obj, schemaProperty, indentLevel);

        if (member.IsEnum && !member.IsFlagsEnum)
        {
            string enumValue = SettingsDocumentGenerator.NormalizeEnumForWrite(member, value);
            return JsonConvert.ToString(enumValue);
        }

        return value.ToString(Formatting.None);
    }

    private static string WriteArray(MemberMetadata member, JArray array, JToken? schemaProperty, int indentLevel)
    {
        if (array.Count == 0)
            return "[]";

        string indent = new(' ', indentLevel * 2);
        string childIndent = new(' ', (indentLevel + 1) * 2);
        JObject? itemSchema = schemaProperty?["items"] as JObject;
        var sb = new StringBuilder();
        sb.AppendLine("[");

        for (int index = 0; index < array.Count; index++)
        {
            JToken item = array[index];
            if (member.ObjectMetadata is not null && item is JObject obj)
                sb.Append(childIndent).Append(WriteObject(member.ObjectMetadata, obj, itemSchema, indentLevel + 1));
            else
                sb.Append(childIndent).Append(item.ToString(Formatting.None));

            if (index < array.Count - 1)
                sb.Append(',');
            sb.AppendLine();
        }

        sb.Append(indent).Append(']');
        return sb.ToString();
    }

    private static string WriteObject(TypeMetadata metadata, JObject obj, JToken? schemaProperty, int indentLevel)
    {
        string indent = new(' ', indentLevel * 2);
        JObject? schemaProperties = ResolveObjectSchemaProperties(metadata, schemaProperty);
        var lines = BuildObjectBody(metadata, obj, schemaProperties, indentLevel + 1);
        if (lines.Count == 0)
            return "{}";

        var sb = new StringBuilder();
        sb.AppendLine("{");
        AppendBodyLines(sb, lines);

        sb.Append(indent).Append('}');
        return sb.ToString();
    }

    private static void AppendBodyLines(StringBuilder sb, List<string> lines)
    {
        int lastValueIndex = FindLastValueIndex(lines);
        for (int index = 0; index < lines.Count; index++)
        {
            if (string.IsNullOrEmpty(lines[index]))
            {
                sb.AppendLine();
                continue;
            }

            sb.Append(lines[index]);
            if (!lines[index].TrimStart().StartsWith("//", StringComparison.Ordinal) && index < lastValueIndex)
                sb.Append(',');
            sb.AppendLine();
        }
    }

    private static int FindLastValueIndex(List<string> lines)
    {
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            string trimmed = lines[index].TrimStart();
            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("//", StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private static JObject? ResolveObjectSchemaProperties(TypeMetadata metadata, JToken? schemaProperty)
    {
        if (schemaProperty is not JObject schemaObject)
            return null;

        if (schemaObject["properties"] is JObject inlineProperties)
            return inlineProperties;

        if (schemaObject["$ref"]?.Value<string>() is string reference && reference.StartsWith("#/definitions/", StringComparison.Ordinal))
        {
            string definitionName = reference["#/definitions/".Length..];
            return schemaObject.Root?["definitions"]?[definitionName]?["properties"] as JObject;
        }

        if (schemaObject["oneOf"] is JArray oneOf)
        {
            foreach (JToken candidate in oneOf)
            {
                JObject? properties = ResolveObjectSchemaProperties(metadata, candidate);
                if (properties is not null)
                    return properties;
            }
        }

        return null;
    }
}