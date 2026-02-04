using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Physics.Physx
{
    /// <summary>
    /// Used to dynamically visualize debug primitives in the scene with instancing (very few draw calls).
    /// </summary>
    public class InstancedDebugVisualizer : XRBase, IDisposable
    {
        public InstancedDebugVisualizer() { }
        public InstancedDebugVisualizer(float pointSize, float lineWidth)
        {
            _pointSize = pointSize;
            _lineWidth = lineWidth;
        }
        public InstancedDebugVisualizer(
            Func<int, (Vector3 pos, ColorF4 color)>? getPoint,
            Func<int, (Vector3 pos0, Vector3 pos1, ColorF4 color)>? getLine,
            Func<int, (Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)>? getTriangle)
        {
            GetPoint = getPoint;
            GetLine = getLine;
            GetTriangle = getTriangle;
        }
        public InstancedDebugVisualizer(
            float pointSize,
            float lineWidth,
            Func<int, (Vector3 pos, ColorF4 color)>? getPoint,
            Func<int, (Vector3 pos0, Vector3 pos1, ColorF4 color)>? getLine,
            Func<int, (Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)>? getTriangle)
        {
            _pointSize = pointSize;
            _lineWidth = lineWidth;

            GetPoint = getPoint;
            GetLine = getLine;
            GetTriangle = getTriangle;
        }

        private float _pointSize = 0.005f;
        public float PointSize
        {
            get => _pointSize;
            set => SetField(ref _pointSize, value);
        }

        private float _lineWidth = 0.001f;
        public float LineWidth
        {
            get => _lineWidth;
            set => SetField(ref _lineWidth, value);
        }

        private Func<int, (Vector3 pos, ColorF4 color)>? _getPoint;
        public Func<int, (Vector3 pos, ColorF4 color)>? GetPoint
        {
            get => _getPoint;
            set => SetField(ref _getPoint, value);
        }

        private Func<int, (Vector3 pos0, Vector3 pos1, ColorF4 color)>? _getLine;
        public Func<int, (Vector3 pos0, Vector3 pos1, ColorF4 color)>? GetLine
        {
            get => _getLine;
            set => SetField(ref _getLine, value);
        }

        private Func<int, (Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)>? _getTriangle;
        public Func<int, (Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)>? GetTriangle
        {
            get => _getTriangle;
            set => SetField(ref _getTriangle, value);
        }

        private uint _pointCount = 0;
        public uint PointCount
        {
            get => _pointCount;
            set => SetField(ref _pointCount, value);
        }

        private uint _lineCount = 0;
        public uint LineCount
        {
            get => _lineCount;
            set => SetField(ref _lineCount, value);
        }

        private uint _triangleCount = 0;
        public uint TriangleCount
        {
            get => _triangleCount;
            set => SetField(ref _triangleCount, value);
        }

        private readonly Task?[] _renderTasks = new Task?[3];
        private XRMeshRenderer? _debugPointsRenderer = null;
        private XRMeshRenderer? _debugLinesRenderer = null;
        private XRMeshRenderer? _debugTrianglesRenderer = null;
        private XRDataBuffer? _debugPointsBuffer = null;
        private XRDataBuffer? _debugLinesBuffer = null;
        private XRDataBuffer? _debugTrianglesBuffer = null;

        /// <summary>
        /// Renders all points, lines, and triangles in the visualizer.
        /// </summary>
        public void Render()
        {
            if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                return;
            
            if (PointCount > 0)
                _debugPointsRenderer?.Render(null, PointCount);

            if (LineCount > 0)
                _debugLinesRenderer?.Render(null, LineCount);

            if (TriangleCount > 0)
                _debugTrianglesRenderer?.Render(null, TriangleCount);
        }

        /// <summary>
        /// Clears all points, lines, and triangles from the visualizer.
        /// </summary>
        public void Clear()
        {
            PointCount = 0;
            LineCount = 0;
            TriangleCount = 0;
        }

        /// <summary>
        /// Disposes of the visualizer and its resources.
        /// </summary>
        public void Dispose()
        {
            _debugPointsBuffer?.Dispose();
            _debugLinesBuffer?.Dispose();
            _debugTrianglesBuffer?.Dispose();
            _debugPointsRenderer?.Destroy();
            _debugLinesRenderer?.Destroy();
            _debugTrianglesRenderer?.Destroy();

            _debugPointsBuffer = null;
            _debugLinesBuffer = null;
            _debugTrianglesBuffer = null;
            _debugPointsRenderer = null;
            _debugLinesRenderer = null;
            _debugTrianglesRenderer = null;

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Adds a point to the visualizer, directly into the buffer.
        /// Not thread-safe.
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="color"></param>
        public void AddPoint(Vector3 pos, ColorF4 color)
            => SetPointAt((int)PointCount++, pos, color);

        /// <summary>
        /// Adds a line to the visualizer, directly into the buffer.
        /// Not thread-safe.
        /// </summary>
        /// <param name="pos0"></param>
        /// <param name="pos1"></param>
        /// <param name="color"></param>
        public void AddLine(Vector3 pos0, Vector3 pos1, ColorF4 color)
            => SetLineAt((int)LineCount++, pos0, pos1, color);

        /// <summary>
        /// Adds a triangle to the visualizer, directly into the buffer.
        /// Not thread-safe.
        /// </summary>
        /// <param name="pos0"></param>
        /// <param name="pos1"></param>
        /// <param name="pos2"></param>
        /// <param name="color"></param>
        public void AddTriangle(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)
            => SetTriangleAt((int)TriangleCount++, pos0, pos1, pos2, color);

        /// <summary>
        /// Populates the buffers, optionally in parallel, with the data from the provided 'Get' functions.
        /// Thread-safe.
        /// </summary>
        /// <param name="parallel"></param>
        public void PopulateBuffers(bool parallel = true)
        {
            if (parallel)
            {
                for (int i = 0; i < 3; i++)
                    _renderTasks[i] = null;

                if (PointCount > 0 && GetPoint is not null)
                    _renderTasks[0] = Task.Run(() => Parallel.For(0, (int)PointCount, SetPointAt));

                if (LineCount > 0 && GetLine is not null)
                    _renderTasks[1] = Task.Run(() => Parallel.For(0, (int)LineCount, SetLineAt));

                if (TriangleCount > 0 && GetTriangle is not null)
                    _renderTasks[2] = Task.Run(() => Parallel.For(0, (int)TriangleCount, SetTriangleAt));

                Task.WaitAll(_renderTasks.Where(x => x is not null).Select(x => x!));
            }
            else
            {
                for (int i = 0; i < PointCount; i++)
                    SetPointAt(i);
                for (int i = 0; i < LineCount; i++)
                    SetLineAt(i);
                for (int i = 0; i < TriangleCount; i++)
                    SetTriangleAt(i);
            }
        }

        private bool _fullPushTriangles = true;
        public void PushTrianglesBuffer()
        {
            if (TriangleCount == 0 || _debugTrianglesBuffer is null)
                return;

            if (_fullPushTriangles)
            {
                _debugTrianglesBuffer.PushData();
                _fullPushTriangles = false;
            }
            else
                _debugTrianglesBuffer.PushSubData();
        }

        private bool _fullPushLines = true;
        public void PushLinesBuffer()
        {
            if (LineCount == 0 || _debugLinesBuffer is null)
                return;

            if (_fullPushLines)
            {
                _debugLinesBuffer.PushData();
                _fullPushLines = false;
            }
            else
                _debugLinesBuffer.PushSubData();
        }

        private bool _fullPushPoints = true;
        public void PushPointsBuffer()
        {
            if (PointCount == 0 || _debugPointsBuffer is null)
                return;

            if (_fullPushPoints)
            {
                _debugPointsBuffer.PushData();
                _fullPushPoints = false;
            }
            else
                _debugPointsBuffer.PushSubData();
        }

        private void SetPointAt(int i)
        {
            var point = GetPoint?.Invoke(i) ?? (Vector3.Zero, ColorF4.White);
            var pos = point.pos;
            var color = point.color;
            SetPointAt(i, pos, color);
        }

        public unsafe void SetPointAt(int i, Vector3 pos, ColorF4 color)
        {
            var x = i * 8;

            var destPoints = (float*)_debugPointsBuffer!.Address;

            destPoints[x + 0] = pos.X;
            destPoints[x + 1] = pos.Y;
            destPoints[x + 2] = pos.Z;
            destPoints[x + 3] = 0.0f;

            destPoints[x + 4] = color.R;
            destPoints[x + 5] = color.G;
            destPoints[x + 6] = color.B;
            destPoints[x + 7] = color.A;
        }

        private void SetLineAt(int i)
        {
            var line = GetLine?.Invoke(i) ?? (Vector3.Zero, Vector3.Zero, ColorF4.White);
            var pos0 = line.pos0;
            var pos1 = line.pos1;
            var color = line.color;
            SetLineAt(i, pos0, pos1, color);
        }

        public unsafe void SetLineAt(int i, Vector3 pos0, Vector3 pos1, ColorF4 color)
        {
            var x = i * 12;

            var destLines = (float*)_debugLinesBuffer!.Address;

            destLines[x + 0] = pos0.X;
            destLines[x + 1] = pos0.Y;
            destLines[x + 2] = pos0.Z;
            destLines[x + 3] = 0.0f;

            destLines[x + 4] = pos1.X;
            destLines[x + 5] = pos1.Y;
            destLines[x + 6] = pos1.Z;
            destLines[x + 7] = 0.0f;

            destLines[x + 8] = color.R;
            destLines[x + 9] = color.G;
            destLines[x + 10] = color.B;
            destLines[x + 11] = color.A;
        }

        private void SetTriangleAt(int i)
        {
            var triangle = GetTriangle?.Invoke(i) ?? (Vector3.Zero, Vector3.Zero, Vector3.Zero, ColorF4.White);
            var pos0 = triangle.pos0;
            var pos1 = triangle.pos1;
            var pos2 = triangle.pos2;
            var color = triangle.color;
            SetTriangleAt(i, pos0, pos1, pos2, color);
        }

        public unsafe void SetTriangleAt(int i, Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)
        {
            var x = i * 16;

            var destTriangles = (float*)_debugTrianglesBuffer!.Address;

            destTriangles[x + 0] = pos0.X;
            destTriangles[x + 1] = pos0.Y;
            destTriangles[x + 2] = pos0.Z;
            destTriangles[x + 3] = 0.0f;

            destTriangles[x + 4] = pos1.X;
            destTriangles[x + 5] = pos1.Y;
            destTriangles[x + 6] = pos1.Z;
            destTriangles[x + 7] = 0.0f;

            destTriangles[x + 8] = pos2.X;
            destTriangles[x + 9] = pos2.Y;
            destTriangles[x + 10] = pos2.Z;
            destTriangles[x + 11] = 0.0f;

            destTriangles[x + 12] = color.R;
            destTriangles[x + 13] = color.G;
            destTriangles[x + 14] = color.B;
            destTriangles[x + 15] = color.A;
        }

        public void CreateOrResizePoints(uint count)
        {
            _debugPointsRenderer?.Material?.SetInt(0, (int)count);

            if (count == 0)
                return;

            _debugPointsRenderer ??= MakePointsRenderer();

            //8 floats: 3 for position, 1 for padding, 4 for color
            if (_debugPointsBuffer is not null)
                _fullPushPoints |= _debugPointsBuffer.Resize(count, true, true);
            else
            {
                _debugPointsBuffer = new XRDataBuffer(
                    "PointsBuffer",
                    EBufferTarget.ShaderStorageBuffer,
                    count,
                    EComponentType.Float,
                    8,
                    false,
                    false,
                    true)
                {
                    BindingIndexOverride = 0,
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _debugPointsRenderer.Buffers.Add(_debugPointsBuffer.AttributeName, _debugPointsBuffer);
                //_debugPointsRenderer.GenerateAsync = false;
                _debugPointsRenderer.SettingUniforms += _debugPointsRenderer_SettingUniforms;
                _fullPushPoints = true;
            }
        }

        private void _debugPointsRenderer_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            PushPointsBuffer();
        }

        public void CreateOrResizeLines(uint count)
        {
            _debugLinesRenderer?.Material?.SetInt(0, (int)count);

            if (count == 0)
                return;

            _debugLinesRenderer ??= MakeLineRenderer();

            //12 floats: 3 for position0, 1 for padding, 3 for position1, 1 for padding, 4 for color
            if (_debugLinesBuffer is not null)
                _fullPushLines |= _debugLinesBuffer.Resize(count * 3, true, true);
            else
            {
                _debugLinesBuffer = new XRDataBuffer(
                    "LinesBuffer",
                    EBufferTarget.ShaderStorageBuffer,
                    count * 3,
                    EComponentType.Float,
                    4,
                    false,
                    false,
                    true)
                {
                    BindingIndexOverride = 0,
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _debugLinesRenderer.Buffers?.Add(_debugLinesBuffer.AttributeName, _debugLinesBuffer);
                //_debugLinesRenderer.GenerateAsync = false;
                _debugLinesRenderer.SettingUniforms += _debugLinesRenderer_SettingUniforms;
                _fullPushLines = true;
            }
        }

        private void _debugLinesRenderer_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            PushLinesBuffer();
        }

        public void CreateOrResizeTriangles(uint count)
        {
            _debugTrianglesRenderer?.Material?.SetInt(0, (int)count);

            if (count == 0)
                return;

            _debugTrianglesRenderer ??= MakeTrianglesRenderer();

            //16 floats: 3 for position0, 1 for padding, 3 for position1, 1 for padding, 3 for position2, 1 for padding, 4 for color
            if (_debugTrianglesBuffer is not null)
                _fullPushTriangles |= _debugTrianglesBuffer.Resize(count, true, true);
            else
            {
                _debugTrianglesBuffer = new XRDataBuffer(
                    "TrianglesBuffer",
                    EBufferTarget.ShaderStorageBuffer,
                    count,
                    EComponentType.Float,
                    16,
                    false,
                    false,
                    true)
                {
                    BindingIndexOverride = 0,
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _debugTrianglesRenderer.Buffers?.Add(_debugTrianglesBuffer.AttributeName, _debugTrianglesBuffer);
                //_debugTrianglesRenderer.GenerateAsync = false;
                _debugTrianglesRenderer.SettingUniforms += _debugTrianglesRenderer_SettingUniforms;
                _fullPushTriangles = true;
            }
        }

        private void _debugTrianglesRenderer_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            PushTrianglesBuffer();
        }

        private XRMeshRenderer MakePointsRenderer()
        {
            var rend = new XRMeshRenderer(CreateDebugMesh(), CreateDebugPointMaterial());
            //rend.GetDefaultVersion().AllowShaderPipelines = false;
            return rend;
        }
        private XRMeshRenderer MakeLineRenderer()
        {
            var rend = new XRMeshRenderer(CreateDebugMesh(), CreateDebugLineMaterial());
            //rend.GetDefaultVersion().AllowShaderPipelines = false;
            return rend;
        }
        private XRMeshRenderer MakeTrianglesRenderer()
        {
            var rend = new XRMeshRenderer(CreateDebugMesh(), CreateDebugTriangleMaterial());
            //rend.GetDefaultVersion().AllowShaderPipelines = false;
            return rend;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(PointSize):
                    _debugPointsRenderer?.Material?.SetFloat(0, PointSize);
                    break;
                case nameof(LineWidth):
                    _debugLinesRenderer?.Material?.SetFloat(0, LineWidth);
                    break;
                case nameof(PointCount):
                    CreateOrResizePoints(PointCount);
                    break;
                case nameof(LineCount):
                    CreateOrResizeLines(LineCount);
                    break;
                case nameof(TriangleCount):
                    CreateOrResizeTriangles(TriangleCount);
                    break;
            }
        }

        private static XRMesh? CreateDebugMesh()
            => new([new Vertex(Vector3.Zero)]);

        private XRMaterial? CreateDebugPointMaterial()
        {
            if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                return null;
            
            try
            {
                XRShader vertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
                XRShader stereoMV2VertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitiveStereoMV2.vs"), EShaderType.Vertex);
                XRShader stereoNVVertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitiveStereoNV.vs"), EShaderType.Vertex);

                XRShader geomShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "PointInstance.gs"), EShaderType.Geometry);
                XRShader fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitivePoint.fs"), EShaderType.Fragment);
                ShaderVar[] vars =
                [
                    new ShaderFloat(PointSize, "PointSize"),
                    new ShaderInt(0, "TotalPoints"),
                ];
                var mat = new XRMaterial(vars, vertShader, stereoMV2VertShader, stereoNVVertShader, geomShader, fragShader);
                mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
                mat.RenderOptions.CullMode = ECullMode.None;
                mat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
                mat.RenderPass = (int)EDefaultRenderPass.OnTopForward;
                //mat.EnableTransparency();
                return mat;
            }
            catch (Exception e)
            {
                Debug.PhysicsException(e, "Failed to create debug point material.");
                Engine.Rendering.State.DebugInstanceRenderingAvailable = false;
                return null;
            }
        }
        private XRMaterial? CreateDebugLineMaterial()
        {
            if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                return null;

            try
            {
                XRShader vertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
                XRShader stereoMV2VertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitiveStereoMV2.vs"), EShaderType.Vertex);
                XRShader stereoNVVertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitiveStereoNV.vs"), EShaderType.Vertex);

                XRShader geomShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "LineInstance.gs"), EShaderType.Geometry);
                XRShader fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitive.fs"), EShaderType.Fragment);
                ShaderVar[] vars =
                [
                    new ShaderFloat(LineWidth, "LineWidth"),
                    new ShaderInt(0, "TotalLines"),
                ];
                var mat = new XRMaterial(vars, vertShader, stereoMV2VertShader, stereoNVVertShader, geomShader, fragShader);
                mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
                mat.RenderOptions.CullMode = ECullMode.None;
                mat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
                mat.RenderPass = (int)EDefaultRenderPass.OnTopForward;
                //mat.EnableTransparency();
                return mat;
            }
            catch (Exception e)
            {
                Debug.PhysicsException(e, "Failed to create debug line material.");
                Engine.Rendering.State.DebugInstanceRenderingAvailable = false;
                return null;
            }
        }
        private static XRMaterial? CreateDebugTriangleMaterial()
        {
            if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                return null;

            try
            {
                XRShader vertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
                XRShader stereoMV2VertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitiveStereoMV2.vs"), EShaderType.Vertex);
                XRShader stereoNVVertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitiveStereoNV.vs"), EShaderType.Vertex);

                XRShader geomShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "TriangleInstance.gs"), EShaderType.Geometry);
                XRShader fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "InstancedDebugPrimitive.fs"), EShaderType.Fragment);
                ShaderVar[] vars =
                [
                    new ShaderInt(0, "TotalTriangles"),
                ];
                var mat = new XRMaterial(vars, vertShader, stereoMV2VertShader, stereoNVVertShader, geomShader, fragShader);
                mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
                mat.RenderOptions.CullMode = ECullMode.None;
                mat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
                mat.RenderPass = (int)EDefaultRenderPass.OnTopForward;
                //mat.EnableTransparency();
                return mat;
            }
            catch (Exception e)
            {
                Debug.PhysicsException(e, "Failed to create debug triangle material.");
                Engine.Rendering.State.DebugInstanceRenderingAvailable = false;
                return null;
            }
        }
    }
}