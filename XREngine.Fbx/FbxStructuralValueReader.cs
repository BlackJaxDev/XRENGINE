using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace XREngine.Fbx;

internal sealed class FbxStructuralValueReader
{
    private readonly FbxStructuralDocument _structural;
    private readonly int[][] _childrenByNode;
    private readonly Dictionary<int, FbxArrayWorkItem> _arrayWorkItemsByPropertyIndex;

    public FbxStructuralValueReader(FbxStructuralDocument structural)
    {
        ArgumentNullException.ThrowIfNull(structural);

        _structural = structural;
        _childrenByNode = BuildChildrenByNode(structural.Nodes);
        _arrayWorkItemsByPropertyIndex = new Dictionary<int, FbxArrayWorkItem>(structural.ArrayWorkItems.Count);
        foreach (FbxArrayWorkItem workItem in structural.ArrayWorkItems)
            _arrayWorkItemsByPropertyIndex[workItem.PropertyIndex] = workItem;
    }

    public IReadOnlyList<int> GetChildren(int nodeIndex)
        => _childrenByNode[nodeIndex];

    public bool TryFindChildNode(int ownerNodeIndex, string childName, out int childNodeIndex)
    {
        foreach (int candidateIndex in _childrenByNode[ownerNodeIndex])
        {
            if (_structural.GetNodeName(_structural.Nodes[candidateIndex]) == childName)
            {
                childNodeIndex = candidateIndex;
                return true;
            }
        }

        childNodeIndex = -1;
        return false;
    }

    public string? ReadLeafString(int ownerNodeIndex, string childName)
    {
        if (!TryGetChildProperty(ownerNodeIndex, childName, 0, out FbxPropertyRecord property))
            return null;

        return ReadScalarAsString(property);
    }

    public long? ReadLeafInt64(int ownerNodeIndex, string childName)
    {
        if (!TryGetChildProperty(ownerNodeIndex, childName, 0, out FbxPropertyRecord property))
            return null;

        return ReadScalarAsInt64(property);
    }

    public double? ReadLeafDouble(int ownerNodeIndex, string childName)
    {
        if (!TryGetChildProperty(ownerNodeIndex, childName, 0, out FbxPropertyRecord property))
            return null;

        return ReadScalarAsDouble(property);
    }

    public int[] ReadInt32ArrayChild(int ownerNodeIndex, string childName)
        => TryReadInt32ArrayChild(ownerNodeIndex, childName)
            ?? throw new FbxParseException($"FBX node is missing required array child '{childName}'", _structural.Nodes[ownerNodeIndex].NameOffset);

    public int[]? TryReadInt32ArrayChild(int ownerNodeIndex, string childName)
    {
        if (!TryFindChildNode(ownerNodeIndex, childName, out int childNodeIndex))
            return null;

        return ReadInt32Array(_structural.Nodes[childNodeIndex], 0);
    }

    public long[] ReadInt64ArrayChild(int ownerNodeIndex, string childName)
        => TryReadInt64ArrayChild(ownerNodeIndex, childName)
            ?? throw new FbxParseException($"FBX node is missing required array child '{childName}'", _structural.Nodes[ownerNodeIndex].NameOffset);

    public long[]? TryReadInt64ArrayChild(int ownerNodeIndex, string childName)
    {
        if (!TryFindChildNode(ownerNodeIndex, childName, out int childNodeIndex))
            return null;

        return ReadInt64Array(_structural.Nodes[childNodeIndex], 0);
    }

    public double[] ReadDoubleArrayChild(int ownerNodeIndex, string childName)
        => TryReadDoubleArrayChild(ownerNodeIndex, childName) ?? [];

    public double[]? TryReadDoubleArrayChild(int ownerNodeIndex, string childName)
    {
        if (!TryFindChildNode(ownerNodeIndex, childName, out int childNodeIndex))
            return null;

        return ReadDoubleArray(_structural.Nodes[childNodeIndex], 0);
    }

    public Vector3[] ReadVector3ArrayChild(int ownerNodeIndex, string childName)
    {
        double[] values = ReadDoubleArrayChild(ownerNodeIndex, childName);
        if (values.Length == 0)
            return [];
        if (values.Length % 3 != 0)
            throw new FbxParseException($"FBX '{childName}' array length is not divisible by 3", _structural.Nodes[ownerNodeIndex].NameOffset);

        Vector3[] result = new Vector3[values.Length / 3];
        for (int index = 0; index < result.Length; index++)
        {
            int valueIndex = index * 3;
            result[index] = new Vector3((float)values[valueIndex], (float)values[valueIndex + 1], (float)values[valueIndex + 2]);
        }

        return result;
    }

    public Matrix4x4? TryReadMatrix4x4Child(int ownerNodeIndex, string childName)
    {
        double[]? values = TryReadDoubleArrayChild(ownerNodeIndex, childName);
        if (values is null || values.Length == 0)
            return null;
        if (values.Length != 16)
            throw new FbxParseException($"FBX '{childName}' matrix array length must be exactly 16.", _structural.Nodes[ownerNodeIndex].NameOffset);

        return new Matrix4x4(
            (float)values[0], (float)values[1], (float)values[2], (float)values[3],
            (float)values[4], (float)values[5], (float)values[6], (float)values[7],
            (float)values[8], (float)values[9], (float)values[10], (float)values[11],
            (float)values[12], (float)values[13], (float)values[14], (float)values[15]);
    }

    private bool TryGetChildProperty(int ownerNodeIndex, string childName, int propertyOffset, out FbxPropertyRecord property)
    {
        if (TryFindChildNode(ownerNodeIndex, childName, out int childNodeIndex))
        {
            FbxNodeRecord childNode = _structural.Nodes[childNodeIndex];
            if (propertyOffset < childNode.PropertyCount)
            {
                property = _structural.Properties[childNode.FirstPropertyIndex + propertyOffset];
                return true;
            }
        }

        property = default;
        return false;
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

    private int[] ReadInt32Array(FbxNodeRecord node, int propertyOffset)
    {
        if (propertyOffset >= node.PropertyCount)
            return [];

        int propertyIndex = node.FirstPropertyIndex + propertyOffset;
        FbxPropertyRecord property = _structural.Properties[propertyIndex];
        return property.Kind switch
        {
            FbxPropertyKind.Int32Array or FbxPropertyKind.Int64Array or FbxPropertyKind.AsciiArray => ReadInt32Array(property, _arrayWorkItemsByPropertyIndex.TryGetValue(propertyIndex, out FbxArrayWorkItem workItem) ? workItem : default, _arrayWorkItemsByPropertyIndex.ContainsKey(propertyIndex)),
            FbxPropertyKind.Int32 or FbxPropertyKind.Int64 or FbxPropertyKind.AsciiScalar => [checked((int)ReadScalarAsInt64(property))],
            _ => []
        };
    }

    private int[] ReadInt32Array(FbxPropertyRecord property, FbxArrayWorkItem workItem, bool hasWorkItem)
    {
        if (property.Kind == FbxPropertyKind.AsciiArray)
            return ParseAsciiNumericTokens(ReadAsciiArrayBody(_structural.GetPropertyData(property)), static text => int.Parse(text, CultureInfo.InvariantCulture), "integer");

        if (!hasWorkItem)
            return [];

        switch (property.Kind)
        {
            case FbxPropertyKind.Int32Array:
                return FbxArrayDecodeHelper.ReadInt32ArrayDirect(_structural, workItem);
            case FbxPropertyKind.Int64Array:
                long[] wideValues = FbxArrayDecodeHelper.ReadInt64ArrayDirect(_structural, workItem);
                int[] result = new int[wideValues.Length];
                for (int index = 0; index < result.Length; index++)
                    result[index] = checked((int)wideValues[index]);
                return result;
        }

        return [];
    }

    private long[] ReadInt64Array(FbxNodeRecord node, int propertyOffset)
    {
        if (propertyOffset >= node.PropertyCount)
            return [];

        int propertyIndex = node.FirstPropertyIndex + propertyOffset;
        FbxPropertyRecord property = _structural.Properties[propertyIndex];
        return property.Kind switch
        {
            FbxPropertyKind.Int32Array or FbxPropertyKind.Int64Array or FbxPropertyKind.AsciiArray => ReadInt64Array(property, _arrayWorkItemsByPropertyIndex.TryGetValue(propertyIndex, out FbxArrayWorkItem workItem) ? workItem : default, _arrayWorkItemsByPropertyIndex.ContainsKey(propertyIndex)),
            FbxPropertyKind.Int32 or FbxPropertyKind.Int64 or FbxPropertyKind.AsciiScalar => [ReadScalarAsInt64(property)],
            _ => []
        };
    }

    private long[] ReadInt64Array(FbxPropertyRecord property, FbxArrayWorkItem workItem, bool hasWorkItem)
    {
        if (property.Kind == FbxPropertyKind.AsciiArray)
            return ParseAsciiNumericTokens(ReadAsciiArrayBody(_structural.GetPropertyData(property)), static text => long.Parse(text, CultureInfo.InvariantCulture), "integer");

        if (!hasWorkItem)
            return [];

        switch (property.Kind)
        {
            case FbxPropertyKind.Int32Array:
                int[] narrowValues = FbxArrayDecodeHelper.ReadInt32ArrayDirect(_structural, workItem);
                long[] widenedValues = new long[narrowValues.Length];
                for (int index = 0; index < widenedValues.Length; index++)
                    widenedValues[index] = narrowValues[index];
                return widenedValues;
            case FbxPropertyKind.Int64Array:
                return FbxArrayDecodeHelper.ReadInt64ArrayDirect(_structural, workItem);
        }

        return [];
    }

    private double[] ReadDoubleArray(FbxNodeRecord node, int propertyOffset)
    {
        if (propertyOffset >= node.PropertyCount)
            return [];

        int propertyIndex = node.FirstPropertyIndex + propertyOffset;
        FbxPropertyRecord property = _structural.Properties[propertyIndex];
        return property.Kind switch
        {
            FbxPropertyKind.Float32Array or FbxPropertyKind.Float64Array or FbxPropertyKind.AsciiArray => ReadDoubleArray(property, _arrayWorkItemsByPropertyIndex.TryGetValue(propertyIndex, out FbxArrayWorkItem workItem) ? workItem : default, _arrayWorkItemsByPropertyIndex.ContainsKey(propertyIndex)),
            FbxPropertyKind.Float32 or FbxPropertyKind.Float64 or FbxPropertyKind.Int32 or FbxPropertyKind.Int64 or FbxPropertyKind.AsciiScalar => [ReadScalarAsDouble(property)],
            _ => []
        };
    }

    private double[] ReadDoubleArray(FbxPropertyRecord property, FbxArrayWorkItem workItem, bool hasWorkItem)
    {
        if (property.Kind == FbxPropertyKind.AsciiArray)
            return ParseAsciiNumericTokens(ReadAsciiArrayBody(_structural.GetPropertyData(property)), static text => double.Parse(text, CultureInfo.InvariantCulture), "number");

        if (!hasWorkItem)
            return [];

        switch (property.Kind)
        {
            case FbxPropertyKind.Float32Array:
                float[] narrowValues = FbxArrayDecodeHelper.ReadFloat32ArrayDirect(_structural, workItem);
                double[] widenedValues = new double[narrowValues.Length];
                for (int index = 0; index < widenedValues.Length; index++)
                    widenedValues[index] = narrowValues[index];
                return widenedValues;
            case FbxPropertyKind.Float64Array:
                return FbxArrayDecodeHelper.ReadFloat64ArrayDirect(_structural, workItem);
        }

        return [];
    }

    private long ReadScalarAsInt64(FbxPropertyRecord property)
        => property.Kind switch
        {
            FbxPropertyKind.Int32 => _structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(_structural.GetPropertyData(property))
                : BinaryPrimitives.ReadInt32LittleEndian(_structural.GetPropertyData(property)),
            FbxPropertyKind.Int64 => _structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadInt64BigEndian(_structural.GetPropertyData(property))
                : BinaryPrimitives.ReadInt64LittleEndian(_structural.GetPropertyData(property)),
            FbxPropertyKind.AsciiScalar => long.Parse(ReadScalarAsString(property), CultureInfo.InvariantCulture),
            FbxPropertyKind.Boolean => _structural.GetPropertyData(property)[0] == 0 ? 0L : 1L,
            _ => 0L,
        };

    private double ReadScalarAsDouble(FbxPropertyRecord property)
        => property.Kind switch
        {
            FbxPropertyKind.Float32 => _structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(_structural.GetPropertyData(property))
                : BinaryPrimitives.ReadSingleLittleEndian(_structural.GetPropertyData(property)),
            FbxPropertyKind.Float64 => _structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadDoubleBigEndian(_structural.GetPropertyData(property))
                : BinaryPrimitives.ReadDoubleLittleEndian(_structural.GetPropertyData(property)),
            FbxPropertyKind.Int32 => _structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(_structural.GetPropertyData(property))
                : BinaryPrimitives.ReadInt32LittleEndian(_structural.GetPropertyData(property)),
            FbxPropertyKind.Int64 => _structural.Header.IsBigEndian
                ? BinaryPrimitives.ReadInt64BigEndian(_structural.GetPropertyData(property))
                : BinaryPrimitives.ReadInt64LittleEndian(_structural.GetPropertyData(property)),
            FbxPropertyKind.AsciiScalar => double.Parse(ReadScalarAsString(property), CultureInfo.InvariantCulture),
            FbxPropertyKind.Boolean => _structural.GetPropertyData(property)[0] == 0 ? 0.0d : 1.0d,
            _ => 0.0d,
        };

    private string ReadScalarAsString(FbxPropertyRecord property)
        => Encoding.UTF8.GetString(_structural.GetPropertyData(property));

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
}