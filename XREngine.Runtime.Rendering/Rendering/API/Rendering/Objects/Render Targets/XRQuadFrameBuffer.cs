using System.IO;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering
{
    /// <summary>
    /// Represents a framebuffer, material, quad (actually a giant triangle), and camera to render with.
    /// </summary>
    public class XRQuadFrameBuffer : XRMaterialFrameBuffer
    {
        /// <summary>
        /// Use to set uniforms to the program containing the fragment shader.
        /// </summary>
        private DelSetUniforms? _settingUniforms;
        private readonly bool _useMultiview;
        private bool _initialRenderingPrepared;
        private XRMeshRenderer.BaseVersion? _initialRenderingVersion;

        public event DelSetUniforms? SettingUniforms
        {
            add
            {
                bool attachForwarder = _settingUniforms is null;
                _settingUniforms += value;
                if (attachForwarder && _settingUniforms is not null)
                    FullScreenMesh.SettingUniforms += SetUniforms;
            }
            remove
            {
                _settingUniforms -= value;
                if (_settingUniforms is null)
                    FullScreenMesh.SettingUniforms -= SetUniforms;
            }
        }

        public XRMeshRenderer FullScreenMesh { get; }

        private static XRMesh Mesh(bool useTriangle)
        {
            if (useTriangle)
            {
                //Render a triangle that overdraws past the screen - discard fragments outside the screen in the shader.
                VertexTriangle triangle = new(
                    new Vector3(-1, -1, 0),
                    new Vector3( 3, -1, 0),
                    new Vector3(-1,  3, 0));

                return XRMesh.Create(triangle);
            }
            else
            {
                //     .3
                //    /|
                //   / |
                // 1.__.2
                VertexTriangle triangle1 = new(
                    new Vector3(-1, -1, 0),
                    new Vector3( 1, -1, 0),
                    new Vector3( 1,  1, 0));

                // 3.__.2
                //  | /
                //  |/
                // 1.
                VertexTriangle triangle2 = new(
                    new Vector3(-1, -1, 0),
                    new Vector3( 1,  1, 0),
                    new Vector3(-1,  1, 0));

                return XRMesh.Create(triangle1, triangle2);
            }
        }

        /// <summary>
        /// Renders a material to the screen using a fullscreen orthographic quad.
        /// </summary>
        /// <param name="mat">The material containing textures to render to this fullscreen quad.</param>
        public XRQuadFrameBuffer(
            XRMaterial mat,
            bool useTriangle = true,
            bool deriveRenderTargetsFromMaterial = true,
            bool useMultiview = false,
            bool prepareForInitialRendering = true)
            : base(mat, deriveRenderTargetsFromMaterial)
        {
            mat.RenderOptions.CullMode = ECullMode.None;
            if (mat.RenderOptions.StencilTest.Enabled == ERenderParamUsage.Unchanged)
                mat.RenderOptions.StencilTest.Enabled = ERenderParamUsage.Disabled;

            if (mat.VertexShaders.Count == 0)
                mat.SetShader(
                    EShaderType.Vertex,
                    XRShader.EngineShader(Path.Combine("Scene3D", "FullscreenTri.vs"), EShaderType.Vertex));

            // Only array-aware fragment shaders participate. Mono utility effects
            // retain their ordinary screen-space shader even in a stereo pipeline.
            for (int i = 0; useMultiview && i < mat.FragmentShaders.Count; i++)
                if (mat.FragmentShaders[i].HasExtension("GL_OVR_multiview2", XRShader.EExtensionBehavior.Require))
                {
                    _useMultiview = true;
                    break;
                }
            if (_useMultiview)
            {
                ForceOvrMultiview = true;
                mat.Shaders.Add(XRShader.EngineShader(Path.Combine("Scene3D", "FullscreenTriOVR.vs"), EShaderType.Vertex));
            }

            FullScreenMesh = new XRMeshRenderer(Mesh(useTriangle), mat);
            FullScreenMesh.ForceOvrMultiview = _useMultiview;
            FullScreenMesh.Name = $"FullscreenQuad:{mat.Name ?? "Material"}";
            FullScreenMesh.GenerateAsync = false;
            FullScreenMesh.CaptureUniformsOnRender = true;
            FullScreenMesh.GenerationPriority = EMeshGenerationPriority.RenderPipeline;
            FullScreenMesh.SetShaderPipelinesAllowedForAllVersions(false);
            if (prepareForInitialRendering)
                PrepareForInitialRendering();
        }

        internal void PrepareForInitialRendering()
        {
            if (_initialRenderingPrepared)
                return;

            PrepareInitialRenderingVersion();

            if (RuntimeEngine.IsRenderThread)
                _initialRenderingVersion!.Generate();

            _initialRenderingPrepared = true;
        }

        internal void PrepareInitialRenderingVersion()
        {
            if (_initialRenderingPrepared || _initialRenderingVersion is not null)
                return;

            FullScreenMesh.EnsureRenderPipelineVersionsCreated();
            _initialRenderingVersion = _useMultiview
                ? FullScreenMesh.GetOVRMultiViewVersion()
                : FullScreenMesh.GetDefaultVersion();
            _initialRenderingVersion.AllowShaderPipelines = false;
            _initialRenderingVersion.Name = $"FullscreenQuad:{Material?.Name ?? "Material"}";
        }

        public XRQuadFrameBuffer(
            XRMaterial material,
            bool useTriangle,
            params (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? targets)
            : this(material, useTriangle, true, false, targets)
        {
        }

        public XRQuadFrameBuffer(
            XRMaterial material,
            bool useTriangle,
            bool deriveRenderTargetsFromMaterial,
            params (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? targets)
            : this(material, useTriangle, deriveRenderTargetsFromMaterial, false, targets)
        {
        }

        /// <summary>
        /// Creates a target-backed fullscreen framebuffer. Set <paramref name="useMultiviewTargets"/> only when
        /// the material contains a fragment shader that requires <c>GL_OVR_multiview2</c>.
        /// </summary>
        public XRQuadFrameBuffer(
            XRMaterial material,
            bool useTriangle,
            bool deriveRenderTargetsFromMaterial,
            bool useMultiviewTargets,
            params (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? targets)
            : this(material, useTriangle, deriveRenderTargetsFromMaterial, useMultiviewTargets) => SetRenderTargets(targets);

        private void SetUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => _settingUniforms?.Invoke(materialProgram);

        // Explicit OVR fullscreen passes retain screen-space positioning while
        // selecting the vertex declaration that broadcasts into both array layers.
        public bool TryPrepareForRendering(bool forceNoStereo = true)
        {
            PrepareForInitialRendering();
            bool prepareWithoutStereo = forceNoStereo && !_useMultiview;
            if (!RenderDiagnosticsFlags.VkTraceDraw)
                return FullScreenMesh.TryPrepareForRendering(prepareWithoutStereo);

            bool prepared = FullScreenMesh.TryPrepareForRendering(out string reason, prepareWithoutStereo);
            Debug.RenderingEvery(
                $"Quad.Prepare.{GetHashCode()}", TimeSpan.FromSeconds(3),
                "[QuadPrepare] name={0} pipeline={1} multiview={2} forceNoStereo={3} prepared={4} reason={5} detail={6}",
                FullScreenMesh.Name ?? "<unnamed>", RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.InstanceId ?? 0,
                _useMultiview, prepareWithoutStereo, prepared, reason, FullScreenMesh.GetLastPrepareDetail(prepareWithoutStereo));
            return prepared;
        }

        /// <summary>
        /// Renders the FBO to the entire region set by RuntimeEngine.Rendering.State.PushRenderArea().
        /// </summary>
        public bool Render(XRFrameBuffer? target = null, bool forceNoStereo = true)
        {
            target?.BindForWriting();
            try
            {
                if (!TryPrepareForRendering(forceNoStereo))
                    return false;

                EnqueueRender(forceNoStereo);
                return true;
            }
            finally
            {
                target?.UnbindFromWriting();
            }
        }

        /// <summary>
        /// Enqueues the fullscreen draw without an eager API-object readiness check.
        /// Use this when typed binding publishers establish descriptor resources as
        /// part of the draw snapshot; those resources do not exist during preflight.
        /// </summary>
        internal void EnqueueRender(bool forceNoStereo = true)
        {
            forceNoStereo &= !_useMultiview;
            if (RenderDiagnosticsFlags.VkTraceDraw)
                Debug.RenderingEvery(
                    $"Quad.Enqueue.{GetHashCode()}", TimeSpan.FromSeconds(3),
                    "[QuadEnqueue] name={0} pipeline={1} multiview={2} forceNoStereo={3}",
                    FullScreenMesh.Name ?? "<unnamed>", RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.InstanceId ?? 0,
                    _useMultiview, forceNoStereo);
            FullScreenMesh.EnsureApiRenderObject(forceNoStereo);
            var state = RuntimeEngine.Rendering.State.RenderingPipelineState;
            if (state != null)
            {
                using (state.PushRenderingCamera(null))
                    FullScreenMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, 1, forceNoStereo);
            }
            else
                FullScreenMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, 1, forceNoStereo);
        }
    }
}
