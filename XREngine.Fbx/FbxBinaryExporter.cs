using System.Numerics;

namespace XREngine.Fbx;

public static class FbxBinaryExporter
{
    public static byte[] Export(
        FbxSemanticDocument semantic,
        FbxGeometryDocument? geometry = null,
        FbxDeformerDocument? deformers = null,
        FbxAnimationDocument? animations = null,
        FbxBinaryExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(semantic);

        IReadOnlyList<FbxBinaryNode>? nodes = null;
        return FbxTrace.TraceOperation(
            "BinaryExporter",
            $"Exporting FBX document ({DescribeInputs(semantic, geometry, deformers, animations)}).",
            payload => $"Exported binary FBX payload with {payload.Length:N0} bytes, rootNodes={nodes?.Count ?? 0:N0}, options={DescribeOptions(ResolveOptions(semantic, options))}",
            () =>
            {
                FbxBinaryExportOptions resolvedOptions = ResolveOptions(semantic, options);
                nodes = BuildDocumentNodes(semantic, geometry, deformers, animations, resolvedOptions);
                return FbxBinaryWriter.WriteToArray(nodes, resolvedOptions);
            });
    }

    public static void Write(
        Stream stream,
        FbxSemanticDocument semantic,
        FbxGeometryDocument? geometry = null,
        FbxDeformerDocument? deformers = null,
        FbxAnimationDocument? animations = null,
        FbxBinaryExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(semantic);

        IReadOnlyList<FbxBinaryNode>? nodes = null;
        FbxTrace.TraceOperation(
            "BinaryExporter",
            $"Writing FBX document to stream ({DescribeInputs(semantic, geometry, deformers, animations)}).",
            () =>
            {
                FbxBinaryExportOptions resolvedOptions = ResolveOptions(semantic, options);
                nodes = BuildDocumentNodes(semantic, geometry, deformers, animations, resolvedOptions);
                FbxBinaryWriter.Write(stream, nodes, resolvedOptions);
            },
            () => $"Wrote FBX stream with rootNodes={nodes?.Count ?? 0:N0}, streamPosition={(stream.CanSeek ? stream.Position.ToString("N0") : "n/a")}, options={DescribeOptions(ResolveOptions(semantic, options))}");
    }

    private static FbxBinaryExportOptions ResolveOptions(FbxSemanticDocument semantic, FbxBinaryExportOptions? options)
    {
        if (options is not null)
            return options;

        return new FbxBinaryExportOptions
        {
            BinaryVersion = semantic.Header.BinaryVersion is 7400 or 7500 ? semantic.Header.BinaryVersion.Value : 7400,
            BigEndian = semantic.Header.IsBigEndian,
        };
    }

    internal static IReadOnlyList<FbxBinaryNode> BuildDocumentNodes(
        FbxSemanticDocument semantic,
        FbxGeometryDocument? geometry,
        FbxDeformerDocument? deformers,
        FbxAnimationDocument? animations,
        FbxBinaryExportOptions options)
    {
        Dictionary<long, FbxClusterBinding> clusterBindingsByObjectId = BuildClusterBindingsByObjectId(deformers);
        Dictionary<long, FbxBlendShapeChannelBinding> shapeBindingsByShapeObjectId = BuildShapeBindingsByShapeObjectId(semantic, deformers);

        List<FbxBinaryNode> documentNodes = [];
        documentNodes.Add(BuildFbxHeaderExtensionNode(options));
        if (options.IncludeGlobalSettings)
            documentNodes.Add(semantic.GlobalSettings is not null ? BuildGlobalSettingsNode(semantic.GlobalSettings) : BuildDefaultGlobalSettingsNode());
        if (options.IncludeDefinitions)
            documentNodes.Add(BuildDefinitionsNode(semantic));

        documentNodes.Add(BuildObjectsNode(semantic, geometry, clusterBindingsByObjectId, shapeBindingsByShapeObjectId, deformers, animations));
        documentNodes.Add(BuildConnectionsNode(semantic));

        if (options.IncludeTakes && semantic.Takes.Count > 0)
            documentNodes.Add(BuildTakesNode(semantic.Takes));

        return documentNodes;
    }

    private static string DescribeInputs(
        FbxSemanticDocument semantic,
        FbxGeometryDocument? geometry,
        FbxDeformerDocument? deformers,
        FbxAnimationDocument? animations)
        => $"objects={semantic.Objects.Count:N0}, connections={semantic.Connections.Count:N0}, meshes={geometry?.MeshesByObjectId.Count ?? 0:N0}, skins={deformers?.SkinsByGeometryObjectId.Count ?? 0:N0}, blendShapeSets={deformers?.BlendShapeChannelsByGeometryObjectId.Count ?? 0:N0}, animationStacks={animations?.Stacks.Count ?? 0:N0}";

    private static string DescribeOptions(FbxBinaryExportOptions options)
        => $"version={options.BinaryVersion}, bigEndian={options.BigEndian}, footer={options.IncludeFooter}, arrayEncoding={options.ArrayEncodingMode}, includeGlobalSettings={options.IncludeGlobalSettings}, includeDefinitions={options.IncludeDefinitions}, includeTakes={options.IncludeTakes}";

    private static FbxBinaryNode BuildFbxHeaderExtensionNode(FbxBinaryExportOptions options)
    {
        FbxBinaryNode node = new("FBXHeaderExtension");
        node.Children.Add(CreateScalarChild("FBXHeaderVersion", FbxBinaryProperty.Int32(1003)));
        node.Children.Add(CreateScalarChild("FBXVersion", FbxBinaryProperty.Int32(options.BinaryVersion)));
        node.Children.Add(CreateScalarChild("Creator", FbxBinaryProperty.String("XREngine.Fbx Binary Exporter")));
        return node;
    }

    private static FbxBinaryNode BuildGlobalSettingsNode(FbxGlobalSettings globalSettings)
    {
        FbxBinaryNode node = new("GlobalSettings");
        node.Children.Add(CreateScalarChild("Version", FbxBinaryProperty.Int32(1000)));

        FbxBinaryNode? propertiesNode = BuildProperties70Node(globalSettings.Properties.Values);
        if (propertiesNode is not null)
            node.Children.Add(propertiesNode);

        return node;
    }

    private static FbxBinaryNode BuildDefaultGlobalSettingsNode()
    {
        FbxBinaryNode node = new("GlobalSettings");
        node.Children.Add(CreateScalarChild("Version", FbxBinaryProperty.Int32(1000)));

        FbxBinaryNode propertiesNode = new("Properties70");
        propertiesNode.Children.Add(CreatePropertyNode("UpAxis", "int", "Integer", "", FbxBinaryProperty.Int32(1)));
        propertiesNode.Children.Add(CreatePropertyNode("UpAxisSign", "int", "Integer", "", FbxBinaryProperty.Int32(1)));
        propertiesNode.Children.Add(CreatePropertyNode("FrontAxis", "int", "Integer", "", FbxBinaryProperty.Int32(2)));
        propertiesNode.Children.Add(CreatePropertyNode("FrontAxisSign", "int", "Integer", "", FbxBinaryProperty.Int32(1)));
        propertiesNode.Children.Add(CreatePropertyNode("CoordAxis", "int", "Integer", "", FbxBinaryProperty.Int32(0)));
        propertiesNode.Children.Add(CreatePropertyNode("CoordAxisSign", "int", "Integer", "", FbxBinaryProperty.Int32(1)));
        propertiesNode.Children.Add(CreatePropertyNode("OriginalUpAxis", "int", "Integer", "", FbxBinaryProperty.Int32(1)));
        propertiesNode.Children.Add(CreatePropertyNode("OriginalUpAxisSign", "int", "Integer", "", FbxBinaryProperty.Int32(1)));
        propertiesNode.Children.Add(CreatePropertyNode("UnitScaleFactor", "double", "Number", "", FbxBinaryProperty.Float64(1.0d)));
        propertiesNode.Children.Add(CreatePropertyNode("OriginalUnitScaleFactor", "double", "Number", "", FbxBinaryProperty.Float64(1.0d)));
        node.Children.Add(propertiesNode);

        return node;
    }

    private static FbxBinaryNode BuildDefinitionsNode(FbxSemanticDocument semantic)
    {
        IReadOnlyList<FbxDefinitionType> definitions = semantic.Definitions.Count > 0
            ? semantic.Definitions.OrderBy(static definition => definition.NodeIndex).ThenBy(static definition => definition.TypeName, StringComparer.Ordinal).ToArray()
            : [..
                semantic.Objects
                    .GroupBy(static sceneObject => sceneObject.ClassName, StringComparer.Ordinal)
                    .OrderBy(static group => group.Key, StringComparer.Ordinal)
                    .Select(static group => new FbxDefinitionType(group.Key, group.Count(), [], -1))];

        FbxBinaryNode node = new("Definitions");
        node.Children.Add(CreateScalarChild("Version", FbxBinaryProperty.Int32(100)));
        node.Children.Add(CreateScalarChild("Count", FbxBinaryProperty.Int32(definitions.Count)));

        foreach (FbxDefinitionType definition in definitions)
        {
            FbxBinaryNode objectTypeNode = new("ObjectType");
            objectTypeNode.Properties.Add(FbxBinaryProperty.String(definition.TypeName));
            objectTypeNode.Children.Add(CreateScalarChild("Count", FbxBinaryProperty.Int32(definition.Count)));
            foreach (FbxDefinitionTemplate template in definition.Templates.OrderBy(static template => template.NodeIndex).ThenBy(static template => template.TemplateName, StringComparer.Ordinal))
            {
                FbxBinaryNode templateNode = new("PropertyTemplate");
                templateNode.Properties.Add(FbxBinaryProperty.String(template.TemplateName));
                FbxBinaryNode? propertiesNode = BuildProperties70Node(template.Properties.Values);
                if (propertiesNode is not null)
                    templateNode.Children.Add(propertiesNode);
                objectTypeNode.Children.Add(templateNode);
            }

            node.Children.Add(objectTypeNode);
        }

        return node;
    }

    private static FbxBinaryNode BuildObjectsNode(
        FbxSemanticDocument semantic,
        FbxGeometryDocument? geometry,
        IReadOnlyDictionary<long, FbxClusterBinding> clusterBindingsByObjectId,
        IReadOnlyDictionary<long, FbxBlendShapeChannelBinding> shapeBindingsByShapeObjectId,
        FbxDeformerDocument? deformers,
        FbxAnimationDocument? animations)
    {
        FbxBinaryNode objectsNode = new("Objects");
        foreach (FbxSceneObject sceneObject in semantic.Objects.OrderBy(static sceneObject => sceneObject.NodeIndex))
            objectsNode.Children.Add(BuildObjectNode(sceneObject, semantic, geometry, clusterBindingsByObjectId, shapeBindingsByShapeObjectId, deformers, animations));
        return objectsNode;
    }

    private static FbxBinaryNode BuildObjectNode(
        FbxSceneObject sceneObject,
        FbxSemanticDocument semantic,
        FbxGeometryDocument? geometry,
        IReadOnlyDictionary<long, FbxClusterBinding> clusterBindingsByObjectId,
        IReadOnlyDictionary<long, FbxBlendShapeChannelBinding> shapeBindingsByShapeObjectId,
        FbxDeformerDocument? deformers,
        FbxAnimationDocument? animations)
    {
        FbxBinaryNode objectNode = new(sceneObject.ClassName);
        objectNode.Properties.Add(FbxBinaryProperty.Int64(sceneObject.Id));
        objectNode.Properties.Add(FbxBinaryProperty.String(sceneObject.QualifiedName));
        objectNode.Properties.Add(FbxBinaryProperty.String(sceneObject.Subclass));

        FbxBinaryNode? propertiesNode = BuildProperties70Node(sceneObject.Properties.Values);
        if (propertiesNode is not null)
            objectNode.Children.Add(propertiesNode);

        foreach ((string attributeName, FbxSemanticValue attributeValue) in sceneObject.InlineAttributes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!ShouldWriteInlineAttribute(sceneObject, attributeName))
                continue;

            objectNode.Children.Add(CreateScalarChild(attributeName, ConvertSemanticValue(attributeValue)));
        }

        switch (sceneObject.Category)
        {
            case FbxObjectCategory.Geometry:
                AppendGeometryChildren(objectNode, sceneObject, geometry, shapeBindingsByShapeObjectId);
                break;

            case FbxObjectCategory.Deformer:
                AppendDeformerChildren(objectNode, sceneObject, clusterBindingsByObjectId, deformers);
                break;

            case FbxObjectCategory.AnimationCurve:
                AppendAnimationCurveChildren(objectNode, sceneObject, animations);
                break;
        }

        return objectNode;
    }

    private static void AppendGeometryChildren(
        FbxBinaryNode objectNode,
        FbxSceneObject sceneObject,
        FbxGeometryDocument? geometry,
        IReadOnlyDictionary<long, FbxBlendShapeChannelBinding> shapeBindingsByShapeObjectId)
    {
        if (sceneObject.Subclass.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
            && geometry is not null
            && geometry.TryGetMeshGeometry(sceneObject.Id, out FbxMeshGeometry meshGeometry))
        {
            objectNode.Children.Add(CreateArrayChild("Vertices", FbxBinaryProperty.Float64Array(FlattenVector3(meshGeometry.ControlPoints))));
            objectNode.Children.Add(CreateArrayChild("PolygonVertexIndex", FbxBinaryProperty.Int32Array([.. meshGeometry.PolygonVertexIndices])));
            objectNode.Children.Add(CreateScalarChild("GeometryVersion", FbxBinaryProperty.Int32(124)));

            int maxLayerIndex = -1;
            for (int index = 0; index < meshGeometry.Normals.Count; index++)
            {
                objectNode.Children.Add(BuildVector3LayerNode("LayerElementNormal", index, meshGeometry.Normals[index], "Normals", "NormalsIndex"));
                maxLayerIndex = Math.Max(maxLayerIndex, index);
            }
            for (int index = 0; index < meshGeometry.Tangents.Count; index++)
            {
                objectNode.Children.Add(BuildVector3LayerNode("LayerElementTangent", index, meshGeometry.Tangents[index], "Tangents", "TangentIndex"));
                maxLayerIndex = Math.Max(maxLayerIndex, index);
            }
            for (int index = 0; index < meshGeometry.TextureCoordinates.Count; index++)
            {
                objectNode.Children.Add(BuildVector2LayerNode("LayerElementUV", index, meshGeometry.TextureCoordinates[index], "UV", "UVIndex"));
                maxLayerIndex = Math.Max(maxLayerIndex, index);
            }
            for (int index = 0; index < meshGeometry.Colors.Count; index++)
            {
                objectNode.Children.Add(BuildVector4LayerNode("LayerElementColor", index, meshGeometry.Colors[index], "Colors", "ColorIndex"));
                maxLayerIndex = Math.Max(maxLayerIndex, index);
            }
            if (meshGeometry.Materials is not null)
            {
                objectNode.Children.Add(BuildIntLayerNode("LayerElementMaterial", 0, meshGeometry.Materials, "Materials", "MaterialIndex"));
                maxLayerIndex = Math.Max(maxLayerIndex, 0);
            }

            for (int layerIndex = 0; layerIndex <= maxLayerIndex; layerIndex++)
            {
                FbxBinaryNode? layerNode = BuildMeshLayerDeclarationNode(meshGeometry, layerIndex);
                if (layerNode is not null)
                    objectNode.Children.Add(layerNode);
            }

            return;
        }

        if (!sceneObject.Subclass.Equals("Shape", StringComparison.OrdinalIgnoreCase)
            || !shapeBindingsByShapeObjectId.TryGetValue(sceneObject.Id, out FbxBlendShapeChannelBinding? channelBinding))
        {
            return;
        }

        int[] controlPointIndices = [.. channelBinding.PositionDeltasByControlPoint.Keys.OrderBy(static value => value)];
        if (controlPointIndices.Length == 0)
            controlPointIndices = [.. channelBinding.NormalDeltasByControlPoint.Keys.OrderBy(static value => value)];
        if (controlPointIndices.Length == 0)
            return;

        objectNode.Children.Add(CreateArrayChild("PolygonVertexIndex", FbxBinaryProperty.Int32Array(BuildPolygonVertexIndices(controlPointIndices))));
        objectNode.Children.Add(CreateArrayChild("Indexes", FbxBinaryProperty.Int32Array(controlPointIndices)));
        objectNode.Children.Add(CreateArrayChild("Vertices", FbxBinaryProperty.Float64Array(FlattenShapeDeltas(controlPointIndices, channelBinding.PositionDeltasByControlPoint))));
        if (channelBinding.NormalDeltasByControlPoint.Count > 0)
            objectNode.Children.Add(CreateArrayChild("Normals", FbxBinaryProperty.Float64Array(FlattenShapeDeltas(controlPointIndices, channelBinding.NormalDeltasByControlPoint))));
    }

    private static FbxBinaryNode? BuildMeshLayerDeclarationNode(FbxMeshGeometry meshGeometry, int layerIndex)
    {
        List<(string Type, int TypedIndex)> entries = [];

        if (layerIndex < meshGeometry.Normals.Count)
            entries.Add(("LayerElementNormal", layerIndex));
        if (layerIndex < meshGeometry.Tangents.Count)
            entries.Add(("LayerElementTangent", layerIndex));
        if (layerIndex < meshGeometry.TextureCoordinates.Count)
            entries.Add(("LayerElementUV", layerIndex));
        if (layerIndex < meshGeometry.Colors.Count)
            entries.Add(("LayerElementColor", layerIndex));
        if (layerIndex == 0 && meshGeometry.Materials is not null)
            entries.Add(("LayerElementMaterial", 0));

        if (entries.Count == 0)
            return null;

        FbxBinaryNode layerNode = new("Layer");
        layerNode.Properties.Add(FbxBinaryProperty.Int32(layerIndex));
        layerNode.Children.Add(CreateScalarChild("Version", FbxBinaryProperty.Int32(100)));

        foreach ((string type, int typedIndex) in entries)
        {
            FbxBinaryNode layerElementNode = new("LayerElement");
            layerElementNode.Children.Add(CreateScalarChild("Type", FbxBinaryProperty.String(type)));
            layerElementNode.Children.Add(CreateScalarChild("TypedIndex", FbxBinaryProperty.Int32(typedIndex)));
            layerNode.Children.Add(layerElementNode);
        }

        return layerNode;
    }

    private static void AppendDeformerChildren(
        FbxBinaryNode objectNode,
        FbxSceneObject sceneObject,
        IReadOnlyDictionary<long, FbxClusterBinding> clusterBindingsByObjectId,
        FbxDeformerDocument? deformers)
    {
        if (sceneObject.Subclass.Equals("Cluster", StringComparison.OrdinalIgnoreCase)
            && clusterBindingsByObjectId.TryGetValue(sceneObject.Id, out FbxClusterBinding? clusterBinding))
        {
            KeyValuePair<int, float>[] weights = [.. clusterBinding.ControlPointWeights.OrderBy(static pair => pair.Key)];
            objectNode.Children.Add(CreateArrayChild("Indexes", FbxBinaryProperty.Int32Array([.. weights.Select(static pair => pair.Key)])));
            objectNode.Children.Add(CreateArrayChild("Weights", FbxBinaryProperty.Float64Array([.. weights.Select(static pair => (double)pair.Value)])));
            objectNode.Children.Add(CreateArrayChild("Transform", FbxBinaryProperty.Float64Array(FlattenMatrix(clusterBinding.TransformMatrix))));
            objectNode.Children.Add(CreateArrayChild("TransformLink", FbxBinaryProperty.Float64Array(FlattenMatrix(clusterBinding.TransformLinkMatrix))));
            return;
        }

        if (sceneObject.Subclass.Equals("BlendShapeChannel", StringComparison.OrdinalIgnoreCase)
            && deformers is not null
            && deformers.TryGetBlendShapeChannel(sceneObject.Id, out FbxBlendShapeChannelBinding? channelBinding))
        {
            objectNode.Children.Add(CreateScalarChild("DeformPercent", FbxBinaryProperty.Float64(channelBinding.DefaultDeformPercent)));
            objectNode.Children.Add(CreateArrayChild("FullWeights", FbxBinaryProperty.Float64Array([channelBinding.FullWeight])));
        }
    }

    private static void AppendAnimationCurveChildren(FbxBinaryNode objectNode, FbxSceneObject sceneObject, FbxAnimationDocument? animations)
    {
        if (animations is null || !animations.TryGetCurve(sceneObject.Id, out FbxScalarCurve? curve))
            return;

        objectNode.Children.Add(CreateArrayChild("KeyTime", FbxBinaryProperty.Int64Array([.. curve.KeyTimes])));
        objectNode.Children.Add(CreateArrayChild("KeyValueFloat", FbxBinaryProperty.Float32Array([.. curve.Values])));
    }

    private static FbxBinaryNode BuildConnectionsNode(FbxSemanticDocument semantic)
    {
        FbxBinaryNode connectionsNode = new("Connections");
        foreach (FbxConnection connection in semantic.Connections.OrderBy(static connection => connection.NodeIndex))
            connectionsNode.Children.Add(BuildConnectionNode(connection.TypeCode, connection.Source, connection.Destination, connection.PropertyName));

        HashSet<long> modelIds = [.. semantic.Objects
            .Where(static sceneObject => sceneObject.Category == FbxObjectCategory.Model)
            .Select(static sceneObject => sceneObject.Id)];

        HashSet<long> modelIdsWithExplicitParent = [.. semantic.Connections
            .Where(connection => connection.Kind == FbxConnectionKind.ObjectToObject
                && connection.Source.Id is long sourceId
                && modelIds.Contains(sourceId)
                && (connection.Destination.Id == 0
                    || (connection.Destination.Id is long destinationId && modelIds.Contains(destinationId))))
            .Select(static connection => connection.Source.Id!.Value)];

        foreach (FbxSceneObject modelObject in semantic.Objects
            .Where(sceneObject => sceneObject.Category == FbxObjectCategory.Model && !modelIdsWithExplicitParent.Contains(sceneObject.Id))
            .OrderBy(static sceneObject => sceneObject.NodeIndex))
        {
            connectionsNode.Children.Add(BuildConnectionNode(
                "OO",
                new FbxObjectReference(modelObject.Id, null),
                new FbxObjectReference(0, null),
                null));
        }

        return connectionsNode;
    }

    private static FbxBinaryNode BuildConnectionNode(string typeCode, FbxObjectReference source, FbxObjectReference destination, string? propertyName)
    {
        FbxBinaryNode connectionNode = new("C");
        connectionNode.Properties.Add(FbxBinaryProperty.String(typeCode));
        connectionNode.Properties.Add(ConvertObjectReference(source));
        connectionNode.Properties.Add(ConvertObjectReference(destination));
        if (!string.IsNullOrWhiteSpace(propertyName))
            connectionNode.Properties.Add(FbxBinaryProperty.String(propertyName));
        return connectionNode;
    }

    private static FbxBinaryNode BuildTakesNode(IReadOnlyList<FbxTake> takes)
    {
        FbxBinaryNode takesNode = new("Takes");
        foreach (FbxTake take in takes.OrderBy(static take => take.NodeIndex))
        {
            FbxBinaryNode takeNode = new("Take");
            takeNode.Properties.Add(FbxBinaryProperty.String(take.Name));
            if (!string.IsNullOrWhiteSpace(take.FileName))
                takeNode.Children.Add(CreateScalarChild("FileName", FbxBinaryProperty.String(take.FileName)));
            if (!string.IsNullOrWhiteSpace(take.LocalTime))
                takeNode.Children.Add(CreateScalarChild("LocalTime", FbxBinaryProperty.String(take.LocalTime)));
            if (!string.IsNullOrWhiteSpace(take.ReferenceTime))
                takeNode.Children.Add(CreateScalarChild("ReferenceTime", FbxBinaryProperty.String(take.ReferenceTime)));
            takesNode.Children.Add(takeNode);
        }

        return takesNode;
    }

    private static FbxBinaryNode? BuildProperties70Node(IEnumerable<FbxPropertyEntry> properties)
    {
        FbxPropertyEntry[] orderedProperties = [.. properties.OrderBy(static property => property.NodeIndex).ThenBy(static property => property.Name, StringComparer.Ordinal)];
        if (orderedProperties.Length == 0)
            return null;

        FbxBinaryNode propertiesNode = new("Properties70");
        foreach (FbxPropertyEntry property in orderedProperties)
        {
            FbxBinaryNode propertyNode = new("P");
            propertyNode.Properties.Add(FbxBinaryProperty.String(property.Name));
            propertyNode.Properties.Add(FbxBinaryProperty.String(property.TypeName));
            propertyNode.Properties.Add(FbxBinaryProperty.String(property.DataTypeName));
            propertyNode.Properties.Add(FbxBinaryProperty.String(property.Flags));
            foreach (FbxSemanticValue value in property.Values)
                propertyNode.Properties.Add(ConvertPropertyValue(property, value));
            propertiesNode.Children.Add(propertyNode);
        }

        return propertiesNode;
    }

    private static FbxBinaryNode CreatePropertyNode(string name, string typeName, string dataTypeName, string flags, params FbxBinaryProperty[] values)
    {
        FbxBinaryNode propertyNode = new("P");
        propertyNode.Properties.Add(FbxBinaryProperty.String(name));
        propertyNode.Properties.Add(FbxBinaryProperty.String(typeName));
        propertyNode.Properties.Add(FbxBinaryProperty.String(dataTypeName));
        propertyNode.Properties.Add(FbxBinaryProperty.String(flags));
        foreach (FbxBinaryProperty value in values)
            propertyNode.Properties.Add(value);
        return propertyNode;
    }

    private static FbxBinaryNode BuildVector3LayerNode(string nodeName, int layerIndex, FbxLayerElement<Vector3> layer, string directChildName, string indexChildName)
    {
        FbxBinaryNode node = BuildLayerNode(nodeName, layerIndex, layer.Name, layer.MappingType, layer.ReferenceType);
        node.Children.Add(CreateArrayChild(directChildName, FbxBinaryProperty.Float64Array(FlattenVector3(layer.DirectValues))));
        if (layer.Indices.Count > 0)
            node.Children.Add(CreateArrayChild(indexChildName, FbxBinaryProperty.Int32Array([.. layer.Indices])));
        return node;
    }

    private static FbxBinaryNode BuildVector2LayerNode(string nodeName, int layerIndex, FbxLayerElement<Vector2> layer, string directChildName, string indexChildName)
    {
        FbxBinaryNode node = BuildLayerNode(nodeName, layerIndex, layer.Name, layer.MappingType, layer.ReferenceType);
        node.Children.Add(CreateArrayChild(directChildName, FbxBinaryProperty.Float64Array(FlattenVector2(layer.DirectValues))));
        if (layer.Indices.Count > 0)
            node.Children.Add(CreateArrayChild(indexChildName, FbxBinaryProperty.Int32Array([.. layer.Indices])));
        return node;
    }

    private static FbxBinaryNode BuildVector4LayerNode(string nodeName, int layerIndex, FbxLayerElement<Vector4> layer, string directChildName, string indexChildName)
    {
        FbxBinaryNode node = BuildLayerNode(nodeName, layerIndex, layer.Name, layer.MappingType, layer.ReferenceType);
        node.Children.Add(CreateArrayChild(directChildName, FbxBinaryProperty.Float64Array(FlattenVector4(layer.DirectValues))));
        if (layer.Indices.Count > 0)
            node.Children.Add(CreateArrayChild(indexChildName, FbxBinaryProperty.Int32Array([.. layer.Indices])));
        return node;
    }

    private static FbxBinaryNode BuildIntLayerNode(string nodeName, int layerIndex, FbxLayerElement<int> layer, string directChildName, string indexChildName)
    {
        FbxBinaryNode node = BuildLayerNode(nodeName, layerIndex, layer.Name, layer.MappingType, layer.ReferenceType);
        node.Children.Add(CreateArrayChild(directChildName, FbxBinaryProperty.Int32Array([.. layer.DirectValues])));
        if (layer.Indices.Count > 0)
            node.Children.Add(CreateArrayChild(indexChildName, FbxBinaryProperty.Int32Array([.. layer.Indices])));
        return node;
    }

    private static FbxBinaryNode BuildLayerNode(string nodeName, int layerIndex, string layerName, FbxLayerElementMappingType mappingType, FbxLayerElementReferenceType referenceType)
    {
        FbxBinaryNode node = new(nodeName);
        node.Properties.Add(FbxBinaryProperty.Int32(layerIndex));
        node.Children.Add(CreateScalarChild("Version", FbxBinaryProperty.Int32(101)));
        node.Children.Add(CreateScalarChild("Name", FbxBinaryProperty.String(layerName)));
        node.Children.Add(CreateScalarChild("MappingInformationType", FbxBinaryProperty.String(MapMappingType(mappingType))));
        node.Children.Add(CreateScalarChild("ReferenceInformationType", FbxBinaryProperty.String(MapReferenceType(referenceType))));
        return node;
    }

    private static Dictionary<long, FbxClusterBinding> BuildClusterBindingsByObjectId(FbxDeformerDocument? deformers)
    {
        Dictionary<long, FbxClusterBinding> result = [];
        if (deformers is null)
            return result;

        foreach (FbxSkinBinding skinBinding in deformers.SkinsByGeometryObjectId.Values)
        {
            foreach (FbxClusterBinding cluster in skinBinding.Clusters)
                result[cluster.ClusterObjectId] = cluster;
        }

        return result;
    }

    private static Dictionary<long, FbxBlendShapeChannelBinding> BuildShapeBindingsByShapeObjectId(FbxSemanticDocument semantic, FbxDeformerDocument? deformers)
    {
        Dictionary<long, FbxBlendShapeChannelBinding> result = [];
        if (deformers is null)
            return result;

        foreach (FbxIntermediateBlendShape blendShape in semantic.IntermediateScene.BlendShapes)
        {
            if (!blendShape.Subclass.Equals("BlendShapeChannel", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!deformers.TryGetBlendShapeChannel(blendShape.ObjectId, out FbxBlendShapeChannelBinding? channelBinding))
                continue;

            foreach (long connectedObjectId in blendShape.ConnectedObjectIds)
            {
                if (!semantic.TryGetObject(connectedObjectId, out FbxSceneObject sceneObject))
                    continue;
                if (sceneObject.Category == FbxObjectCategory.Geometry && sceneObject.Subclass.Equals("Shape", StringComparison.OrdinalIgnoreCase))
                    result[connectedObjectId] = channelBinding;
            }
        }

        return result;
    }

    private static FbxBinaryNode CreateScalarChild(string name, FbxBinaryProperty property)
    {
        FbxBinaryNode node = new(name);
        node.Properties.Add(property);
        return node;
    }

    private static FbxBinaryNode CreateArrayChild(string name, FbxBinaryProperty property)
    {
        FbxBinaryNode node = new(name);
        node.Properties.Add(property);
        return node;
    }

    private static FbxBinaryProperty ConvertSemanticValue(FbxSemanticValue value)
        => value.Kind switch
        {
            FbxSemanticValueKind.Integer => CreateIntegerProperty(value.IntegerValue),
            FbxSemanticValueKind.Number => FbxBinaryProperty.Float64(value.NumberValue),
            FbxSemanticValueKind.Boolean => FbxBinaryProperty.Boolean(value.BooleanValue),
            FbxSemanticValueKind.String or FbxSemanticValueKind.Identifier => FbxBinaryProperty.String(value.TextValue ?? string.Empty),
            _ => FbxBinaryProperty.String(value.AsString()),
        };

    private static FbxBinaryProperty ConvertPropertyValue(FbxPropertyEntry property, FbxSemanticValue value)
    {
        if (value.Kind is FbxSemanticValueKind.String or FbxSemanticValueKind.Identifier)
            return FbxBinaryProperty.String(value.TextValue ?? string.Empty);

        if (ShouldWritePropertyValuesAsBoolean(property))
            return FbxBinaryProperty.Boolean(value.BooleanValue);

        if (ShouldWritePropertyValuesAsInteger(property))
        {
            if (value.TryGetInt64(out long integerValue))
                return CreateIntegerProperty(integerValue);
            return FbxBinaryProperty.Int32(0);
        }

        if (value.TryGetDouble(out double numberValue))
            return FbxBinaryProperty.Float64(numberValue);

        return ConvertSemanticValue(value);
    }

    private static bool ShouldWritePropertyValuesAsBoolean(FbxPropertyEntry property)
    {
        string typeName = property.TypeName;
        string dataTypeName = property.DataTypeName;
        return typeName.Equals("bool", StringComparison.OrdinalIgnoreCase)
            || dataTypeName.Equals("Bool", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Visibility", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldWritePropertyValuesAsInteger(FbxPropertyEntry property)
    {
        string typeName = property.TypeName;
        string dataTypeName = property.DataTypeName;
        return typeName.Equals("int", StringComparison.OrdinalIgnoreCase)
            || typeName.Equals("enum", StringComparison.OrdinalIgnoreCase)
            || dataTypeName.Equals("Integer", StringComparison.OrdinalIgnoreCase)
            || dataTypeName.Equals("int", StringComparison.OrdinalIgnoreCase)
            || property.Name.EndsWith("Axis", StringComparison.OrdinalIgnoreCase)
            || property.Name.EndsWith("AxisSign", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("InheritType", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("RotationOrder", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("TimeMode", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("TimeProtocol", StringComparison.OrdinalIgnoreCase);
    }

    private static FbxBinaryProperty ConvertObjectReference(FbxObjectReference reference)
        => reference.Id.HasValue
            ? FbxBinaryProperty.Int64(reference.Id.Value)
            : FbxBinaryProperty.String(reference.Name ?? string.Empty);

    private static FbxBinaryProperty CreateIntegerProperty(long value)
        => value is >= int.MinValue and <= int.MaxValue
            ? FbxBinaryProperty.Int32((int)value)
            : FbxBinaryProperty.Int64(value);

    private static bool ShouldWriteInlineAttribute(FbxSceneObject sceneObject, string attributeName)
    {
        switch (sceneObject.Category)
        {
            case FbxObjectCategory.Geometry when sceneObject.Subclass.Equals("Mesh", StringComparison.OrdinalIgnoreCase):
                return attributeName is not ("Vertices" or "PolygonVertexIndex");

            case FbxObjectCategory.Geometry when sceneObject.Subclass.Equals("Shape", StringComparison.OrdinalIgnoreCase):
                return attributeName is not ("Vertices" or "PolygonVertexIndex" or "Indexes" or "Normals");

            case FbxObjectCategory.Deformer when sceneObject.Subclass.Equals("Cluster", StringComparison.OrdinalIgnoreCase):
                return attributeName is not ("Indexes" or "Weights" or "Transform" or "TransformLink");

            case FbxObjectCategory.Deformer when sceneObject.Subclass.Equals("BlendShapeChannel", StringComparison.OrdinalIgnoreCase):
                return attributeName is not ("DeformPercent" or "FullWeights");

            case FbxObjectCategory.AnimationCurve:
                return attributeName is not ("KeyTime" or "KeyValueFloat" or "KeyValueDouble");

            default:
                return true;
        }
    }

    private static string MapMappingType(FbxLayerElementMappingType mappingType)
        => mappingType switch
        {
            FbxLayerElementMappingType.ByControlPoint => "ByControlPoint",
            FbxLayerElementMappingType.ByPolygonVertex => "ByPolygonVertex",
            FbxLayerElementMappingType.ByPolygon => "ByPolygon",
            FbxLayerElementMappingType.AllSame => "AllSame",
            _ => "ByPolygonVertex",
        };

    private static string MapReferenceType(FbxLayerElementReferenceType referenceType)
        => referenceType switch
        {
            FbxLayerElementReferenceType.Direct => "Direct",
            FbxLayerElementReferenceType.Index => "Index",
            FbxLayerElementReferenceType.IndexToDirect => "IndexToDirect",
            _ => "Direct",
        };

    private static double[] FlattenVector2(IReadOnlyList<Vector2> values)
    {
        double[] result = new double[values.Count * 2];
        for (int index = 0; index < values.Count; index++)
        {
            int valueIndex = index * 2;
            result[valueIndex] = values[index].X;
            result[valueIndex + 1] = values[index].Y;
        }

        return result;
    }

    private static double[] FlattenVector3(IReadOnlyList<Vector3> values)
    {
        double[] result = new double[values.Count * 3];
        for (int index = 0; index < values.Count; index++)
        {
            int valueIndex = index * 3;
            result[valueIndex] = values[index].X;
            result[valueIndex + 1] = values[index].Y;
            result[valueIndex + 2] = values[index].Z;
        }

        return result;
    }

    private static double[] FlattenVector4(IReadOnlyList<Vector4> values)
    {
        double[] result = new double[values.Count * 4];
        for (int index = 0; index < values.Count; index++)
        {
            int valueIndex = index * 4;
            result[valueIndex] = values[index].X;
            result[valueIndex + 1] = values[index].Y;
            result[valueIndex + 2] = values[index].Z;
            result[valueIndex + 3] = values[index].W;
        }

        return result;
    }

    private static double[] FlattenShapeDeltas(IReadOnlyList<int> controlPointIndices, IReadOnlyDictionary<int, Vector3> deltasByControlPoint)
    {
        double[] result = new double[controlPointIndices.Count * 3];
        for (int index = 0; index < controlPointIndices.Count; index++)
        {
            Vector3 value = deltasByControlPoint.TryGetValue(controlPointIndices[index], out Vector3 delta) ? delta : Vector3.Zero;
            int valueIndex = index * 3;
            result[valueIndex] = value.X;
            result[valueIndex + 1] = value.Y;
            result[valueIndex + 2] = value.Z;
        }

        return result;
    }

    private static double[] FlattenMatrix(Matrix4x4 matrix)
        =>
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];

    private static int[] BuildPolygonVertexIndices(IReadOnlyList<int> controlPointIndices)
    {
        int[] result = new int[controlPointIndices.Count];
        for (int index = 0; index < controlPointIndices.Count - 1; index++)
            result[index] = controlPointIndices[index];
        result[^1] = ~controlPointIndices[^1];
        return result;
    }
}