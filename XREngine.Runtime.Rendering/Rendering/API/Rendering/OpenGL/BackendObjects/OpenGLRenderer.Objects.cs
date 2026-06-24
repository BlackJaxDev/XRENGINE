using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    public void DeleteObjects<T>(params T[] objs) where T : GLObjectBase
    {
        if (objs.Length == 0)
            return;

        if (ShouldOrphanGLHandlesForShutdown)
        {
            for (int i = 0; i < objs.Length; ++i)
                objs[i].OrphanForDeferredDelete();
            return;
        }

        uint[] bindingIds = new uint[objs.Length];
        bindingIds.Fill(GLObjectBase.InvalidBindingId);

        for (int i = 0; i < objs.Length; ++i)
        {
            var o = objs[i];
            if (!o.IsGenerated)
                continue;

            o.PreDeleted();
            bindingIds[i] = o.BindingId;
        }

        bindingIds = bindingIds.Where(i => i != GLObjectBase.InvalidBindingId).ToArray();
        EGLObjectType type = objs[0].Type;
        uint len = (uint)bindingIds.Length;
        switch (type)
        {
            case EGLObjectType.Buffer:
                Api.DeleteBuffers(len, bindingIds);
                break;
            case EGLObjectType.Framebuffer:
                Api.DeleteFramebuffers(len, bindingIds);
                break;
            case EGLObjectType.Program:
                foreach (var i in objs)
                    Api.DeleteProgram(i.BindingId);
                break;
            case EGLObjectType.ProgramPipeline:
                Api.DeleteProgramPipelines(len, bindingIds);
                break;
            case EGLObjectType.Query:
                Api.DeleteQueries(len, bindingIds);
                break;
            case EGLObjectType.Renderbuffer:
                Api.DeleteRenderbuffers(len, bindingIds);
                break;
            case EGLObjectType.Sampler:
                Api.DeleteSamplers(len, bindingIds);
                break;
            case EGLObjectType.Texture:
                Api.DeleteTextures(len, bindingIds);
                break;
            case EGLObjectType.TransformFeedback:
                Api.DeleteTransformFeedbacks(len, bindingIds);
                break;
            case EGLObjectType.VertexArray:
                Api.DeleteVertexArrays(len, bindingIds);
                break;
            case EGLObjectType.Shader:
                foreach (uint i in bindingIds)
                    Api.DeleteShader(i);
                break;
        }

        foreach (var o in objs)
        {
            if (Array.IndexOf(bindingIds, o._bindingId) < 0)
                continue;

            o._bindingId = null;
            o.PostDeleted();
        }
    }

    public uint[] CreateObjects(EGLObjectType type, uint count)
    {
        uint[] ids = new uint[count];
        switch (type)
        {
            case EGLObjectType.Buffer:
                Api.CreateBuffers(count, ids);
                break;
            case EGLObjectType.Framebuffer:
                Api.CreateFramebuffers(count, ids);
                break;
            case EGLObjectType.Program:
                for (int i = 0; i < count; ++i)
                    ids[i] = Api.CreateProgram();
                break;
            case EGLObjectType.ProgramPipeline:
                Api.CreateProgramPipelines(count, ids);
                break;
            case EGLObjectType.Query:
                //throw new InvalidOperationException("Call CreateQueries instead.");
                Api.GenQueries(count, ids);
                break;
            case EGLObjectType.Renderbuffer:
                Api.CreateRenderbuffers(count, ids);
                break;
            case EGLObjectType.Sampler:
                Api.CreateSamplers(count, ids);
                break;
            case EGLObjectType.Texture:
                //throw new InvalidOperationException("Call CreateTextures instead.");
                Api.GenTextures(count, ids);
                break;
            case EGLObjectType.TransformFeedback:
                Api.CreateTransformFeedbacks(count, ids);
                break;
            case EGLObjectType.VertexArray:
                Api.CreateVertexArrays(count, ids);
                break;
            case EGLObjectType.Shader:
                //for (int i = 0; i < count; ++i)
                //    ids[i] = Api.CreateShader(CurrentShaderMode);
                break;
        }
        return ids;
    }

    //public T[] CreateObjects<T>(uint count) where T : GLObjectBase, new()
    //    => CreateObjects(TypeFor<T>(), count).Select(i => (T)Activator.CreateInstance(typeof(T), this, i)!).ToArray();

    private static EGLObjectType TypeFor<T>() where T : GLObjectBase, new()
        => typeof(T) switch
        {
            Type t when typeof(GLDataBuffer).IsAssignableFrom(t)
                => EGLObjectType.Buffer,

            Type t when typeof(GLShader).IsAssignableFrom(t)
                => EGLObjectType.Shader,

            Type t when typeof(GLRenderProgram).IsAssignableFrom(t)
                => EGLObjectType.Program,

            Type t when typeof(GLMeshRenderer).IsAssignableFrom(t)
                => EGLObjectType.VertexArray,

            Type t when typeof(GLRenderQuery).IsAssignableFrom(t)
                => EGLObjectType.Query,

            Type t when typeof(GLRenderProgramPipeline).IsAssignableFrom(t)
                => EGLObjectType.ProgramPipeline,

            Type t when typeof(GLTransformFeedback).IsAssignableFrom(t)
                => EGLObjectType.TransformFeedback,

            Type t when typeof(GLSampler).IsAssignableFrom(t)
                => EGLObjectType.Sampler,

            Type t when typeof(IGLTexture).IsAssignableFrom(t)
                => EGLObjectType.Texture,

            Type t when typeof(GLRenderBuffer).IsAssignableFrom(t)
                => EGLObjectType.Renderbuffer,

            Type t when typeof(GLFrameBuffer).IsAssignableFrom(t)
                => EGLObjectType.Framebuffer,

            Type t when typeof(GLMaterial).IsAssignableFrom(t)
                => EGLObjectType.Material,
            _ => throw new InvalidOperationException($"Type {typeof(T)} is not a valid GLObjectBase type."),
        };

    public uint CreateMemoryObject()
        => EXTMemoryObject?.CreateMemoryObject() ?? 0;

    public uint CreateSemaphore()
        => EXTSemaphore?.GenSemaphore() ?? 0;

    public unsafe uint CreateImportedMemoryObject(ulong size, void* memoryObjectHandle)
    {
        uint memoryObject = CreateMemoryObject();
        if (memoryObject == 0 || EXTMemoryObjectWin32 is null)
            return 0;

        EXTMemoryObjectWin32.ImportMemoryWin32Handle(memoryObject, size, EXT.HandleTypeOpaqueWin32Ext, memoryObjectHandle);
        return memoryObject;
    }

    public unsafe uint CreateImportedSemaphore(void* semaphoreHandle)
    {
        uint semaphore = CreateSemaphore();
        if (semaphore == 0 || EXTSemaphoreWin32 is null)
            return 0;

        EXTSemaphoreWin32.ImportSemaphoreWin32Handle(semaphore, EXT.HandleTypeOpaqueWin32Ext, semaphoreHandle);
        return semaphore;
    }

    public unsafe void DeleteMemoryObject(uint memoryObject)
    {
        if (memoryObject == 0 || EXTMemoryObject is null)
            return;

        EXTMemoryObject.DeleteMemoryObjects(1, &memoryObject);
    }

    public void DeleteSemaphore(uint semaphore)
    {
        if (semaphore == 0)
            return;

        EXTSemaphore?.DeleteSemaphore(semaphore);
    }

    public unsafe void SignalExternalTextureSemaphore(uint semaphore, ReadOnlySpan<uint> textureIds, ReadOnlySpan<Silk.NET.OpenGLES.TextureLayout> layouts)
    {
        if (semaphore == 0 || EXTSemaphore is null || textureIds.Length == 0 || textureIds.Length != layouts.Length)
            return;

        fixed (uint* texturePtr = textureIds)
        fixed (Silk.NET.OpenGLES.TextureLayout* layoutPtr = layouts)
        {
            EXTSemaphore.SignalSemaphore(semaphore, 0, (uint*)null, (uint)textureIds.Length, texturePtr, layoutPtr);
        }
    }

    public unsafe void WaitExternalTextureSemaphore(uint semaphore, ReadOnlySpan<uint> textureIds, ReadOnlySpan<Silk.NET.OpenGLES.TextureLayout> layouts)
    {
        if (semaphore == 0 || EXTSemaphore is null || textureIds.Length == 0 || textureIds.Length != layouts.Length)
            return;

        fixed (uint* texturePtr = textureIds)
        fixed (Silk.NET.OpenGLES.TextureLayout* layoutPtr = layouts)
        {
            EXTSemaphore.WaitSemaphore(semaphore, 0, (uint*)null, (uint)textureIds.Length, texturePtr, layoutPtr);
        }
    }

    public unsafe void SignalExternalTextureSemaphore(uint semaphore, uint textureId, Silk.NET.OpenGLES.TextureLayout layout)
    {
        if (textureId == 0)
            return;

        Span<uint> textureIds = stackalloc uint[1] { textureId };
        Span<Silk.NET.OpenGLES.TextureLayout> layouts = stackalloc Silk.NET.OpenGLES.TextureLayout[1] { layout };
        SignalExternalTextureSemaphore(semaphore, textureIds, layouts);
    }

    public unsafe void WaitExternalTextureSemaphore(uint semaphore, uint textureId, Silk.NET.OpenGLES.TextureLayout layout)
    {
        if (textureId == 0)
            return;

        Span<uint> textureIds = stackalloc uint[1] { textureId };
        Span<Silk.NET.OpenGLES.TextureLayout> layouts = stackalloc Silk.NET.OpenGLES.TextureLayout[1] { layout };
        WaitExternalTextureSemaphore(semaphore, textureIds, layouts);
    }

    public IntPtr GetMemoryObjectHandle(uint memoryObject)
    {
        if (EXTMemoryObject is null)
            return IntPtr.Zero;
        EXTMemoryObject.GetMemoryObjectParameter(memoryObject, EXT.HandleTypeOpaqueWin32Ext, out int handle);
        return (IntPtr)handle;
    }

    public IntPtr GetSemaphoreHandle(uint semaphore)
    {
        if (EXTSemaphore is null)
            return IntPtr.Zero;
        EXTSemaphore.GetSemaphoreParameter(semaphore, EXT.HandleTypeOpaqueWin32Ext, out ulong handle);
        return (IntPtr)handle;
    }

    public unsafe void SetMemoryObjectHandle(uint memoryObject, void* memoryObjectHandle)
        => EXTMemoryObjectWin32?.ImportMemoryWin32Handle(memoryObject, 0, EXT.HandleTypeOpaqueWin32Ext, memoryObjectHandle);

    public unsafe void SetSemaphoreHandle(uint semaphore, void* semaphoreHandle)
        => EXTSemaphoreWin32?.ImportSemaphoreWin32Handle(semaphore, EXT.HandleTypeOpaqueWin32Ext, semaphoreHandle);
}
