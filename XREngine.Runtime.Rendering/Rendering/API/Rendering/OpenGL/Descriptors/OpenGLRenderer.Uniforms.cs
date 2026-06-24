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
    public override void ConfigureVAOAttributesForProgram(XRRenderProgram program, XRMeshRenderer.BaseVersion? version)
    {
        var glProgram = GenericToAPI<GLRenderProgram>(program);
        var glMesh = version is null ? ActiveMeshRenderer : GenericToAPI<GLMeshRenderer>(version);
        if (glProgram is null || glMesh is null || !glProgram.IsLinked)
            return;

        // Bind VAO to ensure we write into the correct object
        BindMeshRenderer(glMesh);

        // Rebind mesh + renderer buffers against this program's attribute locations
        // 1) Mesh vertex buffers
        var mesh = glMesh.Mesh;
        if (mesh?.Buffers is IEventDictionary<string, XRDataBuffer> meshBuffers)
        {
            foreach (var kv in meshBuffers)
            {
                var glBuf = GenericToAPI<GLDataBuffer>(kv.Value);
                if (glBuf is null)
                    continue;

                glBuf.EnsureStorageAllocatedForGpuCopy();
                glBuf.BindToRenderer(glProgram, glMesh);
            }
        }

        // 2) Renderer extra buffers (SSBO/UBO/instance attributes)
        var rendBuffers = glMesh.MeshRenderer.Buffers as IEventDictionary<string, XRDataBuffer>;
        if (rendBuffers is not null)
        {
            foreach (var kv in rendBuffers)
            {
                var glBuf = GenericToAPI<GLDataBuffer>(kv.Value);
                if (glBuf is null)
                    continue;

                glBuf.EnsureStorageAllocatedForGpuCopy();
                glBuf.BindToRenderer(glProgram, glMesh);
            }
        }

        GLDataBuffer? elementBuffer = glMesh.GetActiveElementBuffer();
        if (elementBuffer is not null)
            Api.VertexArrayElementBuffer(glMesh.BindingId, elementBuffer.BindingId);
    }

    public override void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
    {
        var glProgram = GenericToAPI<GLRenderProgram>(program);
        if (glProgram is null)
            return;

        // Mirror GLMaterial.SetEngineUniforms minimal camera bits
        bool stereoPass = RuntimeEngine.Rendering.State.IsStereoPass;
        if (stereoPass)
        {
            var rightCam = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
            PassCameraUniforms(glProgram, camera, EEngineUniform.LeftEyeViewMatrix, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeInverseProjMatrix, EEngineUniform.LeftEyeProjMatrix, EEngineUniform.LeftEyeViewProjectionMatrix);
            PassCameraUniforms(glProgram, rightCam, EEngineUniform.RightEyeViewMatrix, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeInverseProjMatrix, EEngineUniform.RightEyeProjMatrix, EEngineUniform.RightEyeViewProjectionMatrix);
        }
        else
        {
            PassCameraUniforms(glProgram, camera, EEngineUniform.ViewMatrix, EEngineUniform.InverseViewMatrix, EEngineUniform.InverseProjMatrix, EEngineUniform.ProjMatrix, EEngineUniform.ViewProjectionMatrix);
        }
    }

    private static void PassCameraUniforms(GLRenderProgram program, XRCamera? camera, EEngineUniform view, EEngineUniform invView, EEngineUniform invProj, EEngineUniform proj, EEngineUniform viewProj)
    {
        Matrix4x4 viewMatrix;        // The actual view matrix (inverse of camera world transform)
        Matrix4x4 inverseViewMatrix; // The camera's world transform (inverse of view matrix)
        Matrix4x4 inverseProjMatrix;
        Matrix4x4 projMatrix;
        Matrix4x4 viewProjectionMatrix;
        if (camera != null)
        {
            // ViewMatrix is InverseRenderMatrix - the actual view transformation
            // InverseViewMatrix is RenderMatrix - the camera's world position (kept for compatibility)
            viewMatrix = camera.Transform.InverseRenderMatrix;
            inverseViewMatrix = camera.Transform.RenderMatrix;
            // Use unjittered projection when rendering motion vectors to match fragment shader expectations
            bool useUnjittered = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
            projMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
            inverseProjMatrix = useUnjittered ? camera.InverseProjectionMatrixUnjittered : camera.InverseProjectionMatrix;
            viewProjectionMatrix = useUnjittered ? camera.ViewProjectionMatrixUnjittered : camera.ViewProjectionMatrix;
        }
        else
        {
            viewMatrix = Matrix4x4.Identity;
            inverseViewMatrix = Matrix4x4.Identity;
            inverseProjMatrix = Matrix4x4.Identity;
            projMatrix = Matrix4x4.Identity;
            viewProjectionMatrix = Matrix4x4.Identity;
        }

        program.Uniform(view.ToStringFast(), viewMatrix);
        program.Uniform(invView.ToStringFast(), inverseViewMatrix);
        program.Uniform(invProj.ToStringFast(), inverseProjMatrix);
        program.Uniform(proj.ToStringFast(), projMatrix);
        program.Uniform(viewProj.ToStringFast(), viewProjectionMatrix);

        // Use cached uniform names to avoid string allocations per call.
        program.Uniform(view.ToVertexUniformName(), viewMatrix);
        program.Uniform(invView.ToVertexUniformName(), inverseViewMatrix);
        program.Uniform(invProj.ToVertexUniformName(), inverseProjMatrix);
        program.Uniform(proj.ToVertexUniformName(), projMatrix);
        program.Uniform(viewProj.ToVertexUniformName(), viewProjectionMatrix);
    }

    public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
    {
        var glProgram = GenericToAPI<GLRenderProgram>(program);
        var glMaterial = GenericToAPI<GLMaterial>(material);
        if (glProgram is null || glMaterial is null)
            return;

        glMaterial.SetUniforms(glProgram);
        glMaterial.FinalizeUniformBindings(glProgram);
    }
}
