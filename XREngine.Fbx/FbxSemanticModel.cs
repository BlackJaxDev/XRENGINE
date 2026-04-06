using System.Numerics;

namespace XREngine.Fbx;

public enum FbxConnectionKind
{
    Unknown,
    ObjectToObject,
    ObjectToProperty,
    PropertyToObject,
    PropertyToProperty,
}

public enum FbxObjectCategory
{
    Unknown,
    Model,
    Geometry,
    Material,
    Texture,
    Video,
    Deformer,
    AnimationCurve,
    AnimationCurveNode,
    AnimationLayer,
    AnimationStack,
    Pose,
    NodeAttribute,
    Other,
}

public enum FbxPivotImportPolicy
{
    PreservePivotSemantics,
    BakeIntoLocalTransform,
}

public enum FbxModelHierarchyPolicy
{
    PreserveAuthoredNodes,
}

public enum FbxRotationOrder
{
    XYZ = 0,
    XZY = 1,
    YZX = 2,
    YXZ = 3,
    ZXY = 4,
    ZYX = 5,
    SphericXYZ = 6,
    Unknown = 255,
}

public enum FbxSemanticValueKind
{
    Integer,
    Number,
    Boolean,
    String,
    Identifier,
}

public sealed record FbxSceneSemanticsPolicy(
    FbxPivotImportPolicy PivotImportPolicy,
    FbxModelHierarchyPolicy ModelHierarchyPolicy)
{
    public static FbxSceneSemanticsPolicy Default { get; } = new(
        FbxPivotImportPolicy.PreservePivotSemantics,
        FbxModelHierarchyPolicy.PreserveAuthoredNodes);
}

public readonly record struct FbxSignedAxis(int AxisIndex, int Sign)
{
    public Vector3 ToVector3()
        => AxisIndex switch
        {
            0 => new Vector3(Sign, 0.0f, 0.0f),
            1 => new Vector3(0.0f, Sign, 0.0f),
            2 => new Vector3(0.0f, 0.0f, Sign),
            _ => Vector3.Zero,
        };
}

public readonly record struct FbxObjectReference(long? Id, string? Name)
{
    public bool HasValue => Id.HasValue || !string.IsNullOrWhiteSpace(Name);
}

public readonly record struct FbxSemanticValue(
    FbxSemanticValueKind Kind,
    long IntegerValue,
    double NumberValue,
    bool BooleanValue,
    string? TextValue)
{
    public static FbxSemanticValue FromInteger(long value) => new(FbxSemanticValueKind.Integer, value, value, value != 0, null);
    public static FbxSemanticValue FromNumber(double value) => new(FbxSemanticValueKind.Number, checked((long)value), value, value != 0.0d, null);
    public static FbxSemanticValue FromBoolean(bool value) => new(FbxSemanticValueKind.Boolean, value ? 1L : 0L, value ? 1.0d : 0.0d, value, null);
    public static FbxSemanticValue FromString(string value) => new(FbxSemanticValueKind.String, 0L, 0.0d, !string.IsNullOrEmpty(value), value);
    public static FbxSemanticValue FromIdentifier(string value) => new(FbxSemanticValueKind.Identifier, 0L, 0.0d, !string.IsNullOrEmpty(value), value);

    public bool TryGetInt64(out long value)
    {
        if (Kind is FbxSemanticValueKind.Integer or FbxSemanticValueKind.Boolean)
        {
            value = IntegerValue;
            return true;
        }

        if (Kind == FbxSemanticValueKind.Number)
        {
            value = checked((long)NumberValue);
            return true;
        }

        if (TextValue is not null && long.TryParse(TextValue, out value))
            return true;

        value = default;
        return false;
    }

    public bool TryGetDouble(out double value)
    {
        if (Kind is FbxSemanticValueKind.Integer or FbxSemanticValueKind.Boolean)
        {
            value = IntegerValue;
            return true;
        }

        if (Kind == FbxSemanticValueKind.Number)
        {
            value = NumberValue;
            return true;
        }

        if (TextValue is not null && double.TryParse(TextValue, out value))
            return true;

        value = default;
        return false;
    }

    public string AsString()
        => TextValue ?? Kind switch
        {
            FbxSemanticValueKind.Boolean => BooleanValue ? "true" : "false",
            FbxSemanticValueKind.Integer => IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FbxSemanticValueKind.Number => NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
}

public sealed record FbxPropertyEntry(
    string Name,
    string TypeName,
    string DataTypeName,
    string Flags,
    IReadOnlyList<FbxSemanticValue> Values,
    int NodeIndex)
{
    public bool TryGetValue(int index, out FbxSemanticValue value)
    {
        if ((uint)index < (uint)Values.Count)
        {
            value = Values[index];
            return true;
        }

        value = default;
        return false;
    }

    public long GetInt64OrDefault(int index, long fallback = default)
        => TryGetValue(index, out FbxSemanticValue value) && value.TryGetInt64(out long parsed) ? parsed : fallback;

    public double GetDoubleOrDefault(int index, double fallback = default)
        => TryGetValue(index, out FbxSemanticValue value) && value.TryGetDouble(out double parsed) ? parsed : fallback;

    public string? GetStringOrDefault(int index)
        => TryGetValue(index, out FbxSemanticValue value) ? value.AsString() : null;

    public Vector3 GetVector3OrDefault(Vector3 fallback)
    {
        if (Values.Count < 3)
            return fallback;

        if (!Values[0].TryGetDouble(out double x)
            || !Values[1].TryGetDouble(out double y)
            || !Values[2].TryGetDouble(out double z))
        {
            return fallback;
        }

        return new Vector3((float)x, (float)y, (float)z);
    }
}

public sealed record FbxDefinitionTemplate(
    string TemplateName,
    IReadOnlyDictionary<string, FbxPropertyEntry> Properties,
    int NodeIndex);

public sealed record FbxDefinitionType(
    string TypeName,
    int Count,
    IReadOnlyList<FbxDefinitionTemplate> Templates,
    int NodeIndex);

public sealed record FbxAxisSystem(
    FbxSignedAxis UpAxis,
    FbxSignedAxis FrontAxis,
    FbxSignedAxis CoordAxis,
    FbxSignedAxis OriginalUpAxis,
    double UnitScaleFactor,
    double OriginalUnitScaleFactor);

public sealed record FbxGlobalSettings(
    FbxAxisSystem AxisSystem,
    IReadOnlyDictionary<string, FbxPropertyEntry> Properties,
    string? DefaultCamera,
    long? TimeMode,
    long? TimeProtocol,
    int NodeIndex);

public sealed record FbxTransformSemantics(
    Vector3 LocalTranslation,
    Vector3 LocalRotationDegrees,
    Vector3 LocalScaling,
    Vector3 RotationOffset,
    Vector3 RotationPivot,
    Vector3 ScalingOffset,
    Vector3 ScalingPivot,
    Vector3 PreRotationDegrees,
    Vector3 PostRotationDegrees,
    Vector3 GeometricTranslation,
    Vector3 GeometricRotationDegrees,
    Vector3 GeometricScaling,
    FbxRotationOrder RotationOrder,
    int InheritType,
    bool HasPivotData)
{
    public static FbxTransformSemantics Identity { get; } = new(
        Vector3.Zero,
        Vector3.Zero,
        Vector3.One,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.One,
        FbxRotationOrder.XYZ,
        0,
        false);

    public Matrix4x4 CreateNodeLocalTransform(FbxPivotImportPolicy pivotPolicy)
    {
        Matrix4x4 scale = Matrix4x4.CreateScale(LocalScaling);
        Matrix4x4 rotation = CreateRotationMatrix(LocalRotationDegrees, RotationOrder);
        Matrix4x4 translation = Matrix4x4.CreateTranslation(LocalTranslation);
        if (pivotPolicy == FbxPivotImportPolicy.BakeIntoLocalTransform || !HasPivotData)
            return scale * rotation * translation;

        Matrix4x4 preRotation = CreateRotationMatrix(PreRotationDegrees, RotationOrder);
        Matrix4x4 postRotation = CreateRotationMatrix(PostRotationDegrees, RotationOrder);
        Matrix4x4.Invert(postRotation, out Matrix4x4 inversePostRotation);

        return translation
            * Matrix4x4.CreateTranslation(RotationOffset)
            * Matrix4x4.CreateTranslation(RotationPivot)
            * preRotation
            * rotation
            * inversePostRotation
            * Matrix4x4.CreateTranslation(-RotationPivot)
            * Matrix4x4.CreateTranslation(ScalingOffset)
            * Matrix4x4.CreateTranslation(ScalingPivot)
            * scale
            * Matrix4x4.CreateTranslation(-ScalingPivot);
    }

    public Matrix4x4 CreateGeometryTransform()
        => Matrix4x4.CreateScale(GeometricScaling)
            * CreateRotationMatrix(GeometricRotationDegrees, RotationOrder)
            * Matrix4x4.CreateTranslation(GeometricTranslation);

    public static Matrix4x4 CreateRotationMatrix(Vector3 eulerDegrees, FbxRotationOrder order)
    {
        Matrix4x4 x = Matrix4x4.CreateRotationX(float.DegreesToRadians(eulerDegrees.X));
        Matrix4x4 y = Matrix4x4.CreateRotationY(float.DegreesToRadians(eulerDegrees.Y));
        Matrix4x4 z = Matrix4x4.CreateRotationZ(float.DegreesToRadians(eulerDegrees.Z));
        return order switch
        {
            FbxRotationOrder.XYZ => x * y * z,
            FbxRotationOrder.XZY => x * z * y,
            FbxRotationOrder.YZX => y * z * x,
            FbxRotationOrder.YXZ => y * x * z,
            FbxRotationOrder.ZXY => z * x * y,
            FbxRotationOrder.ZYX => z * y * x,
            FbxRotationOrder.SphericXYZ => x * y * z,
            _ => x * y * z,
        };
    }
}

public sealed record FbxSceneObject(
    long Id,
    FbxObjectCategory Category,
    string ClassName,
    string QualifiedName,
    string DisplayName,
    string Subclass,
    IReadOnlyDictionary<string, FbxPropertyEntry> Properties,
    IReadOnlyDictionary<string, FbxSemanticValue> InlineAttributes,
    FbxTransformSemantics TransformSemantics,
    int NodeIndex);

public sealed record FbxConnection(
    FbxConnectionKind Kind,
    string TypeCode,
    FbxObjectReference Source,
    FbxObjectReference Destination,
    string? PropertyName,
    int NodeIndex);

public sealed record FbxTake(
    string Name,
    string? FileName,
    string? LocalTime,
    string? ReferenceTime,
    int NodeIndex);

public sealed record FbxIntermediateNode(
    long ObjectId,
    string Name,
    string Subclass,
    int ObjectIndex,
    int? ParentNodeIndex,
    IReadOnlyList<int> ChildNodeIndices,
    FbxTransformSemantics TransformSemantics,
    Matrix4x4 LocalTransform,
    Matrix4x4 GeometryTransform);

public sealed record FbxIntermediateMesh(
    long ObjectId,
    string Name,
    string GeometryType,
    int ObjectIndex,
    IReadOnlyList<long> ModelObjectIds);

public sealed record FbxIntermediateMaterial(
    long ObjectId,
    string Name,
    int ObjectIndex,
    IReadOnlyList<long> ModelObjectIds,
    IReadOnlyList<long> TextureObjectIds);

public sealed record FbxIntermediateTexture(
    long ObjectId,
    string Name,
    int ObjectIndex,
    IReadOnlyList<long> MaterialObjectIds,
    IReadOnlyList<long> VideoObjectIds);

public sealed record FbxIntermediateSkin(
    long ObjectId,
    string Name,
    string Subclass,
    int ObjectIndex,
    IReadOnlyList<long> ConnectedObjectIds);

public sealed record FbxIntermediateCluster(
    long ObjectId,
    string Name,
    string Subclass,
    int ObjectIndex,
    IReadOnlyList<long> ConnectedObjectIds);

public sealed record FbxIntermediateBlendShape(
    long ObjectId,
    string Name,
    string Subclass,
    int ObjectIndex,
    IReadOnlyList<long> ConnectedObjectIds);

public sealed record FbxIntermediateAnimationCurve(
    long ObjectId,
    string Name,
    int ObjectIndex,
    IReadOnlyList<long> ConnectedObjectIds);

public sealed record FbxIntermediateAnimationStack(
    long ObjectId,
    string Name,
    int ObjectIndex,
    IReadOnlyList<long> ConnectedObjectIds);

public sealed record FbxIntermediateScene(
    FbxSceneSemanticsPolicy Policy,
    IReadOnlyList<FbxIntermediateNode> Nodes,
    IReadOnlyList<FbxIntermediateMesh> Meshes,
    IReadOnlyList<FbxIntermediateMaterial> Materials,
    IReadOnlyList<FbxIntermediateTexture> Textures,
    IReadOnlyList<FbxIntermediateSkin> Skins,
    IReadOnlyList<FbxIntermediateCluster> Clusters,
    IReadOnlyList<FbxIntermediateBlendShape> BlendShapes,
    IReadOnlyList<FbxIntermediateAnimationCurve> AnimationCurves,
    IReadOnlyList<FbxIntermediateAnimationStack> AnimationStacks);

public sealed class FbxSemanticDocument
{
    private readonly Dictionary<long, int> _objectIndexById;
    private readonly int[][] _outboundConnectionIndicesByObject;
    private readonly int[][] _inboundConnectionIndicesByObject;

    internal FbxSemanticDocument(
        FbxHeaderInfo header,
        FbxGlobalSettings? globalSettings,
        FbxDefinitionType[] definitions,
        FbxSceneObject[] objects,
        FbxConnection[] connections,
        FbxTake[] takes,
        FbxIntermediateScene intermediateScene,
        Dictionary<long, int> objectIndexById,
        int[][] outboundConnectionIndicesByObject,
        int[][] inboundConnectionIndicesByObject)
    {
        Header = header;
        GlobalSettings = globalSettings;
        Definitions = definitions;
        Objects = objects;
        Connections = connections;
        Takes = takes;
        IntermediateScene = intermediateScene;
        _objectIndexById = objectIndexById;
        _outboundConnectionIndicesByObject = outboundConnectionIndicesByObject;
        _inboundConnectionIndicesByObject = inboundConnectionIndicesByObject;
    }

    public FbxHeaderInfo Header { get; }
    public FbxGlobalSettings? GlobalSettings { get; }
    public IReadOnlyList<FbxDefinitionType> Definitions { get; }
    public IReadOnlyList<FbxSceneObject> Objects { get; }
    public IReadOnlyList<FbxConnection> Connections { get; }
    public IReadOnlyList<FbxTake> Takes { get; }
    public FbxIntermediateScene IntermediateScene { get; }
    public IReadOnlyDictionary<long, int> ObjectIndexById => _objectIndexById;

    public bool TryGetObject(long objectId, out FbxSceneObject sceneObject)
    {
        if (_objectIndexById.TryGetValue(objectId, out int index))
        {
            sceneObject = Objects[index];
            return true;
        }

        sceneObject = null!;
        return false;
    }

    public ReadOnlySpan<int> GetOutboundConnectionIndices(int objectIndex)
        => _outboundConnectionIndicesByObject[objectIndex];

    public ReadOnlySpan<int> GetInboundConnectionIndices(int objectIndex)
        => _inboundConnectionIndicesByObject[objectIndex];
}