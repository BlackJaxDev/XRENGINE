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
    private readonly SceneNode _parent;
    private readonly BrowserFramePacket _packet = new();
    private readonly List<BrowserMeshComponent> _renderables = new();
    private readonly List<BrowserMeshComponent> _customRenderables = new();
    private readonly Dictionary<BrowserMeshData, int> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserTextureData, int> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserMaterialData, int> _materials = new(ReferenceEqualityComparer.Instance);
    private readonly BrowserMeshData _cubeMesh;
    private readonly BrowserMeshData _panelMesh;
    private readonly BrowserMaterialData _checkerMaterial;
    private readonly BrowserMaterialData _panelMaterial;
    private BrowserViewport _left;
    private BrowserViewport _right;
    private int _instanceCount = 16;
    private bool _graphicsInitialized;
    private bool _splitView;
    private double _accumulator;
    private bool _disposed;

    public BrowserSceneSession(int id, string canvasId)
    {
        Id = id;
        Target = new BrowserCanvasRenderTarget(canvasId);
        _host = new RuntimeSceneHost();
        try
        {
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
            _host.Dispose();
            Data.Core.XRObjectBase.ProcessPendingDestructions();
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
                Span<byte> bytes = _packet.BeginConsume();
                try
                {
                    BrowserSceneExports.SubmitPacket(Id, bytes);
                }
                finally
                {
                    _packet.EndConsume();
                }
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
        for (int i = 0; i < _instanceCount; i++)
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
        => _packet.EnsureDrawCapacity((_instanceCount + _customRenderables.Count) * (_splitView ? 2 : 1));

    private void AddDemoInstance(int index)
    {
        bool cube = (index & 1) == 0;
        AddRenderableCore(cube ? _cubeMesh : _panelMesh, cube ? _checkerMaterial : _panelMaterial,
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
        handle = BrowserSceneExports.CreateMesh(Id, mesh.VertexBytes, mesh.IndexBytes);
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
                textureHandle = BrowserSceneExports.CreateTexture(Id, texture.Width, texture.Height, texture.RgbaBytes);
                if (textureHandle <= 0)
                    throw new InvalidOperationException("Browser renderer rejected a texture upload.");
                _textures.Add(texture, textureHandle);
            }
        }
        Vector4 tint = material.Tint;
        handle = BrowserSceneExports.CreateMaterial(Id, textureHandle, tint.X, tint.Y, tint.Z, tint.W);
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
            BrowserSceneExports.DestroyResource(Id, handle);
        }
        catch (Exception exception)
        {
            firstError ??= exception;
        }
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
            _host.Dispose();
            Data.Core.XRObjectBase.ProcessPendingDestructions();
        }
    }
}
