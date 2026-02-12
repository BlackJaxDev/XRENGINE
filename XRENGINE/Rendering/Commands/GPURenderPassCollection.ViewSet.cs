using System;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        private const int ViewConstantsRingSize = 3;
        private XRDataBuffer? _viewDescriptorBuffer;
        private XRDataBuffer? _viewConstantsBuffer;
        private readonly XRDataBuffer?[] _viewConstantsRing = new XRDataBuffer?[ViewConstantsRingSize];
        private int _viewConstantsRingIndex = -1;
        private XRDataBuffer? _commandViewMaskBuffer;
        private XRDataBuffer? _perViewVisibleIndicesBuffer;
        private XRDataBuffer? _perViewDrawCountBuffer;
        private GPUViewDescriptor[] _cachedViewDescriptors = Array.Empty<GPUViewDescriptor>();
        private uint _cachedViewDescriptorCount;

        private uint _viewSetCapacity;
        private uint _perViewVisibleCapacity;
        private uint _activeViewCount;
        private uint _indirectSourceViewId;
        private uint _commandViewMaskPreparedCount;
        private GPUViewMask _activeCommandViewMask;

        public XRDataBuffer? ViewDescriptorBuffer => _viewDescriptorBuffer;
        public XRDataBuffer? ViewConstantsBuffer => _viewConstantsBuffer;
        public XRDataBuffer? CommandViewMaskBuffer => _commandViewMaskBuffer;
        public XRDataBuffer? PerViewVisibleIndicesBuffer => _perViewVisibleIndicesBuffer;
        public XRDataBuffer? PerViewDrawCountBuffer => _perViewDrawCountBuffer;

        public uint ViewSetCapacity => _viewSetCapacity;
        public uint PerViewVisibleCapacity => _perViewVisibleCapacity;
        public uint ActiveViewCount => _activeViewCount;
        public uint IndirectSourceViewId => _indirectSourceViewId;

        public void ConfigureViewSet(ReadOnlySpan<GPUViewDescriptor> descriptors, ReadOnlySpan<GPUViewConstants> constants)
        {
            if (descriptors.Length != constants.Length)
            {
                throw new ArgumentException(
                    $"View descriptor and constant counts must match ({descriptors.Length} vs {constants.Length}).");
            }

            uint requestedViewCount = (uint)descriptors.Length;
            if (requestedViewCount > GPUViewSetLayout.AbsoluteMaxViewCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(descriptors),
                    $"Requested view count {requestedViewCount} exceeds max supported {GPUViewSetLayout.AbsoluteMaxViewCount}.");
            }

            using (_lock.EnterScope())
            {
                if (requestedViewCount == 0u)
                {
                    _activeViewCount = 0u;
                    _indirectSourceViewId = 0u;
                    ResetPerViewDrawCounts(0u);
                    return;
                }

                uint commandCapacity = Math.Max(_lastMaxCommands, GPUScene.MinCommandCount);
                EnsureViewSetBuffers(commandCapacity);
                EnsureViewCapacity(requestedViewCount);
                EnsurePerViewVisibleIndicesCapacity(commandCapacity, requestedViewCount);

                _viewConstantsRingIndex = (_viewConstantsRingIndex + 1) % ViewConstantsRingSize;
                _viewConstantsBuffer = _viewConstantsRing[_viewConstantsRingIndex];
                if (_viewConstantsBuffer is null)
                {
                    EnsureViewConstantsRingCapacity(requestedViewCount);
                    _viewConstantsBuffer = _viewConstantsRing[_viewConstantsRingIndex];
                }

                for (uint i = 0; i < requestedViewCount; ++i)
                {
                    _viewDescriptorBuffer!.SetDataRawAtIndex(i, descriptors[(int)i]);
                    _viewConstantsBuffer!.SetDataRawAtIndex(i, constants[(int)i]);
                }

                EnsureCachedViewDescriptorCapacity(requestedViewCount);
                for (uint i = 0; i < requestedViewCount; ++i)
                    _cachedViewDescriptors[i] = descriptors[(int)i];
                _cachedViewDescriptorCount = requestedViewCount;

                _viewDescriptorBuffer!.PushSubData(0, requestedViewCount * _viewDescriptorBuffer.ElementSize);
                _viewConstantsBuffer!.PushSubData(0, requestedViewCount * _viewConstantsBuffer.ElementSize);

                _activeViewCount = requestedViewCount;
                _indirectSourceViewId = Math.Min(_indirectSourceViewId, requestedViewCount - 1u);
                _activeCommandViewMask = GPUViewMask.FromViewCount(requestedViewCount);
                ResetPerViewDrawCounts(requestedViewCount);
            }
        }

        internal void SetIndirectSourceViewId(uint viewId)
        {
            if (_activeViewCount == 0u)
            {
                _indirectSourceViewId = 0u;
                return;
            }

            _indirectSourceViewId = Math.Min(viewId, _activeViewCount - 1u);
        }

        internal void PrepareCommandViewMasks(XRDataBuffer sourceCommands, uint commandCount)
        {
            if (_commandViewMaskBuffer is null)
                return;

            uint safeCount = Math.Min(commandCount, _commandViewMaskBuffer.ElementCount);
            if (safeCount == 0u)
            {
                _commandViewMaskPreparedCount = 0u;
                return;
            }

            if (_activeViewCount == 0u || _cachedViewDescriptorCount == 0u)
            {
                GPUViewMask targetMask = GPUViewMask.AllVisible;
                if (_commandViewMaskPreparedCount >= safeCount &&
                    _activeCommandViewMask.BitsLo == targetMask.BitsLo &&
                    _activeCommandViewMask.BitsHi == targetMask.BitsHi)
                {
                    return;
                }

                FillCommandViewMaskRange(0u, safeCount, targetMask);
                _commandViewMaskPreparedCount = safeCount;
                _activeCommandViewMask = targetMask;
                return;
            }

            for (uint commandIndex = 0u; commandIndex < safeCount; ++commandIndex)
            {
                GPUIndirectRenderCommand command = sourceCommands.GetDataRawAtIndex<GPUIndirectRenderCommand>(commandIndex);
                GPUViewMask commandMask = BuildCommandViewMask(command.RenderPass);
                _commandViewMaskBuffer.SetDataRawAtIndex(commandIndex, commandMask);
            }

            uint byteLength = safeCount * _commandViewMaskBuffer.ElementSize;
            _commandViewMaskBuffer.PushSubData(0, byteLength);

            _commandViewMaskPreparedCount = safeCount;
            _activeCommandViewMask = default;
        }

        private GPUViewMask BuildCommandViewMask(uint commandRenderPass)
        {
            uint maxViews = Math.Min(_activeViewCount, _cachedViewDescriptorCount);
            if (maxViews == 0u)
                return GPUViewMask.AllVisible;

            uint bitsLo = 0u;
            uint bitsHi = 0u;
            for (uint viewId = 0u; viewId < maxViews; ++viewId)
            {
                if (!ViewAcceptsRenderPass(_cachedViewDescriptors[viewId], commandRenderPass))
                    continue;

                if (viewId < 32u)
                    bitsLo |= 1u << (int)viewId;
                else
                    bitsHi |= 1u << (int)(viewId - 32u);
            }

            return new GPUViewMask(bitsLo, bitsHi);
        }

        private static bool ViewAcceptsRenderPass(in GPUViewDescriptor descriptor, uint commandRenderPass)
        {
            if (commandRenderPass == uint.MaxValue)
                return true;

            if (commandRenderPass < 32u)
                return (descriptor.RenderPassMaskLo & (1u << (int)commandRenderPass)) != 0u;

            if (commandRenderPass < 64u)
                return (descriptor.RenderPassMaskHi & (1u << (int)(commandRenderPass - 32u))) != 0u;

            return true;
        }

        private void EnsureCachedViewDescriptorCapacity(uint requestedCount)
        {
            if (_cachedViewDescriptors.Length >= requestedCount)
                return;

            Array.Resize(ref _cachedViewDescriptors, (int)requestedCount);
        }

        public uint ReadPerViewDrawCount(uint viewId)
        {
            if (_perViewDrawCountBuffer is null || viewId >= _perViewDrawCountBuffer.ElementCount)
                return 0u;

            return ReadUIntAt(_perViewDrawCountBuffer, viewId);
        }

        internal void EnsureViewSetBuffers(uint commandCapacity)
        {
            uint safeCommandCapacity = Math.Max(commandCapacity, 1u);

            EnsureViewCapacity(GPUViewSetLayout.DefaultMaxViewCount);
            EnsureCommandViewMaskCapacity(safeCommandCapacity);
            EnsurePerViewVisibleIndicesCapacity(safeCommandCapacity, _viewSetCapacity);

            if (_activeViewCount == 0u)
                _activeViewCount = 1u;
        }

        internal void BindViewSetBuffers(XRRenderProgram shader)
        {
            _viewDescriptorBuffer?.BindTo(shader, GPUViewSetBindings.ViewDescriptorBuffer);
            _viewConstantsBuffer?.BindTo(shader, GPUViewSetBindings.ViewConstantsBuffer);
            _commandViewMaskBuffer?.BindTo(shader, GPUViewSetBindings.CommandViewMaskBuffer);
            _perViewVisibleIndicesBuffer?.BindTo(shader, GPUViewSetBindings.PerViewVisibleIndicesBuffer);
            _perViewDrawCountBuffer?.BindTo(shader, GPUViewSetBindings.PerViewDrawCountBuffer);
        }

        private void EnsureViewCapacity(uint requestedViewCapacity)
        {
            requestedViewCapacity = GPUViewSetLayout.ClampViewCount(requestedViewCapacity);

            if (_viewDescriptorBuffer is null)
            {
                _viewDescriptorBuffer = CreateStructBuffer(
                    "ViewDescriptorBuffer",
                    requestedViewCapacity,
                    GPUViewSetLayout.ViewDescriptorSize,
                    GPUViewSetBindings.ViewDescriptorBuffer);
                ZeroStructRange<GPUViewDescriptor>(_viewDescriptorBuffer, 0u, requestedViewCapacity);
            }
            else if (_viewDescriptorBuffer.ElementCount < requestedViewCapacity)
            {
                uint old = _viewDescriptorBuffer.ElementCount;
                _viewDescriptorBuffer.Resize(requestedViewCapacity);
                ZeroStructRange<GPUViewDescriptor>(_viewDescriptorBuffer, old, requestedViewCapacity - old);
            }

            EnsureViewConstantsRingCapacity(requestedViewCapacity);
            if (_viewConstantsBuffer is null)
                _viewConstantsBuffer = _viewConstantsRing[0];

            if (_perViewDrawCountBuffer is null)
            {
                _perViewDrawCountBuffer = CreateUIntBuffer(
                    "PerViewDrawCountBuffer",
                    requestedViewCapacity,
                    GPUViewSetBindings.PerViewDrawCountBuffer,
                    false);
                ZeroUIntRange(_perViewDrawCountBuffer, 0u, requestedViewCapacity);
            }
            else if (_perViewDrawCountBuffer.ElementCount < requestedViewCapacity)
            {
                uint old = _perViewDrawCountBuffer.ElementCount;
                _perViewDrawCountBuffer.Resize(requestedViewCapacity);
                ZeroUIntRange(_perViewDrawCountBuffer, old, requestedViewCapacity - old);
            }

            _viewSetCapacity = Math.Max(_viewSetCapacity, requestedViewCapacity);
        }

        private void EnsureViewConstantsRingCapacity(uint requestedViewCapacity)
        {
            for (int i = 0; i < ViewConstantsRingSize; i++)
            {
                if (_viewConstantsRing[i] is null)
                {
                    _viewConstantsRing[i] = CreateStructBuffer(
                        $"ViewConstantsBuffer.Ring{i}",
                        requestedViewCapacity,
                        GPUViewSetLayout.ViewConstantsSize,
                        GPUViewSetBindings.ViewConstantsBuffer);
                    ZeroStructRange<GPUViewConstants>(_viewConstantsRing[i]!, 0u, requestedViewCapacity);
                }
                else if (_viewConstantsRing[i]!.ElementCount < requestedViewCapacity)
                {
                    uint old = _viewConstantsRing[i]!.ElementCount;
                    _viewConstantsRing[i]!.Resize(requestedViewCapacity);
                    ZeroStructRange<GPUViewConstants>(_viewConstantsRing[i]!, old, requestedViewCapacity - old);
                }
            }
        }

        private void EnsureCommandViewMaskCapacity(uint commandCapacity)
        {
            if (_commandViewMaskBuffer is null)
            {
                _commandViewMaskBuffer = CreateStructBuffer(
                    "CommandViewMaskBuffer",
                    commandCapacity,
                    GPUViewSetLayout.ViewMaskSize,
                    GPUViewSetBindings.CommandViewMaskBuffer);
                FillCommandViewMaskRange(0u, commandCapacity, GPUViewMask.AllVisible);
                return;
            }

            if (_commandViewMaskBuffer.ElementCount >= commandCapacity)
                return;

            uint oldCapacity = _commandViewMaskBuffer.ElementCount;
            _commandViewMaskBuffer.Resize(commandCapacity);
            FillCommandViewMaskRange(oldCapacity, commandCapacity - oldCapacity, GPUViewMask.AllVisible);
        }

        private void EnsurePerViewVisibleIndicesCapacity(uint commandCapacity, uint viewCapacity)
        {
            uint requested = GPUViewSetLayout.ComputePerViewVisibleCapacity(commandCapacity, viewCapacity);
            if (requested == 0u)
                requested = 1u;

            if (_perViewVisibleIndicesBuffer is null)
            {
                _perViewVisibleIndicesBuffer = CreateUIntBuffer(
                    "PerViewVisibleIndicesBuffer",
                    requested,
                    GPUViewSetBindings.PerViewVisibleIndicesBuffer,
                    true);
                ZeroUIntRange(_perViewVisibleIndicesBuffer, 0u, requested);
                _perViewVisibleCapacity = requested;
                return;
            }

            if (_perViewVisibleIndicesBuffer.ElementCount >= requested)
            {
                _perViewVisibleCapacity = _perViewVisibleIndicesBuffer.ElementCount;
                return;
            }

            uint old = _perViewVisibleIndicesBuffer.ElementCount;
            _perViewVisibleIndicesBuffer.Resize(requested);
            ZeroUIntRange(_perViewVisibleIndicesBuffer, old, requested - old);
            _perViewVisibleCapacity = requested;
        }

        private static XRDataBuffer CreateStructBuffer(string name, uint elementCount, uint strideBytes, int bindingIndex)
        {
            var buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Struct, strideBytes, false, false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
                BindingIndexOverride = (uint)bindingIndex,
                PadEndingToVec4 = false
            };
            buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
            buffer.Generate();
            return buffer;
        }

        private static XRDataBuffer CreateUIntBuffer(string name, uint elementCount, int bindingIndex, bool resizable)
        {
            var buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = resizable,
                BindingIndexOverride = (uint)bindingIndex,
                PadEndingToVec4 = false
            };
            buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage;
            buffer.Generate();
            return buffer;
        }

        private static void ZeroStructRange<T>(XRDataBuffer buffer, uint startIndex, uint count) where T : struct
        {
            if (count == 0u)
                return;

            T blank = default;
            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                buffer.SetDataRawAtIndex(i, blank);

            uint byteOffset = startIndex * buffer.ElementSize;
            uint byteLength = count * buffer.ElementSize;
            buffer.PushSubData((int)byteOffset, byteLength);
        }

        private static void ZeroUIntRange(XRDataBuffer buffer, uint startIndex, uint count)
        {
            if (count == 0u)
                return;

            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                buffer.SetDataRawAtIndex(i, 0u);

            uint byteOffset = startIndex * sizeof(uint);
            uint byteLength = count * sizeof(uint);
            buffer.PushSubData((int)byteOffset, byteLength);
        }

        private void FillCommandViewMaskRange(uint startIndex, uint count, GPUViewMask mask)
        {
            if (_commandViewMaskBuffer is null || count == 0u)
                return;

            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                _commandViewMaskBuffer.SetDataRawAtIndex(i, mask);

            uint byteOffset = startIndex * _commandViewMaskBuffer.ElementSize;
            uint byteLength = count * _commandViewMaskBuffer.ElementSize;
            _commandViewMaskBuffer.PushSubData((int)byteOffset, byteLength);
        }

        private void ResetPerViewDrawCounts(uint activeViewCount)
        {
            if (_perViewDrawCountBuffer is null || _viewSetCapacity == 0u)
                return;

            uint resetCount = activeViewCount == 0u
                ? _viewSetCapacity
                : Math.Min(activeViewCount, _viewSetCapacity);

            ZeroUIntRange(_perViewDrawCountBuffer, 0u, resetCount);
        }

        private void DisposeViewSetBuffers()
        {
            _viewDescriptorBuffer?.Dispose();
            for (int i = 0; i < ViewConstantsRingSize; i++)
            {
                _viewConstantsRing[i]?.Dispose();
                _viewConstantsRing[i] = null;
            }
            _commandViewMaskBuffer?.Dispose();
            _perViewVisibleIndicesBuffer?.Dispose();
            _perViewDrawCountBuffer?.Dispose();

            _viewDescriptorBuffer = null;
            _viewConstantsBuffer = null;
            _viewConstantsRingIndex = -1;
            _commandViewMaskBuffer = null;
            _perViewVisibleIndicesBuffer = null;
            _perViewDrawCountBuffer = null;

            _viewSetCapacity = 0u;
            _perViewVisibleCapacity = 0u;
            _activeViewCount = 0u;
            _indirectSourceViewId = 0u;
            _commandViewMaskPreparedCount = 0u;
            _activeCommandViewMask = default;
            _cachedViewDescriptorCount = 0u;
        }
    }
}
