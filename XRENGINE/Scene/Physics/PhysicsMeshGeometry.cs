using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.Scene.Physics;

/// <summary>
/// CPU-authored convex geometry that can be serialized once and consumed by either physics backend.
/// </summary>
[Serializable]
public sealed class PhysicsConvexHullGeometry : XRBase, IPhysicsGeometry
{
    private Vector3[] _vertices = [];
    private uint[] _indices = [];
    private Vector3 _scale = Vector3.One;
    private Quaternion _scaleRotation = Quaternion.Identity;
    private bool _tightBounds;

    public PhysicsConvexHullGeometry() { }

    public PhysicsConvexHullGeometry(
        IEnumerable<Vector3> vertices,
        IEnumerable<uint>? indices = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        _vertices = [.. vertices];
        _indices = indices is null ? [] : [.. indices];
    }

    [Category("Convex Hull")]
    public Vector3[] Vertices
    {
        get => _vertices;
        set => SetField(ref _vertices, value ?? []);
    }

    [Category("Convex Hull")]
    public uint[] Indices
    {
        get => _indices;
        set => SetField(ref _indices, value ?? []);
    }

    [Category("Transform")]
    public Vector3 Scale
    {
        get => _scale;
        set => SetField(ref _scale, value);
    }

    [Category("Transform")]
    public Quaternion ScaleRotation
    {
        get => _scaleRotation;
        set => SetField(ref _scaleRotation, value);
    }

    [Category("Cooking")]
    public bool TightBounds
    {
        get => _tightBounds;
        set => SetField(ref _tightBounds, value);
    }

    public PhysicsConvexHullGeometry Clone()
        => new((Vector3[])Vertices.Clone(), (uint[])Indices.Clone())
        {
            Scale = Scale,
            ScaleRotation = ScaleRotation,
            TightBounds = TightBounds,
        };

    internal void Validate()
    {
        if (Vertices.Length < 4)
            throw new InvalidOperationException("A convex collider requires at least four vertices.");
        if (Indices.Length != 0 && Indices.Length % 3 != 0)
            throw new InvalidOperationException("Convex collider indices must contain complete triangles.");
    }
}

/// <summary>
/// CPU-authored indexed triangle mesh shared by PhysX cooking and native Jolt mesh creation.
/// </summary>
[Serializable]
public sealed class PhysicsTriangleMeshGeometry : XRBase, IPhysicsGeometry
{
    private Vector3[] _vertices = [];
    private uint[] _indices = [];
    private uint[] _sourceFaceIndices = [];
    private Vector3 _scale = Vector3.One;
    private Quaternion _scaleRotation = Quaternion.Identity;
    private bool _tightBounds;
    private bool _doubleSided;

    public PhysicsTriangleMeshGeometry() { }

    public PhysicsTriangleMeshGeometry(IEnumerable<Vector3> vertices, IEnumerable<uint> indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        _vertices = [.. vertices];
        _indices = [.. indices];
    }

    [Category("Triangle Mesh")]
    public Vector3[] Vertices
    {
        get => _vertices;
        set => SetField(ref _vertices, value ?? []);
    }

    [Category("Triangle Mesh")]
    public uint[] Indices
    {
        get => _indices;
        set => SetField(ref _indices, value ?? []);
    }

    /// <summary>
    /// Optional authored identifiers for each triangle. When omitted, backends use the
    /// zero-based triangle index.
    /// </summary>
    [Category("Triangle Mesh")]
    public uint[] SourceFaceIndices
    {
        get => _sourceFaceIndices;
        set => SetField(ref _sourceFaceIndices, value ?? []);
    }

    [Category("Transform")]
    public Vector3 Scale
    {
        get => _scale;
        set => SetField(ref _scale, value);
    }

    [Category("Transform")]
    public Quaternion ScaleRotation
    {
        get => _scaleRotation;
        set => SetField(ref _scaleRotation, value);
    }

    [Category("Cooking")]
    public bool TightBounds
    {
        get => _tightBounds;
        set => SetField(ref _tightBounds, value);
    }

    [Category("Cooking")]
    public bool DoubleSided
    {
        get => _doubleSided;
        set => SetField(ref _doubleSided, value);
    }

    public PhysicsTriangleMeshGeometry Clone()
        => new((Vector3[])Vertices.Clone(), (uint[])Indices.Clone())
        {
            SourceFaceIndices = (uint[])SourceFaceIndices.Clone(),
            Scale = Scale,
            ScaleRotation = ScaleRotation,
            TightBounds = TightBounds,
            DoubleSided = DoubleSided,
        };

    internal void Validate()
    {
        if (Vertices.Length < 3)
            throw new InvalidOperationException("A triangle collider requires at least three vertices.");
        if (Indices.Length == 0 || Indices.Length % 3 != 0)
            throw new InvalidOperationException("Triangle collider indices must contain one or more complete triangles.");
        if (SourceFaceIndices.Length != 0 && SourceFaceIndices.Length != Indices.Length / 3)
            throw new InvalidOperationException("Triangle source-face identifiers must match the triangle count.");
    }
}

[Serializable]
public readonly record struct PhysicsHeightFieldCell(
    bool TessellatedDiagonal,
    bool LowerTriangleHole = false,
    bool UpperTriangleHole = false);

/// <summary>
/// CPU-authored signed-16-bit height-field samples and per-cell topology shared by both backends.
/// </summary>
[Serializable]
public sealed class PhysicsHeightFieldGeometry : XRBase, IPhysicsGeometry
{
    private short[] _samples = [];
    private PhysicsHeightFieldCell[] _cells = [];
    private int _rowCount;
    private int _columnCount;
    private float _heightScale = 1.0f;
    private float _rowScale = 1.0f;
    private float _columnScale = 1.0f;
    private bool _tightBounds;
    private bool _doubleSided;

    public PhysicsHeightFieldGeometry() { }

    public PhysicsHeightFieldGeometry(
        int rowCount,
        int columnCount,
        IEnumerable<short> samples,
        IEnumerable<PhysicsHeightFieldCell>? cells = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _rowCount = rowCount;
        _columnCount = columnCount;
        _samples = [.. samples];
        _cells = cells is null
            ? CreateDefaultCells(rowCount, columnCount)
            : [.. cells];
    }

    [Category("Height Field")]
    public int RowCount
    {
        get => _rowCount;
        set => SetField(ref _rowCount, value);
    }

    [Category("Height Field")]
    public int ColumnCount
    {
        get => _columnCount;
        set => SetField(ref _columnCount, value);
    }

    [Category("Height Field")]
    public short[] Samples
    {
        get => _samples;
        set => SetField(ref _samples, value ?? []);
    }

    [Category("Height Field")]
    public PhysicsHeightFieldCell[] Cells
    {
        get => _cells;
        set => SetField(ref _cells, value ?? []);
    }

    [Category("Scale")]
    public float HeightScale
    {
        get => _heightScale;
        set => SetField(ref _heightScale, value);
    }

    [Category("Scale")]
    public float RowScale
    {
        get => _rowScale;
        set => SetField(ref _rowScale, value);
    }

    [Category("Scale")]
    public float ColumnScale
    {
        get => _columnScale;
        set => SetField(ref _columnScale, value);
    }

    [Category("Cooking")]
    public bool TightBounds
    {
        get => _tightBounds;
        set => SetField(ref _tightBounds, value);
    }

    [Category("Cooking")]
    public bool DoubleSided
    {
        get => _doubleSided;
        set => SetField(ref _doubleSided, value);
    }

    public PhysicsHeightFieldGeometry Clone()
        => new(RowCount, ColumnCount, (short[])Samples.Clone(), (PhysicsHeightFieldCell[])Cells.Clone())
        {
            HeightScale = HeightScale,
            RowScale = RowScale,
            ColumnScale = ColumnScale,
            TightBounds = TightBounds,
            DoubleSided = DoubleSided,
        };

    internal void Validate()
    {
        if (RowCount < 2 || ColumnCount < 2)
            throw new InvalidOperationException("A height-field collider requires at least two rows and columns.");
        if (Samples.Length != checked(RowCount * ColumnCount))
            throw new InvalidOperationException("Height-field sample count does not match its dimensions.");
        if (Cells.Length != checked((RowCount - 1) * (ColumnCount - 1)))
            throw new InvalidOperationException("Height-field cell count does not match its dimensions.");
    }

    private static PhysicsHeightFieldCell[] CreateDefaultCells(int rowCount, int columnCount)
    {
        if (rowCount < 1 || columnCount < 1)
            return [];

        PhysicsHeightFieldCell[] cells = new PhysicsHeightFieldCell[checked((rowCount - 1) * (columnCount - 1))];
        Array.Fill(cells, new PhysicsHeightFieldCell(TessellatedDiagonal: true));
        return cells;
    }
}

/// <summary>
/// Reusable serialized collider authoring asset. Runtime components receive clones so local edits
/// do not mutate the shared asset.
/// </summary>
[Serializable]
public sealed class PhysicsColliderAsset : XRAsset
{
    private List<PhysicsColliderShape> _shapes = [];

    public PhysicsColliderAsset() { }

    public PhysicsColliderAsset(string name)
        : base(name)
    {
    }

    [Category("Collider")]
    public List<PhysicsColliderShape> Shapes
    {
        get => _shapes;
        set => SetField(ref _shapes, value ?? []);
    }

    public List<PhysicsColliderShape> CreateRuntimeShapes()
    {
        List<PhysicsColliderShape> result = new(Shapes.Count);
        for (int index = 0; index < Shapes.Count; index++)
        {
            PhysicsColliderShape source = Shapes[index];
            result.Add(new PhysicsColliderShape
            {
                Enabled = source.Enabled,
                Name = source.Name,
                Geometry = CloneGeometry(source.Geometry),
                Material = CloneMaterial(source.Material),
                LocalPosition = source.LocalPosition,
                LocalRotation = source.LocalRotation,
            });
        }

        return result;
    }

    private static IPhysicsGeometry? CloneGeometry(IPhysicsGeometry? geometry)
        => geometry switch
        {
            IPhysicsGeometry.Sphere sphere => sphere,
            IPhysicsGeometry.Box box => box,
            IPhysicsGeometry.Capsule capsule => capsule,
            IPhysicsGeometry.Plane plane => plane,
            PhysicsConvexHullGeometry convex => convex.Clone(),
            PhysicsTriangleMeshGeometry mesh => mesh.Clone(),
            PhysicsHeightFieldGeometry heightField => heightField.Clone(),
            _ => geometry,
        };

    private static PhysicsMaterialDefinition? CloneMaterial(PhysicsMaterialDefinition? material)
        => material is null
            ? null
            : new PhysicsMaterialDefinition
            {
                StaticFriction = material.StaticFriction,
                DynamicFriction = material.DynamicFriction,
                Restitution = material.Restitution,
                Damping = material.Damping,
            };
}
