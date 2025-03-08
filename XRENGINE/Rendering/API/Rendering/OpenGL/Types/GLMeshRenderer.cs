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
                private set => _triangleIndicesBuffer = value;
            }
            public GLDataBuffer? LineIndicesBuffer
            {
                get => _lineIndicesBuffer;
                private set => _lineIndicesBuffer = value;
            }
            public GLDataBuffer? PointIndicesBuffer
            {
                get => _pointIndicesBuffer;
                private set => _pointIndicesBuffer = value;
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
                Data.RenderRequested += Render;
                MeshRenderer.PropertyChanged += OnMeshRendererPropertyChanged;
                MeshRenderer.PropertyChanging += OnMeshRendererPropertyChanging;

                if (Mesh != null)
                    Mesh.DataChanged += OnMeshChanged;
                OnMeshChanged(Mesh);
            }

            protected override void UnlinkData()
            {
                Data.RenderRequested -= Render;
                MeshRenderer.PropertyChanged -= OnMeshRendererPropertyChanged;
                MeshRenderer.PropertyChanging -= OnMeshRendererPropertyChanging;

                if (Mesh != null)
                    Mesh.DataChanged -= OnMeshChanged;
            }

            private void OnMeshRendererPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
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
                _triangleIndicesBuffer?.Destroy();
                _triangleIndicesBuffer = null;

                _lineIndicesBuffer?.Destroy();
                _lineIndicesBuffer = null;

                _pointIndicesBuffer?.Destroy();
                _pointIndicesBuffer = null;

                var mesh = Mesh;
                if (mesh is null)
                    return;

                SetIndexBuffer(ref _triangleIndicesBuffer, ref _trianglesElementType, mesh, EPrimitiveType.Triangles);
                SetIndexBuffer(ref _lineIndicesBuffer, ref _lineIndicesElementType, mesh, EPrimitiveType.Lines);
                SetIndexBuffer(ref _pointIndicesBuffer, ref _pointIndicesElementType, mesh, EPrimitiveType.Points);
            }

            private void SetIndexBuffer(ref GLDataBuffer? buffer, ref IndexSize bufferElementSize, XRMesh mesh, EPrimitiveType type)
                => buffer = Renderer.GenericToAPI<GLDataBuffer>(mesh.GetIndexBuffer(type, out bufferElementSize))!;

            private void OnMeshChanged(XRMesh? mesh)
            {
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
                if (Data is null || !Renderer.Active)
                    return;

                if (!IsGenerated)
                    Generate();

                GLMaterial material = GetRenderMaterial(materialOverride);
                if (GetPrograms(material, out var vtx, out var mat))
                {
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
                }
                else
                {
                    //Debug.LogWarning("Failed to get programs for mesh renderer.");
                }
            }

            private void BindSSBOs(GLRenderProgram program)
            {
                //TODO: make a more efficient way to bind these right before rendering (because apparently re-bufferbase-ing is important?)
                foreach (var buffer in _bufferCache.Where(x => x.Value.Data.Target == EBufferTarget.ShaderStorageBuffer))
                {
                    var b = buffer.Value;
                    uint resourceIndex = b.Data.BindingIndexOverride ?? Api.GetProgramResourceIndex(program!.BindingId, GLEnum.ShaderStorageBlock, b.Data.BindingName);
                    if (resourceIndex != uint.MaxValue)
                    {
                        b.Bind();
                        Api.BindBufferBase(ToGLEnum(EBufferTarget.ShaderStorageBuffer), resourceIndex, b.BindingId);
                        //Api.ShaderStorageBlockBinding(vertexProgram.BindingId, resourceIndex, b.BindingId);
                        //b.PushSubData();
                        b.Unbind();
                    }
                    else
                    {
                        //Debug.LogWarning($"Failed to bind shader storage buffer '{b.GetDescribingName()}' to program '{program.GetDescribingName()}' with name '{b.Data.BindingName}'.");
                        //program.Data.Shaders.ForEach(x => Debug.Out(x?.Source?.Text ?? string.Empty));
                    }
                }
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
                    return false;

                if (!vertexProgram.Link())
                {
                    vertexProgram = null;
                    return false;
                }

                vertexProgram.Use();
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
                    return true;
                }
                else
                    return false;
            }

            private bool GenerateVertexShader(out GLRenderProgram? vertexProgram, GLRenderProgram? materialProgram, EProgramStageMask mask)
            {
                //If the material doesn't have a custom vertex shader, generate the default one for this mesh
                vertexProgram = _separatedVertexProgram;

                if (materialProgram?.Link() ?? false)
                    _pipeline!.Set(mask, materialProgram);
                else
                    return false;

                if (vertexProgram?.Link() ?? false)
                    _pipeline!.Set(EProgramStageMask.VertexShaderBit, vertexProgram);
                else
                    return false;

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
                    inverseViewMatrix = camera.Transform.WorldMatrix;
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
                MakeIndexBuffers();

                var material = Material;
                //Debug.LogWarning("No material found for mesh renderer, using invalid material.");
                //Don't use GetRenderMaterial here, global and local override materials are for current render only
                //material ??= Renderer.GenericToAPI<GLMaterial>(Engine.Rendering.State.CurrentRenderingPipeline!.InvalidMaterial);
                if (material is null)
                {
                    Debug.LogWarning("Failed to retrieve material or produce the invalid material.");
                    return;
                }

                bool hasNoVertexShaders = material.Data.VertexShaders.Count == 0;

                CollectBuffers();

                //Determine how we're combining the material and vertex shader here
                if (Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines)
                {
                    _combinedProgram = null;

                    IEnumerable<XRShader> shaders = material.Data.VertexShaders;

                    CreateSeparatedVertexProgram(
                        ref _separatedVertexProgram,
                        hasNoVertexShaders,
                        shaders,
                        Data.VertexShaderSelector,
                        () => Data.VertexShaderSource ?? string.Empty);
                }
                else
                {
                    _separatedVertexProgram = null;

                    IEnumerable<XRShader> shaders = material.Data.Shaders;
                    CreateCombinedProgram(
                        ref _combinedProgram,
                        hasNoVertexShaders,
                        shaders,
                        Data.VertexShaderSelector,
                        () => Data.VertexShaderSource ?? string.Empty);
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
                    : shaders.FirstOrDefault(x => x.Type == EShaderType.Vertex && vertexShaderSelector(x)) ?? GenerateVertexShader(vertexSourceGenerator);

                if (!hasNoVertexShaders)
                    shaders = shaders.Where(x => x.Type != EShaderType.Vertex);

                shaders = shaders.Append(vertexShader);
                
                program = Renderer.GenericToAPI<GLRenderProgram>(new XRRenderProgram(shaders, false) { Separable = false })!;
                program.PropertyChanged += CheckProgramLinked;
                InitiateLink(program);
            }

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

                vertexProgram = Renderer.GenericToAPI<GLRenderProgram>(new XRRenderProgram(false, vertexShader) { Separable = true})!;
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

                var meshBuffers = Mesh?.Buffers as IEventDictionary<string, XRDataBuffer>;
                var rendBuffers = (IEventDictionary<string, XRDataBuffer>)MeshRenderer.Buffers;

                if (meshBuffers is not null)
                    foreach (var pair in meshBuffers)
                        _bufferCache.Add(pair.Key, Renderer.GenericToAPI<GLDataBuffer>(pair.Value)!);

                foreach (var pair in rendBuffers)
                    _bufferCache.Add(pair.Key, Renderer.GenericToAPI<GLDataBuffer>(pair.Value)!);

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
                    return;

                Renderer.BindMesh(this);

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

                Renderer.BindMesh(null);

                BuffersBound = true;
            }

            struct DrawElementsIndirectCommand
            {
                public uint count;
                public uint instanceCount;
                public uint firstIndex;
                public uint baseVertex;
                public uint baseInstance;
            };

            private GLDataBuffer _indirectBuffer;
            private GLDataBuffer _combinedVertexBuffer;
            private GLDataBuffer _combinedIndexBuffer;
            private delegate uint DelVertexAction(XRMesh mesh, uint vertexIndex, uint destOffset);
            public void InitializeIndirectBuffer()
            {
                _combinedVertexBuffer = new GLDataBuffer(Renderer, new XRDataBuffer("CombinedVertexBuffer", EBufferTarget.ArrayBuffer, false) { Usage = EBufferUsage.StaticDraw });
                _combinedIndexBuffer = new GLDataBuffer(Renderer, new XRDataBuffer("CombinedIndexBuffer", EBufferTarget.ElementArrayBuffer, true) { Usage = EBufferUsage.StaticDraw });

                _indirectBuffer = new GLDataBuffer(Renderer, new XRDataBuffer("IndirectBuffer", EBufferTarget.DrawIndirectBuffer, true) { Usage = EBufferUsage.StaticDraw });
                _indirectBuffer.Data.Allocate((uint)sizeof(DrawElementsIndirectCommand), (uint)MeshRenderer.Submeshes.Length);

                List<DelVertexAction> vertexActions = [];
                List<XRMesh> meshes = [];

                CalcIndirectBufferSizes(out int stride, vertexActions, out int totalVertexCount, meshes, out uint totalIndexCount);

                _combinedVertexBuffer.Data.Allocate((uint)stride, (uint)totalVertexCount);
                _combinedIndexBuffer.Data.Allocate(sizeof(uint), totalIndexCount);

                WriteIndirectBufferData(vertexActions, meshes);

                _bufferCache.Add("IndirectBuffer", _indirectBuffer);
                _bufferCache.Add("CombinedVertexBuffer", _combinedVertexBuffer);
                _bufferCache.Add("CombinedIndexBuffer", _combinedIndexBuffer);
            }

            private void CalcIndirectBufferSizes(out int stride, List<DelVertexAction> vertexActions, out int totalVertexCount, List<XRMesh> meshes, out uint totalIndexCount)
            {
                var submeshes = MeshRenderer.Submeshes;

                stride = 0;
                totalVertexCount = 0;
                totalIndexCount = 0u;
                for (int i = 0; i < submeshes.Length; i++)
                {
                    var mesh = submeshes[i].Mesh;
                    if (mesh is null)
                        continue;

                    totalVertexCount += mesh.VertexCount;
                    meshes.Add(mesh);

                    var triangleIndices = mesh.Triangles;
                    if (triangleIndices is null)
                        continue;

                    uint indexCount = (uint)triangleIndices.Count * 3u;
                    _indirectBuffer.Data.SetDataRawAtIndex((uint)i, new DrawElementsIndirectCommand
                    {
                        count = indexCount,
                        instanceCount = Instances,
                        firstIndex = totalIndexCount,
                        baseVertex = 0,
                        baseInstance = 0
                    });
                    totalIndexCount += indexCount;

                    VoidPtr? pos = mesh.PositionsBuffer?.Source?.Address;
                    VoidPtr? nrm = mesh.NormalsBuffer?.Source?.Address;
                    VoidPtr? tan = mesh.TangentsBuffer?.Source?.Address;
                    var uvs = mesh.TexCoordBuffers;
                    var clr = mesh.ColorBuffers;
                    if (pos.HasValue)
                    {
                        stride += sizeof(Vector3);
                        vertexActions.Add((mesh, index, offset) =>
                        {
                            Vector3? value = mesh.PositionsBuffer!.Get<Vector3>(index * (uint)sizeof(Vector3));
                            if (value.HasValue)
                                _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
                            return (uint)sizeof(Vector3);
                        });
                    }
                    if (nrm.HasValue)
                    {
                        stride += sizeof(Vector3);
                        vertexActions.Add((mesh, index, offset) =>
                        {
                            Vector3? value = mesh.NormalsBuffer!.Get<Vector3>(index * (uint)sizeof(Vector3));
                            if (value.HasValue)
                                _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
                            return (uint)sizeof(Vector3);
                        });
                    }
                    if (tan.HasValue)
                    {
                        stride += sizeof(Vector3);
                        vertexActions.Add((mesh, index, offset) =>
                        {
                            Vector3? value = mesh.TangentsBuffer!.Get<Vector3>(index * (uint)sizeof(Vector3));
                            if (value.HasValue)
                                _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
                            return (uint)sizeof(Vector3);
                        });
                    }
                    if (uvs is not null)
                    {
                        stride += uvs.Length * sizeof(Vector2);
                        for (int x = 0; x < uvs.Length; x++)
                        {
                            vertexActions.Add((mesh, index, offset) =>
                            {
                                Vector2? value = mesh.TexCoordBuffers?[x].Get<Vector2>(index * (uint)sizeof(Vector2));
                                if (value.HasValue)
                                    _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
                                return (uint)sizeof(Vector2);
                            });
                        }
                    }
                    if (clr is not null)
                    {
                        stride += clr.Length * sizeof(Vector4);
                        for (int x = 0; x < clr.Length; x++)
                        {
                            vertexActions.Add((mesh, index, offset) =>
                            {
                                Vector4? value = mesh.ColorBuffers?[x].Get<Vector4>(index * (uint)sizeof(Vector4));
                                if (value.HasValue)
                                    _combinedVertexBuffer.Data.SetByOffset(offset, value.Value);
                                return (uint)sizeof(Vector4);
                            });
                        }
                    }
                }
            }

            private void WriteIndirectBufferData(List<DelVertexAction> vertexActions, List<XRMesh> meshes)
            {
                uint indexOffset = 0u;
                uint vertexOffset = 0u;
                foreach (var mesh in meshes)
                {
                    for (uint x = 0; x < mesh.VertexCount; x++)
                        foreach (var action in vertexActions)
                            vertexOffset += action(mesh, x, vertexOffset);

                    var triangleIndices = mesh.Triangles!;
                    for (int x = 0; x < triangleIndices.Count; x++)
                    {
                        IndexTriangle? triangle = triangleIndices[x];
                        _combinedIndexBuffer.Data.SetDataRawAtIndex(indexOffset + (uint)(x * 3 + 0), triangle.Point0);
                        _combinedIndexBuffer.Data.SetDataRawAtIndex(indexOffset + (uint)(x * 3 + 1), triangle.Point1);
                        _combinedIndexBuffer.Data.SetDataRawAtIndex(indexOffset + (uint)(x * 3 + 2), triangle.Point2);
                    }
                    indexOffset += (uint)triangleIndices.Count * 3u;
                }
            }

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

        public void BindMesh(GLMeshRenderer? mesh)
        {
            Api.BindVertexArray(mesh?.BindingId ?? 0);
            ActiveMeshRenderer = mesh;
        }
        public void RenderMesh(GLMeshRenderer manager, bool preservePreviouslyBound = true, uint instances = 1)
        {
            GLMeshRenderer? prev = ActiveMeshRenderer;
            BindMesh(manager);
            RenderCurrentMesh(instances);
            BindMesh(preservePreviouslyBound ? prev : null);
        }

        //TODO: use instances for left eye, right eye, visible scene mirrors, and shadow maps in parallel
        public void RenderCurrentMesh(uint instances = 1)
        {
            if (ActiveMeshRenderer?.Mesh is null)
                return;

            uint triangles = ActiveMeshRenderer.TriangleIndicesBuffer?.Data?.ElementCount ?? 0u;
            uint lines = ActiveMeshRenderer.LineIndicesBuffer?.Data?.ElementCount ?? 0u;
            uint points = ActiveMeshRenderer.PointIndicesBuffer?.Data?.ElementCount ?? 0u;

            if (triangles > 0)
            {
                //Api.DrawElements(GLEnum.Triangles, triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null);
                Api.DrawElementsInstanced(GLEnum.Triangles, triangles, ToGLEnum(ActiveMeshRenderer.TrianglesElementType), null, instances);
            }
            if (lines > 0)
            {
                //Api.DrawElements(GLEnum.Lines, lines, ToGLEnum(ActiveMeshRenderer.LineIndicesElementType), null);
                Api.DrawElementsInstanced(GLEnum.Lines, lines, ToGLEnum(ActiveMeshRenderer.LineIndicesElementType), null, instances);
            }
            if (points > 0)
            {
                //Api.DrawElements(GLEnum.Points, points, ToGLEnum(ActiveMeshRenderer.PointIndicesElementType), null);
                Api.DrawElementsInstanced(GLEnum.Points, points, ToGLEnum(ActiveMeshRenderer.PointIndicesElementType), null, instances);
            }

            //Api.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.ClientMappedBufferBarrierBit);
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

        public IGLTexture? BoundTexture { get; set; }

        /// <summary>
        /// Modifies the rendering API's state to adhere to the given material's settings.
        /// </summary>
        /// <param name="r"></param>
        private void ApplyRenderParameters(RenderingParameters r)
        {
            if (r is null)
                return;

            //Api.PointSize(r.PointSize);
            //Api.LineWidth(r.LineWidth.Clamp(0.0f, 1.0f));
            Api.ColorMask(r.WriteRed, r.WriteGreen, r.WriteBlue, r.WriteAlpha);
            Api.FrontFace(ToGLEnum(r.Winding));

            ApplyCulling(r);
            ApplyDepth(r);
            ApplyBlending(r);
            ApplyStencil(r);
            //Alpha testing is done in-shader
        }

        private GLEnum ToGLEnum(EWinding winding)
            => winding switch
            {
                EWinding.Clockwise => GLEnum.CW,
                EWinding.CounterClockwise => GLEnum.Ccw,
                _ => GLEnum.Ccw
            };

        private void ApplyStencil(RenderingParameters r)
        {
            switch (r.StencilTest.Enabled)
            {
                case ERenderParamUsage.Enabled:
                    {
                        StencilTest st = r.StencilTest;
                        StencilTestFace b = st.BackFace;
                        StencilTestFace f = st.FrontFace;
                        Api.StencilOpSeparate(GLEnum.Back,
                            (StencilOp)(int)b.BothFailOp,
                            (StencilOp)(int)b.StencilPassDepthFailOp,
                            (StencilOp)(int)b.BothPassOp);
                        Api.StencilOpSeparate(GLEnum.Front,
                            (StencilOp)(int)f.BothFailOp,
                            (StencilOp)(int)f.StencilPassDepthFailOp,
                            (StencilOp)(int)f.BothPassOp);
                        Api.StencilMaskSeparate(GLEnum.Back, b.WriteMask);
                        Api.StencilMaskSeparate(GLEnum.Front, f.WriteMask);
                        Api.StencilFuncSeparate(GLEnum.Back,
                            StencilFunction.Never + (int)b.Function, b.Reference, b.ReadMask);
                        Api.StencilFuncSeparate(GLEnum.Front,
                            StencilFunction.Never + (int)f.Function, f.Reference, f.ReadMask);
                        break;
                    }

                case ERenderParamUsage.Disabled:
                    //GL.Disable(EnableCap.StencilTest);
                    Api.StencilMask(0);
                    Api.StencilOp(GLEnum.Keep, GLEnum.Keep, GLEnum.Keep);
                    Api.StencilFunc(StencilFunction.Always, 0, 0);
                    break;
            }
        }

        private void ApplyBlending(RenderingParameters r)
        {
            if (r.BlendModeAllDrawBuffers is not null)
            {
                var x = r.BlendModeAllDrawBuffers;
                if (x.Enabled == ERenderParamUsage.Enabled)
                {
                    Api.Enable(EnableCap.Blend);

                    Api.BlendEquationSeparate(
                        ToGLEnum(x.RgbEquation),
                        ToGLEnum(x.AlphaEquation));

                    Api.BlendFuncSeparate(
                        ToGLEnum(x.RgbSrcFactor),
                        ToGLEnum(x.RgbDstFactor),
                        ToGLEnum(x.AlphaSrcFactor),
                        ToGLEnum(x.AlphaDstFactor));
                }
                else if (x.Enabled == ERenderParamUsage.Disabled)
                    Api.Disable(EnableCap.Blend);
            }
            else if (r.BlendModesPerDrawBuffer is not null)
            {
                if (r.BlendModesPerDrawBuffer.Any(r => r.Value.Enabled == ERenderParamUsage.Enabled))
                {
                    Api.Enable(EnableCap.Blend);
                    foreach (KeyValuePair<uint, BlendMode> pair in r.BlendModesPerDrawBuffer)
                    {
                        uint drawBuffer = pair.Key;
                        BlendMode x = pair.Value;
                        if (x.Enabled == ERenderParamUsage.Enabled)
                        {
                            Api.BlendEquationSeparate(
                                drawBuffer,
                                ToGLEnum(x.RgbEquation),
                                ToGLEnum(x.AlphaEquation));

                            Api.BlendFuncSeparate(
                                drawBuffer,
                                ToGLEnum(x.RgbSrcFactor),
                                ToGLEnum(x.RgbDstFactor),
                                ToGLEnum(x.AlphaSrcFactor),
                                ToGLEnum(x.AlphaDstFactor));
                        }
                        else
                        {
                            //Apply a blend mode that mimics non-blending for this draw buffer

                            Api.BlendEquationSeparate(
                                drawBuffer,
                                GLEnum.FuncAdd,
                                GLEnum.FuncAdd);

                            Api.BlendFuncSeparate(
                                drawBuffer,
                                GLEnum.One,
                                GLEnum.Zero,
                                GLEnum.One,
                                GLEnum.Zero);
                        }
                    }
                }
                else if (r.BlendModesPerDrawBuffer.Count == 0 || r.BlendModesPerDrawBuffer.Any(r => r.Value.Enabled == ERenderParamUsage.Disabled))
                    Api.Disable(EnableCap.Blend);
            }
            else
                Api.Disable(EnableCap.Blend);
        }

        private void ApplyCulling(RenderingParameters r)
        {
            if (r.CullMode == ECullMode.None)
                Api.Disable(EnableCap.CullFace);
            else
            {
                Api.Enable(EnableCap.CullFace);
                Api.CullFace(ToGLEnum(r.CullMode));
            }
        }

        private void ApplyDepth(RenderingParameters r)
        {
            switch (r.DepthTest.Enabled)
            {
                case ERenderParamUsage.Enabled:
                    Api.Enable(EnableCap.DepthTest);
                    Api.DepthFunc(ToGLEnum(r.DepthTest.Function));
                    Api.DepthMask(r.DepthTest.UpdateDepth);
                    break;

                case ERenderParamUsage.Disabled:
                    Api.Disable(EnableCap.DepthTest);
                    break;
            }
        }

        private GLEnum ToGLEnum(EBlendingFactor factor)
            => factor switch
            {
                EBlendingFactor.Zero => GLEnum.Zero,
                EBlendingFactor.One => GLEnum.One,
                EBlendingFactor.SrcColor => GLEnum.SrcColor,
                EBlendingFactor.OneMinusSrcColor => GLEnum.OneMinusSrcColor,
                EBlendingFactor.DstColor => GLEnum.DstColor,
                EBlendingFactor.OneMinusDstColor => GLEnum.OneMinusDstColor,
                EBlendingFactor.SrcAlpha => GLEnum.SrcAlpha,
                EBlendingFactor.OneMinusSrcAlpha => GLEnum.OneMinusSrcAlpha,
                EBlendingFactor.DstAlpha => GLEnum.DstAlpha,
                EBlendingFactor.OneMinusDstAlpha => GLEnum.OneMinusDstAlpha,
                EBlendingFactor.ConstantColor => GLEnum.ConstantColor,
                EBlendingFactor.OneMinusConstantColor => GLEnum.OneMinusConstantColor,
                EBlendingFactor.ConstantAlpha => GLEnum.ConstantAlpha,
                EBlendingFactor.OneMinusConstantAlpha => GLEnum.OneMinusConstantAlpha,
                EBlendingFactor.SrcAlphaSaturate => GLEnum.SrcAlphaSaturate,
                _ => GLEnum.Zero,
            };

        private GLEnum ToGLEnum(EBlendEquationMode equation)
            => equation switch
            {
                EBlendEquationMode.FuncAdd => GLEnum.FuncAdd,
                EBlendEquationMode.FuncSubtract => GLEnum.FuncSubtract,
                EBlendEquationMode.FuncReverseSubtract => GLEnum.FuncReverseSubtract,
                EBlendEquationMode.Min => GLEnum.Min,
                EBlendEquationMode.Max => GLEnum.Max,
                _ => GLEnum.FuncAdd,
            };

        private GLEnum ToGLEnum(EComparison function)
            => function switch
            {
                EComparison.Never => GLEnum.Never,
                EComparison.Less => GLEnum.Less,
                EComparison.Equal => GLEnum.Equal,
                EComparison.Lequal => GLEnum.Lequal,
                EComparison.Greater => GLEnum.Greater,
                EComparison.Nequal => GLEnum.Notequal,
                EComparison.Gequal => GLEnum.Gequal,
                EComparison.Always => GLEnum.Always,
                _ => GLEnum.Never,
            };

        private GLEnum ToGLEnum(ECullMode cullMode)
            => cullMode switch
            {
                ECullMode.Front => GLEnum.Front,
                ECullMode.Back => GLEnum.Back,
                _ => GLEnum.FrontAndBack,
            };

        private GLEnum ToGLEnum(IndexSize elementType)
            => elementType switch
            {
                IndexSize.Byte => GLEnum.UnsignedByte,
                IndexSize.TwoBytes => GLEnum.UnsignedShort,
                IndexSize.FourBytes => GLEnum.UnsignedInt,
                _ => GLEnum.UnsignedInt,
            };

        private GLEnum ToGLEnum(EPrimitiveType type)
            => type switch
            {
                EPrimitiveType.Points => GLEnum.Points,
                EPrimitiveType.Lines => GLEnum.Lines,
                EPrimitiveType.LineLoop => GLEnum.LineLoop,
                EPrimitiveType.LineStrip => GLEnum.LineStrip,
                EPrimitiveType.Triangles => GLEnum.Triangles,
                EPrimitiveType.TriangleStrip => GLEnum.TriangleStrip,
                EPrimitiveType.TriangleFan => GLEnum.TriangleFan,
                EPrimitiveType.LinesAdjacency => GLEnum.LinesAdjacency,
                EPrimitiveType.LineStripAdjacency => GLEnum.LineStripAdjacency,
                EPrimitiveType.TrianglesAdjacency => GLEnum.TrianglesAdjacency,
                EPrimitiveType.TriangleStripAdjacency => GLEnum.TriangleStripAdjacency,
                EPrimitiveType.Patches => GLEnum.Patches,
                _ => GLEnum.Triangles,
            };
    }
}