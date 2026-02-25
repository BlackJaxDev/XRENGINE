using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Modeling;
using XREngine.Rendering;
using XREngine.Rendering.Modeling;
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
    private ModelingMeshMetadata _modelingMetadata = new()
    {
        SourcePrimitiveType = ModelingPrimitiveType.Triangles
    };

    private EditableMesh? _mesh;
    public EditableMesh? Mesh
    {
        get => _mesh;
        set => SetField(ref _mesh, value);
    }

    private XRMesh? _loadedMesh;
    public XRMesh? LoadedMesh
    {
        get => _loadedMesh;
        private set => SetField(ref _loadedMesh, value);
    }

    private ModelingMeshDocument? _loadedDocument;
    
    private TransformBase? _targetTransform;
    public TransformBase? TargetTransform
    {
        get => _targetTransform;
        set => SetField(ref _targetTransform, value);
    }

    private readonly HashSet<int> _selectedVertices = [];
    private readonly HashSet<int> _selectedEdges = [];
    private readonly HashSet<int> _selectedFaces = [];
    private readonly List<ModelingMeshValidationIssue> _lastSaveDiagnostics = [];

    public IReadOnlyCollection<int> SelectedVertices => _selectedVertices;
    public IReadOnlyCollection<int> SelectedEdges => _selectedEdges;
    public IReadOnlyCollection<int> SelectedFaces => _selectedFaces;
    public IReadOnlyList<ModelingMeshValidationIssue> LastSaveDiagnostics => _lastSaveDiagnostics;
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
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return;
        }

        List<int> vertices = SelectionMode switch
        {
            PrimitiveSelectionMode.Vertex => [.. _selectedVertices],
            PrimitiveSelectionMode.Edge => [.. CollectVerticesFromEdges(_selectedEdges)],
            PrimitiveSelectionMode.Face => [.. CollectVerticesFromFaces(_selectedFaces)],
            _ => []
        };

        if (vertices.Count == 0)
            return;

        _ = ExecuteMeshEdit("Transform Mesh Selection", mesh =>
        {
            mesh.TransformVertices(vertices, transform);
            return true;
        });
    }

    public int InsertVertexOnSelection(Vector3 position)
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return -1;
        }

        if (_selectedEdges.Count == 0)
        {
            Debug.LogWarning("No edge is selected to insert a vertex on.");
            return -1;
        }

        int selectedEdgeIndex = _selectedEdges.First();
        int newIndex = ExecuteMeshEdit("Insert Vertex On Edge", mesh =>
        {
            EdgeKey targetEdge = mesh.Edges[selectedEdgeIndex];
            return mesh.InsertVertexOnEdge(targetEdge, position);
        });

        _selectedVertices.Clear();
        _selectedVertices.Add(newIndex);
        _selectedEdges.Clear();
        _selectedFaces.Clear();
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
        EdgeKey newEdge = ExecuteMeshEdit("Connect Selected Vertices", mesh => mesh.ConnectSelectedVertices(picked[0], picked[1]));

        _selectedEdges.Clear();
        int edgeIndex = Mesh?.Edges.ToList().FindIndex(edge => edge == newEdge) ?? -1;
        if (edgeIndex >= 0)
            _selectedEdges.Add(edgeIndex);

        _selectedVertices.Clear();
        _selectedFaces.Clear();

        return newEdge;
    }

    public int SplitSelectedEdge(float t = 0.5f, ModelingOperationOptions? options = null)
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return -1;
        }

        if (_selectedEdges.Count == 0)
        {
            Debug.LogWarning("No edge is selected to split.");
            return -1;
        }

        int selectedEdgeIndex = _selectedEdges.First();
        int newVertex = ExecuteMeshEdit("Split Edge", mesh =>
        {
            EdgeKey edge = mesh.Edges[selectedEdgeIndex];
            return mesh.SplitEdge(edge, t, options);
        });

        _selectedVertices.Clear();
        _selectedVertices.Add(newVertex);
        _selectedEdges.Clear();
        _selectedFaces.Clear();
        return newVertex;
    }

    public bool CollapseSelectedEdge(ModelingOperationOptions? options = null)
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return false;
        }

        if (_selectedEdges.Count == 0)
        {
            Debug.LogWarning("No edge is selected to collapse.");
            return false;
        }

        int selectedEdgeIndex = _selectedEdges.First();
        bool collapsed = ExecuteMeshEdit("Collapse Edge", mesh =>
        {
            EdgeKey edge = mesh.Edges[selectedEdgeIndex];
            return mesh.CollapseEdge(edge, options);
        });

        if (!collapsed)
            return false;

        ClearSelection();
        return true;
    }

    public List<int> ExtrudeSelectedFaces(float distance, ModelingOperationOptions? options = null)
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return [];
        }

        if (_selectedFaces.Count == 0)
        {
            Debug.LogWarning("No faces are selected to extrude.");
            return [];
        }

        List<int> selectedFaces = [.. _selectedFaces];
        List<int> createdVertices = ExecuteMeshEdit("Extrude Faces", mesh => mesh.ExtrudeFaces(selectedFaces, distance, options));

        _selectedVertices.Clear();
        foreach (int vertex in createdVertices)
            _selectedVertices.Add(vertex);
        _selectedEdges.Clear();
        _selectedFaces.Clear();
        return createdVertices;
    }

    public List<int> InsetSelectedFaces(float factor, ModelingOperationOptions? options = null)
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return [];
        }

        if (_selectedFaces.Count == 0)
        {
            Debug.LogWarning("No faces are selected to inset.");
            return [];
        }

        List<int> selectedFaces = [.. _selectedFaces];
        List<int> createdVertices = ExecuteMeshEdit("Inset Faces", mesh => mesh.InsetFaces(selectedFaces, factor, options));

        _selectedVertices.Clear();
        foreach (int vertex in createdVertices)
            _selectedVertices.Add(vertex);
        _selectedEdges.Clear();
        _selectedFaces.Clear();
        return createdVertices;
    }

    public List<int> BevelSelectedEdges(float amount, ModelingOperationOptions? options = null)
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return [];
        }

        if (_selectedEdges.Count == 0)
        {
            Debug.LogWarning("No edges are selected to bevel.");
            return [];
        }

        List<int> selectedEdges = [.. _selectedEdges];
        List<int> createdVertices = ExecuteMeshEdit("Bevel Edges", mesh => mesh.BevelEdges(selectedEdges, amount, options));

        _selectedVertices.Clear();
        foreach (int vertex in createdVertices)
            _selectedVertices.Add(vertex);
        _selectedEdges.Clear();
        _selectedFaces.Clear();
        return createdVertices;
    }

    public bool BridgeSelectedEdges()
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return false;
        }

        if (_selectedEdges.Count != 2)
        {
            Debug.LogWarning("Select exactly two edges to bridge.");
            return false;
        }

        int[] edges = [.. _selectedEdges.OrderBy(x => x)];
        int previousFaceCount = _mesh.Faces.Count;
        bool bridged = ExecuteMeshEdit("Bridge Edges", mesh => mesh.BridgeEdges(edges[0], edges[1]));

        if (!bridged)
            return false;

        _selectedFaces.Clear();
        if (Mesh is { Faces.Count: var faceCount } && faceCount >= previousFaceCount + 2)
        {
            _selectedFaces.Add(faceCount - 2);
            _selectedFaces.Add(faceCount - 1);
        }

        _selectedEdges.Clear();
        _selectedVertices.Clear();
        return true;
    }

    public List<int> LoopCutSelectedEdge(float t = 0.5f, ModelingOperationOptions? options = null)
    {
        if (_mesh is null)
        {
            Debug.LogWarning("No mesh is assigned to the MeshEditingPawnComponent.");
            return [];
        }

        if (_selectedEdges.Count == 0)
        {
            Debug.LogWarning("No edge is selected to loop cut.");
            return [];
        }

        int selectedEdgeIndex = _selectedEdges.First();
        List<int> createdVertices = ExecuteMeshEdit("Loop Cut Edge Ring", mesh =>
        {
            EdgeKey edge = mesh.Edges[selectedEdgeIndex];
            return mesh.LoopCutFromEdge(edge, t, options);
        });

        _selectedVertices.Clear();
        foreach (int vertex in createdVertices)
            _selectedVertices.Add(vertex);
        _selectedEdges.Clear();
        _selectedFaces.Clear();
        return createdVertices;
    }

    public TopologyValidationReport ValidateTopology()
    {
        if (_mesh is null)
            throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");

        return _mesh.ValidateTopology();
    }

    public MeshAccelerationData BuildAccelerationData()
        => _mesh?.GenerateAccelerationStructure() ?? throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");

    public (List<Vector3> Vertices, List<int> Indices) BakeToMeshData()
        => _mesh?.Bake() ?? throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");
    
    public void LoadFromXRMesh(XRMesh mesh, XRMeshModelingImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        ModelingMeshDocument document = XRMeshModelingImporter.Import(mesh, options);
        _loadedDocument = document;
        Mesh = EditableMeshConverter.ToEditable(document);
        _modelingMetadata = document.Metadata.Clone();
        LoadedMesh = mesh;
        ClearSelection();
    }

    public XRMesh SaveToXRMesh(XRMeshModelingExportOptions? options = null)
    {
        if (_mesh is null)
            throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");

        options ??= new XRMeshModelingExportOptions();

        using IDisposable interactionScope = Undo.BeginUserInteraction();
        using Undo.ChangeScope changeScope = Undo.BeginChange("Apply Mesh Editing Save");

        Undo.Track(this);
        if (LoadedMesh is not null)
            Undo.Track(LoadedMesh);

        _lastSaveDiagnostics.Clear();

        ModelingMeshDocument document = EditableMeshConverter.FromEditable(_mesh, _modelingMetadata);
        PreserveCompatibleAttributeChannels(document, _loadedDocument, options, _lastSaveDiagnostics);
        if (options.EmitFallbackDiagnostics)
        {
            foreach (ModelingMeshValidationIssue issue in _lastSaveDiagnostics)
            {
                string prefix = issue.Severity == ModelingValidationSeverity.Error ? "[Modeling Save Error]" : "[Modeling Save Warning]";
                Debug.LogWarning($"{prefix} {issue.Code}: {issue.Message}");
            }
        }

        XRMesh mesh = XRMeshModelingExporter.Export(document, options);

        _loadedDocument = document;
        LoadedMesh = mesh;
        return mesh;
    }

    public XRMesh BuildXRMesh(XRMeshModelingExportOptions? options = null)
        => SaveToXRMesh(options);

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

    private static void PreserveCompatibleAttributeChannels(
        ModelingMeshDocument document,
        ModelingMeshDocument? sourceDocument,
        XRMeshModelingExportOptions options,
        List<ModelingMeshValidationIssue> diagnostics)
    {
        if (sourceDocument is null)
            return;

        int vertexCount = document.Positions.Count;

        if (sourceDocument.Normals is { Count: > 0 } normals && normals.Count == vertexCount)
            document.Normals = [.. normals];

        if (sourceDocument.Tangents is { Count: > 0 } tangents && tangents.Count == vertexCount)
            document.Tangents = [.. tangents];

        if (sourceDocument.TexCoordChannels is { Count: > 0 })
        {
            bool allCompatible = sourceDocument.TexCoordChannels.All(channel => channel is { Count: var count } && count == vertexCount);
            if (allCompatible)
            {
                document.TexCoordChannels = sourceDocument.TexCoordChannels
                    .Select(channel => channel is null ? new List<Vector2>() : [.. channel])
                    .ToList();
            }
        }

        if (sourceDocument.ColorChannels is { Count: > 0 })
        {
            bool allCompatible = sourceDocument.ColorChannels.All(channel => channel is { Count: var count } && count == vertexCount);
            if (allCompatible)
            {
                document.ColorChannels = sourceDocument.ColorChannels
                    .Select(channel => channel is null ? new List<Vector4>() : [.. channel])
                    .ToList();
            }
        }

        PreserveSkinningAndBlendshapeChannels(document, sourceDocument, options, diagnostics);
    }

    private static void PreserveSkinningAndBlendshapeChannels(
        ModelingMeshDocument document,
        ModelingMeshDocument sourceDocument,
        XRMeshModelingExportOptions options,
        List<ModelingMeshValidationIssue> diagnostics)
    {
        bool sourceHasSkinning = sourceDocument.Metadata.HasSkinning ||
            (sourceDocument.SkinBones is { Count: > 0 } && sourceDocument.SkinWeights is { Count: > 0 });
        bool sourceHasBlendshapes = sourceDocument.Metadata.HasBlendshapes ||
            sourceDocument.BlendshapeChannels is { Count: > 0 };

        if (!sourceHasSkinning && !sourceHasBlendshapes)
            return;

        int sourceVertexCount = sourceDocument.Positions.Count;
        int targetVertexCount = document.Positions.Count;
        bool sameCardinality = sourceVertexCount == targetVertexCount;

        if (sameCardinality)
        {
            if (sourceDocument.SkinBones is { Count: > 0 } sourceSkinBones && sourceDocument.SkinWeights is { Count: > 0 } sourceSkinWeights)
            {
                document.SkinBones = sourceSkinBones
                    .Select(x => new ModelingSkinBone
                    {
                        Name = x.Name,
                        InverseBindMatrix = x.InverseBindMatrix
                    })
                    .ToList();
                document.SkinWeights = sourceSkinWeights
                    .Select(weightSet => weightSet?.Select(x => new ModelingSkinWeight(x.BoneIndex, x.Weight)).ToList() ?? [])
                    .ToList();
            }

            if (sourceDocument.BlendshapeChannels is { Count: > 0 } sourceBlendshapeChannels)
            {
                document.BlendshapeChannels = sourceBlendshapeChannels
                    .Select(channel => new ModelingBlendshapeChannel
                    {
                        Name = channel.Name,
                        PositionDeltas = [.. channel.PositionDeltas],
                        NormalDeltas = channel.NormalDeltas is null ? null : [.. channel.NormalDeltas],
                        TangentDeltas = channel.TangentDeltas is null ? null : [.. channel.TangentDeltas]
                    })
                    .ToList();
            }

            return;
        }

        switch (options.SkinningBlendshapeFallbackPolicy)
        {
            case XRMeshModelingSkinningBlendshapeFallbackPolicy.Strict:
                diagnostics.Add(new ModelingMeshValidationIssue(
                    ModelingValidationSeverity.Error,
                    "skinning_blendshape_strict_topology_changed",
                    $"Topology changed from {sourceVertexCount} to {targetVertexCount} vertices and strict fallback policy disallows reprojection."));
                throw new InvalidOperationException("Strict skinning/blendshape fallback policy failed because topology changed.");

            case XRMeshModelingSkinningBlendshapeFallbackPolicy.PermissiveDropChannels:
                document.SkinBones = null;
                document.SkinWeights = null;
                document.BlendshapeChannels = null;
                diagnostics.Add(new ModelingMeshValidationIssue(
                    ModelingValidationSeverity.Warning,
                    "skinning_blendshape_channels_dropped",
                    $"Dropped skinning/blendshape channels after topology change ({sourceVertexCount} -> {targetVertexCount})."));
                return;

            case XRMeshModelingSkinningBlendshapeFallbackPolicy.PermissiveNearestSourceVertexReproject:
                ReprojectSkinningAndBlendshapeNearest(document, sourceDocument, diagnostics);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(options.SkinningBlendshapeFallbackPolicy), options.SkinningBlendshapeFallbackPolicy, "Unknown fallback policy.");
        }
    }

    private static void ReprojectSkinningAndBlendshapeNearest(
        ModelingMeshDocument document,
        ModelingMeshDocument sourceDocument,
        List<ModelingMeshValidationIssue> diagnostics)
    {
        int[] nearestMap = BuildNearestSourceVertexMap(sourceDocument.Positions, document.Positions);

        if (sourceDocument.SkinBones is { Count: > 0 } sourceSkinBones && sourceDocument.SkinWeights is { Count: > 0 } sourceSkinWeights)
        {
            document.SkinBones = sourceSkinBones
                .Select(x => new ModelingSkinBone
                {
                    Name = x.Name,
                    InverseBindMatrix = x.InverseBindMatrix
                })
                .ToList();

            List<List<ModelingSkinWeight>> reprojectedWeights = new(document.Positions.Count);
            for (int targetIndex = 0; targetIndex < nearestMap.Length; targetIndex++)
            {
                int sourceIndex = nearestMap[targetIndex];
                if (sourceIndex < 0 || sourceIndex >= sourceSkinWeights.Count)
                {
                    reprojectedWeights.Add([]);
                    continue;
                }

                List<ModelingSkinWeight>? sourceSet = sourceSkinWeights[sourceIndex];
                reprojectedWeights.Add(sourceSet?.Select(x => new ModelingSkinWeight(x.BoneIndex, x.Weight)).ToList() ?? []);
            }

            document.SkinWeights = reprojectedWeights;
        }

        if (sourceDocument.BlendshapeChannels is { Count: > 0 } sourceBlendshapeChannels)
        {
            List<ModelingBlendshapeChannel> reprojectedChannels = new(sourceBlendshapeChannels.Count);
            foreach (ModelingBlendshapeChannel sourceChannel in sourceBlendshapeChannels)
            {
                ModelingBlendshapeChannel reprojected = new()
                {
                    Name = sourceChannel.Name,
                    PositionDeltas = new List<Vector3>(document.Positions.Count),
                    NormalDeltas = sourceChannel.NormalDeltas is null ? null : new List<Vector3>(document.Positions.Count),
                    TangentDeltas = sourceChannel.TangentDeltas is null ? null : new List<Vector3>(document.Positions.Count)
                };

                for (int targetIndex = 0; targetIndex < nearestMap.Length; targetIndex++)
                {
                    int sourceIndex = nearestMap[targetIndex];
                    reprojected.PositionDeltas.Add(sourceIndex >= 0 && sourceIndex < sourceChannel.PositionDeltas.Count
                        ? sourceChannel.PositionDeltas[sourceIndex]
                        : Vector3.Zero);

                    if (reprojected.NormalDeltas is not null)
                    {
                        Vector3 normal = sourceChannel.NormalDeltas is not null && sourceIndex >= 0 && sourceIndex < sourceChannel.NormalDeltas.Count
                            ? sourceChannel.NormalDeltas[sourceIndex]
                            : Vector3.Zero;
                        reprojected.NormalDeltas.Add(normal);
                    }

                    if (reprojected.TangentDeltas is not null)
                    {
                        Vector3 tangent = sourceChannel.TangentDeltas is not null && sourceIndex >= 0 && sourceIndex < sourceChannel.TangentDeltas.Count
                            ? sourceChannel.TangentDeltas[sourceIndex]
                            : Vector3.Zero;
                        reprojected.TangentDeltas.Add(tangent);
                    }
                }

                reprojectedChannels.Add(reprojected);
            }

            document.BlendshapeChannels = reprojectedChannels;
        }

        diagnostics.Add(new ModelingMeshValidationIssue(
            ModelingValidationSeverity.Warning,
            "skinning_blendshape_channels_reprojected",
            $"Reprojected skinning/blendshape channels using nearest-source-vertex policy ({sourceDocument.Positions.Count} -> {document.Positions.Count})."));
    }

    private static int[] BuildNearestSourceVertexMap(IReadOnlyList<Vector3> sourcePositions, IReadOnlyList<Vector3> targetPositions)
    {
        int[] map = new int[targetPositions.Count];
        if (sourcePositions.Count == 0)
        {
            for (int i = 0; i < map.Length; i++)
                map[i] = -1;
            return map;
        }

        for (int targetIndex = 0; targetIndex < targetPositions.Count; targetIndex++)
        {
            Vector3 target = targetPositions[targetIndex];
            int nearestSource = 0;
            float nearestDistanceSq = Vector3.DistanceSquared(target, sourcePositions[0]);
            for (int sourceIndex = 1; sourceIndex < sourcePositions.Count; sourceIndex++)
            {
                float distanceSq = Vector3.DistanceSquared(target, sourcePositions[sourceIndex]);
                if (distanceSq < nearestDistanceSq)
                {
                    nearestDistanceSq = distanceSq;
                    nearestSource = sourceIndex;
                }
            }

            map[targetIndex] = nearestSource;
        }

        return map;
    }

    private T ExecuteMeshEdit<T>(string description, Func<EditableMesh, T> operation)
    {
        if (_mesh is null)
            throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");

        using IDisposable interactionScope = Undo.BeginUserInteraction();
        using Undo.ChangeScope changeScope = Undo.BeginChange(description);

        Undo.Track(this);
        if (LoadedMesh is not null)
            Undo.Track(LoadedMesh);

        EditableMesh working = _mesh.Clone();
        T result = operation(working);
        Mesh = working;
        return result;
    }
}
