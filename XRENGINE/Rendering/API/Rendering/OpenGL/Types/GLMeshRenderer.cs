using Extensions;
using Silk.NET.OpenGL;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// Used to render raw primitive data.
        /// </summary>
        public partial class GLMeshRenderer(OpenGLRenderer renderer, XRMeshRenderer.BaseVersion mesh) : GLObject<XRMeshRenderer.BaseVersion>(renderer, mesh)
        {
            // -------------------------------------------------------------
            // Verbose Debug Support
            // Toggle s_DebugVerbose to enable/disable detailed lifecycle logs.
            // -------------------------------------------------------------
            // Runtime-configurable verbose logging -------------------------------------------------
            private static volatile bool _verbose = false; // default on for now
            private static readonly HashSet<string> _enabledDebugCategories = new(StringComparer.OrdinalIgnoreCase)
            {
                "Lifecycle",
                "Buffers",
                "Programs",
                "Render",
                "Atlas",
                "General"
            };
            /// <summary>Enable/disable all verbose logs at runtime.</summary>
            public static void SetVerbose(bool enabled)
                => _verbose = enabled;
            /// <summary>Enable a specific category (case-insensitive).</summary>
            public static void EnableCategory(string category)
            {
                if (string.IsNullOrWhiteSpace(category)) return;
                lock (_enabledDebugCategories) _enabledDebugCategories.Add(category);
            }
            /// <summary>Disable a specific category.</summary>
            public static void DisableCategory(string category)
            {
                if (string.IsNullOrWhiteSpace(category)) return;
                lock (_enabledDebugCategories) _enabledDebugCategories.Remove(category);
            }
            /// <summary>Replace enabled categories set.</summary>
            public static void SetCategories(IEnumerable<string> categories)
            {
                lock (_enabledDebugCategories)
                {
                    _enabledDebugCategories.Clear();
                    foreach (var c in categories.Distinct(StringComparer.OrdinalIgnoreCase))
                        if (!string.IsNullOrWhiteSpace(c)) _enabledDebugCategories.Add(c);
                }
            }
            [System.Diagnostics.Conditional("DEBUG")]
            private static void Dbg(string msg, string category = "General")
            {
                if (!_verbose)
                    return;

                bool enabled;
                lock (_enabledDebugCategories)
                    enabled = _enabledDebugCategories.Contains(category) || _enabledDebugCategories.Contains("All");

                if (enabled)
                    Debug.Out($"[GLMeshRenderer/{category}] {msg}");
            }

            public XRMeshRenderer MeshRenderer => Data.Parent;
            public XRMesh? Mesh => MeshRenderer.Mesh;

            public override GLObjectType Type => GLObjectType.VertexArray;

            public delegate void DelSettingUniforms(GLRenderProgram vertexProgram, GLRenderProgram materialProgram);

            //Vertex buffer information
            private Dictionary<string, GLDataBuffer> _bufferCache = [];
            private GLRenderProgramPipeline? _pipeline; //Used to connect the material shader(s) to the vertex shader

            /// <summary>
            /// Combined shader program. If this is null, the vertex and fragment etc shaders are separate.
            /// This program may be cached and reused due to the nature of multiple possible combinations of shaders.
            /// </summary>
            private GLRenderProgram? _combinedProgram;

            /// <summary>
            /// This is the program that will be used to render the mesh.
            /// Use this for setting buffer bindings and uniforms.
            /// Either the combined program, the material's program if it contains a vertex shader, or the default vertex program.
            /// </summary>
            public GLRenderProgram GetVertexProgram()
            {
                if (_combinedProgram is not null)
                    return _combinedProgram;

                if (Material?.SeparableProgram?.Data.GetShaderTypeMask().HasFlag(EProgramStageMask.VertexShaderBit) ?? false)
                    return Material.SeparableProgram!;

                return _separatedVertexProgram!;
            }

            /// <summary>
            /// The main vertex shader for this mesh. Used when shader pipelines are enabled.
            /// </summary>
            private GLRenderProgram? _separatedVertexProgram;

            public GLDataBuffer? TriangleIndicesBuffer
            {
                get => _triangleIndicesBuffer;
                set => _triangleIndicesBuffer = value;
            }
            public GLDataBuffer? LineIndicesBuffer
            {
                get => _lineIndicesBuffer;
                set => _lineIndicesBuffer = value;
            }
            public GLDataBuffer? PointIndicesBuffer
            {
                get => _pointIndicesBuffer;
                set => _pointIndicesBuffer = value;
            }

            public IndexSize TrianglesElementType
            {
                get => _trianglesElementType;
                private set => _trianglesElementType = value;
            }
            public IndexSize LineIndicesElementType
            {
                get => _lineIndicesElementType;
                private set => _lineIndicesElementType = value;
            }
            public IndexSize PointIndicesElementType
            {
                get => _pointIndicesElementType;
                private set => _pointIndicesElementType = value;
            }

            private GLDataBuffer? _triangleIndicesBuffer = null;
            private GLDataBuffer? _lineIndicesBuffer = null;
            private GLDataBuffer? _pointIndicesBuffer = null;
            private IndexSize _trianglesElementType;
            private IndexSize _lineIndicesElementType;
            private IndexSize _pointIndicesElementType;

            /// <summary>
            /// Determines how to use the results of the <see cref="ConditionalRenderQuery"/>.
            /// </summary>
            public EConditionalRenderType ConditionalRenderType { get; set; } = EConditionalRenderType.QueryNoWait;
            /// <summary>
            /// A render query that is used to determine if this mesh should be rendered or not.
            /// </summary>
            public GLRenderQuery? ConditionalRenderQuery { get; set; } = null;

            public uint Instances { get; set; } = 1;
            public GLMaterial? Material => Renderer.GenericToAPI<GLMaterial>(MeshRenderer.Material);

            protected override void LinkData()
            {
                Dbg($"LinkData start (MeshRenderer={MeshRenderer?.Name ?? "null"}, Mesh={Mesh?.Name ?? "null"})","Lifecycle");
                Data.RenderRequested += Render;
                MeshRenderer.PropertyChanged += OnMeshRendererPropertyChanged;
                MeshRenderer.PropertyChanging += OnMeshRendererPropertyChanging;

                if (Mesh != null)
                    Mesh.DataChanged += OnMeshChanged;
                OnMeshChanged(Mesh);
                Dbg("LinkData complete","Lifecycle");
            }

            protected override void UnlinkData()
            {
                Dbg("UnlinkData start","Lifecycle");
                Data.RenderRequested -= Render;
                MeshRenderer.PropertyChanged -= OnMeshRendererPropertyChanged;
                MeshRenderer.PropertyChanging -= OnMeshRendererPropertyChanging;

                if (Mesh != null)
                    Mesh.DataChanged -= OnMeshChanged;
                Dbg("UnlinkData complete","Lifecycle");
            }

            private void OnMeshRendererPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                Dbg($"OnMeshRendererPropertyChanged: {e.PropertyName}","Lifecycle");
                switch (e.PropertyName)
                {
                    case nameof(XRMeshRenderer.Mesh):
                        OnMeshChanged(Mesh);
                        break;
                }
            }

            private void OnMeshRendererPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
            {
                //switch (e.PropertyName)
                //{
                //    case nameof(XRMeshRenderer.Mesh):
                //        DestroySkinningBuffers();
                //        break;
                //}
            }

            private void MakeIndexBuffers()
            {
                Dbg("MakeIndexBuffers begin","Buffers");

                var mesh = Mesh;
                if (mesh is null)
                {
                    Dbg("MakeIndexBuffers aborted - mesh null","Buffers");
                    return;
                }

                _triangleIndicesBuffer?.Destroy();
                SetIndexBuffer(ref _triangleIndicesBuffer, ref _trianglesElementType, mesh, EPrimitiveType.Triangles);

                _lineIndicesBuffer?.Destroy();
                SetIndexBuffer(ref _lineIndicesBuffer, ref _lineIndicesElementType, mesh, EPrimitiveType.Lines);

                _pointIndicesBuffer?.Destroy();
                SetIndexBuffer(ref _pointIndicesBuffer, ref _pointIndicesElementType, mesh, EPrimitiveType.Points);

                Dbg($"MakeIndexBuffers done (tri={(TriangleIndicesBuffer!=null ? TriangleIndicesBuffer.Data?.ElementCount : 0)}, line={(LineIndicesBuffer!=null ? LineIndicesBuffer.Data?.ElementCount:0)}, point={(PointIndicesBuffer!=null ? PointIndicesBuffer.Data?.ElementCount:0)})","Buffers");
            }

            private void SetIndexBuffer(ref GLDataBuffer? buffer, ref IndexSize bufferElementSize, XRMesh mesh, EPrimitiveType type)
            {
                buffer = Renderer.GenericToAPI<GLDataBuffer>(mesh.GetIndexBuffer(type, out bufferElementSize))!;
                Dbg($"SetIndexBuffer type={type} elementSize={bufferElementSize}","Buffers");
            }

            private void OnMeshChanged(XRMesh? mesh)
            {
                Dbg($"OnMeshChanged -> {(mesh?.Name ?? "null")}","Lifecycle");
                _separatedVertexProgram?.Destroy();
                _separatedVertexProgram = null;

                _combinedProgram?.Destroy();
                _combinedProgram = null;
            }

            public GLMaterial GetRenderMaterial(XRMaterial? localMaterialOverride = null)
            {
                var globalMaterialOverride = Engine.Rendering.State.RenderingPipelineState?.GlobalMaterialOverride;
                var mat =
                    (globalMaterialOverride is null ? null : (Renderer.GetOrCreateAPIRenderObject(globalMaterialOverride) as GLMaterial)) ??
                    (localMaterialOverride is null ? null : (Renderer.GetOrCreateAPIRenderObject(localMaterialOverride) as GLMaterial)) ??
                    Material;

                if (mat is not null)
                    return mat;

                Debug.LogWarning("No material found for mesh renderer, using invalid material.");
                mat = Renderer.GenericToAPI<GLMaterial>(Engine.Rendering.State.CurrentRenderingPipeline!.InvalidMaterial)!;
                return mat;
            }

            public void Render(
                Matrix4x4 modelMatrix,
                XRMaterial? materialOverride,
                uint instances,
                EMeshBillboardMode billboardMode)
            {
                Dbg($"Render request (instances={instances}, billboard={billboardMode})","Render");

                if (Data is null || !Renderer.Active)
                {
                    Dbg("Render early-out: Data null or renderer inactive","Render");
                    return;
                }

                if (!IsGenerated)
                {
                    Dbg("Not generated yet - calling Generate()","Render");
                    Generate();
                }

                GLMaterial material = GetRenderMaterial(materialOverride);
                if (GetPrograms(material, out var vtx, out var mat))
                {
                    Dbg("Programs ready - binding SSBOs and uniforms","Render");
                    //Api.BindFragDataLocation(materialProgram.BindingId, 0, "OutColor");

                    if (!BuffersBound)
                        return;

                    BindSSBOs(mat!);
                    BindSSBOs(vtx!);

                    MeshRenderer.PushBoneMatricesToGPU();
                    MeshRenderer.PushBlendshapeWeightsToGPU();

                    SetMeshUniforms(modelMatrix, vtx!, materialOverride?.BillboardMode ?? billboardMode);
                    material.SetUniforms(mat);
                    OnSettingUniforms(vtx!, mat!);
                    Renderer.RenderMesh(this, false, instances);
                    Dbg("Render mesh submitted","Render");
                }
                else
                {
                    Dbg("GetPrograms failed - render skipped","Render");
                    //Debug.LogWarning("Failed to get programs for mesh renderer.");
                }
            }

            private void BindSSBOs(GLRenderProgram program)
            {
                int count = 0;
                //TODO: make a more efficient way to bind these right before rendering (because apparently re-bufferbase-ing is important?)
                foreach (var buffer in _bufferCache.Where(x => x.Value.Data.Target == EBufferTarget.ShaderStorageBuffer))
                {
                    buffer.Value.BindSSBO(program);
                    count++;
                }
                if (count>0) Dbg($"BindSSBOs bound {count} SSBO(s)","Buffers");
            }

            private bool GetPrograms(
                GLMaterial material,
                [MaybeNullWhen(false)] out GLRenderProgram? vertexProgram,
                [MaybeNullWhen(false)] out GLRenderProgram? materialProgram)
                => Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines
                    ? GetPipelinePrograms(material, out vertexProgram, out materialProgram)
                    : GetCombinedProgram(out vertexProgram, out materialProgram);

            private bool GetCombinedProgram(out GLRenderProgram? vertexProgram, out GLRenderProgram? materialProgram)
            {
                if ((vertexProgram = materialProgram = _combinedProgram) is null)
                {
                    Dbg("GetCombinedProgram: program null","Programs");
                    return false;
                }

                if (!vertexProgram.Link())
                {
                    vertexProgram = null;
                    Dbg("GetCombinedProgram: link failed","Programs");
                    return false;
                }

                vertexProgram.Use();
                Dbg("GetCombinedProgram: linked & in use","Programs");
                return true;
            }

            private bool GetPipelinePrograms(GLMaterial material, out GLRenderProgram? vertexProgram, out GLRenderProgram? materialProgram)
            {
                _pipeline ??= Renderer.GenericToAPI<GLRenderProgramPipeline>(new XRRenderProgramPipeline())!;
                _pipeline.Bind();
                _pipeline.Clear(EProgramStageMask.AllShaderBits);

                materialProgram = material.SeparableProgram;
                var mask = materialProgram?.Data?.GetShaderTypeMask() ?? EProgramStageMask.None;
                bool includesVertexShader = mask.HasFlag(EProgramStageMask.VertexShaderBit);
                //bool includesGeometryShader = mask.HasFlag(EProgramStageMask.GeometryShaderBit);

                //bool ovrMultiView = false;
                //bool stereoPass = Engine.Rendering.State.IsStereoPass;
                //if (stereoPass)
                //{
                //    ovrMultiView = Engine.Rendering.State.HasOvrMultiViewExtension;
                //    //If ovr multiview is enabled, use the multiview vertex shader.
                //    //Geometry & both tesselation shader types are not supported with multiview.
                //    if (includesGeometryShader ||
                //        mask.HasFlag(EProgramStageMask.TessControlShaderBit) ||
                //        mask.HasFlag(EProgramStageMask.TessEvaluationShaderBit))
                //        ovrMultiView = false;
                //}

                return includesVertexShader
                    ? UseSuppliedVertexShader(out vertexProgram, materialProgram, mask)
                    : GenerateVertexShader(out vertexProgram, materialProgram, mask);
            }

            private bool UseSuppliedVertexShader(out GLRenderProgram? vertexProgram, GLRenderProgram? materialProgram, EProgramStageMask mask)
            {
                vertexProgram = materialProgram;
                if (materialProgram?.Link() ?? false)
                {
                    _pipeline!.Set(mask, materialProgram);
                    Dbg("UseSuppliedVertexShader: material vertex shader linked & set","Programs");
                    return true;
                }
                else
                {
                    Dbg("UseSuppliedVertexShader: link failed","Programs");
                    return false;
                }
            }

            private bool GenerateVertexShader(out GLRenderProgram? vertexProgram, GLRenderProgram? materialProgram, EProgramStageMask mask)
            {
                //If the material doesn't have a custom vertex shader, generate the default one for this mesh
                vertexProgram = _separatedVertexProgram;

                if (materialProgram?.Link() ?? false)
                    _pipeline!.Set(mask, materialProgram);
                else
                {
                    Dbg("GenerateVertexShader: material program link failed","Programs");
                    return false;
                }

                if (vertexProgram?.Link() ?? false)
                    _pipeline!.Set(EProgramStageMask.VertexShaderBit, vertexProgram);
                else
                {
                    Dbg("GenerateVertexShader: vertex program link failed","Programs");
                    return false;
                }

                Dbg("GenerateVertexShader: success","Programs");
                return true;
            }

            private static void SetMeshUniforms(Matrix4x4 modelMatrix, GLRenderProgram vertexProgram, EMeshBillboardMode billboardMode)
            {
                bool stereoPass = Engine.Rendering.State.IsStereoPass;
                var cam = Engine.Rendering.State.RenderingCamera;
                if (stereoPass)
                {
                    var rightCam = Engine.Rendering.State.RenderingStereoRightEyeCamera;
                    PassCameraUniforms(vertexProgram, cam, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeProjMatrix);
                    PassCameraUniforms(vertexProgram, rightCam, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeProjMatrix);
                }
                else
                    PassCameraUniforms(vertexProgram, cam, EEngineUniform.InverseViewMatrix, EEngineUniform.ProjMatrix);
                
                vertexProgram.Uniform(EEngineUniform.ModelMatrix, modelMatrix);
                vertexProgram.Uniform(EEngineUniform.VRMode, stereoPass);
                vertexProgram.Uniform(EEngineUniform.BillboardMode, (int)billboardMode);
            }

            private static void PassCameraUniforms(GLRenderProgram vertexProgram, XRCamera? camera, EEngineUniform invView, EEngineUniform proj)
            {
                Matrix4x4 inverseViewMatrix;
                Matrix4x4 projMatrix;

                if (camera != null)
                {
                    inverseViewMatrix = camera.Transform.RenderMatrix;
                    projMatrix = camera.ProjectionMatrix;
                }
                else
                {
                    //No camera? Everything will be rendered in NDC space instead of world space.
                    //This is used by point lights to render depth cubemaps, for example.
                    inverseViewMatrix = Matrix4x4.Identity;
                    projMatrix = Matrix4x4.Identity;
                }

                vertexProgram.Uniform($"{invView}{DefaultVertexShaderGenerator.VertexUniformSuffix}", inverseViewMatrix);
                vertexProgram.Uniform($"{proj}{DefaultVertexShaderGenerator.VertexUniformSuffix}", projMatrix);
            }

            private void OnSettingUniforms(GLRenderProgram vertexProgram, GLRenderProgram materialProgram)
                => MeshRenderer.OnSettingUniforms(vertexProgram.Data, materialProgram.Data);

            protected internal override void PostGenerated()
            {
                if (MeshRenderer.GenerateAsync)
                    Task.Run(GenProgramsAndBuffers);
                else
                    GenProgramsAndBuffers();
            }

            private void GenProgramsAndBuffers()
            {
                var material = Material;
                if (material is null)
                {
                    _combinedProgram?.Destroy();
                    _combinedProgram = null;

                    _separatedVertexProgram?.Destroy();
                    _separatedVertexProgram = null;

                    return;
                }

                Dbg("GenProgramsAndBuffers start", "Programs");
                MakeIndexBuffers();

                bool hasNoVertexShaders = (material?.Data.VertexShaders.Count ?? 0) == 0;

                CollectBuffers();
                Dbg($"Collected {_bufferCache.Count} buffer(s)","Buffers");

                //Determine how we're combining the material and vertex shader here
                if (Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines && material is not null)
                {
                    _combinedProgram = null;

                    IEnumerable<XRShader> shaders = material.Data.VertexShaders;

                    CreateSeparatedVertexProgram(
                        ref _separatedVertexProgram,
                        hasNoVertexShaders,
                        shaders,
                        Data.VertexShaderSelector,
                        () => Data.VertexShaderSource ?? string.Empty);
                    Dbg("GenProgramsAndBuffers: pipeline mode - separated vertex program initiated","Programs");
                }
                else
                {
                    _separatedVertexProgram = null;

                    IEnumerable<XRShader> shaders = material?.Data?.Shaders ?? [];
                    CreateCombinedProgram(
                        ref _combinedProgram,
                        hasNoVertexShaders,
                        shaders,
                        Data.VertexShaderSelector,
                        () => Data.VertexShaderSource ?? string.Empty);
                    Dbg("GenProgramsAndBuffers: combined program initiated","Programs");
                }
            }

            private void CreateCombinedProgram(
                ref GLRenderProgram? program,
                bool hasNoVertexShaders,
                IEnumerable<XRShader> shaders,
                Func<XRShader, bool> vertexShaderSelector,
                Func<string> vertexSourceGenerator)
            {
                XRShader vertexShader = hasNoVertexShaders
                    ? GenerateVertexShader(vertexSourceGenerator)
                    : FindVertexShader(shaders, vertexShaderSelector) ?? GenerateVertexShader(vertexSourceGenerator);

                if (!hasNoVertexShaders)
                    shaders = shaders.Where(x => x.Type != EShaderType.Vertex);

                shaders = shaders.Append(vertexShader);

                program = Renderer.GenericToAPI<GLRenderProgram>(new XRRenderProgram(false, false, shaders))!;
                program.PropertyChanged += CheckProgramLinked;
                InitiateLink(program);
            }

            private static XRShader? FindVertexShader(IEnumerable<XRShader> shaders, Func<XRShader, bool> vertexShaderSelector)
                => shaders.FirstOrDefault(x => x.Type == EShaderType.Vertex && vertexShaderSelector(x));

            private void CreateSeparatedVertexProgram(
                ref GLRenderProgram? vertexProgram,
                bool hasNoVertexShaders,
                IEnumerable<XRShader> vertexShaders,
                Func<XRShader, bool> vertexShaderSelector,
                Func<string> vertexSourceGenerator)
            {
                XRShader vertexShader = hasNoVertexShaders
                    ? GenerateVertexShader(vertexSourceGenerator)
                    : vertexShaders.FirstOrDefault(vertexShaderSelector) ?? GenerateVertexShader(vertexSourceGenerator);

                vertexProgram = Renderer.GenericToAPI<GLRenderProgram>(new XRRenderProgram(false, true, vertexShader))!;
                vertexProgram.PropertyChanged += CheckProgramLinked;
                InitiateLink(vertexProgram);
            }

            private static XRShader GenerateVertexShader(Func<string> vertexSourceGenerator)
                => new(EShaderType.Vertex, vertexSourceGenerator());

            /// <summary>
            /// Links the provided vertex program.
            /// </summary>
            /// <param name="vertexProgram"></param>
            private void InitiateLink(GLRenderProgram vertexProgram)
            {
                vertexProgram.Data.AllowLink();
                if (!Data.Parent.GenerateAsync)
                    vertexProgram.Link();
            }

            /// <summary>
            /// Collects the buffers from the mesh and the mesh renderer to bind them later.
            /// </summary>
            private void CollectBuffers()
            {
                _bufferCache = [];
                Dbg("CollectBuffers start","Buffers");

                var meshBuffers = Mesh?.Buffers as IEventDictionary<string, XRDataBuffer>;
                var rendBuffers = (IEventDictionary<string, XRDataBuffer>)MeshRenderer.Buffers;

                if (meshBuffers is not null)
                    foreach (var pair in meshBuffers)
                        _bufferCache.Add(pair.Key, Renderer.GenericToAPI<GLDataBuffer>(pair.Value)!);

                foreach (var pair in rendBuffers)
                    _bufferCache.Add(pair.Key, Renderer.GenericToAPI<GLDataBuffer>(pair.Value)!);
                Dbg($"CollectBuffers end. Total={_bufferCache.Count}","Buffers");

                //Data.Buffers.Added += Buffers_Added;
                //Data.Buffers.Removed += Buffers_Removed;
            }

            private void Buffers_Removed(string key, XRDataBuffer value)
            {
                _bufferCache.Remove(key);
            }

            private void Buffers_Added(string key, XRDataBuffer value)
            {
                _bufferCache.Add(key, Renderer.GenericToAPI<GLDataBuffer>(value)!);
            }

            private void CheckProgramLinked(object? s, IXRPropertyChangedEventArgs e)
            {
                GLRenderProgram? program = s as GLRenderProgram;
                if (e.PropertyName != nameof(GLRenderProgram.IsLinked) || !(program?.IsLinked ?? false))
                    return;
                Dbg("CheckProgramLinked: program linked - binding buffers","Programs");

                //Continue linking the program
                program.PropertyChanged -= CheckProgramLinked;
                if (MeshRenderer.GenerateAsync)
                    Engine.EnqueueMainThreadTask(() => BindBuffers(program));
                else
                    BindBuffers(program);
            }

            private bool _buffersBound = false;
            public bool BuffersBound
            {
                get => _buffersBound;
                private set => SetField(ref _buffersBound, value);
            }

            /// <summary>
            /// Creates OpenGL API buffers for the mesh's buffers.
            /// </summary>
            public void BindBuffers(GLRenderProgram program)
            {
                var mesh = Mesh;
                if (mesh is null || BuffersBound)
                {
                    if (mesh is null)
                        Dbg("BindBuffers early-out: mesh null","Buffers");
                    if (BuffersBound)
                        Dbg("BindBuffers early-out: already bound","Buffers");
                    return;
                }

                Renderer.BindForRender(this);
                Dbg("BindBuffers: binding attribute & index buffers","Buffers");

                foreach (GLDataBuffer buffer in _bufferCache.Values)
                {
                    buffer.Generate();
                    //buffer.BindToProgram(program, this);
                }
                
                if (TriangleIndicesBuffer is not null)
                    Api.VertexArrayElementBuffer(BindingId, TriangleIndicesBuffer.BindingId);
                if (LineIndicesBuffer is not null)
                    Api.VertexArrayElementBuffer(BindingId, LineIndicesBuffer.BindingId);
                if (PointIndicesBuffer is not null)
                    Api.VertexArrayElementBuffer(BindingId, PointIndicesBuffer.BindingId);

                Renderer.BindForRender(null);

                BuffersBound = true;
                Dbg("BindBuffers: complete","Buffers");
            }

            //struct DrawElementsIndirectCommand
            //{
            //    public uint count;
            //    public uint instanceCount;
            //    public uint firstIndex;
            //    public uint baseVertex;
            //    public uint baseInstance;
            //};

            //private GLDataBuffer _indirectBuffer;
            //private GLDataBuffer _combinedVertexBuffer;
            //private GLDataBuffer _combinedIndexBuffer;
            //private delegate uint DelVertexAction(XRMesh mesh, uint vertexIndex, uint destOffset);
            //public void InitializeIndirectBuffer()
            //{
            //    _combinedVertexBuffer = new GLDataBuffer(Renderer, new XRDataBuffer("CombinedVertexBuffer", EBufferTarget.ArrayBuffer, false) { Usage = EBufferUsage.StaticDraw });
            //    _combinedIndexBuffer = new GLDataBuffer(Renderer, new XRDataBuffer("CombinedIndexBuffer", EBufferTarget.ElementArrayBuffer, true) { Usage = EBufferUsage.StaticDraw });

            //    _indirectBuffer = new GLDataBuffer(Renderer, new XRDataBuffer("IndirectBuffer", EBufferTarget.DrawIndirectBuffer, true) { Usage = EBufferUsage.StaticDraw });
            //    _indirectBuffer.Data.Allocate((uint)sizeof(DrawElementsIndirectCommand), (uint)MeshRenderer.Submeshes.Length);

            //    List<DelVertexAction> vertexActions = [];
            //    List<XRMesh> meshes = [];

            //    CalcIndirectBufferSizes(out int stride, vertexActions, out int totalVertexCount, meshes, out uint totalIndexCount);

            //    _combinedVertexBuffer.Data.Allocate((uint)stride, (uint)totalVertexCount);
            //    _combinedIndexBuffer.Data.Allocate(sizeof(uint), totalIndexCount);

            //    WriteIndirectBufferData(vertexActions, meshes);

            //    _bufferCache.Add("IndirectBuffer", _indirectBuffer);
            //    _bufferCache.Add("CombinedVertexBuffer", _combinedVertexBuffer);
            //    _bufferCache.Add("CombinedIndexBuffer", _combinedIndexBuffer);
            //}

            //private void CalcIndirectBufferSizes(out int stride, List<DelVertexAction> vertexActions, out int totalVertexCount, List<XRMesh> meshes, out uint totalIndexCount)
            //{
            //    var submeshes = MeshRenderer.Submeshes;

            //    stride = 0;
            //    totalVertexCount = 0;
            //    totalIndexCount = 0u;
            //    for (int i = 0; i < submeshes.Length; i++)
            //    {
            //        var mesh = submeshes[i].Mesh;
            //        if (mesh is null)
            //            continue;

            //        totalVertexCount += mesh.VertexCount;
            //        meshes.Add(mesh);

            //        var triangleIndices = mesh.Triangles;
            //        if (triangleIndices is null)
            //            continue;

            //        uint indexCount = (uint)triangleIndices.Count * 3u;
            //        _indirectBuffer.Data.SetDataRawAtIndex((uint)i, new DrawElementsIndirectCommand
            //        {
            //            count = indexCount,
            //            instanceCount = Instances,
            //            firstIndex = totalIndexCount,
            //            baseVertex = 0,
            //            baseInstance = 0
            //        });
            //        totalIndexCount += indexCount;

            //        VoidPtr? pos = mesh.PositionsBuffer?.ClientSideSource?.Address;
            //        VoidPtr? nrm = mesh.NormalsBuffer?.ClientSideSource?.Address;
            //        VoidPtr? tan = mesh.TangentsBuffer?.ClientSideSource?.Address;
            //        var uvs = mesh.TexCoordBuffers;
            //        var clr = mesh.ColorBuffers;
            //        if (pos.HasValue)
            //        {
            //            stride += sizeof(Vector3);
            //            vertexActions.Add((mesh, index, offset) =>
            //            {
            //                Vector3? value = mesh.PositionsBuffer!.Get<Vector3>(index * (uint)sizeof(Vector3));
            //                if (value.HasValue)
            //                    _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
            //                return (uint)sizeof(Vector3);
            //            });
            //        }
            //        if (nrm.HasValue)
            //        {
            //            stride += sizeof(Vector3);
            //            vertexActions.Add((mesh, index, offset) =>
            //            {
            //                Vector3? value = mesh.NormalsBuffer!.Get<Vector3>(index * (uint)sizeof(Vector3));
            //                if (value.HasValue)
            //                    _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
            //                return (uint)sizeof(Vector3);
            //            });
            //        }
            //        if (tan.HasValue)
            //        {
            //            stride += sizeof(Vector3);
            //            vertexActions.Add((mesh, index, offset) =>
            //            {
            //                Vector3? value = mesh.TangentsBuffer!.Get<Vector3>(index * (uint)sizeof(Vector3));
            //                if (value.HasValue)
            //                    _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
            //                return (uint)sizeof(Vector3);
            //            });
            //        }
            //        if (uvs is not null)
            //        {
            //            stride += uvs.Length * sizeof(Vector2);
            //            for (int x = 0; x < uvs.Length; x++)
            //            {
            //                vertexActions.Add((mesh, index, offset) =>
            //                {
            //                    Vector2? value = mesh.TexCoordBuffers?[x].Get<Vector2>(index * (uint)sizeof(Vector2));
            //                    if (value.HasValue)
            //                        _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
            //                    return (uint)sizeof(Vector2);
            //                });
            //            }
            //        }
            //        if (clr is not null)
            //        {
            //            stride += clr.Length * sizeof(Vector4);
            //            for (int x = 0; x < clr.Length; x++)
            //            {
            //                vertexActions.Add((mesh, index, offset) =>
            //                {
            //                    Vector4? value = mesh.ColorBuffers?[x].Get<Vector4>(index * (uint)sizeof(Vector4));
            //                    if (value.HasValue)
            //                        _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
            //                    return (uint)sizeof(Vector4);
            //                });
            //            }
            //        }
            //    }
            //}

            //private void WriteIndirectBufferData(List<DelVertexAction> vertexActions, List<XRMesh> meshes)
            //{
            //    uint indexOffset = 0u;
            //    uint vertexOffset = 0u;
            //    foreach (var mesh in meshes)
            //    {
            //        for (uint x = 0; x < mesh.VertexCount; x++)
            //            foreach (var action in vertexActions)
            //                vertexOffset += action(mesh, x, vertexOffset);

            //        var triangleIndices = mesh.Triangles!;
            //        for (int x = 0; x < triangleIndices.Count; x++)
            //        {
            //            IndexTriangle? triangle = triangleIndices[x];
            //            _combinedIndexBuffer.Data.SetDataRawAtIndex(indexOffset + (uint)(x * 3 + 0), triangle.Point0);
            //            _combinedIndexBuffer.Data.SetDataRawAtIndex(indexOffset + (uint)(x * 3 + 1), triangle.Point1);
            //            _combinedIndexBuffer.Data.SetDataRawAtIndex(indexOffset + (uint)(x * 3 + 2), triangle.Point2);
            //        }
            //        indexOffset += (uint)triangleIndices.Count * 3u;
            //    }
            //}

            protected internal override void PostDeleted()
            {
                TriangleIndicesBuffer?.Dispose();
                TriangleIndicesBuffer = null;

                LineIndicesBuffer?.Dispose();
                LineIndicesBuffer = null;

                PointIndicesBuffer?.Dispose();
                PointIndicesBuffer = null;

                _pipeline?.Destroy();
                _pipeline = null;

                _separatedVertexProgram?.Destroy();
                _separatedVertexProgram = null;

                _combinedProgram?.Destroy();
                _combinedProgram = null;

                foreach (var buffer in _bufferCache)
                    buffer.Value.Destroy();
                _bufferCache = [];
            }
        }

        public GLMeshRenderer? ActiveMeshRenderer { get; private set; } = null;

        public void Unbind()
        {
            Api.BindVertexArray(0);
            ActiveMeshRenderer = null;
        }
        public void BindForRender(GLMeshRenderer? mesh)
        {
            Api.BindVertexArray(mesh?.BindingId ?? 0);
            ActiveMeshRenderer = mesh;
            if (mesh == null) return;

            // Ensure an element array buffer is bound (required for *ElementsIndirect* draws)
            // Prefer triangle indices, else lines, else points.
            GLDataBuffer? elem = mesh.TriangleIndicesBuffer ?? mesh.LineIndicesBuffer ?? mesh.PointIndicesBuffer;
            if (elem != null)
            {
                elem.Generate(); // guarantee GL name
                Api.VertexArrayElementBuffer(mesh.BindingId, elem.BindingId);
            }
        }
        public void RenderMesh(GLMeshRenderer manager, bool preservePreviouslyBound = true, uint instances = 1)
        {
            GLMeshRenderer? prev = ActiveMeshRenderer;
            BindForRender(manager);
            RenderCurrentMesh(instances);
            BindForRender(preservePreviouslyBound ? prev : null);
        }

        //TODO: use instances for left eye, right eye, visible scene mirrors, and shadow maps in parallel
        public void RenderCurrentMesh(uint instances = 1)
        {
            if (ActiveMeshRenderer?.Mesh is null)
                return;

            uint triangles = ActiveMeshRenderer.TriangleIndicesBuffer?.Data?.ElementCount ?? 0u;
            if (triangles > 0)
            {
                Api.DrawElementsInstanced(GLEnum.Triangles, triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null, instances);
            }
            uint lines = ActiveMeshRenderer.LineIndicesBuffer?.Data?.ElementCount ?? 0u;
            if (lines > 0)
            {
                Api.DrawElementsInstanced(GLEnum.Lines, lines, ToGLEnum(ActiveMeshRenderer.LineIndicesElementType), null, instances);
            }
            uint points = ActiveMeshRenderer.PointIndicesBuffer?.Data?.ElementCount ?? 0u;
            if (points > 0)
            {
                Api.DrawElementsInstanced(GLEnum.Points, points, ToGLEnum(ActiveMeshRenderer.PointIndicesElementType), null, instances);
            }
        }

        public void RenderCurrentMeshIndirect()
        {
            if (ActiveMeshRenderer?.Mesh is null)
                return;

            //uint triangles = ActiveMeshRenderer.TriangleIndicesBuffer?.Data?.ElementCount ?? 0u;
            //uint lines = ActiveMeshRenderer.LineIndicesBuffer?.Data?.ElementCount ?? 0u;
            //uint points = ActiveMeshRenderer.PointIndicesBuffer?.Data?.ElementCount ?? 0u;
            uint meshCount = 1u;

            //Api.BindBuffer(GLEnum.DrawIndirectBuffer, ActiveMeshRenderer.IndirectBuffer);
            Api.MultiDrawElementsIndirect(GLEnum.Triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null, meshCount, 0);
        }
    }
}