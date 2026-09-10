using System.Runtime.InteropServices;
using System.Text;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Vulkan;

internal static partial class SlangShaderReflection
{
    private sealed class PhysicalModule
    {
        private readonly Dictionary<uint, PhysicalType> _types = [];
        private readonly Dictionary<uint, PhysicalVariable> _variables = [];
        private readonly Dictionary<uint, Decorations> _decorations = [];
        private readonly Dictionary<(uint TypeId, uint MemberIndex), Decorations> _memberDecorations = [];
        private readonly Dictionary<uint, string> _names = [];
        private readonly Dictionary<(uint TypeId, uint MemberIndex), string> _memberNames = [];
        private readonly Dictionary<uint, uint> _constants = [];
        private bool _physicalAddressCapability;
        private bool _physicalAddressModel;

        public PhysicalModule(byte[] spirv)
        {
            if (spirv.Length < 20 || spirv.Length % sizeof(uint) != 0)
                throw new InvalidOperationException("SPIR-V module is incomplete.");
            Parse(MemoryMarshal.Cast<byte, uint>(spirv));
        }

        public IReadOnlyDictionary<string, uint> VertexLocations
        {
            get
            {
                Dictionary<string, uint> locations = new(StringComparer.Ordinal);
                foreach (PhysicalVariable variable in _variables.Values)
                {
                    if (variable.StorageClass != 1 || !_decorations.TryGetValue(variable.Id, out Decorations? decorations) ||
                        !decorations.Location.HasValue || !_names.TryGetValue(variable.Id, out string? name) || string.IsNullOrWhiteSpace(name))
                        continue;
                    locations.Add(name, decorations.Location.Value);
                }
                return locations;
            }
        }

        public PhysicalResource GetResource(uint set, uint binding)
        {
            foreach (PhysicalVariable variable in _variables.Values)
            {
                if (!_decorations.TryGetValue(variable.Id, out Decorations? decorations) ||
                    decorations.DescriptorSet != set || decorations.Binding != binding ||
                    !_types.TryGetValue(variable.TypeId, out PhysicalType? pointer) || pointer.Kind != TypeKind.Pointer || !pointer.ElementTypeId.HasValue)
                    continue;
                uint elementType = pointer.ElementTypeId.Value;
                return new PhysicalResource(
                    set,
                    binding,
                    elementType,
                    _decorations.TryGetValue(elementType, out Decorations? elementDecorations) && elementDecorations.Block,
                    this);
            }
            throw new InvalidOperationException($"SPIR-V omitted descriptor {set}:{binding}.");
        }

        private void Parse(ReadOnlySpan<uint> words)
        {
            for (int index = 5; index < words.Length;)
            {
                uint first = words[index];
                int count = (int)(first >> 16);
                if (count <= 0 || index + count > words.Length)
                    throw new InvalidOperationException("SPIR-V instruction is malformed.");
                ushort operation = (ushort)(first & 0xffff);
                ReadOnlySpan<uint> operands = words.Slice(index + 1, count - 1);
                switch (operation)
                {
                    case 17 when operands.Length == 1: _physicalAddressCapability |= operands[0] == 5347; break;
                    case 14 when operands.Length == 2: _physicalAddressModel = operands[0] == 5348; break;
                    case 5: ParseName(operands); break;
                    case 6: ParseMemberName(operands); break;
                    case 21: ParseScalar(operands, TypeKind.Int); break;
                    case 22: ParseScalar(operands, TypeKind.Float); break;
                    case 23: ParseVector(operands); break;
                    case 24: ParseMatrix(operands); break;
                    case 28: ParseArray(operands, TypeKind.Array); break;
                    case 29: ParseArray(operands, TypeKind.RuntimeArray); break;
                    case 30: ParseStruct(operands); break;
                    case 32: ParsePointer(operands); break;
                    // Specialization constants cannot establish an immutable host ABI extent.
                    case 43: ParseConstant(operands); break;
                    case 59: ParseVariable(operands); break;
                    case 71: ParseDecorate(operands); break;
                    case 72: ParseMemberDecorate(operands); break;
                }
                index += count;
            }
        }

        private void ParseName(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 1)
                _names[operands[0]] = Decode(operands[1..]);
        }

        private void ParseMemberName(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 2)
                _memberNames[(operands[0], operands[1])] = Decode(operands[2..]);
        }

        private void ParseScalar(ReadOnlySpan<uint> operands, TypeKind kind)
        {
            if (operands.Length >= 2)
                _types[operands[0]] = new PhysicalType(kind) { Width = operands[1], Signed = kind == TypeKind.Int && operands.Length >= 3 && operands[2] == 1 };
        }

        private void ParseVector(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 3)
                _types[operands[0]] = new PhysicalType(TypeKind.Vector) { ElementTypeId = operands[1], Count = operands[2] };
        }

        private void ParseMatrix(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 3)
                _types[operands[0]] = new PhysicalType(TypeKind.Matrix) { ElementTypeId = operands[1], Count = operands[2] };
        }

        private void ParseArray(ReadOnlySpan<uint> operands, TypeKind kind)
        {
            if (operands.Length >= 2)
                _types[operands[0]] = new PhysicalType(kind)
                {
                    ElementTypeId = operands[1],
                    LengthId = operands.Length >= 3 ? operands[2] : null,
                };
        }

        private void ParseStruct(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 1)
                _types[operands[0]] = new PhysicalType(TypeKind.Struct) { Members = operands[1..].ToArray() };
        }

        private void ParsePointer(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 3)
                _types[operands[0]] = new PhysicalType(TypeKind.Pointer) { StorageClass = operands[1], ElementTypeId = operands[2] };
        }

        private void ParseConstant(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 3)
                _constants[operands[1]] = operands[2];
        }

        private void ParseVariable(ReadOnlySpan<uint> operands)
        {
            if (operands.Length >= 3)
                _variables[operands[1]] = new PhysicalVariable(operands[1], operands[0], operands[2]);
        }

        private void ParseDecorate(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;
            Decorations decorations = GetDecorations(_decorations, operands[0]);
            switch (operands[1])
            {
                case 2: decorations.Block = true; break;
                case 6 when operands.Length >= 3: decorations.ArrayStride = operands[2]; break;
                case 30 when operands.Length >= 3: decorations.Location = operands[2]; break;
                case 33 when operands.Length >= 3: decorations.Binding = operands[2]; break;
                case 34 when operands.Length >= 3: decorations.DescriptorSet = operands[2]; break;
            }
        }

        private void ParseMemberDecorate(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 3)
                return;
            Decorations decorations = GetDecorations(_memberDecorations, (operands[0], operands[1]));
            switch (operands[2])
            {
                case 4: decorations.MatrixOrder = ShaderAbiMatrixOrder.RowMajor; break;
                case 5: decorations.MatrixOrder = ShaderAbiMatrixOrder.ColumnMajor; break;
                case 7 when operands.Length >= 4: decorations.MatrixStride = operands[3]; break;
                case 35 when operands.Length >= 4: decorations.Offset = operands[3]; decorations.HasOffset = true; break;
            }
        }

        internal int GetMemberCount(uint typeId)
            => _types.TryGetValue(typeId, out PhysicalType? type) && type.Kind == TypeKind.Struct ? type.Members.Length : 0;

        internal PhysicalMember GetMember(uint structId, string name)
        {
            if (!_types.TryGetValue(structId, out PhysicalType? structure) || structure.Kind != TypeKind.Struct)
                throw new InvalidOperationException("SPIR-V uniform resource is not a structure.");
            for (uint index = 0; index < structure.Members.Length; index++)
            {
                if (!_memberNames.TryGetValue((structId, index), out string? memberName) || !string.Equals(memberName, name, StringComparison.Ordinal))
                    continue;
                Decorations decorations = _memberDecorations.TryGetValue((structId, index), out Decorations? found) ? found : new Decorations();
                return CreateMember(structure.Members[index], name, decorations);
            }
            throw new InvalidOperationException($"SPIR-V uniform block omitted member '{name}'.");
        }

        internal PhysicalStorageElement GetStorageElement(uint blockStructId)
        {
            if (!_types.TryGetValue(blockStructId, out PhysicalType? block) || block.Kind != TypeKind.Struct || block.Members.Length != 1)
                throw new NotSupportedException("Slang storage buffers must lower to a one-member block wrapper.");
            if (!_memberDecorations.TryGetValue((blockStructId, 0), out Decorations? member) || !member.HasOffset || member.Offset != 0)
                throw new InvalidOperationException("Slang storage array wrapper must have explicit Offset 0.");
            uint runtimeArrayId = block.Members[0];
            if (!_types.TryGetValue(runtimeArrayId, out PhysicalType? runtimeArray) || runtimeArray.Kind != TypeKind.RuntimeArray || !runtimeArray.ElementTypeId.HasValue)
                throw new NotSupportedException("Slang storage buffers must use a runtime array of explicit elements.");
            uint stride = _decorations.TryGetValue(runtimeArrayId, out Decorations? decorations) ? decorations.ArrayStride : 0;
            if (stride == 0)
                throw new InvalidOperationException("SPIR-V storage buffer runtime array omits ArrayStride.");
            return new PhysicalStorageElement(runtimeArray.ElementTypeId.Value, stride, this);
        }

        private PhysicalMember CreateMember(uint typeId, string name, Decorations decorations)
        {
            if (!decorations.HasOffset)
                throw new InvalidOperationException($"SPIR-V member '{name}' omits Offset.");
            uint arrayCount = 0;
            uint arrayStride = 0;
            uint effectiveType = typeId;
            // Slang can lower a std140 array to a one-member wrapper structure.
            if (_types.TryGetValue(effectiveType, out PhysicalType? wrapper) && wrapper.Kind == TypeKind.Struct)
            {
                if (wrapper.Members.Length != 1 ||
                    !_memberDecorations.TryGetValue((effectiveType, 0), out Decorations? nested) || !nested.HasOffset || nested.Offset != 0 ||
                    !_types.TryGetValue(wrapper.Members[0], out PhysicalType? wrappedType) || wrappedType.Kind != TypeKind.Array)
                    throw new NotSupportedException("Only zero-offset Slang std140 array wrappers are supported in uniform fields.");
                effectiveType = wrapper.Members[0];
            }
            if (_types.TryGetValue(effectiveType, out PhysicalType? array) && array.Kind == TypeKind.Array)
            {
                arrayCount = array.LengthId is uint lengthId && _constants.TryGetValue(lengthId, out uint length) ? length : 0;
                arrayStride = _decorations.TryGetValue(effectiveType, out Decorations? arrayDecorations) ? arrayDecorations.ArrayStride : 0;
                if (arrayCount == 0 || arrayStride == 0)
                    throw new InvalidOperationException($"SPIR-V array '{name}' requires a fixed nonzero extent and stride.");
                effectiveType = array.ElementTypeId ?? 0;
            }
            string physicalType = DescribeType(effectiveType);
            if (_types[effectiveType].Kind == TypeKind.Matrix &&
                (decorations.MatrixOrder == ShaderAbiMatrixOrder.None || decorations.MatrixStride == 0))
                throw new InvalidOperationException("Native matrix fields require explicit physical order and stride.");
            if (arrayCount != 0)
                physicalType += "[]";
            uint size = arrayCount != 0 && arrayStride != 0
                ? checked(arrayCount * arrayStride)
                : GetTypeSize(effectiveType, decorations.MatrixStride);
            return new PhysicalMember(name, decorations.Offset, size, physicalType, arrayCount, arrayStride, decorations.MatrixOrder, decorations.MatrixStride);
        }

        private string DescribeType(uint typeId)
        {
            if (!_types.TryGetValue(typeId, out PhysicalType? type))
                return "unknown";
            return type.Kind switch
            {
                TypeKind.Float when type.Width == 32 => "float",
                TypeKind.Int when type.Width == 32 => type.Signed ? "int" : "uint",
                TypeKind.Vector => DescribeType(type.ElementTypeId ?? 0) + type.Count,
                TypeKind.Matrix when _types[type.ElementTypeId!.Value].Count == type.Count => DescribeType(type.ElementTypeId.Value) + "x" + type.Count,
                TypeKind.Pointer when type.StorageClass == 5349 && _physicalAddressCapability && _physicalAddressModel =>
                    "PhysicalStorageBuffer<" + DescribeType(type.ElementTypeId!.Value) + ">",
                _ => throw new NotSupportedException($"Native ABI type {type.Kind}/{type.Width} is not supported by this frontend contract."),
            };
        }

        private uint GetTypeSize(uint typeId, uint matrixStride)
        {
            if (!_types.TryGetValue(typeId, out PhysicalType? type))
                return 0;
            return type.Kind switch
            {
                TypeKind.Float or TypeKind.Int => type.Width / 8,
                TypeKind.Vector => checked(GetTypeSize(type.ElementTypeId ?? 0, 0) * type.Count),
                TypeKind.Matrix when matrixStride != 0 => checked(matrixStride * type.Count),
                TypeKind.Matrix => checked(GetTypeSize(type.ElementTypeId ?? 0, 0) * type.Count),
                TypeKind.Pointer => 8,
                _ => 0,
            };
        }

        private static Decorations GetDecorations<TKey>(Dictionary<TKey, Decorations> map, TKey key) where TKey : notnull
        {
            if (!map.TryGetValue(key, out Decorations? decorations))
            {
                decorations = new Decorations();
                map[key] = decorations;
            }
            return decorations;
        }

        private static string Decode(ReadOnlySpan<uint> words)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<uint, byte>(words);
            int terminator = bytes.IndexOf((byte)0);
            return Encoding.UTF8.GetString(terminator < 0 ? bytes : bytes[..terminator]);
        }

        private enum TypeKind { Unknown, Int, Float, Vector, Matrix, Array, RuntimeArray, Struct, Pointer }
        private sealed class PhysicalType(TypeKind kind)
        {
            public TypeKind Kind { get; } = kind;
            public uint Width { get; init; }
            public bool Signed { get; init; }
            public uint? ElementTypeId { get; init; }
            public uint? LengthId { get; init; }
            public uint Count { get; init; }
            public uint[] Members { get; init; } = [];
            public uint StorageClass { get; init; }
        }
        private sealed class Decorations
        {
            public bool Block { get; set; }
            public uint? DescriptorSet { get; set; }
            public uint? Binding { get; set; }
            public uint? Location { get; set; }
            public uint Offset { get; set; }
            public bool HasOffset { get; set; }
            public uint ArrayStride { get; set; }
            public uint MatrixStride { get; set; }
            public ShaderAbiMatrixOrder MatrixOrder { get; set; }
        }
        private readonly record struct PhysicalVariable(uint Id, uint TypeId, uint StorageClass);
    }

    private sealed class PhysicalResource(uint set, uint binding, uint structId, bool isBlock, PhysicalModule module)
    {
        public uint Set { get; } = set;
        public uint Binding { get; } = binding;
        public bool IsBlock { get; } = isBlock;
        public PhysicalMember GetMember(string name) => module.GetMember(structId, name);
        public PhysicalStorageElement GetStorageElement() => module.GetStorageElement(structId);
    }

    private sealed class PhysicalStorageElement(uint structId, uint stride, PhysicalModule module)
    {
        public uint Stride { get; } = stride;
        public int MemberCount => module.GetMemberCount(structId);
        public PhysicalMember GetMember(string name) => module.GetMember(structId, name);
    }

    private readonly record struct PhysicalMember(
        string Name,
        uint Offset,
        uint Size,
        string PhysicalType,
        uint ArrayCount,
        uint ArrayStride,
        ShaderAbiMatrixOrder MatrixOrder,
        uint MatrixStride);
}
