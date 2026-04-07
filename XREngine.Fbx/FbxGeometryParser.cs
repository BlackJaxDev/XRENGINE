using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace XREngine.Fbx;

public static class FbxGeometryParser
{
    public static FbxGeometryDocument Parse(FbxStructuralDocument structural, FbxSemanticDocument semantic)
    {
        ArgumentNullException.ThrowIfNull(structural);
        ArgumentNullException.ThrowIfNull(semantic);

        int[][] childrenByNode = BuildChildrenByNode(structural.Nodes);
        Dictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex = new(structural.ArrayWorkItems.Count);
        foreach (FbxArrayWorkItem workItem in structural.ArrayWorkItems)
            arrayWorkItemsByPropertyIndex[workItem.PropertyIndex] = workItem;

        Dictionary<long, FbxMeshGeometry> meshesByObjectId = new();
        foreach (FbxIntermediateMesh mesh in semantic.IntermediateScene.Meshes)
        {
            if (!semantic.TryGetObject(mesh.ObjectId, out FbxSceneObject sceneObject))
                continue;

            meshesByObjectId[mesh.ObjectId] = ParseMeshGeometry(structural, sceneObject, childrenByNode, arrayWorkItemsByPropertyIndex);
        }

        return new FbxGeometryDocument(meshesByObjectId);
    }

    private static FbxMeshGeometry ParseMeshGeometry(
        FbxStructuralDocument structural,
        FbxSceneObject sceneObject,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex)
    {
        int nodeIndex = sceneObject.NodeIndex;

        Vector3[] controlPoints = ReadVector3ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, nodeIndex, "Vertices");
        int[] polygonVertexIndices = ReadInt32ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, nodeIndex, "PolygonVertexIndex");

        List<FbxLayerElement<Vector3>> normals = [];
        List<FbxLayerElement<Vector3>> tangents = [];
        List<FbxLayerElement<Vector2>> textureCoordinates = [];
        List<FbxLayerElement<Vector4>> colors = [];
        FbxLayerElement<int>? materials = null;

        foreach (int childIndex in childrenByNode[nodeIndex])
        {
            FbxNodeRecord child = structural.Nodes[childIndex];
            string childName = structural.GetNodeName(child);
            switch (childName)
            {
                case "LayerElementNormal":
                    normals.Add(ParseVector3Layer(structural, childrenByNode, arrayWorkItemsByPropertyIndex, childIndex, "Normals", "NormalsIndex"));
                    break;
                case "LayerElementTangent":
                    tangents.Add(ParseVector3Layer(structural, childrenByNode, arrayWorkItemsByPropertyIndex, childIndex, "Tangents", "TangentIndex"));
                    break;
                case "LayerElementUV":
                    textureCoordinates.Add(ParseVector2Layer(structural, childrenByNode, arrayWorkItemsByPropertyIndex, childIndex, "UV", "UVIndex"));
                    break;
                case "LayerElementColor":
                    colors.Add(ParseVector4Layer(structural, childrenByNode, arrayWorkItemsByPropertyIndex, childIndex, "Colors", "ColorIndex"));
                    break;
                case "LayerElementMaterial":
                    materials = ParseIntLayer(structural, childrenByNode, arrayWorkItemsByPropertyIndex, childIndex, "Materials", "MaterialIndex");
                    break;
            }
        }

        return new FbxMeshGeometry(
            sceneObject.Id,
            sceneObject.DisplayName,
            sceneObject.Subclass,
            ObjectIndex: nodeIndex,
            NodeIndex: nodeIndex,
            ControlPoints: controlPoints,
            PolygonVertexIndices: polygonVertexIndices,
            Normals: normals,
            Tangents: tangents,
            TextureCoordinates: textureCoordinates,
            Colors: colors,
            Materials: materials);
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

    private static FbxLayerElement<Vector3> ParseVector3Layer(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int layerNodeIndex,
        string directChildName,
        string indexChildName)
    {
        string name = ReadLeafString(structural, childrenByNode, layerNodeIndex, "Name") ?? string.Empty;
        FbxLayerElementMappingType mappingType = ParseMappingType(ReadLeafString(structural, childrenByNode, layerNodeIndex, "MappingInformationType"));
        FbxLayerElementReferenceType referenceType = ParseReferenceType(ReadLeafString(structural, childrenByNode, layerNodeIndex, "ReferenceInformationType"));
        Vector3[] directValues = ReadVector3ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, layerNodeIndex, directChildName);
        int[] indices = TryReadInt32ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, layerNodeIndex, indexChildName) ?? [];
        return new FbxLayerElement<Vector3>(name, mappingType, referenceType, directValues, indices, layerNodeIndex);
    }

    private static FbxLayerElement<Vector2> ParseVector2Layer(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int layerNodeIndex,
        string directChildName,
        string indexChildName)
    {
        string name = ReadLeafString(structural, childrenByNode, layerNodeIndex, "Name") ?? string.Empty;
        FbxLayerElementMappingType mappingType = ParseMappingType(ReadLeafString(structural, childrenByNode, layerNodeIndex, "MappingInformationType"));
        FbxLayerElementReferenceType referenceType = ParseReferenceType(ReadLeafString(structural, childrenByNode, layerNodeIndex, "ReferenceInformationType"));
        Vector2[] directValues = ReadVector2ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, layerNodeIndex, directChildName);
        int[] indices = TryReadInt32ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, layerNodeIndex, indexChildName) ?? [];
        return new FbxLayerElement<Vector2>(name, mappingType, referenceType, directValues, indices, layerNodeIndex);
    }

    private static FbxLayerElement<Vector4> ParseVector4Layer(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int layerNodeIndex,
        string directChildName,
        string indexChildName)
    {
        string name = ReadLeafString(structural, childrenByNode, layerNodeIndex, "Name") ?? string.Empty;
        FbxLayerElementMappingType mappingType = ParseMappingType(ReadLeafString(structural, childrenByNode, layerNodeIndex, "MappingInformationType"));
        FbxLayerElementReferenceType referenceType = ParseReferenceType(ReadLeafString(structural, childrenByNode, layerNodeIndex, "ReferenceInformationType"));
        Vector4[] directValues = ReadVector4ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, layerNodeIndex, directChildName);
        int[] indices = TryReadInt32ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, layerNodeIndex, indexChildName) ?? [];
        return new FbxLayerElement<Vector4>(name, mappingType, referenceType, directValues, indices, layerNodeIndex);
    }

    private static FbxLayerElement<int> ParseIntLayer(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int layerNodeIndex,
        string directChildName,
        string indexChildName)
    {
        string name = ReadLeafString(structural, childrenByNode, layerNodeIndex, "Name") ?? string.Empty;
        FbxLayerElementMappingType mappingType = ParseMappingType(ReadLeafString(structural, childrenByNode, layerNodeIndex, "MappingInformationType"));
        FbxLayerElementReferenceType referenceType = ParseReferenceType(ReadLeafString(structural, childrenByNode, layerNodeIndex, "ReferenceInformationType"));
        int[] directValues = ReadInt32ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, layerNodeIndex, directChildName);
        int[] indices = TryReadInt32ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, layerNodeIndex, indexChildName) ?? [];
        return new FbxLayerElement<int>(name, mappingType, referenceType, directValues, indices, layerNodeIndex);
    }

    private static string? ReadLeafString(FbxStructuralDocument structural, int[][] childrenByNode, int ownerNodeIndex, string childName)
    {
        foreach (int childIndex in childrenByNode[ownerNodeIndex])
        {
            FbxNodeRecord child = structural.Nodes[childIndex];
            if (structural.GetNodeName(child) != childName || child.PropertyCount == 0)
                continue;

            return ReadScalarAsString(structural, structural.Properties[child.FirstPropertyIndex]);
        }

        return null;
    }

    private static Vector3[] ReadVector3ArrayChild(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int ownerNodeIndex,
        string childName)
    {
        double[] values = ReadDoubleArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, ownerNodeIndex, childName);
        if (values.Length == 0)
            return [];
        if (values.Length % 3 != 0)
            throw new FbxParseException($"FBX '{childName}' array length is not divisible by 3", structural.Nodes[ownerNodeIndex].NameOffset);

        Vector3[] result = new Vector3[values.Length / 3];
        for (int index = 0; index < result.Length; index++)
        {
            int valueIndex = index * 3;
            result[index] = new Vector3((float)values[valueIndex], (float)values[valueIndex + 1], (float)values[valueIndex + 2]);
        }

        return result;
    }

    private static Vector2[] ReadVector2ArrayChild(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int ownerNodeIndex,
        string childName)
    {
        double[] values = ReadDoubleArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, ownerNodeIndex, childName);
        if (values.Length == 0)
            return [];
        if (values.Length % 2 != 0)
            throw new FbxParseException($"FBX '{childName}' array length is not divisible by 2", structural.Nodes[ownerNodeIndex].NameOffset);

        Vector2[] result = new Vector2[values.Length / 2];
        for (int index = 0; index < result.Length; index++)
        {
            int valueIndex = index * 2;
            result[index] = new Vector2((float)values[valueIndex], (float)values[valueIndex + 1]);
        }

        return result;
    }

    private static Vector4[] ReadVector4ArrayChild(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int ownerNodeIndex,
        string childName)
    {
        double[] values = ReadDoubleArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, ownerNodeIndex, childName);
        if (values.Length == 0)
            return [];
        if (values.Length % 4 != 0 && values.Length % 3 != 0)
            throw new FbxParseException($"FBX '{childName}' array length is not divisible by 3 or 4", structural.Nodes[ownerNodeIndex].NameOffset);

        int stride = values.Length % 4 == 0 ? 4 : 3;
        Vector4[] result = new Vector4[values.Length / stride];
        for (int index = 0; index < result.Length; index++)
        {
            int valueIndex = index * stride;
            result[index] = stride == 4
                ? new Vector4((float)values[valueIndex], (float)values[valueIndex + 1], (float)values[valueIndex + 2], (float)values[valueIndex + 3])
                : new Vector4((float)values[valueIndex], (float)values[valueIndex + 1], (float)values[valueIndex + 2], 1.0f);
        }

        return result;
    }

    private static int[] ReadInt32ArrayChild(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int ownerNodeIndex,
        string childName)
        => TryReadInt32ArrayChild(structural, childrenByNode, arrayWorkItemsByPropertyIndex, ownerNodeIndex, childName)
            ?? throw new FbxParseException($"FBX node is missing required array child '{childName}'", structural.Nodes[ownerNodeIndex].NameOffset);

    private static int[]? TryReadInt32ArrayChild(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int ownerNodeIndex,
        string childName)
    {
        if (!TryFindChildNode(structural, childrenByNode, ownerNodeIndex, childName, out int childNodeIndex))
            return null;

        return ReadInt32Array(structural, arrayWorkItemsByPropertyIndex, structural.Nodes[childNodeIndex], 0);
    }

    private static double[] ReadDoubleArrayChild(
        FbxStructuralDocument structural,
        int[][] childrenByNode,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        int ownerNodeIndex,
        string childName)
    {
        if (!TryFindChildNode(structural, childrenByNode, ownerNodeIndex, childName, out int childNodeIndex))
            return [];

        return ReadDoubleArray(structural, arrayWorkItemsByPropertyIndex, structural.Nodes[childNodeIndex], 0);
    }

    private static bool TryFindChildNode(FbxStructuralDocument structural, int[][] childrenByNode, int ownerNodeIndex, string childName, out int childNodeIndex)
    {
        foreach (int candidateIndex in childrenByNode[ownerNodeIndex])
        {
            if (structural.GetNodeName(structural.Nodes[candidateIndex]) == childName)
            {
                childNodeIndex = candidateIndex;
                return true;
            }
        }

        childNodeIndex = -1;
        return false;
    }

    private static int[] ReadInt32Array(
        FbxStructuralDocument structural,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        FbxNodeRecord node,
        int propertyOffset)
    {
        if (propertyOffset >= node.PropertyCount)
            return [];

        int propertyIndex = node.FirstPropertyIndex + propertyOffset;
        FbxPropertyRecord property = structural.Properties[propertyIndex];
        return property.Kind switch
        {
            FbxPropertyKind.Int32Array or FbxPropertyKind.Int64Array or FbxPropertyKind.AsciiArray => ReadInt32Array(structural, property, arrayWorkItemsByPropertyIndex.TryGetValue(propertyIndex, out FbxArrayWorkItem workItem) ? workItem : default, arrayWorkItemsByPropertyIndex.ContainsKey(propertyIndex)),
            FbxPropertyKind.Int32 or FbxPropertyKind.Int64 or FbxPropertyKind.AsciiScalar => [ReadScalarAsInt32(structural, property)],
            _ => []
        };
    }

    private static int[] ReadInt32Array(FbxStructuralDocument structural, FbxPropertyRecord property, FbxArrayWorkItem workItem, bool hasWorkItem)
    {
        if (property.Kind == FbxPropertyKind.AsciiArray)
            return ParseAsciiNumericTokens(ReadAsciiArrayBody(structural.GetPropertyData(property)), int.Parse, "integer");

        if (!hasWorkItem)
            return [];

        switch (property.Kind)
        {
            case FbxPropertyKind.Int32Array:
                return FbxArrayDecodeHelper.ReadInt32ArrayDirect(structural, workItem);
            case FbxPropertyKind.Int64Array:
                long[] wideValues = FbxArrayDecodeHelper.ReadInt64ArrayDirect(structural, workItem);
                int[] result = new int[wideValues.Length];
                for (int index = 0; index < result.Length; index++)
                    result[index] = checked((int)wideValues[index]);
                return result;
        }

        return [];
    }

    private static double[] ReadDoubleArray(
        FbxStructuralDocument structural,
        IReadOnlyDictionary<int, FbxArrayWorkItem> arrayWorkItemsByPropertyIndex,
        FbxNodeRecord node,
        int propertyOffset)
    {
        if (propertyOffset >= node.PropertyCount)
            return [];

        int propertyIndex = node.FirstPropertyIndex + propertyOffset;
        FbxPropertyRecord property = structural.Properties[propertyIndex];
        return property.Kind switch
        {
            FbxPropertyKind.Float32Array or FbxPropertyKind.Float64Array or FbxPropertyKind.AsciiArray => ReadDoubleArray(structural, property, arrayWorkItemsByPropertyIndex.TryGetValue(propertyIndex, out FbxArrayWorkItem workItem) ? workItem : default, arrayWorkItemsByPropertyIndex.ContainsKey(propertyIndex)),
            FbxPropertyKind.Float32 or FbxPropertyKind.Float64 or FbxPropertyKind.Int32 or FbxPropertyKind.Int64 or FbxPropertyKind.AsciiScalar => [ReadScalarAsDouble(structural, property)],
            _ => []
        };
    }

    private static double[] ReadDoubleArray(FbxStructuralDocument structural, FbxPropertyRecord property, FbxArrayWorkItem workItem, bool hasWorkItem)
    {
        if (property.Kind == FbxPropertyKind.AsciiArray)
            return ParseAsciiNumericTokens(ReadAsciiArrayBody(structural.GetPropertyData(property)), static text => double.Parse(text, CultureInfo.InvariantCulture), "number");

        if (!hasWorkItem)
            return [];

        switch (property.Kind)
        {
            case FbxPropertyKind.Float32Array:
                float[] narrowValues = FbxArrayDecodeHelper.ReadFloat32ArrayDirect(structural, workItem);
                double[] widenedValues = new double[narrowValues.Length];
                for (int index = 0; index < widenedValues.Length; index++)
                    widenedValues[index] = narrowValues[index];
                return widenedValues;
            case FbxPropertyKind.Float64Array:
                return FbxArrayDecodeHelper.ReadFloat64ArrayDirect(structural, workItem);
        }

        return [];
    }

    private static int ReadScalarAsInt32(FbxStructuralDocument structural, FbxPropertyRecord property)
        => property.Kind switch
        {
            FbxPropertyKind.Int32 => structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(structural.GetPropertyData(property))
                : BinaryPrimitives.ReadInt32LittleEndian(structural.GetPropertyData(property)),
            FbxPropertyKind.Int64 => checked((int)(structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadInt64BigEndian(structural.GetPropertyData(property))
                : BinaryPrimitives.ReadInt64LittleEndian(structural.GetPropertyData(property)))),
            FbxPropertyKind.AsciiScalar => int.Parse(ReadScalarAsString(structural, property), CultureInfo.InvariantCulture),
            _ => 0,
        };

    private static double ReadScalarAsDouble(FbxStructuralDocument structural, FbxPropertyRecord property)
        => property.Kind switch
        {
            FbxPropertyKind.Float32 => structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(structural.GetPropertyData(property))
                : BinaryPrimitives.ReadSingleLittleEndian(structural.GetPropertyData(property)),
            FbxPropertyKind.Float64 => structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadDoubleBigEndian(structural.GetPropertyData(property))
                : BinaryPrimitives.ReadDoubleLittleEndian(structural.GetPropertyData(property)),
            FbxPropertyKind.Int32 => structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(structural.GetPropertyData(property))
                : BinaryPrimitives.ReadInt32LittleEndian(structural.GetPropertyData(property)),
            FbxPropertyKind.Int64 => structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadInt64BigEndian(structural.GetPropertyData(property))
                : BinaryPrimitives.ReadInt64LittleEndian(structural.GetPropertyData(property)),
            FbxPropertyKind.AsciiScalar => double.Parse(ReadScalarAsString(structural, property), CultureInfo.InvariantCulture),
            _ => 0.0d,
        };

    private static string ReadScalarAsString(FbxStructuralDocument structural, FbxPropertyRecord property)
        => property.Kind == FbxPropertyKind.String
            ? Encoding.UTF8.GetString(structural.GetPropertyData(property))
            : Encoding.UTF8.GetString(structural.GetPropertyData(property));

    private static string ReadAsciiArrayBody(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);
        int valuesOffset = text.IndexOf("a:", StringComparison.Ordinal);
        if (valuesOffset >= 0)
            text = text[(valuesOffset + 2)..];

        int endOffset = text.LastIndexOf('}');
        if (endOffset >= 0)
            text = text[..endOffset];

        return text;
    }

    private static T[] ParseAsciiNumericTokens<T>(string text, Func<string, T> parser, string expectedType)
    {
        string[] parts = text.Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        T[] result = new T[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            try
            {
                result[index] = parser(parts[index]);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new FbxParseException($"ASCII FBX array contains an invalid {expectedType} token '{parts[index]}'", 0);
            }
        }

        return result;
    }

    private static FbxLayerElementMappingType ParseMappingType(string? value)
        => value switch
        {
            "ByControlPoint" => FbxLayerElementMappingType.ByControlPoint,
            "ByVertice" or "ByVertex" => FbxLayerElementMappingType.ByControlPoint,
            "ByPolygonVertex" => FbxLayerElementMappingType.ByPolygonVertex,
            "ByPolygon" => FbxLayerElementMappingType.ByPolygon,
            "AllSame" => FbxLayerElementMappingType.AllSame,
            _ => FbxLayerElementMappingType.Unknown,
        };

    private static FbxLayerElementReferenceType ParseReferenceType(string? value)
        => value switch
        {
            "Direct" => FbxLayerElementReferenceType.Direct,
            "Index" => FbxLayerElementReferenceType.Index,
            "IndexToDirect" => FbxLayerElementReferenceType.IndexToDirect,
            _ => FbxLayerElementReferenceType.Unknown,
        };
}