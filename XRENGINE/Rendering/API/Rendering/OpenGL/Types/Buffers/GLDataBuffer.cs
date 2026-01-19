using Extensions;
using Silk.NET.OpenGL;
using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using System.Linq;
using System.Collections.Concurrent;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLDataBuffer(OpenGLRenderer renderer, XRDataBuffer buffer) : GLObject<XRDataBuffer>(renderer, buffer), IApiDataBuffer
        {
            private static readonly ConcurrentDictionary<string, byte> _missingInterleavedLogs = new();

            /// <summary>
            /// Tracks the currently allocated GPU memory size for this buffer in bytes.
            /// </summary>
            private long _allocatedVRAMBytes = 0;

            protected override void UnlinkData()
            {
                Data.PushDataRequested -= PushData;
                Data.PushSubDataRequested -= PushSubData;
                Data.FlushRequested -= Flush;
                Data.FlushRangeRequested -= FlushRange;
                Data.SetBlockNameRequested -= SetUniformBlockName;
                Data.SetBlockIndexRequested -= SetBlockIndex;
                Data.BindRequested -= Bind;
                Data.UnbindRequested -= Unbind;
                Data.MapBufferDataRequested -= MapBufferData;
                Data.UnmapBufferDataRequested -= UnmapBufferData;
                Data.BindSSBORequested -= BindSSBO;
            }
            private static bool IsGpuBufferLoggingEnabled()
                => Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;

            protected override void LinkData()
            {
                Data.PushDataRequested += PushData;
                Data.PushSubDataRequested += PushSubData;
                Data.FlushRequested += Flush;
                Data.FlushRangeRequested += FlushRange;
                Data.SetBlockNameRequested += SetUniformBlockName;
                Data.SetBlockIndexRequested += SetBlockIndex;
                Data.BindRequested += Bind;
                Data.UnbindRequested += Unbind;
                Data.MapBufferDataRequested += MapBufferData;
                Data.UnmapBufferDataRequested += UnmapBufferData;
                Data.BindSSBORequested += BindSSBO;
            }

            public override EGLObjectType Type => EGLObjectType.Buffer;

            protected internal override void PostGenerated()
            {
                var rend = Renderer.ActiveMeshRenderer;
                // If a mesh renderer is active, bind attributes now; otherwise just allocate storage so future PushSubData works.
                if (rend is not null && Data.Target == EBufferTarget.ArrayBuffer)
                {
                    BindToRenderer(rend.GetVertexProgram(), rend);
                }
                else if (rend is null && Data.Target == EBufferTarget.ArrayBuffer && IsGpuBufferLoggingEnabled())
                {
                    // Suppress noisy warning; atlas / generic buffers can legitimately be created before a renderer binds them.
                    Debug.Out($"{GetDescribingName()} generated (no active mesh renderer yet) � delaying attribute binding.");
                }

                if (Data.Resizable)
                {
                    // Use dynamic mutable allocation path.
                    PushData();
                }
                else
                {
                    AllocateImmutable();
                    _lastPushedLength = Data.Length;
                }
            }

            private string RangeFlagsString() => Data.RangeFlags.ToString();
            private string StorageFlagsString() => Data.StorageFlags.ToString();
            private string BufferNameOrTarget() => string.IsNullOrWhiteSpace(Data.AttributeName) ? Data.Target.ToString() : Data.AttributeName;

            public void BindToRenderer(GLRenderProgram vertexProgram, GLMeshRenderer? arrayBufferLink, bool pushDataNow = true)
            {
                try
                {
                    BindV2(vertexProgram, arrayBufferLink);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, "Error binding buffer.");
                }
            }

            public bool TryGetAttributeLocation(GLRenderProgram vertexProgram, out uint layoutLocation)
            {
                layoutLocation = GetAttributeLocation(vertexProgram, Data.AttributeName ?? string.Empty);
                return layoutLocation != uint.MaxValue;
            }

            private void BindV2(GLRenderProgram vertexProgram, GLMeshRenderer? arrayBufferLink)
            {
                if (vertexProgram is null)
                {
                    Debug.LogWarning("[GLDataBuffer] Cannot bind buffer without an active GLRenderProgram.");
                    return;
                }

                switch (Data.Target)
                {
                    case EBufferTarget.ArrayBuffer:
                        {
                            uint vaoId = arrayBufferLink?.BindingId ?? 0;
                            if (vaoId == 0)
                            {
                                Debug.LogWarning($"Failed to bind buffer {GetDescribingName()} to mesh renderer.");
                                return;
                            }

                            uint bindingIndex = uint.MaxValue;
                            if (Data.BindingIndexOverride.HasValue)
                                bindingIndex = Data.BindingIndexOverride.Value;
                            else if (!TryGetAttributeLocation(vertexProgram, out bindingIndex))
                            {
                                string programName = vertexProgram.Data?.Name ?? "<unnamed>";
                                Debug.Out($"[GLDataBuffer] Attribute '{Data.AttributeName}' missing in program '{programName}' while binding buffer '{GetDescribingName()}'.");
                                return;
                            }

                            if (Data.InterleavedAttributes.Length > 0)
                            {
                                // Handle interleaved vertex attributes
                                foreach (var (attribIndexOverride, name, offset, componentType, componentCount, integral) in Data.InterleavedAttributes)
                                {
                                    uint attribIndex = attribIndexOverride ?? GetAttributeLocation(vertexProgram, name);
                                    if (attribIndex != uint.MaxValue)
                                    {
                                        Api.EnableVertexArrayAttrib(vaoId, attribIndex);
                                        Api.VertexArrayBindingDivisor(vaoId, attribIndex, Data.InstanceDivisor);
                                        Api.VertexArrayAttribBinding(vaoId, attribIndex, bindingIndex); // Use same binding point
                                        if (integral)
                                            Api.VertexArrayAttribIFormat(vaoId, attribIndex, (int)componentCount, GLEnum.Byte + (int)componentType, offset);
                                        else
                                            Api.VertexArrayAttribFormat(vaoId, attribIndex, (int)componentCount, GLEnum.Byte + (int)componentType, Data.Normalize, offset);
                                    }
                                    else
                                    {
                                        string programName = vertexProgram.Data?.Name ?? "<unnamed>";
                                        string shaderNames = vertexProgram.Data?.Shaders is { Count: > 0 }
                                            ? string.Join(", ", vertexProgram.Data.Shaders.Select(s => s?.Name ?? s?.FilePath ?? "<unnamed>"))
                                            : "<no shaders>";
                                        string key = $"{vertexProgram.BindingId}:{programName}:{name}:{GetDescribingName()}";
                                        if (_missingInterleavedLogs.TryAdd(key, 0))
                                            Debug.Out($"[GLDataBuffer] Interleaved attribute '{name}' missing in program '{programName}' (id {vertexProgram.BindingId}) for buffer '{GetDescribingName()}'. Shaders: [{shaderNames}]");
                                    }
                                }
                                // Bind the interleaved buffer once
                                Api.VertexArrayVertexBuffer(vaoId, bindingIndex, BindingId, 0, Data.ElementSize);
                            }
                            else
                            {
                                int componentType = (int)Data.ComponentType;
                                uint componentCount = Data.ComponentCount;
                                bool integral = Data.Integral;

                                // Original non-interleaved path
                                Api.EnableVertexArrayAttrib(vaoId, bindingIndex);
                                Api.VertexArrayBindingDivisor(vaoId, bindingIndex, Data.InstanceDivisor);
                                Api.VertexArrayAttribBinding(vaoId, bindingIndex, bindingIndex);
                                if (integral)
                                    Api.VertexArrayAttribIFormat(vaoId, bindingIndex, (int)componentCount, GLEnum.Byte + componentType, 0);
                                else
                                    Api.VertexArrayAttribFormat(vaoId, bindingIndex, (int)componentCount, GLEnum.Byte + componentType, Data.Normalize, 0);
                                Api.VertexArrayVertexBuffer(vaoId, bindingIndex, BindingId, 0, Data.ElementSize);
                            }
                        }
                        break;
                    case EBufferTarget.ShaderStorageBuffer:
                    case EBufferTarget.UniformBuffer:
                        {
                            uint bindingIndex = uint.MaxValue;
                            if (Data.BindingIndexOverride.HasValue)
                                bindingIndex = Data.BindingIndexOverride.Value;
                            else if (!TryGetAttributeLocation(vertexProgram, out bindingIndex))
                            {
                                return;
                            }

                            Bind();
                            Api.BindBufferBase(ToGLEnum(Data.Target), bindingIndex, BindingId);
                            Unbind();
                            break;
                        }
                }
            }

            private uint GetAttributeLocation(GLRenderProgram vertexProgram, string attributeName)
            {
                uint index = 0u;

                if (string.IsNullOrWhiteSpace(attributeName))
                {
                    Debug.LogWarning($"{GetDescribingName()} has no attribute name.");
                    return uint.MaxValue;
                }

                switch (Data.Target)
                {
                    case EBufferTarget.ArrayBuffer:
                        int location = vertexProgram.GetAttributeLocation(attributeName);
                        if (location >= 0)
                            index = (uint)location;
                        else
                            return uint.MaxValue;
                        break;
                    case EBufferTarget.ShaderStorageBuffer:
                        index = Data.BindingIndexOverride ?? Api.GetProgramResourceIndex(vertexProgram.BindingId, GLEnum.ShaderStorageBlock, attributeName);
                        break;
                    case EBufferTarget.UniformBuffer:
                        index = Data.BindingIndexOverride ?? Api.GetProgramResourceIndex(vertexProgram.BindingId, GLEnum.UniformBlock, attributeName);
                        break;
                    default:
                        return uint.MaxValue;
                }

                return index;
            }

            private uint _lastPushedLength = 0u;

            /// <summary>
            /// Allocates and pushes the buffer to the GPU.
            /// </summary>
            public void PushData()
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;

                if (Engine.InvokeOnMainThread(PushData, "GLDataBuffer.PushData"))
                    return;

                // Track VRAM deallocation of previous buffer if any
                if (_allocatedVRAMBytes > 0)
                {
                    Engine.Rendering.Stats.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                void* addr = (Data.TryGetAddress(out var address) ? address : VoidPtr.Zero).Pointer;
                Api.NamedBufferData(BindingId, Data.Length, addr, ToGLEnum(Data.Usage));
                _lastPushedLength = Data.Length;

                // Track VRAM allocation
                _allocatedVRAMBytes = Data.Length;
                Engine.Rendering.Stats.AddBufferAllocation(_allocatedVRAMBytes);

                if (Data.DisposeOnPush)
                    Data.Dispose();
            }

            public static GLEnum ToGLEnum(EBufferUsage usage) => usage switch
            {
                EBufferUsage.StaticDraw => GLEnum.StaticDraw,
                EBufferUsage.DynamicDraw => GLEnum.DynamicDraw,
                EBufferUsage.StreamDraw => GLEnum.StreamDraw,
                EBufferUsage.StaticRead => GLEnum.StaticRead,
                EBufferUsage.DynamicRead => GLEnum.DynamicRead,
                EBufferUsage.StreamRead => GLEnum.StreamRead,
                EBufferUsage.StaticCopy => GLEnum.StaticCopy,
                EBufferUsage.DynamicCopy => GLEnum.DynamicCopy,
                EBufferUsage.StreamCopy => GLEnum.StreamCopy,
                _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, null),
            };

            /// <summary>
            /// Pushes the entire buffer to the GPU. Assumes the buffer has already been allocated using PushData.
            /// </summary>
            public void PushSubData()
                => PushSubData(0, Data.Length);

            /// <summary>
            /// Pushes the a portion of the buffer to the GPU. Assumes the buffer has already been allocated using PushData.
            /// </summary>
            public void PushSubData(int offset, uint length)
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;

                if (Engine.InvokeOnMainThread(() => PushSubData(offset, length), "GLDataBuffer.PushSubData"))
                    return;
                
                if (!IsGenerated)
                    Generate();
                else
                {
                    uint lastPushed = _lastPushedLength;

                    // If resizable buffer was grown and we never (re)allocated on the GPU, fall back to full PushData.
                    // Also do this if caller is pushing the whole buffer starting at 0.
                    if (offset == 0 && (lastPushed == 0 || length > lastPushed))
                    {
                        PushData();
                        return;
                    }

                    if (offset + length > lastPushed)
                    {
                        int clamped = (int)lastPushed - offset;
                        if (clamped <= 0)
                        {
                            Debug.LogWarning($"PushSubData called with offset {offset} and length {length}, with an offset that exceeds the last fully-pushed length of {lastPushed}. Ignoring call.");
                            return;
                        }

                        length = (uint)clamped;
                    }

                    void* addr = Data.Address;
                    Api.NamedBufferSubData(BindingId, offset, length, addr);
                }
            }

            public void Flush()
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;
                if (Engine.InvokeOnMainThread(Flush, "GLDataBuffer.Flush"))
                    return;
                Api.FlushMappedNamedBufferRange(BindingId, 0, Data.Length);
            }

            public void FlushRange(int offset, uint length)
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;
                if (Engine.InvokeOnMainThread(() => FlushRange(offset, length), "GLDataBuffer.FlushRange"))
                    return;
                Api.FlushMappedNamedBufferRange(BindingId, offset, length);
            }

            private DataSource? _gpuSideSource = null;
            /// <summary>
            /// The data buffer stored on the GPU side.
            /// </summary>
            public DataSource? GPUSideSource
            {
                get => _gpuSideSource;
                set => SetField(ref _gpuSideSource, value);
            }

            private bool _immutableStorageSet = false;
            public bool ImmutableStorageSet
            {
                get => _immutableStorageSet;
                set => SetField(ref _immutableStorageSet, value);
            }

            public void MapBufferData()
            {
                // If any API wrapper has already mapped this XRDataBuffer, skip remapping
                if (Data.ActivelyMapping.Count > 0)
                {
                    return;
                }

                if (Engine.InvokeOnMainThread(MapBufferData, "GLDataBuffer.MapBufferData"))
                    return;

                // Insert a client-mapped buffer barrier before mapping to ensure visibility of GPU writes to persistently mapped buffers
                Renderer.Api.MemoryBarrier((uint)MemoryBarrierMask.ClientMappedBufferBarrierBit);
                MapToClientSide();
            }

            public void MapToClientSide()
            {
                uint id = BindingId;
                uint length = GetLength();
                GPUSideSource?.Dispose();

                var glRange = (uint)ToGLEnum(Data.RangeFlags);
                var glStorage = (uint)ToGLEnum(Data.StorageFlags);
                if (IsGpuBufferLoggingEnabled())
                    Debug.Out($"[GLBuffer/Map] {GetDescribingName()} name={BufferNameOrTarget()} id={id} len={length} target={Data.Target} storage={StorageFlagsString()} (0x{glStorage:X}) range={RangeFlagsString()} (0x{glRange:X})");

                var addr = Api.MapNamedBufferRange(id, IntPtr.Zero, length, glRange);
                if (addr is null)
                {
                    Debug.LogWarning($"[GLBuffer/Map] {GetDescribingName()} name={BufferNameOrTarget()} returned null pointer.");
                    return;
                }
                GPUSideSource = new DataSource(addr, length);
                Data.ActivelyMapping.Add(this);
            }

            // ----- Added helpers for mapping/immutable storage -----
            private uint GetLength()
            {
                var existingSource = Data.ClientSideSource;
                return existingSource is not null ? existingSource.Length : Data.Length;
            }

            private void AllocateImmutable()
            {
                // Track VRAM deallocation of previous buffer if any
                if (_allocatedVRAMBytes > 0)
                {
                    Engine.Rendering.Stats.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                uint id = BindingId;
                uint length;
                var existingSource = Data.ClientSideSource;
                var glStorage = (uint)ToGLEnum(Data.StorageFlags);
                if (IsGpuBufferLoggingEnabled())
                    Debug.Out($"[GLBuffer/Storage] {GetDescribingName()} name={BufferNameOrTarget()} id={id} len={(existingSource?.Length ?? Data.Length)} storage={StorageFlagsString()} (0x{glStorage:X})");
                if (existingSource is not null)
                    Api.NamedBufferStorage(id, length = existingSource.Length, existingSource.Address.Pointer, glStorage);
                else
                    Api.NamedBufferStorage(id, length = Data.Length, null, glStorage);

                // Track VRAM allocation
                _allocatedVRAMBytes = length;
                Engine.Rendering.Stats.AddBufferAllocation(_allocatedVRAMBytes);
            }
            // ------------------------------------------------------

            public void UnmapBufferData()
            {
                if (!Data.ActivelyMapping.Contains(this))
                    return;

                if (Engine.InvokeOnMainThread(UnmapBufferData, "GLDataBuffer.UnmapBufferData"))
                    return;

                if (IsGpuBufferLoggingEnabled())
                    Debug.Out($"[GLBuffer/Unmap] {GetDescribingName()} name={BufferNameOrTarget()} id={BindingId}");
                Api.UnmapNamedBuffer(BindingId);
                Data.ActivelyMapping.Remove(this);

                GPUSideSource?.Dispose();
                GPUSideSource = null;
            }

            public static GLEnum ToGLEnum(EBufferMapStorageFlags storageFlags)
            {
                GLEnum flags = 0;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.Read))
                    flags |= GLEnum.MapReadBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.Write))
                    flags |= GLEnum.MapWriteBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.Persistent))
                    flags |= GLEnum.MapPersistentBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.Coherent))
                    flags |= GLEnum.MapCoherentBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.ClientStorage))
                    flags |= GLEnum.ClientStorageBit;
                if (storageFlags.HasFlag(EBufferMapStorageFlags.DynamicStorage))
                    flags |= GLEnum.DynamicStorageBit;
                return flags;
            }

            public static GLEnum ToGLEnum(EBufferMapRangeFlags rangeFlags)
            {
                GLEnum flags = 0;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Read))
                    flags |= GLEnum.MapReadBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Write))
                    flags |= GLEnum.MapWriteBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Persistent))
                    flags |= GLEnum.MapPersistentBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Coherent))
                    flags |= GLEnum.MapCoherentBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.InvalidateRange))
                    flags |= GLEnum.MapInvalidateRangeBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.InvalidateBuffer))
                    flags |= GLEnum.MapInvalidateBufferBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.FlushExplicit))
                    flags |= GLEnum.MapFlushExplicitBit;
                if (rangeFlags.HasFlag(EBufferMapRangeFlags.Unsynchronized))
                    flags |= GLEnum.MapUnsynchronizedBit;
                return flags;
            }

            public void SetUniformBlockName(XRRenderProgram program, string blockName)
            {
                var apiProgram = Renderer.GenericToAPI<GLRenderProgram>(program);
                if (apiProgram is null)
                    return;

                var bindingID = apiProgram.BindingId;
                if (bindingID == InvalidBindingId)
                    return;

                if (Engine.InvokeOnMainThread(() => SetUniformBlockName(program, blockName), "GLDataBuffer.SetUniformBlockName"))
                    return;

                Bind();
                SetBlockIndex(Api.GetUniformBlockIndex(bindingID, blockName));
                Unbind();
            }

            public void SetBlockIndex(uint blockIndex)
            {
                if (blockIndex == uint.MaxValue)
                    return;

                if (Engine.InvokeOnMainThread(() => SetBlockIndex(blockIndex), "GLDataBuffer.SetBlockIndex"))
                    return;

                Bind();
                // When using vertex buffers as SSBOs (compute skinning), bind with SSBO target even if the buffer was created as an array buffer.
                GLEnum bindTarget = ToGLEnum(Data.Target);
                if (bindTarget == GLEnum.ArrayBuffer)
                    bindTarget = GLEnum.ShaderStorageBuffer;

                Api.BindBufferBase(bindTarget, blockIndex, BindingId);
                Unbind();
            }

            protected internal override void PreDeleted()
            {
                UnmapBufferData();

                // Track VRAM deallocation
                if (_allocatedVRAMBytes > 0)
                {
                    Engine.Rendering.Stats.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }
            }

            public void Bind()
            {
                if (Engine.InvokeOnMainThread(Bind, "GLDataBuffer.Bind"))
                    return;

                Api.BindBuffer(ToGLEnum(Data.Target), BindingId);
            }
            public void Unbind()
            {
                if (Engine.InvokeOnMainThread(Unbind, "GLDataBuffer.Unbind"))
                    return;

                Api.BindBuffer(ToGLEnum(Data.Target), 0);
            }

            public bool IsMapped => Data.ActivelyMapping.Contains(this);

            public VoidPtr? GetMappedAddress()
                => GPUSideSource?.Address;

            public void BindSSBO(XRRenderProgram program, uint? bindindIndexOverride = null)
            {
                BindSSBO(Renderer.GenericToAPI<GLRenderProgram>(program), bindindIndexOverride);
            }

            public void BindSSBO(GLRenderProgram? program, uint? bindindIndexOverride = null)
            {
                if (program is null)
                {
                    Debug.LogWarning($"Failed to bind SSBO {GetDescribingName()} to program {program?.GetDescribingName() ?? "null"}.");
                    return;
                }

                uint resourceIndex = bindindIndexOverride ?? Data.BindingIndexOverride ?? Api.GetProgramResourceIndex(program.BindingId, GLEnum.ShaderStorageBlock, Data.AttributeName);
                if (resourceIndex == uint.MaxValue)
                    return;

                Bind();
                Api.BindBufferBase(ToGLEnum(EBufferTarget.ShaderStorageBuffer), resourceIndex, BindingId);
                Unbind();
            }
        }
    }
}