using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using XREngine.Diagnostics;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

public readonly record struct DescriptorBindingInfo(
    uint Set,
    uint Binding,
    DescriptorType DescriptorType,
    ShaderStageFlags StageFlags,
    uint Count,
    string Name);

internal static class VulkanShaderCompiler
{
    private static readonly Shaderc ShadercApi = Shaderc.GetApi();

    public static unsafe byte[] Compile(XRShader shader, out string entryPoint)
    {
        entryPoint = "main";
        string source = shader.Source?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' does not contain GLSL source code.");

        Compiler* compiler = ShadercApi.CompilerInitialize();
        if (compiler is null)
            throw new InvalidOperationException("Failed to initialize the shaderc compiler instance.");

        CompileOptions* options = ShadercApi.CompileOptionsInitialize();
        if (options is null)
        {
            ShadercApi.CompilerRelease(compiler);
            throw new InvalidOperationException("Failed to allocate shaderc compile options.");
        }

        ShadercApi.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
        ShadercApi.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);

        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        byte[] nameBytes = GetNullTerminatedUtf8(shader.Name ?? $"Shader_{shader.GetHashCode():X8}");
        byte[] entryPointBytes = GetNullTerminatedUtf8(entryPoint);

        CompilationResult* result;
        fixed (byte* sourcePtr = sourceBytes)
        fixed (byte* namePtr = nameBytes)
        fixed (byte* entryPtr = entryPointBytes)
        {
            result = ShadercApi.CompileIntoSpv(
                compiler,
                sourcePtr,
                (nuint)sourceBytes.Length,
                ToShaderKind(shader.Type),
                namePtr,
                entryPtr,
                options);
        }

        try
        {
            if (result is null)
                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile due to an unknown error.");

            CompilationStatus status = ShadercApi.ResultGetCompilationStatus(result);
            if (status != CompilationStatus.Success)
            {
                string message = SilkMarshal.PtrToString((nint)ShadercApi.ResultGetErrorMessage(result)) ?? "Unknown error";
                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile: {message}");
            }

            nuint length = ShadercApi.ResultGetLength(result);
            if (length == 0)
                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' produced an empty SPIR-V module.");

            byte[] spirv = new byte[(int)length];
            void* bytesPtr = ShadercApi.ResultGetBytes(result);
            Marshal.Copy((nint)bytesPtr, spirv, 0, spirv.Length);

            ShadercApi.ResultRelease(result);
            result = null;
            return spirv;
        }
        finally
        {
            if (result is not null)
                ShadercApi.ResultRelease(result);

            ShadercApi.CompileOptionsRelease(options);
            ShadercApi.CompilerRelease(compiler);
        }
    }

    private static byte[] GetNullTerminatedUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Array.Resize(ref bytes, bytes.Length + 1);
        bytes[^1] = 0;
        return bytes;
    }

    private static ShaderKind ToShaderKind(EShaderType type)
        => type switch
        {
            EShaderType.Vertex => ShaderKind.VertexShader,
            EShaderType.Fragment => ShaderKind.FragmentShader,
            EShaderType.Geometry => ShaderKind.GeometryShader,
            EShaderType.TessControl => ShaderKind.TessControlShader,
            EShaderType.TessEvaluation => ShaderKind.TessEvaluationShader,
            EShaderType.Compute => ShaderKind.ComputeShader,
            EShaderType.Task => ShaderKind.TaskShader,
            EShaderType.Mesh => ShaderKind.MeshShader,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
}

internal static class VulkanShaderReflection
{
    private static readonly Regex LayoutRegex = new(
        "layout\\s*\\((?<qualifiers>[^)]*)\\)\\s*(?<storage>[a-zA-Z]+)\\s+(?<declaration>[^;{]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ArrayRegex = new(@"\\[(?<size>\\d+)\\]", RegexOptions.Compiled);

    public static IReadOnlyList<DescriptorBindingInfo> ExtractBindings(byte[] spirv, ShaderStageFlags stage, string? glslSourceFallback = null)
    {
        if (spirv.Length > 0)
        {
            try
            {
                SpirvModule module = new(spirv, stage);
                var bindings = module.CollectDescriptorBindings();
                if (bindings.Count > 0)
                    return bindings;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"SPIR-V reflection failed ({ex.Message}). Falling back to GLSL source parsing.");
            }
        }

        return ExtractBindingsFromSource(glslSourceFallback, stage);
    }

    private static IReadOnlyList<DescriptorBindingInfo> ExtractBindingsFromSource(string? source, ShaderStageFlags stage)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Array.Empty<DescriptorBindingInfo>();

        List<DescriptorBindingInfo> bindings = new();
        foreach (Match match in LayoutRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string qualifiers = match.Groups["qualifiers"].Value;
            string storage = match.Groups["storage"].Value;
            string declaration = match.Groups["declaration"].Value.Trim();

            if (!TryParseQualifier(qualifiers, "binding", out uint binding))
            {
                Debug.LogWarning($"Shader descriptor '{declaration}' is missing a binding index; skipping.");
                continue;
            }

            TryParseQualifier(qualifiers, "set", out uint set);

            DescriptorType descriptorType = ClassifyDescriptor(storage, declaration, source, match.Index + match.Length);
            uint arraySize = ExtractArraySize(declaration);
            string name = ExtractResourceName(declaration);

            bindings.Add(new DescriptorBindingInfo(set, binding, descriptorType, stage, arraySize == 0 ? 1u : arraySize, name));
        }

        return bindings;
    }

    private static bool TryParseQualifier(string qualifiers, string key, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(qualifiers))
            return false;

        string[] parts = qualifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            int equals = part.IndexOf('=');
            if (equals < 0)
                continue;

            string qualifierKey = part[..equals].Trim();
            if (!qualifierKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            string rawValue = part[(equals + 1)..].Trim();
            if (uint.TryParse(rawValue, out value))
                return true;
        }

        return false;
    }

    private static DescriptorType ClassifyDescriptor(string storage, string declaration, string source, int lookAheadIndex)
    {
        if (storage.Equals("buffer", StringComparison.OrdinalIgnoreCase))
            return DescriptorType.StorageBuffer;

        if (storage.Equals("uniform", StringComparison.OrdinalIgnoreCase))
        {
            if (DeclaresBlock(source, lookAheadIndex))
                return DescriptorType.UniformBuffer;

            if (declaration.Contains("sampler", StringComparison.OrdinalIgnoreCase))
                return DescriptorType.CombinedImageSampler;

            if (declaration.Contains("image", StringComparison.OrdinalIgnoreCase))
                return DescriptorType.StorageImage;

            return DescriptorType.UniformBuffer;
        }

        return DescriptorType.UniformBuffer;
    }

    private static bool DeclaresBlock(string source, int index)
    {
        for (int i = index; i < source.Length; i++)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
                continue;
            return c == '{';
        }
        return false;
    }

    private static uint ExtractArraySize(string declaration)
    {
        Match match = ArrayRegex.Match(declaration);
        return match.Success && uint.TryParse(match.Groups["size"].Value, out uint size) ? size : 0u;
    }

    private static string ExtractResourceName(string declaration)
    {
        string sanitized = declaration;
        int bracketIndex = sanitized.IndexOf('[');
        if (bracketIndex >= 0)
            sanitized = sanitized[..bracketIndex];

        string[] tokens = sanitized.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? string.Empty : tokens[^1];
    }

    private sealed class SpirvModule
    {
        private readonly uint[] _words;
        private readonly ShaderStageFlags _stage;
        private readonly Dictionary<uint, SpirvType> _types = new();
        private readonly Dictionary<uint, SpirvVariable> _variables = new();
        private readonly Dictionary<uint, SpirvDecorations> _decorations = new();
        private readonly Dictionary<uint, string> _names = new();
        private readonly Dictionary<uint, ulong> _constants = new();

        private const int HeaderWordCount = 5;

        public SpirvModule(byte[] spirv, ShaderStageFlags stage)
        {
            if (spirv.Length % sizeof(uint) != 0)
                throw new InvalidOperationException("SPIR-V bytecode length must be divisible by 4.");

            _words = MemoryMarshal.Cast<byte, uint>(spirv).ToArray();
            _stage = stage;
            Parse();
        }

        public List<DescriptorBindingInfo> CollectDescriptorBindings()
        {
            List<DescriptorBindingInfo> bindings = new();

            foreach (SpirvVariable variable in _variables.Values)
            {
                if (!_decorations.TryGetValue(variable.Id, out SpirvDecorations? decoration) || !decoration.HasBinding)
                    continue;

                if (!_types.TryGetValue(variable.TypeId, out SpirvType? pointer) || pointer.Kind != SpirvTypeKind.Pointer || pointer.ElementTypeId is null)
                    continue;

                uint elementTypeId = pointer.ElementTypeId.Value;
                uint descriptorCount = ResolveDescriptorCount(elementTypeId, out uint leafTypeId);
                DescriptorType descriptorType = ResolveDescriptorType(variable.StorageClass, leafTypeId);

                uint set = decoration.DescriptorSet ?? 0;
                uint binding = decoration.Binding ?? 0;
                string name = _names.TryGetValue(variable.Id, out string? foundName) ? foundName : string.Empty;

                bindings.Add(new DescriptorBindingInfo(set, binding, descriptorType, _stage, descriptorCount == 0 ? 1u : descriptorCount, name));
            }

            return bindings;
        }

        private void Parse()
        {
            if (_words.Length < HeaderWordCount)
                throw new InvalidOperationException("SPIR-V module header is incomplete.");

            int index = HeaderWordCount;
            while (index < _words.Length)
            {
                uint word = _words[index];
                int wordCount = (int)(word >> 16);
                ushort opCode = (ushort)(word & 0xFFFF);

                if (wordCount <= 0)
                    throw new InvalidOperationException($"Invalid SPIR-V word count for opcode {opCode}.");

                if (index + wordCount > _words.Length)
                    throw new InvalidOperationException("SPIR-V instruction extends beyond buffer.");

                ReadOnlySpan<uint> operands = new(_words, index + 1, wordCount - 1);
                switch ((SpirvOp)opCode)
                {
                    case SpirvOp.OpName:
                        ParseOpName(operands);
                        break;
                    case SpirvOp.OpDecorate:
                        ParseOpDecorate(operands);
                        break;
                    case SpirvOp.OpConstant:
                    case SpirvOp.OpSpecConstant:
                        ParseOpConstant(operands);
                        break;
                    case SpirvOp.OpVariable:
                        ParseOpVariable(operands);
                        break;
                    case SpirvOp.OpTypePointer:
                        ParseOpTypePointer(operands);
                        break;
                    case SpirvOp.OpTypeStruct:
                        ParseOpTypeStruct(operands);
                        break;
                    case SpirvOp.OpTypeArray:
                        ParseOpTypeArray(operands);
                        break;
                    case SpirvOp.OpTypeRuntimeArray:
                        ParseOpTypeRuntimeArray(operands);
                        break;
                    case SpirvOp.OpTypeImage:
                        ParseOpTypeImage(operands);
                        break;
                    case SpirvOp.OpTypeSampledImage:
                        ParseOpTypeSampledImage(operands);
                        break;
                    case SpirvOp.OpTypeSampler:
                        ParseOpTypeSampler(operands);
                        break;
                }

                index += wordCount;
            }
        }

        private void ParseOpName(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 1)
                return;

            uint targetId = operands[0];
            string name = DecodeString(operands.Slice(1));
            if (!string.IsNullOrEmpty(name))
                _names[targetId] = name;
        }

        private void ParseOpDecorate(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;

            uint targetId = operands[0];
            SpirvDecoration decoration = (SpirvDecoration)operands[1];
            SpirvDecorations info = GetOrCreateDecoration(targetId);

            switch (decoration)
            {
                case SpirvDecoration.DescriptorSet:
                    if (operands.Length >= 3)
                        info.DescriptorSet = operands[2];
                    break;
                case SpirvDecoration.Binding:
                    if (operands.Length >= 3)
                        info.Binding = operands[2];
                    break;
                case SpirvDecoration.Block:
                    info.Block = true;
                    break;
                case SpirvDecoration.BufferBlock:
                    info.BufferBlock = true;
                    break;
            }
        }

        private void ParseOpConstant(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;

            uint resultId = operands[1];
            ulong value = 0;
            for (int i = 2; i < operands.Length; i++)
                value |= (ulong)operands[i] << ((i - 2) * 32);

            _constants[resultId] = value;
        }

        private void ParseOpVariable(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 3)
                return;

            uint resultTypeId = operands[0];
            uint resultId = operands[1];
            SpirvStorageClass storageClass = (SpirvStorageClass)operands[2];

            _variables[resultId] = new SpirvVariable(resultId, resultTypeId, storageClass);
        }

        private void ParseOpTypePointer(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 3)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.Pointer,
                StorageClass = (SpirvStorageClass)operands[1],
                ElementTypeId = operands[2],
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeStruct(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 1)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.Struct,
                Members = operands.Length > 1 ? operands.Slice(1).ToArray() : Array.Empty<uint>()
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeArray(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 3)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.Array,
                ElementTypeId = operands[1],
                LengthId = operands[2],
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeRuntimeArray(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.RuntimeArray,
                ElementTypeId = operands[1],
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeImage(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 8)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.Image,
                ImageType = new SpirvImageInfo
                {
                    SampledTypeId = operands[1],
                    Dim = (SpirvDim)operands[2],
                    Depth = operands[3],
                    Arrayed = operands[4],
                    Multisampled = operands[5],
                    Sampled = operands[6],
                    ImageFormat = operands[7],
                    AccessQualifier = operands.Length >= 9 ? operands[8] : 0,
                }
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeSampledImage(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.SampledImage,
                ElementTypeId = operands[1],
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeSampler(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 1)
                return;

            uint resultId = operands[0];
            _types[resultId] = new SpirvType(resultId) { Kind = SpirvTypeKind.Sampler };
        }

        private uint ResolveDescriptorCount(uint typeId, out uint leafTypeId)
        {
            uint count = 1;
            uint current = typeId;

            while (_types.TryGetValue(current, out SpirvType? type))
            {
                if (type.Kind == SpirvTypeKind.Array)
                {
                    if (type.LengthId.HasValue && _constants.TryGetValue(type.LengthId.Value, out ulong length) && length > 0)
                        count *= (uint)Math.Max(1ul, length);

                    current = type.ElementTypeId ?? 0;
                }
                else if (type.Kind == SpirvTypeKind.RuntimeArray)
                {
                    current = type.ElementTypeId ?? 0;
                    break;
                }
                else
                {
                    break;
                }
            }

            leafTypeId = current;
            return count;
        }

        private DescriptorType ResolveDescriptorType(SpirvStorageClass storageClass, uint typeId)
        {
            if (!_types.TryGetValue(typeId, out SpirvType? type))
                return DescriptorType.UniformBuffer;

            switch (storageClass)
            {
                case SpirvStorageClass.UniformConstant:
                    return ResolveUniformConstantType(type);
                case SpirvStorageClass.Uniform:
                    return IsBufferBlock(typeId) ? DescriptorType.StorageBuffer : DescriptorType.UniformBuffer;
                case SpirvStorageClass.StorageBuffer:
                    return DescriptorType.StorageBuffer;
                default:
                    return DescriptorType.UniformBuffer;
            }
        }

        private DescriptorType ResolveUniformConstantType(SpirvType type)
        {
            switch (type.Kind)
            {
                case SpirvTypeKind.SampledImage:
                    return DescriptorType.CombinedImageSampler;
                case SpirvTypeKind.Sampler:
                    return DescriptorType.Sampler;
                case SpirvTypeKind.Image:
                    return ResolveImageDescriptor(type.ImageType);
                default:
                    return DescriptorType.UniformBuffer;
            }
        }

        private DescriptorType ResolveImageDescriptor(SpirvImageInfo? info)
        {
            if (info is null)
                return DescriptorType.UniformBuffer;

            if (info.Dim == SpirvDim.SubpassData)
                return DescriptorType.InputAttachment;

            bool storage = info.Sampled == 2;
            if (info.Dim == SpirvDim.Buffer)
                return storage ? DescriptorType.StorageTexelBuffer : DescriptorType.UniformTexelBuffer;

            return storage ? DescriptorType.StorageImage : DescriptorType.SampledImage;
        }

        private bool IsBufferBlock(uint typeId)
        {
            return _decorations.TryGetValue(typeId, out SpirvDecorations? decorations) && decorations.BufferBlock;
        }

        private SpirvDecorations GetOrCreateDecoration(uint id)
        {
            if (!_decorations.TryGetValue(id, out SpirvDecorations? decor))
            {
                decor = new SpirvDecorations();
                _decorations[id] = decor;
            }

            return decor;
        }

        private static string DecodeString(ReadOnlySpan<uint> words)
        {
            if (words.Length == 0)
                return string.Empty;

            ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<uint, byte>(words);
            int nullIndex = bytes.IndexOf((byte)0);
            int length = nullIndex >= 0 ? nullIndex : bytes.Length;
            return length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes[..length]);
        }
    }

    private enum SpirvOp : ushort
    {
        OpName = 5,
        OpMemberName = 6,
        OpTypeInt = 21,
        OpTypeFloat = 22,
        OpTypeVector = 23,
        OpTypeMatrix = 24,
        OpTypeImage = 25,
        OpTypeSampler = 26,
        OpTypeSampledImage = 27,
        OpTypeArray = 28,
        OpTypeRuntimeArray = 29,
        OpTypeStruct = 30,
        OpTypePointer = 32,
        OpVariable = 59,
        OpConstant = 43,
        OpSpecConstant = 45,
        OpDecorate = 71,
    }

    private enum SpirvStorageClass : uint
    {
        UniformConstant = 0,
        Input = 1,
        Uniform = 2,
        Output = 3,
        Workgroup = 4,
        CrossWorkgroup = 5,
        Private = 6,
        Function = 7,
        Generic = 8,
        PushConstant = 9,
        AtomicCounter = 10,
        Image = 11,
        StorageBuffer = 12,
    }

    private enum SpirvDecoration : uint
    {
        Block = 2,
        BufferBlock = 3,
        Binding = 33,
        DescriptorSet = 34,
    }

    private enum SpirvDim : uint
    {
        Dim1D = 0,
        Dim2D = 1,
        Dim3D = 2,
        Cube = 3,
        Rect = 4,
        Buffer = 5,
        SubpassData = 6,
        Dim1DArray = 7,
        Dim2DArray = 8,
        CubeArray = 9,
    }

    private enum SpirvTypeKind
    {
        Unknown,
        Pointer,
        Struct,
        Array,
        RuntimeArray,
        Image,
        SampledImage,
        Sampler,
    }

    private sealed record SpirvType(uint Id)
    {
        public SpirvTypeKind Kind { get; init; } = SpirvTypeKind.Unknown;
        public SpirvStorageClass? StorageClass { get; init; }
        public uint? ElementTypeId { get; init; }
        public uint? LengthId { get; init; }
        public uint[] Members { get; init; } = Array.Empty<uint>();
        public SpirvImageInfo? ImageType { get; init; }
    }

    private sealed record SpirvVariable(uint Id, uint TypeId, SpirvStorageClass StorageClass);

    private sealed class SpirvDecorations
    {
        public uint? DescriptorSet { get; set; }
        public uint? Binding { get; set; }
        public bool Block { get; set; }
        public bool BufferBlock { get; set; }
        public bool HasBinding => Binding.HasValue;
    }

    private sealed class SpirvImageInfo
    {
        public uint SampledTypeId { get; init; }
        public SpirvDim Dim { get; init; }
        public uint Depth { get; init; }
        public uint Arrayed { get; init; }
        public uint Multisampled { get; init; }
        public uint Sampled { get; init; }
        public uint ImageFormat { get; init; }
        public uint AccessQualifier { get; init; }
    }
}
