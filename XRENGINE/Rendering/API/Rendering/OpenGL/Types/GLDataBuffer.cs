using Extensions;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLDataBuffer(OpenGLRenderer renderer, XRDataBuffer buffer) : GLObject<XRDataBuffer>(renderer, buffer)
        {
            protected override void UnlinkData()
            {
                Data.PushDataRequested -= PushData;
                Data.PushSubDataRequested -= PushSubData;
                Data.SetBlockNameRequested -= SetUniformBlockName;
                Data.SetBlockIndexRequested -= SetBlockIndex;
                Data.BindRequested -= Bind;
                Data.UnbindRequested -= Unbind;
            }
            protected override void LinkData()
            {
                Data.PushDataRequested += PushData;
                Data.PushSubDataRequested += PushSubData;
                Data.SetBlockNameRequested += SetUniformBlockName;
                Data.SetBlockIndexRequested += SetBlockIndex;
                Data.BindRequested += Bind;
                Data.UnbindRequested += Unbind;
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
                    if (!TryGetBindingIndex(vertexProgram, out uint index))
                    {
                        Debug.LogWarning($"Failed to bind buffer {GetDescribingName()}.");
                        vertexProgram.Data.Shaders.ForEach(x => Debug.Out(x?.Source?.Text ?? string.Empty));
                        return;
                    }

                    int componentType = (int)Data.ComponentType;
                    uint componentCount = Data.ComponentCount;
                    bool integral = Data.Integral;

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
                    BindV2(index, componentType, componentCount, integral, arrayBufferLink);
                    //        break;
                    //}
                }
                catch (Exception e)
                {
                    Debug.LogException(e, "Error binding buffer.");
                }

                if (pushDataNow)
                {
                    if (Data.Mapped)
                        MapBufferData();
                    else
                        PushData();
                }
            }

            public bool TryGetBindingIndex(GLRenderProgram vertexProgram, out uint index)
            {
                index = GetBindingLocation(vertexProgram);
                return index != uint.MaxValue;
            }

            private void BindV2(uint index, int componentType, uint componentCount, bool integral, GLMeshRenderer? arrayBufferLink)
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
                            Api.EnableVertexArrayAttrib(vaoId, index);
                            Api.VertexArrayBindingDivisor(vaoId, index, Data.InstanceDivisor);
                            Api.VertexArrayAttribBinding(vaoId, index, index);
                            if (integral)
                                Api.VertexArrayAttribIFormat(vaoId, index, (int)componentCount, GLEnum.Byte + componentType, 0);
                            else
                                Api.VertexArrayAttribFormat(vaoId, index, (int)componentCount, GLEnum.Byte + componentType, Data.Normalize, 0);
                            Api.VertexArrayVertexBuffer(vaoId, index, BindingId, 0, Data.ElementSize);
                        }
                        break;
                    case EBufferTarget.ShaderStorageBuffer:
                    case EBufferTarget.UniformBuffer:
                        Bind();
                        //Api.BufferData(ToGLEnum(Data.Target), Data.Length, Data.Address.Pointer, ToGLEnum(Data.Usage));
                        Api.BindBufferBase(ToGLEnum(Data.Target), index, BindingId);
                        //Api.ShaderStorageBlockBinding(renderer.VertexProgram.BindingId, index, BindingId);
                        Unbind();
                        break;
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

            private uint GetBindingLocation(GLRenderProgram vertexProgram)
            {
                uint index = 0u;
                string bindingName = Data.BindingName;

                if (string.IsNullOrWhiteSpace(bindingName))
                    Debug.LogWarning($"{GetDescribingName()} has no binding name.");

                switch (Data.Target)
                {
                    case EBufferTarget.ArrayBuffer:

                        int location = vertexProgram.GetAttributeLocation(bindingName);
                        if (location >= 0)
                            index = (uint)location;

                        break;
                    case EBufferTarget.ShaderStorageBuffer:
                        index = Data.BindingIndexOverride ?? Api.GetProgramResourceIndex(vertexProgram.BindingId, GLEnum.ShaderStorageBlock, bindingName);
                        break;
                    case EBufferTarget.UniformBuffer:
                        index = Data.BindingIndexOverride ?? Api.GetProgramResourceIndex(vertexProgram.BindingId, GLEnum.UniformBlock, bindingName);
                        break;
                    default:
                        return 0;
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

                void* addr = Data.Address;
                Api.NamedBufferData(BindingId, Data.Length, addr, ToGLEnum(Data.Usage));
                _lastPushedLength = Data.Length;
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

            public void MapBufferData()
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;

                if (Engine.InvokeOnMainThread(MapBufferData))
                    return;
                                
                uint id = BindingId;
                uint length = Data.Source!.Length;

                VoidPtr addr = Data.Source.Address;
                Api.NamedBufferStorage(id, length, ref addr, (uint)ToGLEnum(Data.StorageFlags));

                Data.ActivelyMapping.Add(this);

                Data.Source?.Dispose();
                Data.Source = new DataSource(Api.MapNamedBufferRange(id, IntPtr.Zero, length, (uint)ToGLEnum(Data.RangeFlags)), length);
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

            public void UnmapBufferData()
            {
                if (!Data.ActivelyMapping.Contains(this))
                    return;

                if (Engine.InvokeOnMainThread(UnmapBufferData))
                    return;

                Api.UnmapNamedBuffer(BindingId);
                Data.ActivelyMapping.Remove(this);
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
        }
    }
}