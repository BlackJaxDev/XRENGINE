using System.Globalization;
using System.Numerics;

namespace XREngine.Fbx;

public static class FbxSemanticParser
{
    public static FbxSemanticDocument ParseFile(string path, FbxSceneSemanticsPolicy? policy = null, FbxReaderOptions? readerOptions = null)
    {
        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(path, readerOptions);
        return Parse(structural, policy);
    }

    public static FbxSemanticDocument Parse(byte[] source, FbxSceneSemanticsPolicy? policy = null, FbxReaderOptions? readerOptions = null)
    {
        using FbxStructuralDocument structural = FbxStructuralParser.Parse(source, readerOptions);
        return Parse(structural, policy);
    }

    public static FbxSemanticDocument Parse(FbxStructuralDocument structural, FbxSceneSemanticsPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(structural);

        policy ??= FbxSceneSemanticsPolicy.Default;
        int[][] childrenByNode = BuildChildrenByNode(structural.Nodes);

        int globalSettingsNodeIndex = FindTopLevelNode(structural, "GlobalSettings");
        int definitionsNodeIndex = FindTopLevelNode(structural, "Definitions");
        int objectsNodeIndex = FindTopLevelNode(structural, "Objects");
        int connectionsNodeIndex = FindTopLevelNode(structural, "Connections");
        int takesNodeIndex = FindTopLevelNode(structural, "Takes");

        FbxGlobalSettings? globalSettings = globalSettingsNodeIndex >= 0 ? ParseGlobalSettings(structural, childrenByNode, globalSettingsNodeIndex) : null;
        FbxDefinitionType[] definitions = definitionsNodeIndex >= 0 ? ParseDefinitions(structural, childrenByNode, definitionsNodeIndex) : [];
        FbxSceneObject[] objects = objectsNodeIndex >= 0 ? ParseObjects(structural, childrenByNode, objectsNodeIndex) : [];
        FbxConnection[] connections = connectionsNodeIndex >= 0 ? ParseConnections(structural, childrenByNode, connectionsNodeIndex) : [];
        FbxTake[] takes = takesNodeIndex >= 0 ? ParseTakes(structural, childrenByNode, takesNodeIndex) : [];

        Dictionary<long, int> objectIndexById = new(objects.Length);
        for (int index = 0; index < objects.Length; index++)
            objectIndexById[objects[index].Id] = index;

        (int[][] outbound, int[][] inbound) = BuildConnectionIndices(objects, connections, objectIndexById);
        FbxIntermediateScene intermediateScene = BuildIntermediateScene(policy, objects, connections, objectIndexById, outbound, inbound);

        return new FbxSemanticDocument(
            structural.Header,
            globalSettings,
            definitions,
            objects,
            connections,
            takes,
            intermediateScene,
            objectIndexById,
            outbound,
            inbound);
    }

    private static int[][] BuildChildrenByNode(IReadOnlyList<FbxNodeRecord> nodes)
    {
        List<int>[] children = new List<int>[nodes.Count];
        for (int index = 0; index < nodes.Count; index++)
        {
            int parentIndex = nodes[index].ParentIndex;
            if (parentIndex < 0)
                continue;

            (children[parentIndex] ??= []).Add(index);
        }

        int[][] result = new int[nodes.Count][];
        for (int index = 0; index < children.Length; index++)
            result[index] = children[index]?.ToArray() ?? [];
        return result;
    }

    private static int FindTopLevelNode(FbxStructuralDocument structural, string name)
    {
        foreach (FbxNodeRecord node in structural.Nodes)
        {
            if (node.ParentIndex >= 0)
                continue;
            if (structural.GetNodeName(node) == name)
                return node.Index;
        }

        return -1;
    }

    private static FbxGlobalSettings ParseGlobalSettings(FbxStructuralDocument structural, int[][] childrenByNode, int nodeIndex)
    {
        Dictionary<string, FbxPropertyEntry> properties = ParseProperties70Block(structural, childrenByNode, nodeIndex);

        FbxAxisSystem axisSystem = new(
            UpAxis: new FbxSignedAxis((int)GetPropertyInteger(properties, "UpAxis", 1), (int)GetPropertyInteger(properties, "UpAxisSign", 1)),
            FrontAxis: new FbxSignedAxis((int)GetPropertyInteger(properties, "FrontAxis", 2), (int)GetPropertyInteger(properties, "FrontAxisSign", 1)),
            CoordAxis: new FbxSignedAxis((int)GetPropertyInteger(properties, "CoordAxis", 0), (int)GetPropertyInteger(properties, "CoordAxisSign", 1)),
            OriginalUpAxis: new FbxSignedAxis((int)GetPropertyInteger(properties, "OriginalUpAxis", 1), (int)GetPropertyInteger(properties, "OriginalUpAxisSign", 1)),
            UnitScaleFactor: GetPropertyDouble(properties, "UnitScaleFactor", 1.0d),
            OriginalUnitScaleFactor: GetPropertyDouble(properties, "OriginalUnitScaleFactor", 1.0d));

        return new FbxGlobalSettings(
            axisSystem,
            properties,
            DefaultCamera: GetPropertyString(properties, "DefaultCamera"),
            TimeMode: TryGetPropertyInteger(properties, "TimeMode"),
            TimeProtocol: TryGetPropertyInteger(properties, "TimeProtocol"),
            NodeIndex: nodeIndex);
    }

    private static FbxDefinitionType[] ParseDefinitions(FbxStructuralDocument structural, int[][] childrenByNode, int nodeIndex)
    {
        List<FbxDefinitionType> definitions = [];
        foreach (int childIndex in childrenByNode[nodeIndex])
        {
            FbxNodeRecord child = structural.Nodes[childIndex];
            if (structural.GetNodeName(child) != "ObjectType")
                continue;

            string typeName = GetNodePropertyString(structural, child, 0) ?? string.Empty;
            int count = 0;
            List<FbxDefinitionTemplate> templates = [];
            foreach (int objectTypeChildIndex in childrenByNode[childIndex])
            {
                FbxNodeRecord objectTypeChild = structural.Nodes[objectTypeChildIndex];
                string childName = structural.GetNodeName(objectTypeChild);
                if (childName == "Count")
                {
                    count = (int)GetNodePropertyInteger(structural, objectTypeChild, 0);
                    continue;
                }

                if (childName != "PropertyTemplate")
                    continue;

                string templateName = GetNodePropertyString(structural, objectTypeChild, 0) ?? string.Empty;
                templates.Add(new FbxDefinitionTemplate(
                    templateName,
                    ParseProperties70Block(structural, childrenByNode, objectTypeChildIndex),
                    objectTypeChildIndex));
            }

            definitions.Add(new FbxDefinitionType(typeName, count, templates.ToArray(), childIndex));
        }

        return definitions.ToArray();
    }

    private static FbxSceneObject[] ParseObjects(FbxStructuralDocument structural, int[][] childrenByNode, int nodeIndex)
    {
        List<FbxSceneObject> objects = [];
        foreach (int childIndex in childrenByNode[nodeIndex])
        {
            FbxNodeRecord child = structural.Nodes[childIndex];
            string className = structural.GetNodeName(child);
            if (child.PropertyCount == 0)
                continue;

            long objectId = GetNodePropertyInteger(structural, child, 0);
            string qualifiedName = GetNodePropertyString(structural, child, 1) ?? string.Empty;
            string subtype = GetNodePropertyString(structural, child, 2) ?? string.Empty;
            Dictionary<string, FbxPropertyEntry> properties = ParseProperties70Block(structural, childrenByNode, childIndex);
            Dictionary<string, FbxSemanticValue> inlineAttributes = ParseInlineScalarChildren(structural, childrenByNode, childIndex);

            objects.Add(new FbxSceneObject(
                Id: objectId,
                Category: DetermineCategory(className, subtype),
                ClassName: className,
                QualifiedName: qualifiedName,
                DisplayName: GetDisplayName(qualifiedName),
                Subclass: subtype,
                Properties: properties,
                InlineAttributes: inlineAttributes,
                TransformSemantics: BuildTransformSemantics(properties),
                NodeIndex: childIndex));
        }

        return objects.ToArray();
    }

    private static FbxConnection[] ParseConnections(FbxStructuralDocument structural, int[][] childrenByNode, int nodeIndex)
    {
        List<FbxConnection> connections = [];
        foreach (int childIndex in childrenByNode[nodeIndex])
        {
            FbxNodeRecord child = structural.Nodes[childIndex];
            string childName = structural.GetNodeName(child);
            if (childName is not ("C" or "Connect"))
                continue;

            string typeCode = GetNodePropertyString(structural, child, 0) ?? string.Empty;
            FbxObjectReference source = GetNodeObjectReference(structural, child, 1);
            FbxObjectReference destination = GetNodeObjectReference(structural, child, 2);
            string? propertyName = child.PropertyCount > 3 ? GetNodePropertyString(structural, child, 3) : null;
            connections.Add(new FbxConnection(MapConnectionKind(typeCode), typeCode, source, destination, propertyName, childIndex));
        }

        return connections.ToArray();
    }

    private static FbxTake[] ParseTakes(FbxStructuralDocument structural, int[][] childrenByNode, int nodeIndex)
    {
        string? currentTakeName = null;
        List<FbxTake> takes = [];

        foreach (int childIndex in childrenByNode[nodeIndex])
        {
            FbxNodeRecord child = structural.Nodes[childIndex];
            string childName = structural.GetNodeName(child);
            if (childName == "Current")
            {
                currentTakeName = GetNodePropertyString(structural, child, 0);
                continue;
            }

            if (childName != "Take")
                continue;

            string name = GetNodePropertyString(structural, child, 0) ?? string.Empty;
            string? fileName = null;
            string? localTime = null;
            string? referenceTime = null;

            foreach (int takeChildIndex in childrenByNode[childIndex])
            {
                FbxNodeRecord takeChild = structural.Nodes[takeChildIndex];
                string takeChildName = structural.GetNodeName(takeChild);
                switch (takeChildName)
                {
                    case "FileName":
                        fileName = GetNodePropertyString(structural, takeChild, 0);
                        break;
                    case "LocalTime":
                        localTime = JoinNodePropertyValues(structural, takeChild);
                        break;
                    case "ReferenceTime":
                        referenceTime = JoinNodePropertyValues(structural, takeChild);
                        break;
                }
            }

            takes.Add(new FbxTake(string.IsNullOrWhiteSpace(name) ? currentTakeName ?? string.Empty : name, fileName, localTime, referenceTime, childIndex));
        }

        return takes.ToArray();
    }

    private static (int[][] outbound, int[][] inbound) BuildConnectionIndices(FbxSceneObject[] objects, FbxConnection[] connections, Dictionary<long, int> objectIndexById)
    {
        List<int>[] outboundLists = new List<int>[objects.Length];
        List<int>[] inboundLists = new List<int>[objects.Length];
        for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
        {
            FbxConnection connection = connections[connectionIndex];
            if (connection.Source.Id is long sourceId && objectIndexById.TryGetValue(sourceId, out int sourceObjectIndex))
                (outboundLists[sourceObjectIndex] ??= []).Add(connectionIndex);

            if (connection.Destination.Id is long destinationId && objectIndexById.TryGetValue(destinationId, out int destinationObjectIndex))
                (inboundLists[destinationObjectIndex] ??= []).Add(connectionIndex);
        }

        return (Materialize(outboundLists), Materialize(inboundLists));

        static int[][] Materialize(List<int>[] lists)
        {
            int[][] result = new int[lists.Length][];
            for (int index = 0; index < lists.Length; index++)
                result[index] = lists[index]?.ToArray() ?? [];
            return result;
        }
    }

    private static FbxIntermediateScene BuildIntermediateScene(FbxSceneSemanticsPolicy policy, FbxSceneObject[] objects, FbxConnection[] connections, Dictionary<long, int> objectIndexById, int[][] outbound, int[][] inbound)
    {
        Dictionary<long, List<long>> outboundObjectIds = BuildConnectedObjectMap(connections, sourceToDestination: true);
        Dictionary<long, List<long>> inboundObjectIds = BuildConnectedObjectMap(connections, sourceToDestination: false);

        List<FbxIntermediateNode> nodes = [];
        Dictionary<long, int> nodeIndexById = new();
        List<(long objectId, int objectIndex, string name, string subclass, FbxTransformSemantics transform)> pendingNodes = [];
        foreach (FbxSceneObject sceneObject in objects)
        {
            if (sceneObject.Category != FbxObjectCategory.Model)
                continue;

            int objectIndex = objectIndexById[sceneObject.Id];
            nodeIndexById[sceneObject.Id] = pendingNodes.Count;
            pendingNodes.Add((sceneObject.Id, objectIndex, sceneObject.DisplayName, sceneObject.Subclass, sceneObject.TransformSemantics));
        }

        for (int pendingIndex = 0; pendingIndex < pendingNodes.Count; pendingIndex++)
        {
            (long objectId, int objectIndex, string name, string subclass, FbxTransformSemantics transform) = pendingNodes[pendingIndex];
            int? parentNodeIndex = null;
            if (outboundObjectIds.TryGetValue(objectId, out List<long>? parentCandidates))
            {
                long parentId = parentCandidates.FirstOrDefault(id => id != 0 && nodeIndexById.ContainsKey(id));
                if (parentId != 0 && nodeIndexById.TryGetValue(parentId, out int resolvedParentIndex))
                    parentNodeIndex = resolvedParentIndex;
            }

            nodes.Add(new FbxIntermediateNode(
                ObjectId: objectId,
                Name: name,
                Subclass: subclass,
                ObjectIndex: objectIndex,
                ParentNodeIndex: parentNodeIndex,
                ChildNodeIndices: [],
                TransformSemantics: transform,
                LocalTransform: transform.CreateNodeLocalTransform(policy.PivotImportPolicy),
                GeometryTransform: transform.CreateGeometryTransform()));
        }

        for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            FbxIntermediateNode node = nodes[nodeIndex];
            if (node.ParentNodeIndex is int parentIndex)
            {
                FbxIntermediateNode parent = nodes[parentIndex];
                int[] childIndices = parent.ChildNodeIndices.Concat([nodeIndex]).ToArray();
                nodes[parentIndex] = parent with { ChildNodeIndices = childIndices };
            }
        }

        List<FbxIntermediateMesh> meshes = [];
        List<FbxIntermediateMaterial> materials = [];
        List<FbxIntermediateTexture> textures = [];
        List<FbxIntermediateSkin> skins = [];
        List<FbxIntermediateCluster> clusters = [];
        List<FbxIntermediateBlendShape> blendShapes = [];
        List<FbxIntermediateAnimationCurve> animationCurves = [];
        List<FbxIntermediateAnimationStack> animationStacks = [];

        foreach (FbxSceneObject sceneObject in objects)
        {
            List<long> outboundIds = outboundObjectIds.TryGetValue(sceneObject.Id, out List<long>? resolvedOutbound) ? resolvedOutbound : [];
            List<long> inboundIds = inboundObjectIds.TryGetValue(sceneObject.Id, out List<long>? resolvedInbound) ? resolvedInbound : [];
            int objectIndex = objectIndexById[sceneObject.Id];

            switch (sceneObject.Category)
            {
                case FbxObjectCategory.Geometry:
                    meshes.Add(new FbxIntermediateMesh(sceneObject.Id, sceneObject.DisplayName, sceneObject.Subclass, objectIndex, outboundIds.Where(id => objectIndexById.TryGetValue(id, out int connectedIndex) && objects[connectedIndex].Category == FbxObjectCategory.Model).ToArray()));
                    break;

                case FbxObjectCategory.Material:
                    materials.Add(new FbxIntermediateMaterial(
                        sceneObject.Id,
                        sceneObject.DisplayName,
                        objectIndex,
                        outboundIds.Where(id => objectIndexById.TryGetValue(id, out int connectedIndex) && objects[connectedIndex].Category == FbxObjectCategory.Model).ToArray(),
                        inboundIds.Where(id => objectIndexById.TryGetValue(id, out int connectedIndex) && objects[connectedIndex].Category == FbxObjectCategory.Texture).ToArray()));
                    break;

                case FbxObjectCategory.Texture:
                    textures.Add(new FbxIntermediateTexture(
                        sceneObject.Id,
                        sceneObject.DisplayName,
                        objectIndex,
                        outboundIds.Where(id => objectIndexById.TryGetValue(id, out int connectedIndex) && objects[connectedIndex].Category == FbxObjectCategory.Material).ToArray(),
                        inboundIds.Where(id => objectIndexById.TryGetValue(id, out int connectedIndex) && objects[connectedIndex].Category == FbxObjectCategory.Video).ToArray()));
                    break;

                case FbxObjectCategory.Deformer when sceneObject.Subclass.Equals("Skin", StringComparison.Ordinal):
                    skins.Add(new FbxIntermediateSkin(sceneObject.Id, sceneObject.DisplayName, sceneObject.Subclass, objectIndex, outboundIds.ToArray()));
                    break;

                case FbxObjectCategory.Deformer when sceneObject.Subclass.Equals("Cluster", StringComparison.Ordinal):
                    clusters.Add(new FbxIntermediateCluster(sceneObject.Id, sceneObject.DisplayName, sceneObject.Subclass, objectIndex, outboundIds.Concat(inboundIds).Distinct().ToArray()));
                    break;

                case FbxObjectCategory.Deformer when sceneObject.Subclass is "BlendShape" or "BlendShapeChannel":
                    blendShapes.Add(new FbxIntermediateBlendShape(sceneObject.Id, sceneObject.DisplayName, sceneObject.Subclass, objectIndex, outboundIds.Concat(inboundIds).Distinct().ToArray()));
                    break;

                case FbxObjectCategory.AnimationCurve:
                case FbxObjectCategory.AnimationCurveNode:
                    animationCurves.Add(new FbxIntermediateAnimationCurve(sceneObject.Id, sceneObject.DisplayName, objectIndex, outboundIds.Concat(inboundIds).Distinct().ToArray()));
                    break;

                case FbxObjectCategory.AnimationStack:
                case FbxObjectCategory.AnimationLayer:
                    animationStacks.Add(new FbxIntermediateAnimationStack(sceneObject.Id, sceneObject.DisplayName, objectIndex, outboundIds.Concat(inboundIds).Distinct().ToArray()));
                    break;
            }
        }

        return new FbxIntermediateScene(policy, nodes.ToArray(), meshes.ToArray(), materials.ToArray(), textures.ToArray(), skins.ToArray(), clusters.ToArray(), blendShapes.ToArray(), animationCurves.ToArray(), animationStacks.ToArray());
    }

    private static Dictionary<long, List<long>> BuildConnectedObjectMap(FbxConnection[] connections, bool sourceToDestination)
    {
        Dictionary<long, List<long>> map = new();
        foreach (FbxConnection connection in connections)
        {
            long? key = sourceToDestination ? connection.Source.Id : connection.Destination.Id;
            long? value = sourceToDestination ? connection.Destination.Id : connection.Source.Id;
            if (!key.HasValue || !value.HasValue)
                continue;

            if (!map.TryGetValue(key.Value, out List<long>? values))
            {
                values = [];
                map[key.Value] = values;
            }

            values.Add(value.Value);
        }

        return map;
    }

    private static Dictionary<string, FbxPropertyEntry> ParseProperties70Block(FbxStructuralDocument structural, int[][] childrenByNode, int ownerNodeIndex)
    {
        foreach (int childIndex in childrenByNode[ownerNodeIndex])
        {
            if (structural.GetNodeName(structural.Nodes[childIndex]) != "Properties70")
                continue;

            Dictionary<string, FbxPropertyEntry> properties = new(StringComparer.Ordinal);
            foreach (int propertyNodeIndex in childrenByNode[childIndex])
            {
                FbxNodeRecord propertyNode = structural.Nodes[propertyNodeIndex];
                if (structural.GetNodeName(propertyNode) != "P" || propertyNode.PropertyCount == 0)
                    continue;

                string name = GetNodePropertyString(structural, propertyNode, 0) ?? string.Empty;
                string typeName = GetNodePropertyString(structural, propertyNode, 1) ?? string.Empty;
                string dataTypeName = GetNodePropertyString(structural, propertyNode, 2) ?? string.Empty;
                string flags = GetNodePropertyString(structural, propertyNode, 3) ?? string.Empty;

                List<FbxSemanticValue> values = [];
                for (int propertyIndex = 4; propertyIndex < propertyNode.PropertyCount; propertyIndex++)
                    values.Add(ReadPropertyValue(structural, structural.Properties[propertyNode.FirstPropertyIndex + propertyIndex]));

                properties[name] = new FbxPropertyEntry(name, typeName, dataTypeName, flags, values.ToArray(), propertyNodeIndex);
            }

            return properties;
        }

        return new Dictionary<string, FbxPropertyEntry>(StringComparer.Ordinal);
    }

    private static Dictionary<string, FbxSemanticValue> ParseInlineScalarChildren(FbxStructuralDocument structural, int[][] childrenByNode, int ownerNodeIndex)
    {
        Dictionary<string, FbxSemanticValue> attributes = new(StringComparer.Ordinal);
        foreach (int childIndex in childrenByNode[ownerNodeIndex])
        {
            FbxNodeRecord child = structural.Nodes[childIndex];
            if (childrenByNode[childIndex].Length != 0 || child.PropertyCount == 0)
                continue;

            attributes[structural.GetNodeName(child)] = ReadPropertyValue(structural, structural.Properties[child.FirstPropertyIndex]);
        }

        return attributes;
    }

    private static FbxTransformSemantics BuildTransformSemantics(IReadOnlyDictionary<string, FbxPropertyEntry> properties)
    {
        Vector3 localTranslation = GetPropertyVector(properties, "Lcl Translation", Vector3.Zero);
        Vector3 localRotation = GetPropertyVector(properties, "Lcl Rotation", Vector3.Zero);
        Vector3 localScaling = GetPropertyVector(properties, "Lcl Scaling", Vector3.One);
        Vector3 rotationOffset = GetPropertyVector(properties, "RotationOffset", Vector3.Zero);
        Vector3 rotationPivot = GetPropertyVector(properties, "RotationPivot", Vector3.Zero);
        Vector3 scalingOffset = GetPropertyVector(properties, "ScalingOffset", Vector3.Zero);
        Vector3 scalingPivot = GetPropertyVector(properties, "ScalingPivot", Vector3.Zero);
        Vector3 preRotation = GetPropertyVector(properties, "PreRotation", Vector3.Zero);
        Vector3 postRotation = GetPropertyVector(properties, "PostRotation", Vector3.Zero);
        Vector3 geometricTranslation = GetPropertyVector(properties, "GeometricTranslation", Vector3.Zero);
        Vector3 geometricRotation = GetPropertyVector(properties, "GeometricRotation", Vector3.Zero);
        Vector3 geometricScaling = GetPropertyVector(properties, "GeometricScaling", Vector3.One);
        FbxRotationOrder rotationOrder = (FbxRotationOrder)(TryGetPropertyInteger(properties, "RotationOrder") ?? 0L);
        int inheritType = (int)(TryGetPropertyInteger(properties, "InheritType") ?? 0L);

        bool hasPivotData = rotationOffset != Vector3.Zero
            || rotationPivot != Vector3.Zero
            || scalingOffset != Vector3.Zero
            || scalingPivot != Vector3.Zero
            || preRotation != Vector3.Zero
            || postRotation != Vector3.Zero;

        return new FbxTransformSemantics(
            localTranslation,
            localRotation,
            localScaling,
            rotationOffset,
            rotationPivot,
            scalingOffset,
            scalingPivot,
            preRotation,
            postRotation,
            geometricTranslation,
            geometricRotation,
            geometricScaling,
            Enum.IsDefined(rotationOrder) ? rotationOrder : FbxRotationOrder.Unknown,
            inheritType,
            hasPivotData);
    }

    private static FbxObjectCategory DetermineCategory(string className, string subtype)
        => className switch
        {
            "Model" => FbxObjectCategory.Model,
            "Geometry" => FbxObjectCategory.Geometry,
            "Material" => FbxObjectCategory.Material,
            "Texture" => FbxObjectCategory.Texture,
            "Video" => FbxObjectCategory.Video,
            "Deformer" => FbxObjectCategory.Deformer,
            "AnimationCurve" => FbxObjectCategory.AnimationCurve,
            "AnimationCurveNode" => FbxObjectCategory.AnimationCurveNode,
            "AnimationLayer" => FbxObjectCategory.AnimationLayer,
            "AnimationStack" => FbxObjectCategory.AnimationStack,
            "Pose" => FbxObjectCategory.Pose,
            "NodeAttribute" => FbxObjectCategory.NodeAttribute,
            _ => !string.IsNullOrWhiteSpace(subtype) ? FbxObjectCategory.Other : FbxObjectCategory.Unknown,
        };

    private static FbxConnectionKind MapConnectionKind(string typeCode)
        => typeCode switch
        {
            "OO" => FbxConnectionKind.ObjectToObject,
            "OP" => FbxConnectionKind.ObjectToProperty,
            "PO" => FbxConnectionKind.PropertyToObject,
            "PP" => FbxConnectionKind.PropertyToProperty,
            _ => FbxConnectionKind.Unknown,
        };

    private static string GetDisplayName(string qualifiedName)
    {
        int separator = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? qualifiedName[(separator + 2)..] : qualifiedName;
    }

    private static FbxObjectReference GetNodeObjectReference(FbxStructuralDocument structural, FbxNodeRecord node, int propertyIndex)
    {
        if (propertyIndex >= node.PropertyCount)
            return default;

        FbxSemanticValue value = ReadNodePropertyValue(structural, node, propertyIndex);
        if (value.TryGetInt64(out long id))
            return new FbxObjectReference(id, null);

        string? name = value.AsString();
        return string.IsNullOrWhiteSpace(name) ? default : new FbxObjectReference(null, name);
    }

    private static string JoinNodePropertyValues(FbxStructuralDocument structural, FbxNodeRecord node)
    {
        string[] values = new string[node.PropertyCount];
        for (int propertyIndex = 0; propertyIndex < node.PropertyCount; propertyIndex++)
            values[propertyIndex] = ReadNodePropertyValue(structural, node, propertyIndex).AsString();
        return string.Join(',', values);
    }

    private static FbxSemanticValue ReadNodePropertyValue(FbxStructuralDocument structural, FbxNodeRecord node, int propertyIndex)
        => ReadPropertyValue(structural, structural.Properties[node.FirstPropertyIndex + propertyIndex]);

    private static FbxSemanticValue ReadPropertyValue(FbxStructuralDocument structural, FbxPropertyRecord property)
    {
        ReadOnlySpan<byte> data = structural.GetPropertyData(property);
        bool bigEndian = structural.Header.IsBigEndian;
        return property.Kind switch
        {
            FbxPropertyKind.Int8 => FbxSemanticValue.FromInteger((sbyte)data[0]),
            FbxPropertyKind.Int16 => FbxSemanticValue.FromInteger(bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(data) : System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data)),
            FbxPropertyKind.Boolean => FbxSemanticValue.FromBoolean(data[0] != 0),
            FbxPropertyKind.Byte => FbxSemanticValue.FromInteger(data[0]),
            FbxPropertyKind.Int32 => FbxSemanticValue.FromInteger(bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data) : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data)),
            FbxPropertyKind.Int64 => FbxSemanticValue.FromInteger(bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(data) : System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data)),
            FbxPropertyKind.Float32 => FbxSemanticValue.FromNumber(bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(data) : System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(data)),
            FbxPropertyKind.Float64 => FbxSemanticValue.FromNumber(bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(data) : System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(data)),
            FbxPropertyKind.String => FbxSemanticValue.FromString(System.Text.Encoding.UTF8.GetString(data)),
            FbxPropertyKind.AsciiScalar => ParseAsciiScalar(data),
            _ => FbxSemanticValue.FromIdentifier(System.Text.Encoding.UTF8.GetString(data)),
        };
    }

    private static FbxSemanticValue ParseAsciiScalar(ReadOnlySpan<byte> data)
    {
        string text = System.Text.Encoding.UTF8.GetString(data);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integerValue))
            return FbxSemanticValue.FromInteger(integerValue);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double numberValue))
            return FbxSemanticValue.FromNumber(numberValue);
        if (string.Equals(text, "T", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
            return FbxSemanticValue.FromBoolean(true);
        if (string.Equals(text, "F", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
            return FbxSemanticValue.FromBoolean(false);
        return FbxSemanticValue.FromString(text);
    }

    private static string? GetNodePropertyString(FbxStructuralDocument structural, FbxNodeRecord node, int propertyIndex)
        => propertyIndex < node.PropertyCount ? ReadNodePropertyValue(structural, node, propertyIndex).AsString() : null;

    private static long GetNodePropertyInteger(FbxStructuralDocument structural, FbxNodeRecord node, int propertyIndex)
        => ReadNodePropertyValue(structural, node, propertyIndex).TryGetInt64(out long value)
            ? value
            : throw new FbxParseException($"Node '{structural.GetNodeName(node)}' property {propertyIndex} is not an integer", node.NameOffset);

    private static long? TryGetPropertyInteger(IReadOnlyDictionary<string, FbxPropertyEntry> properties, string propertyName)
        => properties.TryGetValue(propertyName, out FbxPropertyEntry? property) && property.TryGetValue(0, out FbxSemanticValue value) && value.TryGetInt64(out long parsed)
            ? parsed
            : null;

    private static double GetPropertyDouble(IReadOnlyDictionary<string, FbxPropertyEntry> properties, string propertyName, double fallback)
        => properties.TryGetValue(propertyName, out FbxPropertyEntry? property) && property.TryGetValue(0, out FbxSemanticValue value) && value.TryGetDouble(out double parsed)
            ? parsed
            : fallback;

    private static long GetPropertyInteger(IReadOnlyDictionary<string, FbxPropertyEntry> properties, string propertyName, long fallback)
        => TryGetPropertyInteger(properties, propertyName) ?? fallback;

    private static string? GetPropertyString(IReadOnlyDictionary<string, FbxPropertyEntry> properties, string propertyName)
        => properties.TryGetValue(propertyName, out FbxPropertyEntry? property) ? property.GetStringOrDefault(0) : null;

    private static Vector3 GetPropertyVector(IReadOnlyDictionary<string, FbxPropertyEntry> properties, string propertyName, Vector3 fallback)
        => properties.TryGetValue(propertyName, out FbxPropertyEntry? property) ? property.GetVector3OrDefault(fallback) : fallback;
}