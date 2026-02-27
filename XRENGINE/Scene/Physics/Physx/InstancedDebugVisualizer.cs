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

        /// <summary>
        /// Bulk-write delegate for points. Receives (destination float*, element count).
        /// The destination layout per element is 8 floats: posX, posY, posZ, pad(0), R, G, B, A.
        /// Set this to enable <see cref="EDebugVisualizerPopulationMode.DirectMemory"/>.
        /// </summary>
        public Action<IntPtr, int>? BulkPopulatePoints { get; set; }

        /// <summary>
        /// Bulk-write delegate for lines. Receives (destination float*, element count).
        /// The destination layout per element is 12 floats: pos0X,Y,Z, pad, pos1X,Y,Z, pad, R,G,B,A.
        /// Set this to enable <see cref="EDebugVisualizerPopulationMode.DirectMemory"/>.
        /// </summary>
        public Action<IntPtr, int>? BulkPopulateLines { get; set; }

        /// <summary>
        /// Bulk-write delegate for triangles. Receives (destination float*, element count).
        /// The destination layout per element is 16 floats: pos0X,Y,Z,pad, pos1X,Y,Z,pad, pos2X,Y,Z,pad, R,G,B,A.
        /// Set this to enable <see cref="EDebugVisualizerPopulationMode.DirectMemory"/>.
        /// </summary>
        public Action<IntPtr, int>? BulkPopulateTriangles { get; set; }

        /// <summary>
        /// Compressed bulk-write delegate for points. Receives (destination float*, element count).
        /// Layout per element is 4 floats: posX, posY, posZ, packedColor (uint as float bits).
        /// </summary>
        public Action<IntPtr, int>? BulkPopulatePointsCompressed { get; set; }

        /// <summary>
        /// Compressed bulk-write delegate for lines. Receives (destination float*, element count).
        /// Layout per element is 7 floats: pos0X,Y,Z, pos1X,Y,Z, packedColor.
        /// </summary>
        public Action<IntPtr, int>? BulkPopulateLinesCompressed { get; set; }

        /// <summary>
        /// Compressed bulk-write delegate for triangles. Receives (destination float*, element count).
        /// Layout per element is 10 floats: pos0X,Y,Z, pos1X,Y,Z, pos2X,Y,Z, packedColor.
        /// </summary>
        public Action<IntPtr, int>? BulkPopulateTrianglesCompressed { get; set; }

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

        private bool _useCompressedBuffers = true;
        /// <summary>
        /// When true, buffers use the compressed layout (packed uint color, no position padding).
        /// Changing this disposes existing renderers/buffers so they are recreated with the new format.
        /// </summary>
        public bool UseCompressedBuffers
        {
            get => _useCompressedBuffers;
            set => SetField(ref _useCompressedBuffers, value);
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
        /// Populates the buffers using the strategy selected in editor preferences.
        /// Thread-safe.
        /// </summary>
        public void PopulateBuffers()
        {
            var mode = Engine.EditorPreferences?.Debug?.DebugVisualizerPopulationMode
                ?? EDebugVisualizerPopulationMode.Tasks;
            PopulateBuffers(mode);
        }

        /// <summary>
        /// Populates the buffers using the specified strategy.
        /// Thread-safe.
        /// </summary>
        public void PopulateBuffers(EDebugVisualizerPopulationMode mode)
        {
            switch (mode)
            {
                case EDebugVisualizerPopulationMode.TasksWithParallelFor:
                    PopulateBuffersTasksWithParallelFor();
                    break;
                case EDebugVisualizerPopulationMode.Tasks:
                    PopulateBuffersTasks();
                    break;
                case EDebugVisualizerPopulationMode.ParallelFor:
                    PopulateBuffersParallelFor();
                    break;
                case EDebugVisualizerPopulationMode.JobSystem:
                    PopulateBuffersJobSystem();
                    break;
                case EDebugVisualizerPopulationMode.Sequential:
                default:
                    PopulateBuffersSequential();
                    break;
                case EDebugVisualizerPopulationMode.DirectMemory:
                    PopulateBuffersDirectMemory();
                    break;
            }
        }

        /// <summary>
        /// Legacy path: Task.Run wrapping Parallel.For per type + Task.WaitAll.
        /// </summary>
        private void PopulateBuffersTasksWithParallelFor()
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

        /// <summary>
        /// Task.Run per type with sequential inner loop + Task.WaitAll.
        /// </summary>
        private void PopulateBuffersTasks()
        {
            for (int i = 0; i < 3; i++)
                _renderTasks[i] = null;

            int ptCount = (int)PointCount;
            int lnCount = (int)LineCount;
            int triCount = (int)TriangleCount;

            if (ptCount > 0 && GetPoint is not null)
                _renderTasks[0] = Task.Run(() =>
                {
                    for (int i = 0; i < ptCount; i++)
                        SetPointAt(i);
                });

            if (lnCount > 0 && GetLine is not null)
                _renderTasks[1] = Task.Run(() =>
                {
                    for (int i = 0; i < lnCount; i++)
                        SetLineAt(i);
                });

            if (triCount > 0 && GetTriangle is not null)
                _renderTasks[2] = Task.Run(() =>
                {
                    for (int i = 0; i < triCount; i++)
                        SetTriangleAt(i);
                });

            Task.WaitAll(_renderTasks.Where(x => x is not null).Select(x => x!));
        }

        /// <summary>
        /// Parallel.For per type, run sequentially type-by-type.
        /// </summary>
        private void PopulateBuffersParallelFor()
        {
            if (PointCount > 0 && GetPoint is not null)
                Parallel.For(0, (int)PointCount, SetPointAt);

            if (LineCount > 0 && GetLine is not null)
                Parallel.For(0, (int)LineCount, SetLineAt);

            if (TriangleCount > 0 && GetTriangle is not null)
                Parallel.For(0, (int)TriangleCount, SetTriangleAt);
        }

        /// <summary>
        /// Schedules ActionJobs on engine persistent worker threads.
        /// </summary>
        private void PopulateBuffersJobSystem()
        {
            int ptCount = (int)PointCount;
            int lnCount = (int)LineCount;
            int triCount = (int)TriangleCount;

            JobHandle hp = default, hl = default, ht = default;

            if (ptCount > 0 && GetPoint is not null)
                hp = Engine.Jobs.Schedule(new ActionJob(() =>
                {
                    for (int i = 0; i < ptCount; i++)
                        SetPointAt(i);
                }), JobPriority.High);

            if (lnCount > 0 && GetLine is not null)
                hl = Engine.Jobs.Schedule(new ActionJob(() =>
                {
                    for (int i = 0; i < lnCount; i++)
                        SetLineAt(i);
                }), JobPriority.High);

            if (triCount > 0 && GetTriangle is not null)
                ht = Engine.Jobs.Schedule(new ActionJob(() =>
                {
                    for (int i = 0; i < triCount; i++)
                        SetTriangleAt(i);
                }), JobPriority.High);

            hp.Wait();
            hl.Wait();
            ht.Wait();
        }

        /// <summary>
        /// All on the calling thread.
        /// </summary>
        private void PopulateBuffersSequential()
        {
            for (int i = 0; i < PointCount; i++)
                SetPointAt(i);
            for (int i = 0; i < LineCount; i++)
                SetLineAt(i);
            for (int i = 0; i < TriangleCount; i++)
                SetTriangleAt(i);
        }

        /// <summary>
        /// Calls the bulk-write delegates directly, bypassing per-element Func delegates.
        /// Selects expanded or compressed bulk delegates based on <see cref="UseCompressedBuffers"/>.
        /// Falls back to sequential per-element path for any type without a bulk delegate.
        /// </summary>
        private void PopulateBuffersDirectMemory()
        {
            int ptCount = (int)PointCount;
            int lnCount = (int)LineCount;
            int triCount = (int)TriangleCount;

            if (ptCount > 0)
            {
                var bulkPt = _useCompressedBuffers ? BulkPopulatePointsCompressed : BulkPopulatePoints;
                if (bulkPt is not null && _debugPointsBuffer is not null)
                    bulkPt(_debugPointsBuffer.Address, ptCount);
                else
                    for (int i = 0; i < ptCount; i++)
                        SetPointAt(i);
            }

            if (lnCount > 0)
            {
                var bulkLn = _useCompressedBuffers ? BulkPopulateLinesCompressed : BulkPopulateLines;
                if (bulkLn is not null && _debugLinesBuffer is not null)
                    bulkLn(_debugLinesBuffer.Address, lnCount);
                else
                    for (int i = 0; i < lnCount; i++)
                        SetLineAt(i);
            }

            if (triCount > 0)
            {
                var bulkTri = _useCompressedBuffers ? BulkPopulateTrianglesCompressed : BulkPopulateTriangles;
                if (bulkTri is not null && _debugTrianglesBuffer is not null)
                    bulkTri(_debugTrianglesBuffer.Address, triCount);
                else
                    for (int i = 0; i < triCount; i++)
                        SetTriangleAt(i);
            }
        }

        /// <summary>
        /// Packs a floating-point RGBA color into a single uint (R in bits 0-7, G 8-15, B 16-23, A 24-31).
        /// Matches the layout expected by GLSL <c>unpackUnorm4x8</c>.
        /// </summary>
        private static uint PackColor(ColorF4 color)
        {
            uint r = (uint)(color.R * 255.0f + 0.5f);
            uint g = (uint)(color.G * 255.0f + 0.5f);
            uint b = (uint)(color.B * 255.0f + 0.5f);
            uint a = (uint)(color.A * 255.0f + 0.5f);
            return r | (g << 8) | (b << 16) | (a << 24);
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
            if (_useCompressedBuffers)
            {
                var x = i * 4;
                var dest = (float*)_debugPointsBuffer!.Address.Pointer;
                dest[x + 0] = pos.X;
                dest[x + 1] = pos.Y;
                dest[x + 2] = pos.Z;
                ((uint*)dest)[x + 3] = PackColor(color);
            }
            else
            {
                var x = i * 8;
                var destPoints = (float*)_debugPointsBuffer!.Address.Pointer;
                destPoints[x + 0] = pos.X;
                destPoints[x + 1] = pos.Y;
                destPoints[x + 2] = pos.Z;
                destPoints[x + 3] = 0.0f;
                destPoints[x + 4] = color.R;
                destPoints[x + 5] = color.G;
                destPoints[x + 6] = color.B;
                destPoints[x + 7] = color.A;
            }
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
            if (_useCompressedBuffers)
            {
                var x = i * 7;
                var dest = (float*)_debugLinesBuffer!.Address.Pointer;
                dest[x + 0] = pos0.X;
                dest[x + 1] = pos0.Y;
                dest[x + 2] = pos0.Z;
                dest[x + 3] = pos1.X;
                dest[x + 4] = pos1.Y;
                dest[x + 5] = pos1.Z;
                ((uint*)dest)[x + 6] = PackColor(color);
            }
            else
            {
                var x = i * 12;
                var destLines = (float*)_debugLinesBuffer!.Address.Pointer;
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
            if (_useCompressedBuffers)
            {
                var x = i * 10;
                var dest = (float*)_debugTrianglesBuffer!.Address.Pointer;
                dest[x + 0] = pos0.X;
                dest[x + 1] = pos0.Y;
                dest[x + 2] = pos0.Z;
                dest[x + 3] = pos1.X;
                dest[x + 4] = pos1.Y;
                dest[x + 5] = pos1.Z;
                dest[x + 6] = pos2.X;
                dest[x + 7] = pos2.Y;
                dest[x + 8] = pos2.Z;
                ((uint*)dest)[x + 9] = PackColor(color);
            }
            else
            {
                var x = i * 16;
                var destTriangles = (float*)_debugTrianglesBuffer!.Address.Pointer;
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
        }

        public void CreateOrResizePoints(uint count)
        {
            _debugPointsRenderer?.Material?.SetInt(0, (int)count);

            if (count == 0)
                return;

            _debugPointsRenderer ??= MakePointsRenderer();

            // Expanded: 8 floats (pos3 + pad + color4). Compressed: 4 floats (pos3 + packedColor).
            uint componentsPerElement = _useCompressedBuffers ? 4u : 8u;
            if (_debugPointsBuffer is not null)
                _fullPushPoints |= _debugPointsBuffer.Resize(count, true, true);
            else
            {
                _debugPointsBuffer = new XRDataBuffer(
                    "PointsBuffer",
                    EBufferTarget.ShaderStorageBuffer,
                    count,
                    EComponentType.Float,
                    componentsPerElement,
                    false,
                    false,
                    true)
                {
                    BindingIndexOverride = 0,
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _debugPointsRenderer.Buffers.Add(_debugPointsBuffer.AttributeName, _debugPointsBuffer);
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

            // Expanded: 3 vec4s per line (count*3 elements, 4 components). Compressed: 7 floats (count elements, 7 components).
            uint elementCount = _useCompressedBuffers ? count : count * 3;
            uint componentsPerElement = _useCompressedBuffers ? 7u : 4u;
            if (_debugLinesBuffer is not null)
                _fullPushLines |= _debugLinesBuffer.Resize(elementCount, true, true);
            else
            {
                _debugLinesBuffer = new XRDataBuffer(
                    "LinesBuffer",
                    EBufferTarget.ShaderStorageBuffer,
                    elementCount,
                    EComponentType.Float,
                    componentsPerElement,
                    false,
                    false,
                    true)
                {
                    BindingIndexOverride = 0,
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _debugLinesRenderer.Buffers?.Add(_debugLinesBuffer.AttributeName, _debugLinesBuffer);
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

            // Expanded: 16 floats per triangle. Compressed: 10 floats per triangle.
            uint componentsPerElement = _useCompressedBuffers ? 10u : 16u;
            if (_debugTrianglesBuffer is not null)
                _fullPushTriangles |= _debugTrianglesBuffer.Resize(count, true, true);
            else
            {
                _debugTrianglesBuffer = new XRDataBuffer(
                    "TrianglesBuffer",
                    EBufferTarget.ShaderStorageBuffer,
                    count,
                    EComponentType.Float,
                    componentsPerElement,
                    false,
                    false,
                    true)
                {
                    BindingIndexOverride = 0,
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _debugTrianglesRenderer.Buffers?.Add(_debugTrianglesBuffer.AttributeName, _debugTrianglesBuffer);
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
                case nameof(UseCompressedBuffers):
                    // Dispose everything so buffers + renderers are recreated with the new format/shaders.
                    DisposeRenderersAndBuffers();
                    break;
            }
        }

        /// <summary>
        /// Disposes all renderers and buffers, forcing recreation on next use.
        /// Called when buffer format changes.
        /// </summary>
        private void DisposeRenderersAndBuffers()
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

            _fullPushPoints = true;
            _fullPushLines = true;
            _fullPushTriangles = true;
        }

        private static XRMesh? CreateDebugMesh()
            => new([new Vertex(Vector3.Zero)]);

        private XRMaterial? CreateDebugPointMaterial()
        {
            if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                return null;
            
            try
            {
                XRShader vertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
                XRShader stereoMV2VertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoMV2.vs"), EShaderType.Vertex);
                XRShader[] vertexShaders = Engine.Rendering.State.IsVulkan
                    ? [vertShader, stereoMV2VertShader]
                    :
                    [
                        vertShader,
                        stereoMV2VertShader,
                        ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoNV.vs"), EShaderType.Vertex),
                    ];

                XRShader geomShader = ShaderHelper.LoadEngineShader(
                    Path.Combine("Common", "Debug", "gs", _useCompressedBuffers ? "PointInstanceCompressed.gs" : "PointInstance.gs"),
                    EShaderType.Geometry);
                XRShader fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitivePoint.fs"), EShaderType.Fragment);
                ShaderVar[] vars =
                [
                    new ShaderFloat(PointSize, "PointSize"),
                    new ShaderInt(0, "TotalPoints"),
                ];
                var mat = new XRMaterial(vars, [.. vertexShaders, geomShader, fragShader]);
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
                XRShader vertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
                XRShader stereoMV2VertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoMV2.vs"), EShaderType.Vertex);
                XRShader[] vertexShaders = Engine.Rendering.State.IsVulkan
                    ? [vertShader, stereoMV2VertShader]
                    :
                    [
                        vertShader,
                        stereoMV2VertShader,
                        ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoNV.vs"), EShaderType.Vertex),
                    ];

                XRShader geomShader = ShaderHelper.LoadEngineShader(
                    Path.Combine("Common", "Debug", "gs", _useCompressedBuffers ? "LineInstanceCompressed.gs" : "LineInstance.gs"),
                    EShaderType.Geometry);
                XRShader fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitive.fs"), EShaderType.Fragment);
                ShaderVar[] vars =
                [
                    new ShaderFloat(LineWidth, "LineWidth"),
                    new ShaderInt(0, "TotalLines"),
                ];
                var mat = new XRMaterial(vars, [.. vertexShaders, geomShader, fragShader]);
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
        private XRMaterial? CreateDebugTriangleMaterial()
        {
            if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                return null;

            try
            {
                XRShader vertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"), EShaderType.Vertex);
                XRShader stereoMV2VertShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoMV2.vs"), EShaderType.Vertex);
                XRShader[] vertexShaders = Engine.Rendering.State.IsVulkan
                    ? [vertShader, stereoMV2VertShader]
                    :
                    [
                        vertShader,
                        stereoMV2VertShader,
                        ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitiveStereoNV.vs"), EShaderType.Vertex),
                    ];

                XRShader geomShader = ShaderHelper.LoadEngineShader(
                    Path.Combine("Common", "Debug", "gs", _useCompressedBuffers ? "TriangleInstanceCompressed.gs" : "TriangleInstance.gs"),
                    EShaderType.Geometry);
                XRShader fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitive.fs"), EShaderType.Fragment);
                ShaderVar[] vars =
                [
                    new ShaderInt(0, "TotalTriangles"),
                ];
                var mat = new XRMaterial(vars, [.. vertexShaders, geomShader, fragShader]);
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