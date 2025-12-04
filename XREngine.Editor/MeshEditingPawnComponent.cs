using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Modeling;
using XREngine.Rendering.Picking;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public enum PrimitiveSelectionMode
{
    Vertex,
    Edge,
    Face
}

/// <summary>
/// Editor pawn that redirects picking events into mesh primitive selections so meshes can be edited in-place.
/// Selection and mesh topology operations live here so callers only need to interact with the pawn itself.
/// </summary>
public sealed class MeshEditingPawnComponent : EditorFlyingCameraPawnComponent
{
    private EditableMesh? _mesh;
    public EditableMesh? Mesh
    {
        get => _mesh;
        set => SetField(ref _mesh, value);
    }
    
    private TransformBase? _targetTransform;
    public TransformBase? TargetTransform
    {
        get => _targetTransform;
        set => SetField(ref _targetTransform, value);
    }

    private readonly HashSet<int> _selectedVertices = [];
    private readonly HashSet<int> _selectedEdges = [];
    private readonly HashSet<int> _selectedFaces = [];

    public IReadOnlyCollection<int> SelectedVertices => _selectedVertices;
    public IReadOnlyCollection<int> SelectedEdges => _selectedEdges;
    public IReadOnlyCollection<int> SelectedFaces => _selectedFaces;
    public PrimitiveSelectionMode SelectionMode { get; private set; } = PrimitiveSelectionMode.Vertex;

    public override void RegisterInput(InputInterface input)
    {
        base.RegisterInput(input);
        input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Pressed, HandlePrimitiveSelection);
    }

    private void HandlePrimitiveSelection()
    {
        PrimitiveSelectionMode mode = MapRaycastMode(CurrentRaycastMode);
        if (SelectionMode != mode)
            SetSelectionMode(mode);

        int? pickedIndex = mode switch
        {
            PrimitiveSelectionMode.Vertex when CurrentVertexPickResult is { VertexIndex: var vertexIndex } => vertexIndex,
            PrimitiveSelectionMode.Edge when CurrentEdgePickResult is MeshEdgePickResult edgeHit => ResolveEdgeIndex(edgeHit),
            PrimitiveSelectionMode.Face when CurrentMeshPickResult is { TriangleIndex: var faceIndex } => faceIndex >= 0 ? faceIndex : null,
            _ => null
        };

        if (pickedIndex is null)
            return;

        var keyboard = LocalInput?.Keyboard;
        bool ctrl = keyboard?.GetKeyState(EKey.ControlLeft, EButtonInputType.Pressed) == true
                    || keyboard?.GetKeyState(EKey.ControlRight, EButtonInputType.Pressed) == true;
        bool shift = keyboard?.GetKeyState(EKey.ShiftLeft, EButtonInputType.Pressed) == true
                     || keyboard?.GetKeyState(EKey.ShiftRight, EButtonInputType.Pressed) == true;
        bool alt = keyboard?.GetKeyState(EKey.AltLeft, EButtonInputType.Pressed) == true
                   || keyboard?.GetKeyState(EKey.AltRight, EButtonInputType.Pressed) == true;

        IEnumerable<int> selection = [pickedIndex.Value];

        if (alt)
        {
            RemoveSelection(selection);
            return;
        }

        if (ctrl)
        {
            ToggleSelection(selection);
            return;
        }

        if (shift)
        {
            SelectMultiple(selection, append: true);
            return;
        }

        SelectSingle(pickedIndex.Value);
    }

    private int? ResolveEdgeIndex(MeshEdgePickResult edgeHit)
    {
        int start, end;
        var indices = edgeHit.FaceHit.Indices;
        switch (edgeHit.EdgeIndex)
        {
            case 0:
                start = indices.Point0;
                end = indices.Point1;
                break;
            case 1:
                start = indices.Point1;
                end = indices.Point2;
                break;
            case 2:
                start = indices.Point2;
                end = indices.Point0;
                break;
            default:
                return null;
        }

        EdgeKey key = new(start, end);
        var edges = _mesh?.Edges;
        if (edges is null)
            return null;

        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i] == key)
                return i;
        }

        return null;
    }

    private static PrimitiveSelectionMode MapRaycastMode(ERaycastHitMode mode)
    {
        return mode switch
        {
            ERaycastHitMode.Points => PrimitiveSelectionMode.Vertex,
            ERaycastHitMode.Lines => PrimitiveSelectionMode.Edge,
            _ => PrimitiveSelectionMode.Face
        };
    }

    public void RemoveSelection(IEnumerable<int> indices)
    {
        HashSet<int> activeSet = GetActiveSelectionSet();
        foreach (int index in indices)
            activeSet.Remove(index);
    }

    public void SelectSingle(int index, bool toggle = false)
    {
        HashSet<int> activeSet = GetActiveSelectionSet();
        if (!toggle)
            activeSet.Clear();

        switch (SelectionMode)
        {
            case PrimitiveSelectionMode.Vertex:
                UpdateSelection(_selectedVertices, index, toggle);
                break;
            case PrimitiveSelectionMode.Edge:
                UpdateSelection(_selectedEdges, index, toggle);
                break;
            case PrimitiveSelectionMode.Face:
                UpdateSelection(_selectedFaces, index, toggle);
                break;
        }
    }

    public void SelectMultiple(IEnumerable<int> indices, bool append = false)
    {
        HashSet<int> activeSet = GetActiveSelectionSet();
        if (!append)
            activeSet.Clear();

        foreach (int index in indices)
            activeSet.Add(index);
    }

    public void ToggleSelection(IEnumerable<int> indices)
    {
        HashSet<int> activeSet = GetActiveSelectionSet();
        foreach (int index in indices)
        {
            if (!activeSet.Add(index))
                activeSet.Remove(index);
        }
    }

    public void TransformSelection(Matrix4x4 transform)
    {
        IEnumerable<int> vertices = SelectionMode switch
        {
            PrimitiveSelectionMode.Vertex => _selectedVertices,
            PrimitiveSelectionMode.Edge => CollectVerticesFromEdges(_selectedEdges),
            PrimitiveSelectionMode.Face => CollectVerticesFromFaces(_selectedFaces),
            _ => []
        };

        _mesh?.TransformVertices(vertices, transform);
    }

    public int InsertVertexOnSelection(Vector3 position)
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return -1;
        }

        if (!_selectedEdges.Any())
        {
            Debug.LogWarning("No edge is selected to insert a vertex on.");
            return -1;
        }

        EdgeKey targetEdge = _mesh.Edges.ElementAt(_selectedEdges.First());
        int newIndex = _mesh.InsertVertexOnEdge(targetEdge, position);

        _selectedVertices.Clear();
        _selectedVertices.Add(newIndex);
        _selectedEdges.Clear();
        return newIndex;
    }

    public EdgeKey ConnectSelectedVertices()
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return default;
        }
        
        if (_selectedVertices.Count < 2)
        {
            Debug.LogWarning("Select at least two vertices to create an edge.");
            return default;
        }

        int[] picked = [.. _selectedVertices.Take(2)];
        EdgeKey newEdge = _mesh.ConnectSelectedVertices(picked[0], picked[1]);

        _selectedEdges.Clear();
        int edgeIndex = _mesh.Edges.ToList().FindIndex(edge => edge == newEdge);
        if (edgeIndex >= 0)
            _selectedEdges.Add(edgeIndex);

        return newEdge;
    }

    public MeshAccelerationData BuildAccelerationData()
        => _mesh?.GenerateAccelerationStructure() ?? throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");

    public (List<Vector3> Vertices, List<int> Indices) BakeToMeshData()
        => _mesh?.Bake() ?? throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");

    public void SetSelectionMode(PrimitiveSelectionMode mode)
    {
        if (SelectionMode == mode)
            return;
        SelectionMode = mode;
        ClearSelection();
    }

    private void ClearSelection()
    {
        _selectedVertices.Clear();
        _selectedEdges.Clear();
        _selectedFaces.Clear();
    }

    private static void UpdateSelection(HashSet<int> set, int index, bool toggle)
    {
        if (toggle)
        {
            if (!set.Add(index))
                set.Remove(index);
        }
        else
            set.Add(index);
    }

    private HashSet<int> GetActiveSelectionSet()
    {
        return SelectionMode switch
        {
            PrimitiveSelectionMode.Vertex => _selectedVertices,
            PrimitiveSelectionMode.Edge => _selectedEdges,
            PrimitiveSelectionMode.Face => _selectedFaces,
            _ => _selectedVertices
        };
    }

    private IEnumerable<int> CollectVerticesFromEdges(IEnumerable<int> edgeIds)
    {
        if (_mesh is null)
            yield break;
        foreach (int edgeId in edgeIds)
        {
            EdgeKey edge = _mesh.Edges.ElementAt(edgeId);
            yield return edge.A;
            yield return edge.B;
        }
    }

    private IEnumerable<int> CollectVerticesFromFaces(IEnumerable<int> faceIds)
    {
        if (_mesh is null)
            yield break;
        foreach (int faceId in faceIds)
        {
            EditableFaceData face = _mesh.Faces.ElementAt(faceId);
            yield return face.A;
            yield return face.B;
            yield return face.C;
        }
    }
}
