using Extensions;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLDataBuffer(OpenGLRenderer renderer, XRDataBuffer buffer) : GLObject<XRDataBuffer>(renderer, buffer), IApiDataBuffer
        {
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
            }
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
            }

            public override GLObjectType Type => GLObjectType.Buffer;

            protected internal override void PostGenerated()
            {
                var rend = Renderer.ActiveMeshRenderer;
                if (rend is null)
                {
                    var target = Data.Target;
                    if (target == EBufferTarget.ArrayBuffer)
                        Debug.LogWarning($"{GetDescribingName()} generated without a mesh renderer.");
                    return;
                }
                BindToProgram(rend.GetVertexProgram(), rend);
            }

            public void BindToProgram(GLRenderProgram vertexProgram, GLMeshRenderer? arrayBufferLink, bool pushDataNow = true)
            {
                try
                {
                    //const int glVer = 2;
                    //switch (glVer)
                    //{
                    //    case 0:
                    //        BindV0(index, componentType, componentCount, integral);
                    //        break;
                    //    case 1:
                    //        BindV1(index, componentType, componentCount, integral);
                    //        break;
                    //    default:
                    //    case 2:
                    BindV2(vertexProgram, arrayBufferLink);
                    //        break;
                    //}
                }
                catch (Exception e)
                {
                    Debug.LogException(e, "Error binding buffer.");
                }

                if (pushDataNow)
                {
                    if (Data.Resizable)
                        PushData();
                    else
                        AllocateImmutable();
                }
            }

            public bool TryGetAttributeLocation(GLRenderProgram vertexProgram, out uint layoutLocation)
            {
                layoutLocation = GetAttributeLocation(vertexProgram, Data.AttributeName ?? string.Empty);
                return layoutLocation != uint.MaxValue;
            }

            private void BindV2(GLRenderProgram vertexProgram, GLMeshRenderer? arrayBufferLink)
            {
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
                                //Debug.LogWarning($"Failed to bind buffer {GetDescribingName()} to {Data.AttributeName}.");
                                //vertexProgram.Data.Shaders.ForEach(x => Debug.Out(x?.Source?.Text ?? string.Empty));
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
                                //Debug.LogWarning($"Failed to bind buffer {GetDescribingName()} to {Data.AttributeName}.");
                                //vertexProgram.Data.Shaders.ForEach(x => Debug.Out(x?.Source?.Text ?? string.Empty));
                                return;
                            }

                            Bind();
                            Api.BindBufferBase(ToGLEnum(Data.Target), bindingIndex, BindingId);
                            Unbind();
                            break;
                        }
                }
            }

            //private void BindV1(uint index, int componentType, uint componentCount, bool integral)
            //{
            //    Api.BindVertexBuffer(index, BindingId, IntPtr.Zero, Data.ElementSize);
            //    switch (Data.Target)
            //    {
            //        case EBufferTarget.ArrayBuffer:
            //            Api.EnableVertexAttribArray(index);
            //            Api.VertexAttribBinding(index, index);
            //            if (integral)
            //                Api.VertexAttribIFormat(index, (int)componentCount, GLEnum.Byte + componentType, 0);
            //            else
            //                Api.VertexAttribFormat(index, (int)componentCount, GLEnum.Byte + componentType, Data.Normalize, 0);
            //            break;
            //    }
            //}

            //private void BindV0(uint index, int componentType, uint componentCount, bool integral)
            //{
            //    Bind();
            //    switch (Data.Target)
            //    {
            //        case EBufferTarget.ArrayBuffer:
            //            Api.EnableVertexAttribArray(index);
            //            void* addr = Data.Address;
            //            if (integral)
            //                Api.VertexAttribIPointer(index, (int)componentCount, GLEnum.Byte + componentType, 0, addr);
            //            else
            //                Api.VertexAttribPointer(index, (int)componentCount, GLEnum.Byte + componentType, Data.Normalize, 0, addr);
            //            break;
            //    }
            //    Unbind();
            //}

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

                if (Engine.InvokeOnMainThread(PushData))
                    return;

                void* addr = (Data.TryGetAddress(out var address) ? address : VoidPtr.Zero).Pointer;
                Api.NamedBufferData(BindingId, Data.Length, addr, ToGLEnum(Data.Usage));
                _lastPushedLength = Data.Length;
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

                if (Engine.InvokeOnMainThread(() => PushSubData(offset, length)))
                    return;
                
                if (!IsGenerated)
                    Generate();
                else
                {
                    uint lastPushed = _lastPushedLength;
                    if (offset + length > lastPushed)
                    {
                        int clamped = (int)lastPushed - offset;
                        if (clamped <= 0)
                        {
                            Debug.LogWarning($"PushSubData called with offset {offset} and length {length}, with an offset that exceeds the last fully-pushed length of {lastPushed}. Ignoring call.");
                            return;
                        }

                        //Debug.LogWarning($"PushSubData called with offset {offset} and length {length} that exceeds the last fully-pushed length of {lastPushed}. Clamping length to {clamped}.");
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
                if (Engine.InvokeOnMainThread(Flush))
                    return;
                Api.FlushMappedNamedBufferRange(BindingId, 0, Data.Length);
            }

            public void FlushRange(int offset, uint length)
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;
                if (Engine.InvokeOnMainThread(() => FlushRange(offset, length)))
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
                if (Data.ActivelyMapping.Contains(this))
                {
                    Debug.LogWarning($"Buffer {GetDescribingName()} is already mapped.");
                    return;
                }
                if (Data.Resizable)
                {
                    Debug.LogWarning($"Buffer {GetDescribingName()} is resizable and cannot be mapped.");
                    return;
                }
                //if (!ImmutableStorageSet)
                //{
                //    Debug.LogWarning($"Buffer {GetDescribingName()} has not had immutable storage set.");
                //    return;
                //}

                if (Engine.InvokeOnMainThread(MapBufferData))
                    return;

                MapToClientSide();
            }

            public void MapToClientSide()
            {
                uint id = BindingId;
                uint length = GetLength();
                GPUSideSource?.Dispose();
                GPUSideSource = new DataSource(Api.MapNamedBufferRange(id, IntPtr.Zero, length, (uint)ToGLEnum(Data.RangeFlags)), length);
                Data.ActivelyMapping.Add(this);
            }

            public void MapToClientSide(int offset, uint length)
            {
                uint id = BindingId;
                GPUSideSource?.Dispose();
                GPUSideSource = new DataSource(Api.MapNamedBufferRange(id, offset, length, (uint)ToGLEnum(Data.RangeFlags)), length);
                Data.ActivelyMapping.Add(this);
            }

            public uint GetLength()
            {
                var existingSource = Data.ClientSideSource;
                return existingSource is not null ? existingSource.Length : Data.Length;
            }

            public void AllocateImmutable()
            {
                uint id = BindingId;
                uint length;
                var existingSource = Data.ClientSideSource;
                if (existingSource is not null)
                    Api.NamedBufferStorage(id, length = existingSource.Length, existingSource.Address.Pointer, (uint)ToGLEnum(Data.StorageFlags));
                else
                    Api.NamedBufferStorage(id, length = Data.Length, null, (uint)ToGLEnum(Data.StorageFlags));
            }

            public void UnmapBufferData()
            {
                if (!Data.ActivelyMapping.Contains(this))
                    return;

                if (Engine.InvokeOnMainThread(UnmapBufferData))
                    return;

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

                if (Engine.InvokeOnMainThread(() => SetUniformBlockName(program, blockName)))
                    return;

                Bind();
                SetBlockIndex(Api.GetUniformBlockIndex(bindingID, blockName));
                Unbind();
            }

            public void SetBlockIndex(uint blockIndex)
            {
                if (blockIndex == uint.MaxValue)
                    return;

                if (Engine.InvokeOnMainThread(() => SetBlockIndex(blockIndex)))
                    return;

                Bind();
                Api.BindBufferBase(ToGLEnum(Data.Target), blockIndex, BindingId);
                Unbind();
            }

            protected internal override void PreDeleted()
                => UnmapBufferData();

            public void Bind()
            {
                if (Engine.InvokeOnMainThread(Bind))
                    return;

                Api.BindBuffer(ToGLEnum(Data.Target), BindingId);
            }
            public void Unbind()
            {
                if (Engine.InvokeOnMainThread(Unbind))
                    return;

                Api.BindBuffer(ToGLEnum(Data.Target), 0);
            }

            public bool IsMapped => Data.ActivelyMapping.Contains(this);

            public VoidPtr? GetMappedAddress()
                => GPUSideSource?.Address;
        }
    }
}