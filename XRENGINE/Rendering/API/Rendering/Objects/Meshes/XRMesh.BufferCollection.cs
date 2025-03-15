using Extensions;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;

namespace XREngine.Rendering
{
    public partial class XRMesh
    {
        private bool _maxBlendshapeAccumulation = false;
        public bool MaxBlendshapeAccumulation
        {
            get => _maxBlendshapeAccumulation;
            set => SetField(ref _maxBlendshapeAccumulation, value);
        }

        private bool _supportsBillboarding = false;
        /// <summary>
        /// If true, the vertex shader will include billboarding code.
        /// Use the 'BillboardMode' engine uniform to set the type of billboarding.
        /// </summary>
        public bool SupportsBillboarding
        {
            get => _supportsBillboarding;
            set => SetField(ref _supportsBillboarding, value);
        }
        public bool HasNormals => NormalsBuffer != null || (Interleaved && NormalOffset.HasValue);
        public bool HasTangents => TangentsBuffer != null || (Interleaved && TangentOffset.HasValue);
        public bool HasColors => ColorCount > 0;
        public bool HasTexCoords => TexCoordCount > 0;

        public class BufferCollection : XRBase, IEventDictionary<string, XRDataBuffer>
        {
            private EventDictionary<string, XRDataBuffer> _buffers = [];

            //public XRDataBuffer? this[string bindingName]
            //{
            //    get => _buffers.TryGetValue(bindingName, out XRDataBuffer? buffer) ? buffer : null;
            //    set
            //    {
            //        if (value is null)
            //            _buffers.Remove(bindingName);
            //        else if (!_buffers.TryAdd(bindingName, value))
            //            _buffers[bindingName] = value;
            //    }
            //}

            public void RemoveBuffer(string name)
            {
                if (_buffers is null)
                    return;

                if (_buffers.TryGetValue(name, out XRDataBuffer? buffer))
                {
                    _buffers.Remove(name);
                    buffer.Dispose();
                }
            }

            public XRDataBuffer SetBufferRaw<T>(
                IList<T> bufferData,
                string bindingName,
                bool remap = false,
                bool integral = false,
                bool isMapped = false,
                uint instanceDivisor = 0,
                EBufferTarget target = EBufferTarget.ArrayBuffer) where T : struct
            {
                XRDataBuffer buffer = new(bindingName, target, integral)
                {
                    InstanceDivisor = instanceDivisor,
                    Mapped = isMapped,
                };
                AddOrUpdateBufferRaw(
                    bufferData,
                    bindingName,
                    remap,
                    instanceDivisor,
                    buffer);
                return buffer;
            }

            public XRDataBuffer SetBuffer<T>(
                IList<T> bufferData,
                string bindingName,
                bool remap = false,
                bool integral = false,
                bool isMapped = false,
                uint instanceDivisor = 0,
                EBufferTarget target = EBufferTarget.ArrayBuffer) where T : unmanaged, IBufferable
            {
                _buffers ??= [];
                XRDataBuffer buffer = new(bindingName, target, integral)
                {
                    InstanceDivisor = instanceDivisor,
                    Mapped = isMapped
                };
                AddOrUpdateBuffer(bufferData, bindingName, remap, instanceDivisor, buffer);
                return buffer;
            }

            public Remapper? GetBuffer<T>(string bindingName, out T[]? array, bool remap = false) where T : unmanaged, IBufferable
            {
                array = null;
                return _buffers.TryGetValue(bindingName, out var buffer) ? buffer.GetData(out array, remap) : null;
            }

            public Remapper? GetBufferRaw<T>(string bindingName, out T[]? array, bool remap = false) where T : struct
            {
                array = null;
                return _buffers.TryGetValue(bindingName, out var buffer) ? buffer.GetDataRaw(out array, remap) : null;
            }

            private void AddOrUpdateBufferRaw<T>(IList<T> bufferData, string bindingName, bool remap, uint instanceDivisor, XRDataBuffer buffer) where T : struct
            {
                if (!_buffers.TryAdd(bindingName, buffer))
                    _buffers[bindingName] = buffer;

                var remapper = buffer.SetDataRaw(bufferData, remap);
                if (buffer.Target == EBufferTarget.ArrayBuffer)
                    UpdateFaceIndices?.Invoke(bufferData.Count, bindingName, remap, instanceDivisor, remapper);
            }

            private void AddOrUpdateBuffer<T>(IList<T> bufferData, string bindingName, bool remap, uint instanceDivisor, XRDataBuffer buffer) where T : unmanaged, IBufferable
            {
                if (!_buffers.TryAdd(bindingName, buffer))
                    _buffers[bindingName] = buffer;

                var remapper = buffer.SetDataRaw(bufferData, remap);
                if (buffer.Target == EBufferTarget.ArrayBuffer)
                    UpdateFaceIndices?.Invoke(bufferData.Count, bindingName, remap, instanceDivisor, remapper);
            }

            public delegate void DelUpdateFaceIndices(int count, string bindingName, bool remap, uint instanceDivisor, Remapper? remapper);
            public event DelUpdateFaceIndices? UpdateFaceIndices;

            public event EventDictionary<string, XRDataBuffer>.DelAdded? Added
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Added += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Added -= value;
            }

            public event EventDictionary<string, XRDataBuffer>.DelCleared? Cleared
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Cleared += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Cleared -= value;
            }

            public event EventDictionary<string, XRDataBuffer>.DelRemoved? Removed
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Removed += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Removed -= value;
            }

            public event EventDictionary<string, XRDataBuffer>.DelSet? Set
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Set += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Set -= value;
            }

            public event Action? Changed
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Changed += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Changed -= value;
            }

            public void Add(string key, XRDataBuffer value) => ((IDictionary<string, XRDataBuffer>)_buffers).Add(key, value);
            public bool ContainsKey(string key) => ((IDictionary<string, XRDataBuffer>)_buffers).ContainsKey(key);
            public bool Remove(string key) => ((IDictionary<string, XRDataBuffer>)_buffers).Remove(key);
            public bool TryGetValue(string key, [MaybeNullWhen(false)] out XRDataBuffer value) => ((IDictionary<string, XRDataBuffer>)_buffers).TryGetValue(key, out value);
            public void Add(KeyValuePair<string, XRDataBuffer> item) => ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Add(item);
            public void Clear() => ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Clear();
            public bool Contains(KeyValuePair<string, XRDataBuffer> item) => ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Contains(item);
            public void CopyTo(KeyValuePair<string, XRDataBuffer>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).CopyTo(array, arrayIndex);
            public bool Remove(KeyValuePair<string, XRDataBuffer> item) => ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Remove(item);
            public void Add(object key, object? value) => ((IDictionary)_buffers).Add(key, value);
            public bool Contains(object key) => ((IDictionary)_buffers).Contains(key);
            public IDictionaryEnumerator GetEnumerator() => ((IDictionary)_buffers).GetEnumerator();
            public void Remove(object key) => ((IDictionary)_buffers).Remove(key);
            public void CopyTo(Array array, int index) => ((ICollection)_buffers).CopyTo(array, index);
            public void GetObjectData(SerializationInfo info, StreamingContext context) => ((ISerializable)_buffers).GetObjectData(info, context);
            public void OnDeserialization(object? sender) => ((IDeserializationCallback)_buffers).OnDeserialization(sender);
            IEnumerator<KeyValuePair<string, XRDataBuffer>> IEnumerable<KeyValuePair<string, XRDataBuffer>>.GetEnumerator() => ((IEnumerable<KeyValuePair<string, XRDataBuffer>>)_buffers).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_buffers).GetEnumerator();
            public EventDictionary<string, XRDataBuffer> Buffers
            {
                get => _buffers;
                set => SetField(ref _buffers, value);
            }
            public ICollection<string> Keys => ((IDictionary<string, XRDataBuffer>)_buffers).Keys;
            public ICollection<XRDataBuffer> Values => ((IDictionary<string, XRDataBuffer>)_buffers).Values;
            public int Count => ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Count;
            public bool IsReadOnly => ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).IsReadOnly;
            public bool IsFixedSize => ((IDictionary)_buffers).IsFixedSize;
            public bool IsSynchronized => ((ICollection)_buffers).IsSynchronized;
            public object SyncRoot => ((ICollection)_buffers).SyncRoot;
            IEnumerable<string> IReadOnlyDictionary<string, XRDataBuffer>.Keys => ((IReadOnlyDictionary<string, XRDataBuffer>)_buffers).Keys;
            IEnumerable<XRDataBuffer> IReadOnlyDictionary<string, XRDataBuffer>.Values => ((IReadOnlyDictionary<string, XRDataBuffer>)_buffers).Values;
            XRDataBuffer IReadOnlyDictionary<string, XRDataBuffer>.this[string key] => ((IReadOnlyDictionary<string, XRDataBuffer>)_buffers)[key];
            public XRDataBuffer this[string key] { get => ((IDictionary<string, XRDataBuffer>)_buffers)[key]; set => ((IDictionary<string, XRDataBuffer>)_buffers)[key] = value; }
            public object? this[object key] { get => ((IDictionary)_buffers)[key]; set => ((IDictionary)_buffers)[key] = value; }
        }

        public bool GetBlendshapeIndex(string name, out uint index)
        {
            index = 0;
            int i = BlendshapeNames.IndexOf(name);
            if (i == -1)
                return false;
            index = (uint)i;
            return true;
        }
    }
}
