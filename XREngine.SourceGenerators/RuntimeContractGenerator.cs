using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace XREngine.SourceGenerators;

/// <summary>Generates explicit runtime type identities and member bindings.</summary>
[Generator]
public sealed class RuntimeContractGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor UnsupportedMember = new(
        "XREG001", "Unsupported animation member", "'{0}.{1}' must be a public writable instance field or property with a supported animation value type", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor DuplicateBinding = new(
        "XREG002", "Duplicate animation binding", "'{0}.{1}' has more than one animation binding declaration", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor MissingType = new(
        "XREG003", "Missing animation target", "The animation binding target must be a closed, accessible class", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor DuplicateTypeId = new(
        "XREG004", "Duplicate runtime type ID", "Runtime type ID '{0}' is declared more than once", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor InvalidTypeContract = new(
        "XREG005", "Invalid runtime type contract", "Runtime type contract '{0}' must identify a closed, accessible type and use a positive schema version", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor EditorOnlyType = new(
        "XREG006", "Editor type in player contract", "Runtime contract '{0}' references editor-only type '{1}'", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor InvalidCodec = new(
        "XREG007", "Invalid cooked codec declaration", "Cooked codec for '{0}' must target a closed asset in the current assembly with a supported codec", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor DuplicateCodec = new(
        "XREG008", "Duplicate cooked codec", "Cooked codec for '{0}' is declared more than once", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor SchemaSnapshotMismatch = new(
        "XREG009", "Runtime schema snapshot mismatch", "Runtime contract '{0}' schema version {1} has fingerprint {2}; update its version and reviewed schema snapshot together", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsupportedFormatter = new(
        "XREG010", "Unsupported closed formatter", "'{0}' must be a closed List, Dictionary, HashSet, Nullable, ValueTuple, vector array, or value type", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor DuplicateFormatter = new(
        "XREG011", "Duplicate closed formatter", "Closed formatter '{0}' is declared more than once", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsupportedAccessor = new(
        "XREG012", "Unsupported runtime accessor", "'{0}.{1}' must be one accessible instance field/property/event or one public void method without ref, out, or generic parameters", "RuntimeContracts", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor DuplicateAccessor = new(
        "XREG013", "Duplicate runtime accessor", "Runtime accessor '{0}.{1}' is declared more than once", "RuntimeContracts", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var mode = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.XREngineRuntimeContractMode", out var value) ? value : "");
        var schemas = context.AdditionalTextsProvider
            .Where(static file => System.IO.Path.GetFileName(file.Path) == "RuntimeContractSchemas.txt")
            .Select(static (file, token) => file.GetText(token)?.ToString() ?? string.Empty)
            .Collect();
        context.RegisterSourceOutput(context.CompilationProvider.Combine(mode).Combine(schemas),
            static (output, pair) => Generate(output, pair.Left.Left, pair.Left.Right, pair.Right.FirstOrDefault() ?? string.Empty));
    }

    private static void Generate(SourceProductionContext context, Compilation compilation, string mode, string schemaSnapshot)
    {
        if (mode != "Portable" && mode != "Desktop" && mode != "CommandsOnly" && mode != "Contracts")
            return;

        var bindings = new List<Binding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var contracts = new List<TypeContract>();
        var snapshots = ParseSnapshots(schemaSnapshot);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol referencedAssembly)
                continue;
            foreach (var referencedAttribute in referencedAssembly.GetAttributes())
                if (referencedAttribute.AttributeClass?.ToDisplayString() == "XREngine.RuntimeTypeContractAttribute" &&
                    referencedAttribute.ConstructorArguments.Length > 1 &&
                    referencedAttribute.ConstructorArguments[1].Value is string referencedId)
                    if (!ids.Add(referencedId))
                        context.ReportDiagnostic(Diagnostic.Create(DuplicateTypeId, Location.None, referencedId));
        }
        var codecs = new List<CookedCodec>();
        var codecTypes = new HashSet<string>(StringComparer.Ordinal);
        var formatters = new List<ClosedFormatter>();
        var formatterTypes = new HashSet<string>(StringComparer.Ordinal);
        var accessors = new List<MemberAccessor>();
        var accessorNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == "XREngine.RuntimeMemberAccessAttribute")
            {
                var accessorLocation = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not INamedTypeSymbol owner ||
                    attribute.ConstructorArguments[1].Value is not string accessorName ||
                    owner.TypeKind != TypeKind.Class || owner.IsGenericType || owner.IsUnboundGenericType ||
                    !compilation.IsSymbolAccessibleWithin(owner, compilation.Assembly))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedAccessor, accessorLocation, "unknown", "unknown"));
                    continue;
                }
                string ownerName = owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!accessorNames.Add(ownerName + "." + accessorName))
                {
                    context.ReportDiagnostic(Diagnostic.Create(DuplicateAccessor, accessorLocation, ownerName, accessorName));
                    continue;
                }
                var candidates = owner.GetMembers(accessorName).Where(symbol => symbol is IPropertySymbol or IFieldSymbol or IMethodSymbol or IEventSymbol).ToArray();
                if (candidates.Length != 1 || !TryMemberAccessor(candidates[0], compilation, out var accessor))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedAccessor, accessorLocation, ownerName, accessorName));
                    continue;
                }
                accessors.Add(new MemberAccessor(ownerName, accessorName, accessor));
                continue;
            }
            if (attribute.AttributeClass?.ToDisplayString() == "XREngine.RuntimeClosedFormatterAttribute")
            {
                var formatterLocation = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not ITypeSymbol closedType ||
                    !TryFormatterKind(closedType, out string formatterKind))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedFormatter, formatterLocation,
                        attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value?.ToString() ?? "unknown" : "unknown"));
                    continue;
                }
                string name = closedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!formatterTypes.Add(name))
                {
                    context.ReportDiagnostic(Diagnostic.Create(DuplicateFormatter, formatterLocation, name));
                    continue;
                }
                string[] arguments = closedType is IArrayTypeSymbol arrayType
                    ? new[] { arrayType.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) }
                    : ((INamedTypeSymbol)closedType).TypeArguments.Select(static argument => argument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToArray();
                formatters.Add(new ClosedFormatter(name, formatterKind, arguments));
                continue;
            }
            if (attribute.AttributeClass?.ToDisplayString() == "XREngine.RuntimeCookedAssetAttribute")
            {
                var codecLocation = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not INamedTypeSymbol assetType ||
                    assetType.IsGenericType || assetType.IsAbstract ||
                    !SymbolEqualityComparer.Default.Equals(assetType.ContainingAssembly, compilation.Assembly) ||
                    attribute.ConstructorArguments[1].Value is not int codecKind || codecKind is < 0 or > 7)
                {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidCodec, codecLocation, attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value?.ToString() ?? "unknown" : "unknown"));
                    continue;
                }
                var codecTypeName = assetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if ((codecKind is >= 2 and <= 6 && !IsSupportedAnimationCodec(codecKind, assetType)) ||
                    (codecKind == 7 && assetType.ToDisplayString() != "XREngine.Rendering.XRMesh"))
                {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidCodec, codecLocation, codecTypeName));
                    continue;
                }
                if (!codecTypes.Add(codecTypeName))
                {
                    context.ReportDiagnostic(Diagnostic.Create(DuplicateCodec, codecLocation, codecTypeName));
                    continue;
                }
                if (codecKind == 1 && !assetType.InstanceConstructors.Any(static ctor => ctor.Parameters.Length == 0 && ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal))
                {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidCodec, codecLocation, codecTypeName));
                    continue;
                }
                codecs.Add(new CookedCodec(codecTypeName, codecKind));
                continue;
            }
            if (attribute.AttributeClass?.ToDisplayString() == "XREngine.RuntimeTypeContractAttribute")
            {
                var contractLocation = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                string id = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is string text ? text : "";
                if (attribute.ConstructorArguments.Length != 3 ||
                    attribute.ConstructorArguments[0].Value is not INamedTypeSymbol contractType ||
                    contractType.IsUnboundGenericType || contractType.IsGenericType ||
                    !compilation.IsSymbolAccessibleWithin(contractType, compilation.Assembly) ||
                    string.IsNullOrWhiteSpace(id) || attribute.ConstructorArguments[2].Value is not int version || version <= 0)
                {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidTypeContract, contractLocation, id));
                    continue;
                }
                if (!ids.Add(id))
                {
                    context.ReportDiagnostic(Diagnostic.Create(DuplicateTypeId, contractLocation, id));
                    continue;
                }
                if (contractType.ContainingAssembly.Identity.Name.Contains("Editor") && mode != "Desktop")
                {
                    context.ReportDiagnostic(Diagnostic.Create(EditorOnlyType, contractLocation, id, contractType.ToDisplayString()));
                    continue;
                }
                string fingerprint = Fingerprint(contractType);
                if (!snapshots.TryGetValue(id, out var snapshot) || snapshot.Version != version || snapshot.Fingerprint != fingerprint)
                    context.ReportDiagnostic(Diagnostic.Create(SchemaSnapshotMismatch, contractLocation, id, version, fingerprint));
                contracts.Add(new TypeContract(contractType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), id, version));
                continue;
            }
            if (attribute.AttributeClass?.ToDisplayString() != "XREngine.RuntimeAnimationBindingAttribute")
                continue;
            var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
            if (attribute.ConstructorArguments.Length != 2 ||
                attribute.ConstructorArguments[0].Value is not INamedTypeSymbol target ||
                attribute.ConstructorArguments[1].Value is not string memberName ||
                target.TypeKind != TypeKind.Class || target.IsUnboundGenericType || target.IsGenericType ||
                !compilation.IsSymbolAccessibleWithin(target, compilation.Assembly))
            {
                context.ReportDiagnostic(Diagnostic.Create(MissingType, location));
                continue;
            }

            var targetName = target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!seen.Add(targetName + "." + memberName))
            {
                context.ReportDiagnostic(Diagnostic.Create(DuplicateBinding, location, targetName, memberName));
                continue;
            }

            var member = target.GetMembers(memberName).FirstOrDefault(symbol => symbol is IPropertySymbol or IFieldSymbol);
            ITypeSymbol? valueType = null;
            bool writable = false;
            if (member is IPropertySymbol property)
            {
                valueType = property.Type;
                writable = !property.IsStatic && property.Parameters.Length == 0 && property.SetMethod is not null &&
                    compilation.IsSymbolAccessibleWithin(property.SetMethod, compilation.Assembly);
            }
            else if (member is IFieldSymbol field)
            {
                valueType = field.Type;
                writable = !field.IsStatic && !field.IsReadOnly && !field.IsConst &&
                    compilation.IsSymbolAccessibleWithin(field, compilation.Assembly);
            }
            if (!writable || valueType is null || !Supported(valueType))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedMember, location, targetName, memberName));
                continue;
            }
            bindings.Add(new Binding(targetName, memberName, valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        bindings.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Target + "." + a.Member, b.Target + "." + b.Member));
        contracts.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        codecs.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Type, b.Type));
        formatters.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Type, b.Type));
        accessors.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Target + "." + a.Member, b.Target + "." + b.Member));
        string className = "GeneratedRuntimeContracts_" + compilation.Assembly.Identity.Name.Replace('.', '_');
        var source = new StringBuilder("// <auto-generated />\n#nullable enable\n#pragma warning disable CS8603\nnamespace XREngine.Generated;\ninternal static class ")
            .Append(className).Append("\n{\n    internal static global::System.IDisposable Install()\n        => global::XREngine.Data.RegistrationLeaseGroup.Create(static leases =>\n        {\n");
        foreach (var contract in contracts)
            source.Append("            leases.Add(global::XREngine.RuntimeTypeContractRegistry.Register(typeof(")
                .Append(contract.Type).Append("), ")
                .Append(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(contract.Id, true)).Append(", ")
                .Append(contract.Version).Append("));\n");
        foreach (var codec in codecs)
        {
            if (codec.Kind == 1)
                source.Append("            leases.Add(global::XREngine.Core.Files.RuntimeCookedBinarySerializer.RegisterRuntimeFactory(typeof(")
                    .Append(codec.Type).Append("), static () => new ").Append(codec.Type).Append("()));\n");
            else if (codec.Kind == 7)
                source.Append("            leases.Add(global::XREngine.Core.Files.RuntimeCookedBinarySerializer.RegisterRuntimeFactory(typeof(")
                    .Append(codec.Type).Append("), static () => ").Append(codec.Type).Append(".CreateDeferredForDeserialization()));\n");
            source.Append("            leases.Add(global::XREngine.Core.Files.PublishedCookedAssetRegistry.Register(typeof(")
                .Append(codec.Type).Append("), ");
            if (codec.Kind == 0)
                source.Append("static (asset, writer) => global::MemoryPack.MemoryPackSerializer.Serialize(writer, (")
                    .Append(codec.Type).Append(")asset), static (payload, _) => global::MemoryPack.MemoryPackSerializer.Deserialize<")
                    .Append(codec.Type).Append(">(payload)");
            else if (codec.Kind == 1)
                source.Append("static (asset, writer) => ").Append(codec.Type)
                    .Append(".WriteTextureStreamingPayload((").Append(codec.Type)
                    .Append(")asset, writer), static (payload, _) => ").Append(codec.Type)
                    .Append(".TryDeserializeTextureStreamingPayload(payload, out var texture) ? texture : null");
            else if (codec.Kind == 7)
                source.Append("static (asset, writer) => global::XREngine.Core.Files.RuntimeCookedBinarySerializer.Serialize((")
                    .Append(codec.Type).Append(")asset, writer), static (payload, assetType) => global::XREngine.Core.Files.RuntimeCookedBinarySerializer.Deserialize(assetType, payload)");
            else
                AppendAnimationCodec(source, codec);
            source.Append(", ").Append(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(compilation.Assembly.Identity.Name, true))
                .Append("));\n");
        }
        foreach (var formatter in formatters)
        {
            source.Append("            leases.Add(global::XREngine.Core.Files.CookedBinaryFormatterRegistry.RegisterKnownType(typeof(")
                .Append(formatter.Type).Append(")));\n");
            string formatterCall = formatter.Kind switch
            {
                "List" => "RegisterList<" + formatter.Arguments[0] + ">()",
                "Dictionary" => "RegisterDictionary<" + formatter.Arguments[0] + ", " + formatter.Arguments[1] + ">()",
                "HashSet" => "RegisterHashSet<" + formatter.Arguments[0] + ">()",
                "Nullable" => "RegisterNullable<" + formatter.Arguments[0] + ">()",
                "Array" => "RegisterArray<" + formatter.Arguments[0] + ">()",
                "ValueDefault" => "RegisterValueDefault<" + formatter.Type + ">()",
                _ => "",
            };
            if (formatter.Kind == "ValueTuple")
            {
                source.Append("            leases.Add(global::XREngine.Core.Files.CookedBinaryFormatterRegistry.RegisterTuple(new global::System.Type[] { ");
                source.Append(string.Join(", ", formatter.Arguments.Select(static argument => "typeof(" + argument + ")")));
                source.Append(" }, static values => new global::System.ValueTuple<")
                    .Append(string.Join(", ", formatter.Arguments)).Append(">(");
                source.Append(string.Join(", ", formatter.Arguments.Select(static (argument, index) => "(" + argument + ")values[" + index + "]!")));
                source.Append(")));\n");
                source.Append("            leases.Add(global::XREngine.Core.Files.CookedBinaryFormatterRegistry.RegisterValueDefault<")
                    .Append(formatter.Type).Append(">());\n");
            }
            else
                source.Append("            leases.Add(global::XREngine.Core.Files.CookedBinaryFormatterRegistry.")
                    .Append(formatterCall).Append(");\n");
        }
        foreach (var binding in bindings)
            source.Append("            leases.Add(global::XREngine.Animation.AnimationMemberBindingRegistry.Register<")
                .Append(binding.Target).Append(", ").Append(binding.ValueType).Append(">(")
                .Append(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(binding.Member, true)).Append(", static (target, value) => target.")
                .Append(binding.Member).Append(" = value));\n");
        foreach (var accessor in accessors)
            source.Append("            leases.Add(").Append(accessor.Registration).Append(");\n");
        source.Append("        });\n}\n");
        context.AddSource("GeneratedRuntimeContracts.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static bool Supported(ITypeSymbol type)
    {
        if (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Single)
            return true;
        var name = type.ToDisplayString();
        return name is "System.Numerics.Vector2" or "System.Numerics.Vector3" or "System.Numerics.Vector4" or "System.Numerics.Quaternion";
    }

    private static bool TryMemberAccessor(ISymbol member, Compilation compilation, out string registration)
    {
        registration = string.Empty;
        if (member.IsStatic || !compilation.IsSymbolAccessibleWithin(member, compilation.Assembly) || member.ContainingType is not INamedTypeSymbol target)
            return false;
        string targetName = target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string name = member.Name;
        string literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(name, true);
        string expression = "global::XREngine.RuntimeMemberAccessorRegistry.";
        if (member is IPropertySymbol property && property.Parameters.Length == 0 && property.GetMethod is not null &&
            compilation.IsSymbolAccessibleWithin(property.GetMethod, compilation.Assembly))
        {
            string value = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string setter = property.SetMethod is not null && compilation.IsSymbolAccessibleWithin(property.SetMethod, compilation.Assembly)
                ? "static (target, value) => target." + name + " = value" : "null";
            registration = expression + "RegisterProperty<" + targetName + ", " + value + ">(" + literal + ", static target => target." + name + ", " + setter + ")";
            return true;
        }
        if (member is IFieldSymbol field && !field.IsConst)
        {
            string value = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string setter = field.IsReadOnly ? "null" : "static (target, value) => target." + name + " = value";
            registration = expression + "RegisterField<" + targetName + ", " + value + ">(" + literal + ", static target => target." + name + ", " + setter + ")";
            return true;
        }
        if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary && !method.IsGenericMethod &&
            method.ReturnsVoid && method.Parameters.All(static p => p.RefKind == RefKind.None))
        {
            string[] arguments = method.Parameters.Select(static p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToArray();
            string typeArray = "new global::System.Type[] { " + string.Join(", ", arguments.Select(static p => "typeof(" + p + ")")) + " }";
            string callArguments = string.Join(", ", arguments.Select(static (p, i) => "(" + p + ")args[" + i + "]!"));
            registration = expression + "RegisterMethod<" + targetName + ">(" + literal + ", " + typeArray + ", static (target, args) => target." + name + "(" + callArguments + "))";
            return true;
        }
        if (member is IEventSymbol ev && ev.AddMethod is not null && ev.RemoveMethod is not null &&
            compilation.IsSymbolAccessibleWithin(ev.AddMethod, compilation.Assembly) &&
            compilation.IsSymbolAccessibleWithin(ev.RemoveMethod, compilation.Assembly))
        {
            string handler = ev.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            registration = expression + "RegisterEvent<" + targetName + ", " + handler + ">(" + literal +
                ", static (target, value) => target." + name + " += value, static (target, value) => target." + name + " -= value)";
            return true;
        }
        return false;
    }

    private static bool IsSupportedAnimationCodec(int kind, INamedTypeSymbol assetType)
    {
        string expected = kind switch
        {
            2 => "XREngine.Animation.AnimationClip",
            3 => "XREngine.Animation.BlendTree1D",
            4 => "XREngine.Animation.BlendTree2D",
            5 => "XREngine.Animation.BlendTreeDirect",
            6 => "XREngine.Animation.AnimStateMachine",
            _ => string.Empty,
        };
        return assetType.ToDisplayString() == expected;
    }

    private static void AppendAnimationCodec(StringBuilder source, CookedCodec codec)
    {
        string model = codec.Kind switch
        {
            2 => "AnimationClipSerializedModel",
            3 => "BlendTree1DSerializedModel",
            4 => "BlendTree2DSerializedModel",
            5 => "BlendTreeDirectSerializedModel",
            _ => "AnimStateMachineSerializedModel",
        };
        string serializer = codec.Kind switch
        {
            2 => "AnimationClipSerialization",
            6 => "AnimStateMachineSerialization",
            _ => "BlendTreeSerialization",
        };
        string root = "global::XREngine.";
        source.Append("static (asset, writer) => global::MemoryPack.MemoryPackSerializer.Serialize(writer, (")
            .Append(root).Append(model).Append(")").Append(root).Append(serializer)
            .Append(".CreatePublishedModel((").Append(codec.Type).Append(")asset)), ");
        source.Append("static (payload, _) => { var model = global::MemoryPack.MemoryPackSerializer.Deserialize<")
            .Append(root).Append(model).Append(">(payload); ");
        if (codec.Kind is 3 or 4 or 5)
            source.Append("return model is null ? null : ").Append(root)
                .Append("BlendTreeSerialization.CreatePublishedRuntimeBlendTree(typeof(").Append(codec.Type)
                .Append("), model); }");
        else
            source.Append("var result = new ").Append(codec.Type).Append("(); ")
                .Append(root).Append(serializer).Append(".ApplyPublishedModel(result, model); return result; }");
    }

    private static bool TryFormatterKind(ITypeSymbol type, out string kind)
    {
        kind = string.Empty;
        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1 || !IsClosedFormatterElement(array.ElementType))
                return false;
            kind = "Array";
            return true;
        }
        if (type is not INamedTypeSymbol namedType)
            return false;
        if (!IsClosedFormatterElement(namedType))
            return false;
        if (namedType.IsGenericType &&
            namedType.ContainingNamespace.ToDisplayString() is ("System" or "System.Collections.Generic"))
        {
            kind = namedType.Name;
            bool supported = kind switch
            {
                "List" or "HashSet" or "Nullable" => namedType.TypeArguments.Length == 1,
                "Dictionary" => namedType.TypeArguments.Length == 2,
                "ValueTuple" => namedType.TypeArguments.Length is >= 1 and <= 8,
                _ => false,
            };
            if (supported)
                return true;
        }
        if (namedType.TypeKind == TypeKind.Struct)
        {
            kind = "ValueDefault";
            return true;
        }
        return false;
    }

    private static bool IsClosedFormatterElement(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => array.Rank == 1 && IsClosedFormatterElement(array.ElementType),
            INamedTypeSymbol named => !named.IsUnboundGenericType && named.TypeArguments.All(static argument => IsClosedFormatterElement(argument)),
            _ => type.TypeKind != TypeKind.TypeParameter && type.TypeKind != TypeKind.Error,
        };

    private static Dictionary<string, (int Version, string Fingerprint)> ParseSnapshots(string content)
    {
        var result = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("#", StringComparison.Ordinal))
                continue;
            var fields = line.Split('|');
            if (fields.Length == 3 && int.TryParse(fields[1], out int version))
                result[fields[0]] = (version, fields[2]);
        }
        return result;
    }

    private static string Fingerprint(INamedTypeSymbol type)
    {
        var members = new List<string>();
        for (var current = type; current != null; current = current.BaseType)
            foreach (var member in current.GetMembers())
            {
                if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic)
                    continue;
                if (member is IPropertySymbol property && property.Parameters.Length == 0)
                    members.Add("P|" + current.ToDisplayString() + "|" + property.Name + "|" + property.Type.ToDisplayString() + "|" + (property.SetMethod is null ? "R" : "W"));
                else if (member is IFieldSymbol field)
                    members.Add("F|" + current.ToDisplayString() + "|" + field.Name + "|" + field.Type.ToDisplayString() + "|" + (field.IsReadOnly ? "R" : "W"));
            }
        members.Sort(StringComparer.Ordinal);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", members)))).Replace("-", string.Empty).ToLowerInvariant();
    }

    private readonly struct Binding(string target, string member, string valueType)
    {
        internal string Target { get; } = target;
        internal string Member { get; } = member;
        internal string ValueType { get; } = valueType;
    }

    private readonly struct TypeContract(string type, string id, int version)
    {
        internal string Type { get; } = type;
        internal string Id { get; } = id;
        internal int Version { get; } = version;
    }

    private readonly struct CookedCodec(string type, int kind)
    {
        internal string Type { get; } = type;
        internal int Kind { get; } = kind;
    }

    private readonly struct ClosedFormatter(string type, string kind, string[] arguments)
    {
        internal string Type { get; } = type;
        internal string Kind { get; } = kind;
        internal string[] Arguments { get; } = arguments;
    }

    private readonly struct MemberAccessor(string target, string member, string registration)
    {
        internal string Target { get; } = target;
        internal string Member { get; } = member;
        internal string Registration { get; } = registration;
    }
}
