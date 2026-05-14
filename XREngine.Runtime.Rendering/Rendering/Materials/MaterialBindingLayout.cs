using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Materials
{
    public enum EMaterialBindingScope
    {
        Unspecified,
        Frame,
        Camera,
        Pass,
        Draw,
        Instance,
        Material,
        Texture,
        Static,
    }

    public enum EMaterialBindingStorage
    {
        Unspecified,
        Uniform,
        UniformBuffer,
        Field,
        Bindless,
        DescriptorIndex,
        Static,
    }

    public enum EMaterialBindingResolverOutcome
    {
        MaterialTableCompatible,
        PerMaterialRequired,
        Invalid,
    }

    public sealed record MaterialBindingOutput(
        string Name,
        int Location,
        string GlslType);

    public sealed record MaterialBindingField(
        string Name,
        string GlslType,
        string Semantic,
        string DefaultLiteral,
        EMaterialBindingScope Scope = EMaterialBindingScope.Material,
        EMaterialBindingStorage Storage = EMaterialBindingStorage.Field);

    public sealed record MaterialTextureBinding(
        string Name,
        string Semantic,
        string Dimensionality,
        string DefaultLiteral = "0u",
        EMaterialBindingStorage Storage = EMaterialBindingStorage.Bindless);

    public sealed record MaterialBindingPackedMember(
        string Name,
        string GlslType,
        uint WordOffset,
        uint WordCount,
        string DefaultLiteral);

    public sealed class MaterialBindingLayout
    {
        private readonly Dictionary<string, MaterialBindingField> _fieldsBySemantic;
        private readonly Dictionary<string, MaterialTextureBinding> _texturesBySemantic;
        private readonly Dictionary<string, MaterialBindingPackedMember> _packedMembersByName;

        public MaterialBindingLayout(
            string name,
            int renderPass,
            IReadOnlyList<MaterialBindingOutput> outputs,
            IReadOnlyList<MaterialBindingField> fields,
            IReadOnlyList<MaterialTextureBinding> textures,
            bool usesFlags = true,
            bool supportsGeneratedMaterialTableDispatch = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(outputs);
            ArgumentNullException.ThrowIfNull(fields);
            ArgumentNullException.ThrowIfNull(textures);

            MaterialBindingOutput[] outputArray = outputs.ToArray();
            MaterialBindingField[] fieldArray = fields.ToArray();
            MaterialTextureBinding[] textureArray = textures.ToArray();
            ValidateDefinition(name, outputArray, fieldArray, textureArray);

            Name = name;
            RenderPass = renderPass;
            Outputs = outputArray;
            Fields = fieldArray;
            Textures = textureArray;
            UsesFlags = usesFlags;
            SupportsGeneratedMaterialTableDispatch = supportsGeneratedMaterialTableDispatch;
            PackedMembers = BuildPackedMembers(textureArray, fieldArray, usesFlags);
            ValidatePackedMembers(PackedMembers);
            RowWordCount = PackedMembers.Count == 0
                ? 0u
                : PackedMembers[^1].WordOffset + PackedMembers[^1].WordCount;
            LayoutHash = ComputeLayoutHash();

            _fieldsBySemantic = Fields.ToDictionary(static x => x.Semantic, StringComparer.OrdinalIgnoreCase);
            _texturesBySemantic = Textures.ToDictionary(static x => x.Semantic, StringComparer.OrdinalIgnoreCase);
            _packedMembersByName = PackedMembers.ToDictionary(static x => x.Name, StringComparer.Ordinal);
        }

        public string Name { get; }
        public int RenderPass { get; }
        public IReadOnlyList<MaterialBindingOutput> Outputs { get; }
        public IReadOnlyList<MaterialBindingField> Fields { get; }
        public IReadOnlyList<MaterialTextureBinding> Textures { get; }
        public IReadOnlyList<MaterialBindingPackedMember> PackedMembers { get; }
        public bool UsesFlags { get; }
        public bool SupportsGeneratedMaterialTableDispatch { get; }
        public uint RowWordCount { get; }
        public uint RowByteCount => RowWordCount * sizeof(uint);
        public string LayoutHash { get; }

        public bool HasFieldSemantic(string semantic)
            => _fieldsBySemantic.ContainsKey(semantic);

        public bool HasTextureSemantic(string semantic)
            => _texturesBySemantic.ContainsKey(semantic);

        public bool TryGetPackedMember(string name, out MaterialBindingPackedMember member)
            => _packedMembersByName.TryGetValue(name, out member!);

        private static void ValidateDefinition(
            string name,
            IReadOnlyList<MaterialBindingOutput> outputs,
            IReadOnlyList<MaterialBindingField> fields,
            IReadOnlyList<MaterialTextureBinding> textures)
        {
            HashSet<string> outputNames = new(StringComparer.Ordinal);
            HashSet<int> outputLocations = [];
            foreach (MaterialBindingOutput output in outputs)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(output.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(output.GlslType);
                if (!outputNames.Add(output.Name))
                    throw new ArgumentException($"Material binding layout '{name}' declares duplicate output '{output.Name}'.", nameof(outputs));
                if (!outputLocations.Add(output.Location))
                    throw new ArgumentException($"Material binding layout '{name}' declares duplicate output location {output.Location}.", nameof(outputs));
                if (output.Location < 0)
                    throw new ArgumentException($"Material binding layout '{name}' declares a negative output location for '{output.Name}'.", nameof(outputs));
                if (!IsSupportedGlslValueType(output.GlslType))
                    throw new ArgumentException($"Material binding layout '{name}' output '{output.Name}' uses unsupported GLSL type '{output.GlslType}'.", nameof(outputs));
            }

            HashSet<string> fieldNames = new(StringComparer.Ordinal);
            HashSet<string> fieldSemantics = new(StringComparer.OrdinalIgnoreCase);
            foreach (MaterialBindingField field in fields)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(field.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(field.GlslType);
                ArgumentException.ThrowIfNullOrWhiteSpace(field.Semantic);
                ArgumentException.ThrowIfNullOrWhiteSpace(field.DefaultLiteral);

                if (!fieldNames.Add(field.Name))
                    throw new ArgumentException($"Material binding layout '{name}' declares duplicate field '{field.Name}'.", nameof(fields));
                if (!fieldSemantics.Add(field.Semantic))
                    throw new ArgumentException($"Material binding layout '{name}' declares duplicate field semantic '{field.Semantic}'.", nameof(fields));
                if (!IsSupportedGlslValueType(field.GlslType))
                    throw new ArgumentException($"Material binding layout '{name}' field '{field.Name}' uses unsupported GLSL type '{field.GlslType}'.", nameof(fields));
                if (field.Scope != EMaterialBindingScope.Material)
                    throw new ArgumentException($"Material binding layout '{name}' field '{field.Name}' must use material scope.", nameof(fields));
                if (field.Storage != EMaterialBindingStorage.Field)
                    throw new ArgumentException($"Material binding layout '{name}' field '{field.Name}' must use field storage.", nameof(fields));
                if (!IsKnownSemanticTypeCompatible(field.Semantic, field.GlslType))
                    throw new ArgumentException($"Material binding layout '{name}' field '{field.Name}' semantic '{field.Semantic}' is incompatible with GLSL type '{field.GlslType}'.", nameof(fields));
                if (!IsDefaultLiteralCompatible(field.GlslType, field.DefaultLiteral))
                    throw new ArgumentException($"Material binding layout '{name}' field '{field.Name}' has invalid default literal '{field.DefaultLiteral}' for type '{field.GlslType}'.", nameof(fields));
            }

            HashSet<string> textureNames = new(StringComparer.Ordinal);
            HashSet<string> textureSemantics = new(StringComparer.OrdinalIgnoreCase);
            foreach (MaterialTextureBinding texture in textures)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(texture.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(texture.Semantic);
                ArgumentException.ThrowIfNullOrWhiteSpace(texture.Dimensionality);
                ArgumentException.ThrowIfNullOrWhiteSpace(texture.DefaultLiteral);

                if (!textureNames.Add(texture.Name))
                    throw new ArgumentException($"Material binding layout '{name}' declares duplicate texture '{texture.Name}'.", nameof(textures));
                if (!textureSemantics.Add(texture.Semantic))
                    throw new ArgumentException($"Material binding layout '{name}' declares duplicate texture semantic '{texture.Semantic}'.", nameof(textures));
                if (!IsSupportedTextureDimensionality(texture.Dimensionality))
                    throw new ArgumentException($"Material binding layout '{name}' texture '{texture.Name}' uses unsupported dimensionality '{texture.Dimensionality}'.", nameof(textures));
                if (texture.Storage is not EMaterialBindingStorage.Bindless and not EMaterialBindingStorage.DescriptorIndex)
                    throw new ArgumentException($"Material binding layout '{name}' texture '{texture.Name}' must use bindless or descriptor-index storage.", nameof(textures));
                if (!IsKnownTextureSemanticDimensionalityCompatible(texture.Semantic, texture.Dimensionality))
                    throw new ArgumentException($"Material binding layout '{name}' texture '{texture.Name}' semantic '{texture.Semantic}' is incompatible with dimensionality '{texture.Dimensionality}'.", nameof(textures));
                if (!IsDefaultLiteralCompatible("uint", texture.DefaultLiteral))
                    throw new ArgumentException($"Material binding layout '{name}' texture '{texture.Name}' has invalid default index literal '{texture.DefaultLiteral}'.", nameof(textures));
            }
        }

        private static void ValidatePackedMembers(IReadOnlyList<MaterialBindingPackedMember> members)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (MaterialBindingPackedMember member in members)
            {
                if (!names.Add(member.Name))
                    throw new ArgumentException($"Material binding layout produces duplicate packed member '{member.Name}'.");
            }
        }

        private static MaterialBindingPackedMember[] BuildPackedMembers(
            IReadOnlyList<MaterialTextureBinding> textures,
            IReadOnlyList<MaterialBindingField> fields,
            bool usesFlags)
        {
            List<MaterialBindingPackedMember> members = [];
            uint wordOffset = 0u;

            foreach (MaterialTextureBinding texture in textures)
            {
                members.Add(new(texture.Name + "HandleIndex", "uint", wordOffset++, 1u, texture.DefaultLiteral));
            }

            if (usesFlags)
                members.Add(new("Flags", "uint", wordOffset++, 1u, "0u"));

            foreach (MaterialBindingField field in fields)
            {
                uint alignment = GetStd430WordAlignment(field.GlslType);
                wordOffset = Align(wordOffset, alignment);
                uint wordCount = GetStd430WordCount(field.GlslType);
                members.Add(new(field.Name, field.GlslType, wordOffset, wordCount, field.DefaultLiteral));
                wordOffset += wordCount;
            }

            return [.. members];
        }

        private string ComputeLayoutHash()
        {
            StringBuilder builder = new();
            builder.Append(Name).Append('|')
                .Append(RenderPass.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(UsesFlags ? '1' : '0').Append('|')
                .Append(SupportsGeneratedMaterialTableDispatch ? '1' : '0').AppendLine();

            foreach (MaterialBindingOutput output in Outputs)
            {
                builder.Append("out:")
                    .Append(output.Name).Append('|')
                    .Append(output.Location.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(output.GlslType)
                    .AppendLine();
            }

            foreach (MaterialTextureBinding texture in Textures)
            {
                builder.Append("tex:")
                    .Append(texture.Name).Append('|')
                    .Append(texture.Semantic).Append('|')
                    .Append(texture.Dimensionality).Append('|')
                    .Append(texture.Storage).Append('|')
                    .Append(texture.DefaultLiteral)
                    .AppendLine();
            }

            foreach (MaterialBindingField field in Fields)
            {
                builder.Append("field:")
                    .Append(field.Name).Append('|')
                    .Append(field.GlslType).Append('|')
                    .Append(field.Semantic).Append('|')
                    .Append(field.Scope).Append('|')
                    .Append(field.Storage).Append('|')
                    .Append(field.DefaultLiteral)
                    .AppendLine();
            }

            foreach (MaterialBindingPackedMember member in PackedMembers)
            {
                builder.Append("packed:")
                    .Append(member.Name).Append('|')
                    .Append(member.GlslType).Append('|')
                    .Append(member.WordOffset.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(member.WordCount.ToString(CultureInfo.InvariantCulture))
                    .AppendLine();
            }

            byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        }

        private static uint Align(uint value, uint alignment)
        {
            if (alignment <= 1u)
                return value;

            uint remainder = value % alignment;
            return remainder == 0u ? value : value + alignment - remainder;
        }

        private static uint GetStd430WordAlignment(string glslType)
            => NormalizeGlslType(glslType) switch
            {
                "vec2" or "ivec2" or "uvec2" or "bvec2" => 2u,
                "vec3" or "ivec3" or "uvec3" or "bvec3" => 4u,
                "vec4" or "ivec4" or "uvec4" or "bvec4" => 4u,
                "mat2" => 2u,
                "mat3" or "mat4" => 4u,
                _ => 1u,
            };

        private static uint GetStd430WordCount(string glslType)
            => NormalizeGlslType(glslType) switch
            {
                "vec2" or "ivec2" or "uvec2" or "bvec2" => 2u,
                "vec3" or "ivec3" or "uvec3" or "bvec3" => 4u,
                "vec4" or "ivec4" or "uvec4" or "bvec4" => 4u,
                "mat2" => 4u,
                "mat3" => 12u,
                "mat4" => 16u,
                _ => 1u,
            };

        private static string NormalizeGlslType(string glslType)
            => glslType.Trim().ToLowerInvariant();

        private static bool IsSupportedGlslValueType(string glslType)
            => NormalizeGlslType(glslType) is
                "float" or "int" or "uint" or "bool" or
                "vec2" or "vec3" or "vec4" or
                "ivec2" or "ivec3" or "ivec4" or
                "uvec2" or "uvec3" or "uvec4" or
                "bvec2" or "bvec3" or "bvec4" or
                "mat2" or "mat3" or "mat4";

        private static bool IsKnownSemanticTypeCompatible(string semantic, string glslType)
        {
            string type = NormalizeGlslType(glslType);
            return semantic.Trim().ToLowerInvariant() switch
            {
                "basecoloropacity" => type == "vec4",
                "basecolor" => type is "vec3" or "vec4",
                "opacity" => type == "float",
                "roughnessmetallicspecularemission" => type == "vec4",
                "roughness" or "metallic" or "specular" or "emission" or "normalstrength" => type == "float",
                _ => true,
            };
        }

        private static bool IsKnownTextureSemanticDimensionalityCompatible(string semantic, string dimensionality)
        {
            string normalized = NormalizeTextureDimensionality(dimensionality);
            return semantic.Trim().ToLowerInvariant() switch
            {
                "albedo" or "normal" or "metallicroughness" or "roughnessmetallic" => normalized is "2d" or "2darray",
                _ => true,
            };
        }

        private static bool IsSupportedTextureDimensionality(string dimensionality)
            => NormalizeTextureDimensionality(dimensionality) is
                "1d" or "2d" or "3d" or "cube" or
                "1darray" or "2darray" or "cubearray";

        private static string NormalizeTextureDimensionality(string dimensionality)
            => dimensionality.Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        private static bool IsDefaultLiteralCompatible(string glslType, string defaultLiteral)
        {
            if (string.IsNullOrWhiteSpace(defaultLiteral))
                return false;

            string type = NormalizeGlslType(glslType);
            string literal = defaultLiteral.Trim();
            if (type.StartsWith("vec", StringComparison.Ordinal) ||
                type.StartsWith("ivec", StringComparison.Ordinal) ||
                type.StartsWith("uvec", StringComparison.Ordinal) ||
                type.StartsWith("bvec", StringComparison.Ordinal) ||
                type.StartsWith("mat", StringComparison.Ordinal))
            {
                return literal.StartsWith(type + "(", StringComparison.OrdinalIgnoreCase) &&
                    literal.EndsWith(')');
            }

            return type switch
            {
                "float" => float.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                "int" => int.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                "uint" => TryParseUIntLiteral(literal, out _),
                "bool" => literal.Equals("true", StringComparison.OrdinalIgnoreCase) || literal.Equals("false", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        private static bool TryParseUIntLiteral(string literal, out uint value)
        {
            string trimmed = literal.Trim();
            if (trimmed.EndsWith('u') || trimmed.EndsWith('U'))
                trimmed = trimmed[..^1];

            return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    public static class MaterialBindingRowPacker
    {
        public static void WriteDefaultRow(MaterialBindingLayout layout, Span<uint> destination)
        {
            EnsureDestinationSize(layout, destination);
            destination[..(int)layout.RowWordCount].Clear();

            foreach (MaterialBindingPackedMember member in layout.PackedMembers)
                WriteDefaultMember(destination, member);
        }

        public static bool TryWriteOpaqueDeferred(
            MaterialBindingLayout layout,
            GPUMaterialEntry entry,
            Span<uint> destination,
            out string error)
        {
            EnsureDestinationSize(layout, destination);
            destination[..(int)layout.RowWordCount].Clear();

            if (!TryWriteUInt(layout, destination, "AlbedoHandleIndex", entry.AlbedoHandleIndex, out error) ||
                !TryWriteUInt(layout, destination, "NormalHandleIndex", entry.NormalHandleIndex, out error) ||
                !TryWriteUInt(layout, destination, "RMHandleIndex", entry.RMHandleIndex, out error) ||
                !TryWriteUInt(layout, destination, "Flags", entry.Flags, out error) ||
                !TryWriteVector4(layout, destination, "BaseColorOpacity", entry.BaseColorOpacity, out error) ||
                !TryWriteVector4(layout, destination, "RMSE", entry.RMSE, out error))
            {
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static void EnsureDestinationSize(MaterialBindingLayout layout, Span<uint> destination)
        {
            if ((uint)destination.Length < layout.RowWordCount)
                throw new ArgumentException($"Destination has {destination.Length} words but layout '{layout.Name}' requires {layout.RowWordCount}.", nameof(destination));
        }

        private static bool TryWriteUInt(MaterialBindingLayout layout, Span<uint> destination, string memberName, uint value, out string error)
        {
            if (!TryGetMember(layout, memberName, "uint", out MaterialBindingPackedMember member, out error))
                return false;

            destination[(int)member.WordOffset] = value;
            return true;
        }

        private static bool TryWriteVector4(MaterialBindingLayout layout, Span<uint> destination, string memberName, Vector4 value, out string error)
        {
            if (!TryGetMember(layout, memberName, "vec4", out MaterialBindingPackedMember member, out error))
                return false;

            int offset = (int)member.WordOffset;
            destination[offset + 0] = BitConverter.SingleToUInt32Bits(value.X);
            destination[offset + 1] = BitConverter.SingleToUInt32Bits(value.Y);
            destination[offset + 2] = BitConverter.SingleToUInt32Bits(value.Z);
            destination[offset + 3] = BitConverter.SingleToUInt32Bits(value.W);
            return true;
        }

        private static bool TryGetMember(
            MaterialBindingLayout layout,
            string memberName,
            string expectedType,
            out MaterialBindingPackedMember member,
            out string error)
        {
            if (!layout.TryGetPackedMember(memberName, out member))
            {
                error = $"Layout '{layout.Name}' does not contain packed member '{memberName}'.";
                return false;
            }

            if (!string.Equals(member.GlslType, expectedType, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Layout '{layout.Name}' member '{memberName}' is '{member.GlslType}', expected '{expectedType}'.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static void WriteDefaultMember(Span<uint> destination, MaterialBindingPackedMember member)
        {
            string type = member.GlslType.Trim().ToLowerInvariant();
            int offset = (int)member.WordOffset;
            switch (type)
            {
                case "uint":
                    destination[offset] = ParseUIntLiteral(member.DefaultLiteral);
                    break;
                case "int":
                    destination[offset] = unchecked((uint)ParseIntLiteral(member.DefaultLiteral));
                    break;
                case "float":
                    destination[offset] = BitConverter.SingleToUInt32Bits(ParseFloatLiteral(member.DefaultLiteral));
                    break;
                case "bool":
                    destination[offset] = member.DefaultLiteral.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;
                    break;
                case "vec4":
                    WriteFloatVector(destination[offset..], ParseFloatVector(member.DefaultLiteral, 4), 4);
                    break;
                case "vec3":
                    WriteFloatVector(destination[offset..], ParseFloatVector(member.DefaultLiteral, 3), 3);
                    break;
                case "vec2":
                    WriteFloatVector(destination[offset..], ParseFloatVector(member.DefaultLiteral, 2), 2);
                    break;
            }
        }

        private static void WriteFloatVector(Span<uint> destination, float[] values, int count)
        {
            for (int i = 0; i < count; i++)
                destination[i] = BitConverter.SingleToUInt32Bits(values[i]);
        }

        private static float[] ParseFloatVector(string literal, int count)
        {
            string[] parts = ParseConstructorArguments(literal);
            float[] values = new float[count];
            if (parts.Length == 1)
            {
                Array.Fill(values, ParseFloatLiteral(parts[0]));
                return values;
            }

            for (int i = 0; i < count && i < parts.Length; i++)
                values[i] = ParseFloatLiteral(parts[i]);
            return values;
        }

        private static string[] ParseConstructorArguments(string literal)
        {
            int open = literal.IndexOf('(');
            int close = literal.LastIndexOf(')');
            if (open < 0 || close <= open)
                return [];

            return literal[(open + 1)..close]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static float ParseFloatLiteral(string literal)
            => float.Parse(literal.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

        private static int ParseIntLiteral(string literal)
            => int.Parse(literal.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static uint ParseUIntLiteral(string literal)
        {
            string trimmed = literal.Trim();
            if (trimmed.EndsWith('u') || trimmed.EndsWith('U'))
                trimmed = trimmed[..^1];

            return uint.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
    }

    public static class MaterialBindingLayouts
    {
        public const uint MaterialTableSsboBinding = 11u;
        public const uint MaterialTextureHandleTableSsboBinding = 17u;

        public static MaterialBindingLayout OpaqueDeferred { get; } = new(
            "DeferredOpaque",
            (int)EDefaultRenderPass.OpaqueDeferred,
            [
                new("AlbedoOpacity", 0, "vec4"),
                new("Normal", 1, "vec2"),
                new("RMSE", 2, "vec4"),
                new("TransformId", 3, "uint"),
            ],
            [
                new("BaseColorOpacity", "vec4", "baseColorOpacity", "vec4(1.0, 1.0, 1.0, 1.0)"),
                new("RMSE", "vec4", "roughnessMetallicSpecularEmission", "vec4(1.0, 0.0, 1.0, 0.0)"),
            ],
            [
                new("Albedo", "albedo", "2D"),
                new("Normal", "normal", "2D"),
                new("RM", "metallicRoughness", "2D"),
            ],
            supportsGeneratedMaterialTableDispatch: true);

        public static MaterialBindingLayout ForwardOpaque { get; } = new(
            "ForwardOpaque",
            (int)EDefaultRenderPass.OpaqueForward,
            [
                new("Color", 0, "vec4"),
            ],
            [
                new("BaseColorOpacity", "vec4", "baseColorOpacity", "vec4(1.0, 1.0, 1.0, 1.0)"),
                new("RMSE", "vec4", "roughnessMetallicSpecularEmission", "vec4(1.0, 0.0, 1.0, 0.0)"),
            ],
            [
                new("Albedo", "albedo", "2D"),
                new("Normal", "normal", "2D"),
                new("RM", "metallicRoughness", "2D"),
            ]);

        public static bool TryGetDefaultForRenderPass(int renderPass, out MaterialBindingLayout layout)
        {
            if (renderPass == OpaqueDeferred.RenderPass)
            {
                layout = OpaqueDeferred;
                return true;
            }

            if (renderPass == ForwardOpaque.RenderPass)
            {
                layout = ForwardOpaque;
                return true;
            }

            layout = OpaqueDeferred;
            return false;
        }

        public static bool TryGetGeneratedMaterialTableDispatchLayout(int renderPass, out MaterialBindingLayout layout)
            => TryGetDefaultForRenderPass(renderPass, out layout) && layout.SupportsGeneratedMaterialTableDispatch;
    }

    public static class MaterialBindingGlslGenerator
    {
        public static string GenerateMaterialTableDefinitions(
            MaterialBindingLayout layout,
            bool bindless,
            uint materialTableBinding = MaterialBindingLayouts.MaterialTableSsboBinding,
            uint textureHandleTableBinding = MaterialBindingLayouts.MaterialTextureHandleTableSsboBinding)
        {
            StringBuilder sb = new();
            AppendMaterialTableDefinitions(sb, layout, bindless, materialTableBinding, textureHandleTableBinding);
            return sb.ToString();
        }

        public static void AppendMaterialTableDefinitions(
            StringBuilder sb,
            MaterialBindingLayout layout,
            bool bindless,
            uint materialTableBinding = MaterialBindingLayouts.MaterialTableSsboBinding,
            uint textureHandleTableBinding = MaterialBindingLayouts.MaterialTextureHandleTableSsboBinding)
        {
            sb.AppendLine($"// XR generated material layout: {layout.Name} ({layout.LayoutHash})");
            sb.AppendLine($"// Row size: {layout.RowByteCount} bytes / {layout.RowWordCount} uint words.");
            sb.AppendLine("struct XR_MaterialRecord");
            sb.AppendLine("{");
            foreach (MaterialBindingPackedMember member in layout.PackedMembers)
                sb.AppendLine($"    {member.GlslType} {member.Name};");
            sb.AppendLine("};");
            sb.AppendLine($"layout(std430, binding = {materialTableBinding}) readonly buffer XR_MaterialTableBuffer {{ XR_MaterialRecord XR_MaterialTable[]; }};");
            sb.AppendLine("#define MaterialEntry XR_MaterialRecord");
            sb.AppendLine("#define MaterialTable XR_MaterialTable");
            sb.AppendLine("bool XR_TryLoadMaterial(uint materialId, out XR_MaterialRecord material)");
            sb.AppendLine("{");
            sb.AppendLine("    if (materialId >= uint(XR_MaterialTable.length()))");
            sb.AppendLine("        return false;");
            sb.AppendLine("    material = XR_MaterialTable[materialId];");
            sb.AppendLine("    return true;");
            sb.AppendLine("}");
            sb.AppendLine("void XR_LoadMaterial(uint materialId, out XR_MaterialRecord material)");
            sb.AppendLine("{");
            sb.AppendLine("    if (!XR_TryLoadMaterial(materialId, material))");
            sb.AppendLine($"        material = {BuildDefaultRecordConstructor(layout)};");
            sb.AppendLine("}");

            if (!bindless)
                return;

            sb.AppendLine();
            sb.AppendLine("struct TextureHandleEntry");
            sb.AppendLine("{");
            sb.AppendLine("    uvec2 Handle;");
            sb.AppendLine("    uint Flags;");
            sb.AppendLine("    uint Padding0;");
            sb.AppendLine("};");
            sb.AppendLine($"layout(std430, binding = {textureHandleTableBinding}) readonly buffer XR_MaterialTextureHandleTableBuffer {{ TextureHandleEntry XR_TextureHandleTable[]; }};");
            sb.AppendLine("#define TextureHandleTable XR_TextureHandleTable");
            sb.AppendLine("uint64_t XR_CombineHandle(uvec2 parts)");
            sb.AppendLine("{");
            sb.AppendLine("    return (uint64_t(parts.y) << 32) | uint64_t(parts.x);");
            sb.AppendLine("}");
            sb.AppendLine("vec4 XR_TEXTURE2D(uint handleIndex, vec2 uv, vec4 fallback)");
            sb.AppendLine("{");
            sb.AppendLine("    if (handleIndex == 0u || handleIndex >= uint(XR_TextureHandleTable.length()))");
            sb.AppendLine("        return fallback;");
            sb.AppendLine("    TextureHandleEntry entry = XR_TextureHandleTable[handleIndex];");
            sb.AppendLine("    if ((entry.Flags & 1u) == 0u)");
            sb.AppendLine("        return fallback;");
            sb.AppendLine("    return texture(sampler2D(XR_CombineHandle(entry.Handle)), uv);");
            sb.AppendLine("}");
            sb.AppendLine("vec4 SampleBindlessTexture(uint handleIndex, vec2 uv, vec4 fallback)");
            sb.AppendLine("{");
            sb.AppendLine("    return XR_TEXTURE2D(handleIndex, uv, fallback);");
            sb.AppendLine("}");
        }

        private static string BuildDefaultRecordConstructor(MaterialBindingLayout layout)
        {
            string[] defaults = layout.PackedMembers
                .Select(static member => string.IsNullOrWhiteSpace(member.DefaultLiteral) ? DefaultLiteralForType(member.GlslType) : member.DefaultLiteral)
                .ToArray();

            return $"XR_MaterialRecord({string.Join(", ", defaults)})";
        }

        private static string DefaultLiteralForType(string glslType)
            => glslType.Trim().ToLowerInvariant() switch
            {
                "vec2" => "vec2(0.0)",
                "vec3" => "vec3(0.0)",
                "vec4" => "vec4(0.0)",
                "ivec2" => "ivec2(0)",
                "ivec3" => "ivec3(0)",
                "ivec4" => "ivec4(0)",
                "uvec2" => "uvec2(0u)",
                "uvec3" => "uvec3(0u)",
                "uvec4" => "uvec4(0u)",
                "mat2" => "mat2(1.0)",
                "mat3" => "mat3(1.0)",
                "mat4" => "mat4(1.0)",
                "float" => "0.0",
                "int" => "0",
                "uint" => "0u",
                "bool" => "false",
                _ => $"{glslType}(0)",
            };
    }

    public sealed record MaterialBindingResolverResult(
        EMaterialBindingResolverOutcome Outcome,
        string Reason,
        MaterialBindingLayout? Layout)
    {
        public static MaterialBindingResolverResult Compatible(MaterialBindingLayout layout)
            => new(EMaterialBindingResolverOutcome.MaterialTableCompatible, string.Empty, layout);

        public static MaterialBindingResolverResult PerMaterial(string reason, MaterialBindingLayout? layout = null)
            => new(EMaterialBindingResolverOutcome.PerMaterialRequired, reason, layout);

        public static MaterialBindingResolverResult Invalid(string reason, MaterialBindingLayout? layout = null)
            => new(EMaterialBindingResolverOutcome.Invalid, reason, layout);
    }

    public static class MaterialBindingVariantResolver
    {
        public static MaterialBindingResolverResult Resolve(MaterialBindingLayout? layout, ShaderUiManifest? manifest)
        {
            if (layout is null)
                return MaterialBindingResolverResult.PerMaterial("Render pass does not declare a material binding layout.");

            if (manifest is null || manifest.Bindings.Count == 0)
                return MaterialBindingResolverResult.PerMaterial("Shader has no material binding metadata.", layout);

            foreach (ShaderUiValidationIssue issue in manifest.ValidationIssues)
            {
                if (issue.Severity == EShaderUiValidationSeverity.Error)
                    return MaterialBindingResolverResult.Invalid(issue.Message, layout);
            }

            foreach (ShaderBindingMetadata binding in manifest.Bindings)
            {
                if (binding.Scope == EMaterialBindingScope.Unspecified)
                    return MaterialBindingResolverResult.PerMaterial($"Shader binding '{binding.Name}' has no scope.", layout);

                if (binding.Scope is EMaterialBindingScope.Material or EMaterialBindingScope.Texture &&
                    string.IsNullOrWhiteSpace(binding.Semantic))
                {
                    return MaterialBindingResolverResult.PerMaterial(
                        $"Shader binding '{binding.Name}' has no material semantic.",
                        layout);
                }

                if (binding.Scope == EMaterialBindingScope.Material &&
                    !layout.HasFieldSemantic(binding.Semantic!))
                {
                    return MaterialBindingResolverResult.Invalid(
                        $"Material binding '{binding.Name}' uses unknown semantic '{binding.Semantic}' for layout '{layout.Name}'.",
                        layout);
                }

                if (binding.Scope == EMaterialBindingScope.Texture &&
                    !layout.HasTextureSemantic(binding.Semantic!))
                {
                    return MaterialBindingResolverResult.Invalid(
                        $"Texture binding '{binding.Name}' uses unknown semantic '{binding.Semantic}' for layout '{layout.Name}'.",
                        layout);
                }
            }

            return MaterialBindingResolverResult.Compatible(layout);
        }
    }
}
