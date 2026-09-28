using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

/// <summary>Owns one real scene, its browser GPU resources, clock and batched frame submission.</summary>
public sealed class BrowserSceneSession : IDisposable
{
    private const double FixedStep = 1.0 / 60.0;
    private const int MaxStepsPerFrame = 4;
    private const int MaxInstances = 256;

    private readonly RuntimeSceneHost _host;
    private readonly IBrowserRendererHost _renderer;
    private readonly BrowserSceneSnapshot? _snapshot;
    private readonly SceneNode _parent;
    private readonly BrowserFramePacket _packet = new();
    private readonly List<BrowserMeshComponent> _renderables = new();
    private readonly List<BrowserMeshComponent> _customRenderables = new();
    private readonly Dictionary<BrowserMeshData, int> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserTextureData, int> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserMaterialData, int> _materials = new(ReferenceEqualityComparer.Instance);
    private BrowserMeshData? _cubeMesh;
    private BrowserMeshData? _panelMesh;
    private BrowserMaterialData? _checkerMaterial;
    private BrowserMaterialData? _panelMaterial;
    private BrowserViewport _left;
    private BrowserViewport _right;
    private int _instanceCount = 16;
    private bool _graphicsInitialized;
    private bool _splitView;
    private double _accumulator;
    private bool _disposed;

    public BrowserSceneSession(int id, string canvasId, BrowserSceneSnapshot? snapshot = null)
    {
        Id = id;
        _snapshot = snapshot;
        Target = new BrowserCanvasRenderTarget(canvasId);
        _renderer = BrowserRendererComposition.CreateRequired(Target);
        _host = new RuntimeSceneHost();
        try
        {
            if (snapshot is not null)
            {
                _parent = new SceneNode("ImportedBrowserScene", new Transform());
                _host.RootNodes.Add(_parent);
                for (int i = 0; i < snapshot.Instances.Count; i++)
                {
                    BrowserSceneInstance instance = snapshot.Instances[i];
                    AddRenderableCore(snapshot.Meshes[instance.MeshIndex], snapshot.Materials[instance.MaterialIndex],
                        ImportTransform(instance.ModelMatrix), _renderables);
                }
                _host.Start();
                ReservePacket();
                return;
            }

            _cubeMesh = CreateCubeMesh();
            _panelMesh = new BrowserMeshData(
                [-0.43f, -0.43f, 0, 0, 1, 0.43f, -0.43f, 0, 1, 1,
                    0.43f, 0.43f, 0, 1, 0, -0.43f, 0.43f, 0, 0, 0],
                [0, 1, 2, 0, 2, 3]);
            byte[] pixels = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int offset = (y * 8 + x) * 4;
                    byte shade = ((x ^ y) & 1) == 0 ? (byte)245 : (byte)48;
                    pixels[offset] = shade;
                    pixels[offset + 1] = shade;
                    pixels[offset + 2] = shade;
                    pixels[offset + 3] = 255;
                }
            _checkerMaterial = new BrowserMaterialData(new Vector4(0.65f, 0.83f, 1, 1),
                new BrowserTextureData(8, 8, pixels));
            _panelMaterial = new BrowserMaterialData(new Vector4(1, 0.37f, 0.2f, 1));

            _parent = new SceneNode("BrowserInstances", new Transform(new Vector3(0, 0, -2.5f)));
            _host.RootNodes.Add(_parent);
            BrowserSpinComponent spin = _parent.AddComponent(static () => new BrowserSpinComponent())
                ?? throw new InvalidOperationException("Browser scene component creation failed.");
            spin.Target = Target;
            for (int i = 0; i < _instanceCount; i++)
                AddDemoInstance(i);
            LayoutInstances();
            _host.Start();
        }
        catch
        {
            try
            {
                _host.Dispose();
                Data.Core.XRObjectBase.ProcessPendingDestructions();
            }
            finally
            {
                _renderer.Dispose();
            }
            throw;
        }
    }

    public int Id { get; }
    public BrowserCanvasRenderTarget Target { get; }

    /// <summary>Adopts a transform into this scene and reuses uploads for identical descriptor objects.</summary>
    public BrowserMeshComponent AddRenderable(BrowserMeshData mesh, BrowserMaterialData material, Transform transform)
        => AddRenderableCore(mesh, material, transform, _customRenderables);

    private BrowserMeshComponent AddRenderableCore(BrowserMeshData mesh, BrowserMaterialData material,
        Transform transform, List<BrowserMeshComponent> destination)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(transform);
        if (_renderables.Count + _customRenderables.Count >= 2048)
            throw new InvalidOperationException("Browser scene renderable capacity is exhausted.");

        // Upload only on scene composition changes, never while traversing a frame.
        if (_graphicsInitialized)
        {
            UploadMesh(mesh);
            UploadMaterial(material);
        }

        SceneNode node = new(_parent, "BrowserRenderable", transform);
        BrowserMeshComponent component = node.AddComponent(static () => new BrowserMeshComponent())
            ?? throw new InvalidOperationException("Browser mesh component creation failed.");
        component.Mesh = mesh;
        component.Material = material;
        if (_graphicsInitialized)
            SetHandles(component);
        destination.Add(component);
        return component;
    }

    /// <summary>Maps the already-created scene to its renderer and uploads every unique descriptor once.</summary>
    public void InitializeGraphics()
    {
        ThrowIfDisposed();
        if (_graphicsInitialized)
            return;
        try
        {
            _renderer.MarkReady(Id);
            for (int i = 0; i < _renderables.Count; i++)
            {
                BrowserMeshComponent component = _renderables[i];
                UploadMesh(component.Mesh!);
                UploadMaterial(component.Material!);
                SetHandles(component);
            }
            for (int i = 0; i < _customRenderables.Count; i++)
            {
                BrowserMeshComponent component = _customRenderables[i];
                UploadMesh(component.Mesh!);
                UploadMaterial(component.Material!);
                SetHandles(component);
            }
            _graphicsInitialized = true;
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    public void SetInstanceCount(int count)
    {
        ThrowIfDisposed();
        if (_snapshot is not null)
            throw new NotSupportedException("Imported scenes have a fixed set of instances.");
        if (count is < 1 or > MaxInstances)
            throw new ArgumentOutOfRangeException(nameof(count), "Instance count must be from 1 to 256.");
        for (int i = _renderables.Count; i < count; i++)
            AddDemoInstance(i);
        _instanceCount = count;
        LayoutInstances();
        ReservePacket();
    }

    public void Resize(RuntimeSurfaceState surface)
    {
        ThrowIfDisposed();
        RuntimeSurfaceState previous = Target.Surface;
        Target.UpdateSurface(surface);
        if (!surface.CanRender || surface.Generation != previous.Generation)
            _accumulator = 0;
        if (surface.PhysicalWidth != previous.PhysicalWidth || surface.PhysicalHeight != previous.PhysicalHeight)
            RebuildViews();
    }

    public void Input(RuntimeInputState input)
    {
        ThrowIfDisposed();
        Target.UpdateInput(input);
    }

    public void SetSplitView(bool split)
    {
        ThrowIfDisposed();
        if (_snapshot is not null && split)
            throw new NotSupportedException("Imported camera projections require a single viewport.");
        if (_splitView == split)
            return;
        _splitView = split;
        RebuildViews();
        ReservePacket();
    }

    public void ResetClock()
    {
        ThrowIfDisposed();
        Target.ResetFrameClock();
        _accumulator = 0;
    }

    public void Frame(double timestampMilliseconds)
    {
        ThrowIfDisposed();
        if (!_graphicsInitialized)
            throw new InvalidOperationException("Browser graphics have not been initialized.");
        if (!Target.TryBeginFrame(timestampMilliseconds, 0, out double elapsed))
            return;

        _accumulator = Math.Min(_accumulator + Math.Min(elapsed, FixedStep * MaxStepsPerFrame),
            FixedStep * MaxStepsPerFrame);
        try
        {
            int steps = 0;
            while (_accumulator >= FixedStep && steps < MaxStepsPerFrame)
            {
                _host.Advance((float)FixedStep);
                _accumulator -= FixedStep;
                steps++;
            }
            Data.Core.XRObjectBase.ProcessPendingDestructions();

            ReservePacket();
            _packet.Begin(Id, Target.Surface.Generation);
            try
            {
                AddViewDraws(_left);
                if (_splitView)
                    AddViewDraws(_right);
                _packet.Seal();
                _renderer.SubmitPacket(_packet);
            }
            catch
            {
                _packet.Abort();
                throw;
            }
        }
        finally
        {
            Data.Core.XRObjectBase.ProcessPendingDestructions();
        }
    }

    private void AddViewDraws(BrowserViewport viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return;
        for (int i = 0; i < (_snapshot is null ? _instanceCount : _renderables.Count); i++)
        {
            AddComponentDraw(_renderables[i], viewport);
        }
        for (int i = 0; i < _customRenderables.Count; i++)
            AddComponentDraw(_customRenderables[i], viewport);
    }

    private void AddComponentDraw(BrowserMeshComponent component, BrowserViewport viewport)
    {
        if (!component.IsRenderable)
            return;
        // System.Numerics row vectors become WGSL column vectors when row-major
        // fields are read as columns by the browser renderer.
        Matrix4x4 matrix = component.SceneNode.Transform.RenderMatrix * viewport.ViewProjection;
        _packet.AddDraw(BrowserResourceHandle.FromPacked(component.MeshHandle),
            BrowserResourceHandle.FromPacked(component.MaterialHandle), viewport.X, viewport.Y,
            viewport.Width, viewport.Height, 0, component.Mesh!.IndexCount, in matrix);
    }

    private void RebuildViews()
    {
        RuntimeSurfaceState surface = Target.Surface;
        int width = surface.PhysicalWidth;
        int height = surface.PhysicalHeight;
        if (_snapshot is not null)
        {
            // The captured projection is used unchanged across canvas sizes; the viewport
            // stretches its original aspect ratio when the canvas aspect differs.
            _left = new BrowserViewport(0, 0, width, height, _snapshot.Camera.ViewProjection);
            _right = default;
            return;
        }
        if (!_splitView)
        {
            _left = BrowserViewport.Create(0, 0, width, height, Matrix4x4.Identity);
            _right = default;
            return;
        }

        int leftWidth = width / 2;
        _left = BrowserViewport.Create(0, 0, leftWidth, height, Matrix4x4.Identity);
        _right = BrowserViewport.Create(leftWidth, 0, width - leftWidth, height,
            Matrix4x4.CreateLookAt(new Vector3(1.5f, 0.8f, 0), new Vector3(0, 0, -2.5f), Vector3.UnitY));
    }

    private void ReservePacket()
        => _packet.EnsureDrawCapacity(((_snapshot is null ? _instanceCount : _renderables.Count) +
            _customRenderables.Count) * (_splitView ? 2 : 1));

    private void AddDemoInstance(int index)
    {
        bool cube = (index & 1) == 0;
        AddRenderableCore(cube ? _cubeMesh! : _panelMesh!, cube ? _checkerMaterial! : _panelMaterial!,
            new Transform(), _renderables);
    }

    private void LayoutInstances()
    {
        int columns = (int)MathF.Ceiling(MathF.Sqrt(_instanceCount));
        int rows = (_instanceCount + columns - 1) / columns;
        float spacing = MathF.Min(1.05f, 7.2f / Math.Max(columns, rows));
        for (int i = 0; i < _instanceCount; i++)
        {
            int x = i % columns;
            int y = i / columns;
            Transform transform = (Transform)_renderables[i].SceneNode.Transform;
            transform.Translation = new Vector3((x - (columns - 1) * 0.5f) * spacing,
                ((rows - 1) * 0.5f - y) * spacing, -4.5f + (i % 3) * 0.13f);
            transform.Scale = new Vector3(spacing * 0.72f);
            transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (i % 5) * 0.24f);
        }
    }

    private int UploadMesh(BrowserMeshData mesh)
    {
        if (_meshes.TryGetValue(mesh, out int handle))
            return handle;
        handle = _renderer.CreateMesh(mesh);
        if (handle <= 0)
            throw new InvalidOperationException("Browser renderer rejected a mesh upload.");
        _meshes.Add(mesh, handle);
        return handle;
    }

    private int UploadMaterial(BrowserMaterialData material)
    {
        if (_materials.TryGetValue(material, out int handle))
            return handle;
        int textureHandle = 0;
        if (material.Texture is BrowserTextureData texture)
        {
            if (!_textures.TryGetValue(texture, out textureHandle))
            {
                textureHandle = _renderer.CreateTexture(texture);
                if (textureHandle <= 0)
                    throw new InvalidOperationException("Browser renderer rejected a texture upload.");
                _textures.Add(texture, textureHandle);
            }
        }
        handle = _renderer.CreateMaterial(material, textureHandle);
        if (handle <= 0)
            throw new InvalidOperationException("Browser renderer rejected a material upload.");
        _materials.Add(material, handle);
        return handle;
    }

    private void SetHandles(BrowserMeshComponent component)
    {
        component.MeshHandle = _meshes[component.Mesh!];
        component.MaterialHandle = _materials[component.Material!];
    }

    private void ReleaseResources()
    {
        // Materials retain texture handles, so release them before textures and meshes.
        Exception? error = null;
        foreach (int handle in _materials.Values)
            TryDestroy(handle, ref error);
        _materials.Clear();
        foreach (int handle in _textures.Values)
            TryDestroy(handle, ref error);
        _textures.Clear();
        foreach (int handle in _meshes.Values)
            TryDestroy(handle, ref error);
        _meshes.Clear();
        if (error is not null)
            throw error;
    }

    private void TryDestroy(int handle, ref Exception? firstError)
    {
        try
        {
            _renderer.DestroyResource(handle);
        }
        catch (Exception exception)
        {
            firstError ??= exception;
        }
    }

    private static Transform ImportTransform(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
            throw new NotSupportedException("Imported model transform cannot be represented as scale, rotation, and translation.");
        Matrix4x4 reconstructed = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(translation);
        if (!ApproximatelyEqual(matrix, reconstructed))
            throw new NotSupportedException("Imported model transform contains shear or perspective.");
        return new Transform(scale, translation, rotation);
    }

    private static bool ApproximatelyEqual(Matrix4x4 actual, Matrix4x4 expected)
    {
        Span<float> a = stackalloc float[16]
        {
            actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44
        };
        Span<float> b = stackalloc float[16]
        {
            expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44
        };
        for (int i = 0; i < a.Length; i++)
            if (MathF.Abs(a[i] - b[i]) > 0.0001f * MathF.Max(1.0f, MathF.Abs(a[i])))
                return false;
        return true;
    }

    private static BrowserMeshData CreateCubeMesh()
    {
        List<float> vertices = new(24 * 5);
        List<uint> indices = new(36);
        static void Face(List<float> vertices, List<uint> indices, Vector3 center, Vector3 right, Vector3 up)
        {
            uint start = (uint)(vertices.Count / 5);
            Vector3 a = center - right - up;
            Vector3 b = center + right - up;
            Vector3 c = center + right + up;
            Vector3 d = center - right + up;
            static void Vertex(List<float> vertices, Vector3 p, float u, float v)
            {
                vertices.Add(p.X); vertices.Add(p.Y); vertices.Add(p.Z); vertices.Add(u); vertices.Add(v);
            }
            Vertex(vertices, a, 0, 1); Vertex(vertices, b, 1, 1);
            Vertex(vertices, c, 1, 0); Vertex(vertices, d, 0, 0);
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }
        Face(vertices, indices, new Vector3(0, 0, 0.4f), new Vector3(0.4f, 0, 0), new Vector3(0, 0.4f, 0));
        Face(vertices, indices, new Vector3(0, 0, -0.4f), new Vector3(-0.4f, 0, 0), new Vector3(0, 0.4f, 0));
        Face(vertices, indices, new Vector3(0.4f, 0, 0), new Vector3(0, 0, -0.4f), new Vector3(0, 0.4f, 0));
        Face(vertices, indices, new Vector3(-0.4f, 0, 0), new Vector3(0, 0, 0.4f), new Vector3(0, 0.4f, 0));
        Face(vertices, indices, new Vector3(0, 0.4f, 0), new Vector3(0.4f, 0, 0), new Vector3(0, 0, -0.4f));
        Face(vertices, indices, new Vector3(0, -0.4f, 0), new Vector3(0.4f, 0, 0), new Vector3(0, 0, 0.4f));
        return new BrowserMeshData(vertices.ToArray(), indices.ToArray());
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Marks this scene's renderer unusable after browser initialization or device loss.</summary>
    public void RendererFailed(bool deviceLost)
    {
        ThrowIfDisposed();
        _renderer.MarkFailed(deviceLost);
        ResetClock();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            ReleaseResources();
        }
        finally
        {
            try
            {
                _host.Dispose();
                Data.Core.XRObjectBase.ProcessPendingDestructions();
            }
            finally
            {
                _renderer.Dispose();
            }
        }
    }
}
