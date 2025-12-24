using Extensions;
using MemoryPack;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Objects;
using YamlDotNet.Serialization;

namespace XREngine.Rendering
{
    [MemoryPackable]
    public partial class XRDataBuffer : GenericRenderObject, IDisposable
    {
        public delegate void DelPushSubData(int offset, uint length);
        public delegate void DelSetBlockName(XRRenderProgram program, string blockName);
        public delegate void DelSetBlockIndex(uint blockIndex);
        public delegate void DelFlushRange(int offset, uint length);
        public delegate void DelBindSSBO(XRRenderProgram program, uint? bindingIndexOverride = null);

        public event Action? PushDataRequested;
        public event DelPushSubData? PushSubDataRequested;
        public event Action? MapBufferDataRequested;
        public event Action? UnmapBufferDataRequested;
        public event DelSetBlockName? SetBlockNameRequested;
        public event DelSetBlockIndex? SetBlockIndexRequested;
        public event Action<VoidPtr>? DataPointerSet;
        public event Action? BindRequested;
        public event Action? UnbindRequested;
        public event Action? FlushRequested;
        public event DelFlushRange? FlushRangeRequested;
        public event DelBindSSBO? BindSSBORequested;

        [MemoryPackConstructor]
        public XRDataBuffer() { }
        public XRDataBuffer(
            string bindingName,
            EBufferTarget target,
            uint elementCount,
            EComponentType componentType,
            uint componentCount,
            bool normalize,
            bool integral,
            bool alignClientSourceToPowerOf2 = false)
        {
            AttributeName = bindingName;
            Target = target;

            _componentType = componentType;
            _componentCount = componentCount;
            _elementCount = elementCount;
            _normalize = normalize;
            _integral = integral;

            if (alignClientSourceToPowerOf2)
                _clientSideSource = DataSource.Allocate(XRMath.NextPowerOfTwo(_elementCount) * ElementSize);
            else
                _clientSideSource = DataSource.Allocate(Length);
        }

        public XRDataBuffer(
            string bindingName,
            EBufferTarget target,
            bool integral)
        {
            AttributeName = bindingName;
            Target = target;
            _integral = integral;
        }

        public XRDataBuffer(
            EBufferTarget target,
            bool integral)
        {
            Target = target;
            _integral = integral;
        }

        /// <summary>
        /// The current mapping state of this buffer.
        /// If the buffer is mapped, this means any updates to the buffer will be shown by the GPU immediately.
        /// If the buffer is not mapped, any updates will have to be pushed to the GPU using PushData or PushSubData.
        /// </summary>
        [YamlIgnore]
        public List<IApiDataBuffer> ActivelyMapping { get; } = [];
        
        private bool _padEndingToVec4 = true;
        public bool PadEndingToVec4
        {
            get => _padEndingToVec4;
            set => SetField(ref _padEndingToVec4, value);
        }

        private bool _mapped = false;
        /// <summary>
        /// Determines if this buffer should be mapped when it is generated.
        /// If the buffer is mapped, this means any updates to the buffer will be shown by the GPU immediately.
        /// If the buffer is not mapped, any updates will have to be pushed to the GPU using PushData or PushSubData.
        /// </summary>
        public bool ShouldMap
        {
            get => _mapped;
            set => SetField(ref _mapped, value);
        }

        public IEnumerable<VoidPtr> GetMappedAddresses()
            => ActivelyMapping.Select(x => x.GetMappedAddress()).Where(x => x.HasValue).Select(x => x!.Value);

        // Defaults: no implicit bits. Callers set exactly what they need.
        private EBufferMapStorageFlags _storageFlags = 0;
        public EBufferMapStorageFlags StorageFlags
        {
            get => _storageFlags;
            set => SetField(ref _storageFlags, value);
        }

        private EBufferMapRangeFlags _rangeFlags = 0;
        public EBufferMapRangeFlags RangeFlags 
        {
            get => _rangeFlags;
            set => SetField(ref _rangeFlags, value);
        }

        private EBufferTarget _target = EBufferTarget.ArrayBuffer;
        /// <summary>
        /// The type of data this buffer will be used for.
        /// </summary>
        public EBufferTarget Target
        {
            get => _target;
            private set => SetField(ref _target, value);
        }

        private EBufferUsage _usage = EBufferUsage.StaticCopy;
        /// <summary>
        /// Determines how this buffer will be used.
        /// Data can be streamed in/out frequently, be unchanging, or be modified infrequently.
        /// </summary>
        public EBufferUsage Usage
        {
            get => _usage;
            set => SetField(ref _usage, value);
        }

        private EComponentType _componentType;
        public EComponentType ComponentType => _componentType;

        private bool _normalize;
        public bool Normalize
        {
            get => _normalize;
            set => SetField(ref _normalize, value);
        }

        private uint _componentCount;
        public uint ComponentCount
        {
            get => _componentType == EComponentType.Struct ? 1 : _componentCount;
            set => SetField(ref _componentCount, value);
        }

        private uint _elementCount;
        public uint ElementCount
        {
            get => _elementCount;
            set => SetField(ref _elementCount, value);
        }

        private DataSource? _clientSideSource = null;
        /// <summary>
        /// The data buffer stored on the CPU side.
        /// </summary>
        public DataSource? ClientSideSource
        {
            get => _clientSideSource;
            set => SetField(ref _clientSideSource, value);
        }

        private bool _integral = false;
        /// <summary>
        /// Determines if this buffer has integer-type data or otherwise, floating point.
        /// </summary>
        public bool Integral
        {
            get => _integral;
            set => SetField(ref _integral, value);
        }

        private string _attributeName = string.Empty;
        /// <summary>
        /// The name of the attribute this buffer is bound to.
        /// </summary>
        public string AttributeName
        {
            get => _attributeName;
            set => SetField(ref _attributeName, value);
        }

        private InterleavedAttribute[] _interleavedAttributeNames = [];
        /// <summary>
        /// 
        /// </summary>
        public InterleavedAttribute[] InterleavedAttributes
        {
            get => _interleavedAttributeNames;
            set => SetField(ref _interleavedAttributeNames, value);
        }

        private uint _instanceDivisor = 0;
        public uint InstanceDivisor
        {
            get => _instanceDivisor;
            set => SetField(ref _instanceDivisor, value);
        }

        [YamlIgnore]
        public VoidPtr Address => _clientSideSource?.Address ?? VoidPtr.Zero;

        public bool TryGetAddress(out VoidPtr address)
        {
            if (_clientSideSource != null)
            {
                address = _clientSideSource.Address;
                return true;
            }
            address = VoidPtr.Zero;
            return false;
        }

        /// <summary>
        /// The total size in bytes of this buffer.
        /// </summary>
        [YamlIgnore]
        public uint Length
        {
            get
            {
                uint size = ElementCount * ElementSize;
                return PadEndingToVec4 ? size.Align(0x10) : size;
            }
        }

        /// <summary>
        /// The size in bytes of a single element in the buffer.
        /// </summary>
        [YamlIgnore]
        public uint ElementSize => ComponentCount * ComponentSize;

        /// <summary>
        /// The size in memory of a single component.
        /// A single element in the buffer can contain multiple components.
        /// </summary>
        [YamlIgnore]
        private uint ComponentSize
            => _componentType switch
            {
                EComponentType.SByte => sizeof(sbyte),
                EComponentType.Byte => sizeof(byte),
                EComponentType.Short => sizeof(short),
                EComponentType.UShort => sizeof(ushort),
                EComponentType.Int => sizeof(int),
                EComponentType.UInt => sizeof(uint),
                EComponentType.Float => sizeof(float),
                EComponentType.Double => sizeof(double),
                EComponentType.Struct => _componentCount,
                _ => 1,
            };

        private uint? _bindingIndexOverride;
        /// <summary>
        /// Forces a specific binding index for the mesh.
        /// </summary>
        public uint? BindingIndexOverride
        {
            get => _bindingIndexOverride;
            set => SetField(ref _bindingIndexOverride, value);
        }

        private bool _resizable = true;
        public bool Resizable
        {
            get => _resizable;
            set => SetField(ref _resizable, value);
        }

        private bool _disposeOnPush = false;
        public bool DisposeOnPush
        {
            get => _disposeOnPush;
            set => SetField(ref _disposeOnPush, value);
        }

        /// <summary>
        /// Allocates and pushes the buffer to the GPU.
        /// </summary>
        public void PushData()
            => PushDataRequested?.Invoke();

        /// <summary>
        /// Pushes the entire buffer to the GPU. Assumes the buffer has already been allocated using PushData.
        /// </summary>
        public void PushSubData()
            => PushSubData(0, Length);

        /// <summary>
        /// Pushes the a portion of the buffer to the GPU. Assumes the buffer has already been allocated using PushData.
        /// </summary>
        public void PushSubData(int offset, uint length)
            => PushSubDataRequested?.Invoke(offset, length);

        public void MapBufferData()
            => MapBufferDataRequested?.Invoke();
        public void UnmapBufferData()
            => UnmapBufferDataRequested?.Invoke();

        public void SetBlockName(XRRenderProgram program, string blockName)
            => SetBlockNameRequested?.Invoke(program, blockName);
        public void SetBlockIndex(uint blockIndex)
            => SetBlockIndexRequested?.Invoke(blockIndex);

        public void Bind()
            => BindRequested?.Invoke();
        public void Unbind()
            => UnbindRequested?.Invoke();

        public void FlushRange(int offset, uint length)
            => FlushRangeRequested?.Invoke(offset, length);
        public void Flush()
            => FlushRequested?.Invoke();

        /// <summary>
        /// Reads the struct value at the given offset into the buffer.
        /// Offset is in bytes; NOT relative to the size of the struct.
        /// </summary>
        /// <typeparam name="T">The type of value to read.</typeparam>
        /// <param name="offset">The offset into the buffer, in bytes.</param>
        /// <returns>The T value at the given offset.</returns>
        [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "<Pending>")]
        public T? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(uint offset) where T : struct
            => _clientSideSource != null ? Marshal.PtrToStructure<T>(_clientSideSource.Address + offset) : default;

        /// <summary>
        /// Writes the struct value into the buffer at the given index.
        /// This will not update the data in GPU memory unless this buffer is mapped.
        /// To update the GPU data, call PushData or PushSubData after this call.
        /// </summary>
        /// <typeparam name="T">The type of value to write.</typeparam>
        /// <param name="index">The index of the value in the buffer.</param>
        /// <param name="value">The value to write.</param>
        public void Set<T>(uint index, T value) where T : struct
        {
            if (_clientSideSource != null)
                Marshal.StructureToPtr(value, _clientSideSource.Address[index, ElementSize], true);
        }
        
        public void SetByOffset<T>(uint offset, T value) where T : struct
        {
            if (_clientSideSource != null)
                Marshal.StructureToPtr(value, _clientSideSource.Address + offset, true);
        }

        public void SetDataPointer(VoidPtr data)
        {
            if (_clientSideSource != null)
                Memory.Move(_clientSideSource.Address, data, Length);
            else
                DataPointerSet?.Invoke(data);
        }

        public void Allocate<T>(uint listCount) where T : struct
        {
            _componentCount = 1;
            
            switch (typeof(T))
            {
                case Type t when t == typeof(sbyte):
                    _componentType = EComponentType.SByte;
                    break;
                case Type t when t == typeof(byte):
                    _componentType = EComponentType.Byte;
                    break;
                case Type t when t == typeof(short):
                    _componentType = EComponentType.Short;
                    break;
                case Type t when t == typeof(ushort):
                    _componentType = EComponentType.UShort;
                    break;
                case Type t when t == typeof(int):
                    _componentType = EComponentType.Int;
                    break;
                case Type t when t == typeof(uint):
                    _componentType = EComponentType.UInt;
                    break;
                case Type t when t == typeof(float):
                    _componentType = EComponentType.Float;
                    break;
                case Type t when t == typeof(double):
                    _componentType = EComponentType.Double;
                    break;
                case Type t when t == typeof(Vector2):
                    _componentType = EComponentType.Float;
                    _componentCount = 2;
                    break;
                case Type t when t == typeof(IVector2):
                    _componentType = EComponentType.Int;
                    _componentCount = 2;
                    break;
                case Type t when t == typeof(Vector3):
                    _componentType = EComponentType.Float;
                    _componentCount = 3;
                    break;
                case Type t when t == typeof(Vector4):
                    _componentType = EComponentType.Float;
                    _componentCount = 4;
                    break;
                case Type t when t == typeof(Matrix4x4):
                    _componentType = EComponentType.Float;
                    _componentCount = 16;
                    break;
                case Type t when t == typeof(Quaternion):
                    _componentType = EComponentType.Float;
                    _componentCount = 4;
                    break;
                default:
                    throw new InvalidOperationException("Not a proper numeric data type.");
            }

            _normalize = false;
            _elementCount = listCount;
            _clientSideSource = DataSource.Allocate(Length);
        }

        public void Allocate(uint stride, uint count)
        {
            _elementCount = count;
            _componentCount = stride;
            _componentType = EComponentType.Struct;
            _normalize = false;
            _clientSideSource = DataSource.Allocate(stride * count);
        }

        public void SetDataRawAtIndex<T>(uint index, T data) where T : struct
        {
            if (_clientSideSource is null)
                throw new InvalidOperationException($"Cannot set data at index {index}: client-side buffer has not been allocated.");
            Marshal.StructureToPtr(data, _clientSideSource.Address[index, ElementSize], true);
        }
        
        public T GetDataRawAtIndex<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(uint index) where T : struct
        {
            if (_clientSideSource is null)
                throw new InvalidOperationException($"Cannot get data at index {index}: client-side buffer has not been allocated.");
            if (index >= _elementCount)
                throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} is out of range. Element count: {_elementCount}");
            return Marshal.PtrToStructure<T>(_clientSideSource.Address[index, ElementSize]);
        }

        public void SetDataArrayRawAtIndex<T>(uint index, T[] data) where T : struct
        {
            uint stride = ElementSize;
            for (uint i = 0; i < data.Length; ++i)
                Marshal.StructureToPtr(data[i], _clientSideSource!.Address[index + i, stride], true);
        }

        /// <summary>
        /// Retrieves an array of structs from the buffer starting at the given index.
        /// This method is client-side only and does not read from GPU memory.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="index"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public T[] GetDataArrayRawAtIndex<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(uint index, int count) where T : struct
        {
            T[] arr = new T[count];
            uint stride = ElementSize;
            for (uint i = 0; i < count; ++i)
                arr[i] = Marshal.PtrToStructure<T>(_clientSideSource!.Address[index + i, stride]);
            return arr;
        }

        public unsafe void SetFloat(uint index, float data)
            => ((float*)_clientSideSource!.Address.Pointer)[index] = data;
        public unsafe float GetFloat(uint index)
            => ((float*)_clientSideSource!.Address.Pointer)[index];
        public unsafe void SetFloatAtOffset(uint offset, float data)
            => ((float*)(_clientSideSource!.Address + offset).Pointer)[0] = data;
        public unsafe float GetFloatAtOffset(uint offset)
            => ((float*)(_clientSideSource!.Address + offset).Pointer)[0];

        public unsafe void SetVector2(uint index, Vector2 data)
            => ((Vector2*)_clientSideSource!.Address.Pointer)[index] = data;
        public unsafe Vector2 GetVector2(uint index)
            => ((Vector2*)_clientSideSource!.Address.Pointer)[index];
        public unsafe void SetVector2AtOffset(uint offset, Vector2 data)
            => ((Vector2*)(_clientSideSource!.Address + offset).Pointer)[0] = data;
        public unsafe Vector2 GetVector2AtOffset(uint offset)
            => ((Vector2*)(_clientSideSource!.Address + offset).Pointer)[0];

        public unsafe void SetVector3(uint index, Vector3 data)
            => ((Vector3*)_clientSideSource!.Address.Pointer)[index] = data;
        public unsafe Vector3 GetVector3(uint index)
            => ((Vector3*)_clientSideSource!.Address.Pointer)[index];
        public unsafe void SetVector3AtOffset(uint offset, Vector3 data)
            => ((Vector3*)(_clientSideSource!.Address + offset).Pointer)[0] = data;
        public unsafe Vector3 GetVector3AtOffset(uint offset)
            => ((Vector3*)(_clientSideSource!.Address + offset).Pointer)[0];

        public unsafe void SetVector4(uint index, Vector4 data)
            => ((Vector4*)_clientSideSource!.Address.Pointer)[index] = data;
        public unsafe Vector4 GetVector4(uint index)
            => ((Vector4*)_clientSideSource!.Address.Pointer)[index];
        public unsafe void SetVector4AtOffset(uint offset, Vector4 data)
            => ((Vector4*)(_clientSideSource!.Address + offset).Pointer)[0] = data;
        public unsafe Vector4 GetVector4AtOffset(uint offset)
            => ((Vector4*)(_clientSideSource!.Address + offset).Pointer)[0];

        public Remapper? SetDataRaw<T>(IEnumerable<T> items, int count, bool remap = false) where T : struct
        {
            _componentCount = 1;

            switch (typeof(T))
            {
                case Type t when t == typeof(sbyte):
                    _componentType = EComponentType.SByte;
                    break;
                case Type t when t == typeof(byte):
                    _componentType = EComponentType.Byte;
                    break;
                case Type t when t == typeof(short):
                    _componentType = EComponentType.Short;
                    break;
                case Type t when t == typeof(ushort):
                    _componentType = EComponentType.UShort;
                    break;
                case Type t when t == typeof(int):
                    _componentType = EComponentType.Int;
                    break;
                case Type t when t == typeof(uint):
                    _componentType = EComponentType.UInt;
                    break;
                case Type t when t == typeof(float):
                    _componentType = EComponentType.Float;
                    break;
                case Type t when t == typeof(double):
                    _componentType = EComponentType.Double;
                    break;
                case Type t when t == typeof(Vector2):
                    _componentType = EComponentType.Float;
                    _componentCount = 2;
                    break;
                case Type t when t == typeof(Vector3):
                    _componentType = EComponentType.Float;
                    _componentCount = 3;
                    break;
                case Type t when t == typeof(Vector4):
                    _componentType = EComponentType.Float;
                    _componentCount = 4;
                    break;
                case Type t when t == typeof(Matrix4x4):
                    _componentType = EComponentType.Float;
                    _componentCount = 16;
                    break;
                case Type t when t == typeof(Quaternion):
                    _componentType = EComponentType.Float;
                    _componentCount = 4;
                    break;
                default:
                    throw new InvalidOperationException("Not a proper numeric data type.");
            }

            _normalize = false;
            if (remap)
            {
                Remapper remapper = new();
                var arr = items as T[] ?? [.. items];
                remapper.Remap(arr, null);
                _elementCount = remapper.ImplementationLength;
                _clientSideSource = DataSource.Allocate(Length);
                uint stride = ElementSize;
                for (uint i = 0; i < remapper.ImplementationLength; ++i)
                {
                    VoidPtr addr = _clientSideSource.Address[i, stride];
                    T value = arr[remapper.ImplementationTable![i]];
                    Marshal.StructureToPtr(value, addr, true);
                }
                return remapper;
            }
            else
            {
                _elementCount = (uint)count;
                _clientSideSource = DataSource.Allocate(Length);
                uint stride = ElementSize;
                uint i = 0;
                foreach (var value in items)
                {
                    VoidPtr addr = _clientSideSource.Address[i, stride];
                    Marshal.StructureToPtr(value, addr, true);
                    i++;
                }
                return null;
            }
        }
        public Remapper? SetDataRaw<T>(IList<T> list, bool remap = false) where T : struct
        {
            _componentCount = 1;

            switch (typeof(T))
            {
                case Type t when t == typeof(sbyte):
                    _componentType = EComponentType.SByte;
                    break;
                case Type t when t == typeof(byte):
                    _componentType = EComponentType.Byte;
                    break;
                case Type t when t == typeof(short):
                    _componentType = EComponentType.Short;
                    break;
                case Type t when t == typeof(ushort):
                    _componentType = EComponentType.UShort;
                    break;
                case Type t when t == typeof(int):
                    _componentType = EComponentType.Int;
                    break;
                case Type t when t == typeof(uint):
                    _componentType = EComponentType.UInt;
                    break;
                case Type t when t == typeof(float):
                    _componentType = EComponentType.Float;
                    break;
                case Type t when t == typeof(double):
                    _componentType = EComponentType.Double;
                    break;
                case Type t when t == typeof(Vector2):
                    _componentType = EComponentType.Float;
                    _componentCount = 2;
                    break;
                case Type t when t == typeof(Vector3):
                    _componentType = EComponentType.Float;
                    _componentCount = 3;
                    break;
                case Type t when t == typeof(Vector4):
                    _componentType = EComponentType.Float;
                    _componentCount = 4;
                    break;
                case Type t when t == typeof(Matrix4x4):
                    _componentType = EComponentType.Float;
                    _componentCount = 16;
                    break;
                case Type t when t == typeof(Quaternion):
                    _componentType = EComponentType.Float;
                    _componentCount = 4;
                    break;
                default:
                    _componentType = EComponentType.Struct;
                    _componentCount = (uint)Marshal.SizeOf<T>();
                    break;
            }

            _normalize = false;
            if (remap)
            {
                Remapper remapper = new();
                remapper.Remap(list, null);
                _elementCount = remapper.ImplementationLength;
                _clientSideSource = DataSource.Allocate(Length);
                uint stride = ElementSize;
                for (uint i = 0; i < remapper.ImplementationLength; ++i)
                {
                    VoidPtr addr = _clientSideSource.Address[i, stride];
                    T value = list[remapper.ImplementationTable![i]];
                    Marshal.StructureToPtr(value, addr, true);
                }
                return remapper;
            }
            else
            {
                _elementCount = (uint)list.Count;
                _clientSideSource = DataSource.Allocate(Length);
                uint stride = ElementSize;
                for (uint i = 0; i < list.Count; ++i)
                {
                    VoidPtr addr = _clientSideSource.Address[i, stride];
                    T value = list[(int)i];
                    Marshal.StructureToPtr(value, addr, true);
                }
                return null;
            }
        }
        public Remapper? SetData<T>(IList<T> list, bool remap = false) where T : unmanaged, IBufferable
        {
            IBufferable d = default(T);
            _componentType = d.ComponentType;
            _componentCount = d.ComponentCount;
            _normalize = d.Normalize;

            if (remap)
            {
                Remapper remapper = new();
                remapper.Remap(list, null);

                _elementCount = remapper.ImplementationLength;
                _clientSideSource = DataSource.Allocate(Length);
                uint stride = ElementSize;
                for (uint i = 0; i < remapper.ImplementationLength; ++i)
                {
                    var item = list[remapper.ImplementationTable![i]];
                    item.Write(_clientSideSource.Address[i, stride]);
                }
                return remapper;
            }
            else
            {
                _elementCount = (uint)list.Count;
                _clientSideSource = DataSource.Allocate(Length);
                uint stride = ElementSize;
                for (uint i = 0; i < list.Count; ++i)
                {
                    var item = list[(int)i];
                    item.Write(_clientSideSource.Address[i, stride]);
                }
                return null;
            }
        }

        public Remapper? GetData<T>(out T[] array, bool remap = true) where T : unmanaged, IBufferable
        {
            IBufferable d = default(T);
            var componentType = d.ComponentType;
            var componentCount = d.ComponentCount;
            var normalize = d.Normalize;
            if (componentType != _componentType || componentCount != _componentCount || normalize != _normalize)
                throw new InvalidOperationException("Data type mismatch.");

            uint stride = ElementSize;
            array = new T[_elementCount];
            for (uint i = 0; i < _elementCount; ++i)
            {
                T value = default;
                value.Read(_clientSideSource!.Address[i, stride]);
                array[i] = value;
            }

            if (!remap)
                return null;

            Remapper remapper = new();
            remapper.Remap(array);
            return remapper;
        }

        public Remapper? GetDataRaw<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(out T[] array, bool remap = true) where T : struct
        {
            EComponentType componentType = EComponentType.Float;
            var componentCount = 1;
            var normalize = false;
            switch (typeof(T))
            {
                case Type t when t == typeof(sbyte):
                    componentType = EComponentType.SByte;
                    break;
                case Type t when t == typeof(byte):
                    componentType = EComponentType.Byte;
                    break;
                case Type t when t == typeof(short):
                    componentType = EComponentType.Short;
                    break;
                case Type t when t == typeof(ushort):
                    componentType = EComponentType.UShort;
                    break;
                case Type t when t == typeof(int):
                    componentType = EComponentType.Int;
                    break;
                case Type t when t == typeof(uint):
                    componentType = EComponentType.UInt;
                    break;
                case Type t when t == typeof(float):
                    //componentType = EComponentType.Float;
                    break;
                case Type t when t == typeof(double):
                    componentType = EComponentType.Double;
                    break;
                case Type t when t == typeof(Vector2):
                    //componentType = EComponentType.Float;
                    componentCount = 2;
                    break;
                case Type t when t == typeof(Vector3):
                    //componentType = EComponentType.Float;
                    componentCount = 3;
                    break;
                case Type t when t == typeof(Vector4):
                    //componentType = EComponentType.Float;
                    componentCount = 4;
                    break;
                case Type t when t == typeof(Matrix4x4):
                    //componentType = EComponentType.Float;
                    componentCount = 16;
                    break;
                case Type t when t == typeof(Quaternion):
                    //componentType = EComponentType.Float;
                    componentCount = 4;
                    break;
                default:
                    //componentType = EComponentType.Struct;
                    componentCount = Marshal.SizeOf<T>();
                    break;
            }

            if (componentType != _componentType || componentCount != _componentCount || normalize != _normalize)
                throw new InvalidOperationException("Data type mismatch.");

            uint stride = ElementSize;
            array = new T[_elementCount];
            for (uint i = 0; i < _elementCount; ++i)
                array[i] = Marshal.PtrToStructure<T>(_clientSideSource!.Address[i, stride]);
            
            if (!remap)
                return null;

            Remapper remapper = new();
            remapper.Remap(array);
            return remapper;
        }

        ~XRDataBuffer() { Dispose(false); }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private bool _disposedValue = false;
        protected void Dispose(bool disposing)
        {
            if (_disposedValue)
                return;

            //if (disposing)
            //    Destroy();

            if (_clientSideSource != null)
            {
                _clientSideSource.Dispose();
                _clientSideSource = null;
            }

            //_vaoId = 0;
            _disposedValue = true;
        }

        public XRDataBuffer Clone(bool cloneBuffer, EBufferTarget target)
        {
            XRDataBuffer clone = new(target, _integral)
            {
                _componentType = _componentType,
                _componentCount = _componentCount,
                _elementCount = _elementCount,
                _normalize = _normalize,
                _clientSideSource = cloneBuffer ? _clientSideSource?.Clone() : _clientSideSource,
            };
            return clone;
        }

        /// <summary>
        /// Resizes the buffer to the given element count.
        /// If copyData is true, the data will be copied to the new buffer.
        /// If alignClientSourceToPowerOf2 is true, the new length of the buffer will be rounded up to the next power of 2.
        /// This can help mitigate constantly resizing the actual internal buffer.
        /// Returns true if the buffer was resized, false if the buffer was already the correct size.
        /// </summary>
        /// <param name="elementCount"></param>
        /// <param name="copyData"></param>
        /// <param name="alignClientSourceToPowerOf2"></param>
        public bool Resize(uint elementCount, bool copyData = true, bool alignClientSourceToPowerOf2 = false)
        {
            if (ElementCount == elementCount)
                return false;

            uint oldLength = Length;
            ElementCount = elementCount;
            uint newLength = Length;

            if (alignClientSourceToPowerOf2)
                newLength = XRMath.NextPowerOfTwo(newLength);
            
            if (_clientSideSource?.Length == newLength)
                return false;

            DataSource newSource = DataSource.Allocate(newLength);
            uint minMatch = Math.Min(oldLength, newLength);
            if (copyData && _clientSideSource != null && minMatch > 0u)
                Memory.Move(newSource.Address, _clientSideSource.Address, minMatch);

            _clientSideSource?.Dispose();
            _clientSideSource = newSource;

            //TODO: inform api objects this changed

            return true;
        }

        public unsafe void Print()
        {
            switch (ComponentType)
            {
                case EComponentType.SByte:
                    Print<sbyte>();
                    break;
                case EComponentType.Byte:
                    Print<byte>();
                    break;
                case EComponentType.Short:
                    Print<short>();
                    break;
                case EComponentType.UShort:
                    Print<ushort>();
                    break;
                case EComponentType.Int:
                    Print<int>();
                    break;
                case EComponentType.UInt:
                    Print<uint>();
                    break;
                case EComponentType.Float:
                    Print<float>();
                    break;
                case EComponentType.Double:
                    Print<double>();
                    break;
                default:
                    Debug.Out("Unsupported data type.");
                    break;
            }
        }

        private void Print<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>() where T : struct
        {
            GetDataRaw(out T[] array);
            StringBuilder sb = new();
            foreach (T item in array)
            {
                sb.Append(item);
                sb.Append(' ');
            }
            Debug.Out(sb.ToString());
        }

        public void BindTo(XRRenderProgram program, uint index)
            => BindSSBORequested?.Invoke(program, index);

        public static implicit operator VoidPtr(XRDataBuffer b) => b.Address;
    }

    public record struct InterleavedAttribute(uint? AttribIndexOverride, string AttributeName, uint Offset, EComponentType Type, uint Count, bool Integral)
    {
        public static implicit operator (uint? attribIndexOverride, string attributeName, uint offset, EComponentType type, uint count, bool integral)(InterleavedAttribute value)
            => (value.AttribIndexOverride, value.AttributeName, value.Offset, value.Type, value.Count, value.Integral);
        public static implicit operator InterleavedAttribute((uint? attribIndexOverride, string attributeName, uint offset, EComponentType type, uint count, bool integral) value)
            => new(value.attribIndexOverride, value.attributeName, value.offset, value.type, value.count, value.integral);
    }
}