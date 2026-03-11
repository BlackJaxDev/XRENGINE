using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Serialization;

namespace XREngine.Core.Files;

public sealed class CookedBinarySchema
{
    internal CookedBinarySchema(Type? rootType, bool includesValues, CookedBinarySchemaNode root)
    {
        RootType = rootType;
        IncludesValues = includesValues;
        Root = root;
    }

    public Type? RootType { get; }
    public bool IncludesValues { get; }
    public CookedBinarySchemaNode Root { get; }
    public long? TotalSize => Root.Size;
    public string TotalSizeDescription => Root.SizeDescription;

    public string ToAsciiTree()
        => CookedBinarySerializer.CookedBinarySchemaFormatter.Format(this);

    public override string ToString()
        => ToAsciiTree();
}

public sealed class CookedBinarySchemaNode
{
    private readonly List<CookedBinarySchemaNode> _children = [];

    internal CookedBinarySchemaNode(string name, string kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }
    public string Kind { get; }
    public string? Marker { get; internal set; }
    public string? TypeName { get; internal set; }
    public long? Offset { get; internal set; }
    public long? Size { get; internal set; }
    public string SizeDescription { get; internal set; } = "unknown";
    public string? ValueDisplay { get; internal set; }
    public string? Notes { get; internal set; }
    public IReadOnlyList<CookedBinarySchemaNode> Children => _children;

    internal List<CookedBinarySchemaNode> MutableChildren => _children;
}

public static partial class CookedBinarySerializer
{
    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static CookedBinarySchema InspectSchema(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var builder = new CookedBinarySchemaBuilder(callbacks: null, includeValues: false);
        CookedBinarySchemaNode root = builder.BuildTypeNode("root", type, allowCustom: true);
        return new CookedBinarySchema(type, includesValues: false, root);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static CookedBinarySchema InspectValue(object? value, CookedBinarySerializationCallbacks? callbacks = null)
    {
        var builder = new CookedBinarySchemaBuilder(callbacks, includeValues: true);
        Type? rootType = value?.GetType();
        CookedBinarySchemaNode root = builder.BuildValueNode("root", rootType, value, allowCustom: true);
        AssignOffsets(root, 0L);
        return new CookedBinarySchema(rootType, includesValues: true, root);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static string FormatSchema(Type type)
        => InspectSchema(type).ToAsciiTree();

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static string FormatValue(object? value, CookedBinarySerializationCallbacks? callbacks = null)
        => InspectValue(value, callbacks).ToAsciiTree();

    private static void AssignOffsets(CookedBinarySchemaNode node, long offset)
    {
        node.Offset = offset;
        long cursor = offset;
        foreach (var child in node.Children)
        {
            AssignOffsets(child, cursor);
            cursor += child.Size ?? 0L;
        }
    }

    private sealed class CookedBinarySchemaBuilder(CookedBinarySerializationCallbacks? callbacks, bool includeValues)
    {
        private readonly CookedBinarySerializationCallbacks? _callbacks = callbacks;
        private readonly bool _includeValues = includeValues;

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public CookedBinarySchemaNode BuildValueNode(string name, Type? declaredType, object? value, bool allowCustom)
        {
            object? serializedValue = _callbacks?.OnSerializingValue?.Invoke(value) ?? value;
            var node = NewNode(name, "value", declaredType?.FullName ?? declaredType?.Name);

            if (serializedValue is null)
                return FinalizeNullNode(node, declaredType, "null");

            Type runtimeType = serializedValue.GetType();

            if (IsRuntimeOnlyType(runtimeType))
                return FinalizeNullNode(node, runtimeType, "runtime-only type; cooked serializer emits null");

            foreach (var module in SerializationModules)
            {
                var builtNode = module.TryBuildValueSchema(this, name, declaredType, serializedValue, runtimeType, allowCustom);
                if (builtNode is not null)
                    return builtNode;
            }

            throw new NotSupportedException($"No cooked binary schema module handled '{runtimeType.FullName ?? runtimeType.Name}'.");
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public CookedBinarySchemaNode BuildTypeNode(string name, Type type, bool allowCustom)
        {
            var node = NewNode(name, "schema", type.FullName ?? type.Name);

            if (IsRuntimeOnlyType(type))
                return FinalizeNullSchemaNode(node, type, "runtime-only type; cooked serializer emits null");

            foreach (var module in SerializationModules)
            {
                var builtNode = module.TryBuildTypeSchema(this, name, type, allowCustom);
                if (builtNode is not null)
                    return builtNode;
            }

            throw new NotSupportedException($"No cooked binary schema module handled '{type.FullName ?? type.Name}'.");
        }

        internal CookedBinarySchemaNode BuildMemberEntryNode(MemberMetadata member, object? memberValue, bool allowCustom)
        {
            var entry = NewNode(member.Name, "member", member.MemberType.FullName ?? member.MemberType.Name);
            AddStringLeaf(entry, "memberName", member.Name);
            entry.MutableChildren.Add(BuildValueNode("memberValue", member.MemberType, memberValue, allowCustom));
            return FinalizeNode(entry);
        }

        internal CookedBinarySchemaNode BuildMemberSchemaEntryNode(MemberMetadata member, bool allowCustom)
        {
            var entry = NewNode(member.Name, "member", member.MemberType.FullName ?? member.MemberType.Name);
            AddStringTemplateLeaf(entry, "memberName", member.Name);
            entry.MutableChildren.Add(BuildTypeNode("memberValue", member.MemberType, allowCustom));
            return FinalizeNode(entry, allowUnknownChildren: true);
        }

        internal bool TryBuildPrimitiveValueNode(CookedBinarySchemaNode node, Type runtimeType, object value)
        {
            if (!TryGetPrimitiveMarker(runtimeType, out CookedBinaryTypeMarker marker))
                return false;

            node.Marker = marker.ToString();
            AddFixedLeaf(node, "marker", "marker", size: 1, valueDisplay: node.Marker);

            switch (marker)
            {
                case CookedBinaryTypeMarker.String:
                {
                    string text = (string)value;
                    int byteCount = Encoding.UTF8.GetByteCount(text);
                    AddFixedLeaf(node, "utf8ByteCount", "length", size: SizeOf7BitEncodedInt(byteCount), valueDisplay: byteCount.ToString(CultureInfo.InvariantCulture));
                    AddFixedLeaf(node, "utf8Data", "payload", size: byteCount, valueDisplay: PreviewString(text));
                    break;
                }
                case CookedBinaryTypeMarker.Guid:
                    AddFixedLeaf(node, "payload", "payload", size: 16, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Vector2:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 2, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Vector3:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 3, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Vector4:
                case CookedBinaryTypeMarker.Quaternion:
                case CookedBinaryTypeMarker.ColorF4:
                case CookedBinaryTypeMarker.Plane:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 4, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Matrix4x4:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 16, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.ColorF3:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 3, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.DateTimeOffset:
                    AddFixedLeaf(node, "ticks", "payload", size: sizeof(long), valueDisplay: ((DateTimeOffset)value).Ticks.ToString(CultureInfo.InvariantCulture));
                    AddFixedLeaf(node, "offsetMinutes", "payload", size: sizeof(short), valueDisplay: ((short)((DateTimeOffset)value).Offset.TotalMinutes).ToString(CultureInfo.InvariantCulture));
                    break;
                case CookedBinaryTypeMarker.Uri:
                case CookedBinaryTypeMarker.Version:
                case CookedBinaryTypeMarker.CultureInfo:
                    AddFixedLeaf(node, "utf8ByteCount", "length", size: SizeOf7BitEncodedInt(Encoding.UTF8.GetByteCount(value.ToString() ?? string.Empty)), valueDisplay: "variable");
                    AddFixedLeaf(node, "utf8Data", "payload", size: Encoding.UTF8.GetByteCount(value.ToString() ?? string.Empty), valueDisplay: PreviewString(value.ToString() ?? string.Empty));
                    break;
                case CookedBinaryTypeMarker.BigInteger:
                {
                    byte[] bytes = ((BigInteger)value).ToByteArray();
                    AddFixedLeaf(node, "length", "length", size: sizeof(int), valueDisplay: bytes.Length.ToString(CultureInfo.InvariantCulture));
                    AddFixedLeaf(node, "data", "payload", size: bytes.Length, valueDisplay: PreviewBytes(bytes));
                    break;
                }
                case CookedBinaryTypeMarker.IPAddress:
                {
                    byte[] bytes = ((IPAddress)value).GetAddressBytes();
                    AddFixedLeaf(node, "length", "length", size: 1, valueDisplay: bytes.Length.ToString(CultureInfo.InvariantCulture));
                    AddFixedLeaf(node, "data", "payload", size: bytes.Length, valueDisplay: PreviewBytes(bytes), notes: value.ToString());
                    break;
                }
                case CookedBinaryTypeMarker.IPEndPoint:
                {
                    var endPoint = (IPEndPoint)value;
                    byte[] bytes = endPoint.Address.GetAddressBytes();
                    AddFixedLeaf(node, "addressLength", "length", size: 1, valueDisplay: bytes.Length.ToString(CultureInfo.InvariantCulture));
                    AddFixedLeaf(node, "address", "payload", size: bytes.Length, valueDisplay: PreviewBytes(bytes), notes: endPoint.Address.ToString());
                    AddFixedLeaf(node, "port", "payload", size: sizeof(int), valueDisplay: endPoint.Port.ToString(CultureInfo.InvariantCulture));
                    break;
                }
                case CookedBinaryTypeMarker.Regex:
                {
                    var regex = (Regex)value;
                    AddStringLeaf(node, "pattern", regex.ToString());
                    AddFixedLeaf(node, "options", "payload", size: sizeof(int), valueDisplay: ((int)regex.Options).ToString(CultureInfo.InvariantCulture), notes: regex.Options.ToString());
                    break;
                }
                case CookedBinaryTypeMarker.BitArray:
                {
                    var bitArray = (BitArray)value;
                    int byteCount = (bitArray.Length + 7) / 8;
                    byte[] bytes = new byte[byteCount];
                    bitArray.CopyTo(bytes, 0);
                    AddFixedLeaf(node, "bitLength", "length", size: sizeof(int), valueDisplay: bitArray.Length.ToString(CultureInfo.InvariantCulture));
                    AddFixedLeaf(node, "data", "payload", size: byteCount, valueDisplay: PreviewBytes(bytes));
                    break;
                }
                case CookedBinaryTypeMarker.Range:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(int) + sizeof(bool) + sizeof(int) + sizeof(bool), valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Index:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(int) + sizeof(bool), valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Segment:
                case CookedBinaryTypeMarker.Ray:
                case CookedBinaryTypeMarker.AABB:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 6, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Sphere:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 4, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Triangle:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 9, valueDisplay: value.ToString());
                    break;
                case CookedBinaryTypeMarker.Frustum:
                    AddFixedLeaf(node, "payload", "payload", size: sizeof(float) * 8 * 3, valueDisplay: value.ToString());
                    break;
                default:
                    AddFixedLeaf(node, "payload", "payload", size: GetPrimitivePayloadSize(marker), valueDisplay: value.ToString());
                    break;
            }

            return true;
        }

        internal bool TryBuildPrimitiveTypeNode(CookedBinarySchemaNode node, Type type)
        {
            if (!TryGetPrimitiveMarker(type, out CookedBinaryTypeMarker marker))
                return false;

            node.Marker = marker.ToString();
            AddFixedLeaf(node, "marker", "marker", size: 1, valueDisplay: node.Marker);

            switch (marker)
            {
                case CookedBinaryTypeMarker.String:
                    AddUnknownLeaf(node, "utf8ByteCount", "length", "7-bit encoded UTF-8 byte count");
                    AddUnknownLeaf(node, "utf8Data", "payload", "UTF-8 bytes");
                    break;
                case CookedBinaryTypeMarker.Guid:
                    AddFixedLeaf(node, "payload", "payload", size: 16, valueDisplay: "16 bytes");
                    break;
                case CookedBinaryTypeMarker.Uri:
                case CookedBinaryTypeMarker.Version:
                case CookedBinaryTypeMarker.CultureInfo:
                    AddUnknownLeaf(node, "utf8Data", "payload", "7-bit length-prefixed UTF-8 string");
                    break;
                case CookedBinaryTypeMarker.BigInteger:
                    AddFixedLeaf(node, "length", "length", size: sizeof(int), valueDisplay: "variable");
                    AddUnknownLeaf(node, "data", "payload", "length bytes");
                    break;
                case CookedBinaryTypeMarker.IPAddress:
                    AddFixedLeaf(node, "length", "length", size: 1, valueDisplay: "4 or 16");
                    AddUnknownLeaf(node, "data", "payload", "address bytes");
                    break;
                case CookedBinaryTypeMarker.IPEndPoint:
                    AddFixedLeaf(node, "addressLength", "length", size: 1, valueDisplay: "4 or 16");
                    AddUnknownLeaf(node, "address", "payload", "address bytes");
                    AddFixedLeaf(node, "port", "payload", size: sizeof(int), valueDisplay: "int32");
                    break;
                case CookedBinaryTypeMarker.Regex:
                    AddUnknownLeaf(node, "pattern", "payload", "7-bit length-prefixed UTF-8 string");
                    AddFixedLeaf(node, "options", "payload", size: sizeof(int), valueDisplay: "int32");
                    break;
                case CookedBinaryTypeMarker.BitArray:
                    AddFixedLeaf(node, "bitLength", "length", size: sizeof(int), valueDisplay: "int32");
                    AddUnknownLeaf(node, "data", "payload", "(bitLength + 7) / 8 bytes");
                    break;
                default:
                    AddFixedLeaf(node, "payload", "payload", size: GetPrimitivePayloadSize(marker), valueDisplay: type.Name);
                    break;
            }

            return true;
        }

        internal void AddPersistentCallListNode(CookedBinarySchemaNode node, List<XRPersistentCall>? calls)
        {
            int count = calls?.Count ?? 0;
            AddFixedLeaf(node, "callCount", "count", size: sizeof(int), valueDisplay: count.ToString(CultureInfo.InvariantCulture));
            if (count == 0 || calls is null)
                return;

            for (int i = 0; i < calls.Count; i++)
            {
                XRPersistentCall call = calls[i];
                var callNode = NewNode($"call{i}", "persistentCall", nameof(XRPersistentCall));
                AddFixedLeaf(callNode, nameof(XRPersistentCall.NodeId), "payload", size: 16, valueDisplay: call.NodeId.ToString());
                AddFixedLeaf(callNode, nameof(XRPersistentCall.TargetObjectId), "payload", size: 16, valueDisplay: call.TargetObjectId.ToString());
                AddStringLeaf(callNode, nameof(XRPersistentCall.MethodName), call.MethodName ?? string.Empty);
                AddFixedLeaf(callNode, nameof(XRPersistentCall.UseTupleExpansion), "flag", size: sizeof(bool), valueDisplay: call.UseTupleExpansion.ToString());

                string[]? paramTypes = call.ParameterTypeNames;
                AddFixedLeaf(callNode, "parameterTypeCount", "count", size: sizeof(int), valueDisplay: (paramTypes?.Length ?? 0).ToString(CultureInfo.InvariantCulture));
                if (paramTypes is { Length: > 0 })
                {
                    for (int j = 0; j < paramTypes.Length; j++)
                        AddStringLeaf(callNode, $"parameterType{j}", paramTypes[j] ?? string.Empty);
                }

                node.MutableChildren.Add(FinalizeNode(callNode));
            }
        }

        internal void AddCustomPayloadNode(CookedBinarySchemaNode node, Type runtimeType, long payloadSize, string note)
        {
            AddFixedLeaf(node, "marker", "marker", size: 1, valueDisplay: node.Marker);
            AddStringLeaf(node, "runtimeType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);
            AddFixedLeaf(node, "payload", "customPayload", size: payloadSize, valueDisplay: $"{payloadSize} bytes", notes: note);
        }

        internal void AddExpandedCustomModelValueNode(CookedBinarySchemaNode node, Type runtimeType, object model, Type modelType, string note)
        {
            AddFixedLeaf(node, "marker", "marker", size: 1, valueDisplay: node.Marker);
            AddStringLeaf(node, "runtimeType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);

            var payloadNode = BuildValueNode("payload", modelType, model, allowCustom: true);
            payloadNode.Notes = string.IsNullOrWhiteSpace(payloadNode.Notes)
                ? note
                : payloadNode.Notes + "; " + note;
            node.MutableChildren.Add(payloadNode);
        }

        internal void AddCustomSchemaNode(CookedBinarySchemaNode node, Type runtimeType, string note)
        {
            AddFixedLeaf(node, "marker", "marker", size: 1, valueDisplay: node.Marker);
            AddStringTemplateLeaf(node, "runtimeType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);
            AddUnknownLeaf(node, "payload", "customPayload", note);
        }

        internal void AddExpandedCustomModelSchemaNode(CookedBinarySchemaNode node, Type runtimeType, Type modelType, string note)
        {
            AddFixedLeaf(node, "marker", "marker", size: 1, valueDisplay: node.Marker);
            AddStringTemplateLeaf(node, "runtimeType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);

            var payloadNode = BuildTypeNode("payload", modelType, allowCustom: true);
            payloadNode.Notes = string.IsNullOrWhiteSpace(payloadNode.Notes)
                ? note
                : payloadNode.Notes + "; " + note;
            node.MutableChildren.Add(payloadNode);
        }

        internal CookedBinarySchemaNode FinalizeNullNode(CookedBinarySchemaNode node, Type? type, string note)
        {
            node.Marker = CookedBinaryTypeMarker.Null.ToString();
            node.TypeName = type?.FullName ?? type?.Name;
            node.Notes = note;
            AddFixedLeaf(node, "marker", "marker", size: 1, valueDisplay: node.Marker);
            return FinalizeNode(node);
        }

        internal CookedBinarySchemaNode FinalizeNullSchemaNode(CookedBinarySchemaNode node, Type type, string note)
        {
            node.Marker = CookedBinaryTypeMarker.Null.ToString();
            node.TypeName = type.FullName ?? type.Name;
            node.Notes = note;
            AddFixedLeaf(node, "marker", "marker", size: 1, valueDisplay: node.Marker);
            return FinalizeNode(node, allowUnknownChildren: true);
        }

        internal CookedBinarySchemaNode FinalizeNode(CookedBinarySchemaNode node, bool allowUnknownChildren = false)
        {
            long total = 0;
            bool allKnown = true;
            foreach (var child in node.Children)
            {
                if (child.Size.HasValue)
                    total += child.Size.Value;
                else
                    allKnown = false;
            }

            if (allKnown || (!allowUnknownChildren && node.Children.Count == 0))
            {
                node.Size = total;
                node.SizeDescription = $"{total} byte{(total == 1 ? string.Empty : "s")}";
            }
            else if (allKnown)
            {
                node.Size = total;
                node.SizeDescription = $"{total} byte{(total == 1 ? string.Empty : "s")}";
            }
            else
            {
                node.Size = null;
                node.SizeDescription = "variable";
            }

            return node;
        }

        internal CookedBinarySchemaNode NewNode(string name, string kind, string? typeName)
            => new(name, kind) { TypeName = typeName };

        internal void AddFixedLeaf(CookedBinarySchemaNode parent, string name, string kind, long size, string? valueDisplay = null, string? notes = null)
        {
            parent.MutableChildren.Add(new CookedBinarySchemaNode(name, kind)
            {
                Size = size,
                SizeDescription = $"{size} byte{(size == 1 ? string.Empty : "s")}",
                ValueDisplay = valueDisplay,
                Notes = notes
            });
        }

        internal void AddUnknownLeaf(CookedBinarySchemaNode parent, string name, string kind, string description, string? notes = null)
        {
            parent.MutableChildren.Add(new CookedBinarySchemaNode(name, kind)
            {
                Size = null,
                SizeDescription = description,
                Notes = notes
            });
        }

        internal void AddStringLeaf(CookedBinarySchemaNode parent, string name, string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            parent.MutableChildren.Add(new CookedBinarySchemaNode(name, "string")
            {
                Size = SizeOfString(value),
                SizeDescription = $"{SizeOfString(value)} bytes",
                ValueDisplay = PreviewString(value),
                Notes = $"UTF-8 byte count: {byteCount}"
            });
        }

        internal void AddStringTemplateLeaf(CookedBinarySchemaNode parent, string name, string value)
        {
            parent.MutableChildren.Add(new CookedBinarySchemaNode(name, "string")
            {
                Size = SizeOfString(value),
                SizeDescription = $"{SizeOfString(value)} bytes",
                ValueDisplay = PreviewString(value),
                Notes = "type-name string in current assembly-qualified form"
            });
        }
    }

    internal static class CookedBinarySchemaFormatter
    {
        public static string Format(CookedBinarySchema schema)
        {
            var sb = new StringBuilder();
            sb.Append("Cooked binary ");
            sb.Append(schema.IncludesValues ? "value" : "schema");
            sb.Append(" view");
            if (schema.RootType is not null)
            {
                sb.Append(" for ");
                sb.Append(schema.RootType.FullName ?? schema.RootType.Name);
            }

            sb.AppendLine();
            sb.Append("Total size: ");
            sb.AppendLine(schema.TotalSizeDescription);
            WriteNode(sb, schema.Root, prefix: string.Empty, isLast: true);
            return sb.ToString();
        }

        private static void WriteNode(StringBuilder sb, CookedBinarySchemaNode node, string prefix, bool isLast)
        {
            sb.Append(prefix);
            sb.Append(isLast ? "\\- " : "+- ");
            sb.Append(node.Name);
            sb.Append(" [");
            sb.Append(node.Kind);
            sb.Append(']');

            if (!string.IsNullOrWhiteSpace(node.Marker))
            {
                sb.Append(" marker=");
                sb.Append(node.Marker);
            }

            if (!string.IsNullOrWhiteSpace(node.TypeName))
            {
                sb.Append(" type=");
                sb.Append(node.TypeName);
            }

            if (node.Offset.HasValue)
            {
                sb.Append(" offset=");
                sb.Append(node.Offset.Value.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(" size=");
            sb.Append(node.SizeDescription);

            if (!string.IsNullOrWhiteSpace(node.ValueDisplay))
            {
                sb.Append(" value=");
                sb.Append(node.ValueDisplay);
            }

            if (!string.IsNullOrWhiteSpace(node.Notes))
            {
                sb.Append(" note=");
                sb.Append(node.Notes);
            }

            sb.AppendLine();

            string childPrefix = prefix + (isLast ? "   " : "|  ");
            for (int i = 0; i < node.Children.Count; i++)
                WriteNode(sb, node.Children[i], childPrefix, i == node.Children.Count - 1);
        }
    }

    private static bool TryGetPrimitiveMarker(Type type, out CookedBinaryTypeMarker marker)
    {
        if (type == typeof(bool)) { marker = CookedBinaryTypeMarker.Boolean; return true; }
        if (type == typeof(byte)) { marker = CookedBinaryTypeMarker.Byte; return true; }
        if (type == typeof(sbyte)) { marker = CookedBinaryTypeMarker.SByte; return true; }
        if (type == typeof(short)) { marker = CookedBinaryTypeMarker.Int16; return true; }
        if (type == typeof(ushort)) { marker = CookedBinaryTypeMarker.UInt16; return true; }
        if (type == typeof(int)) { marker = CookedBinaryTypeMarker.Int32; return true; }
        if (type == typeof(uint)) { marker = CookedBinaryTypeMarker.UInt32; return true; }
        if (type == typeof(long)) { marker = CookedBinaryTypeMarker.Int64; return true; }
        if (type == typeof(ulong)) { marker = CookedBinaryTypeMarker.UInt64; return true; }
        if (type == typeof(float)) { marker = CookedBinaryTypeMarker.Single; return true; }
        if (type == typeof(double)) { marker = CookedBinaryTypeMarker.Double; return true; }
        if (type == typeof(decimal)) { marker = CookedBinaryTypeMarker.Decimal; return true; }
        if (type == typeof(char)) { marker = CookedBinaryTypeMarker.Char; return true; }
        if (type == typeof(string)) { marker = CookedBinaryTypeMarker.String; return true; }
        if (type == typeof(Guid)) { marker = CookedBinaryTypeMarker.Guid; return true; }
        if (type == typeof(DateTime)) { marker = CookedBinaryTypeMarker.DateTime; return true; }
        if (type == typeof(TimeSpan)) { marker = CookedBinaryTypeMarker.TimeSpan; return true; }
        if (type == typeof(Vector2)) { marker = CookedBinaryTypeMarker.Vector2; return true; }
        if (type == typeof(Vector3)) { marker = CookedBinaryTypeMarker.Vector3; return true; }
        if (type == typeof(Vector4)) { marker = CookedBinaryTypeMarker.Vector4; return true; }
        if (type == typeof(Quaternion)) { marker = CookedBinaryTypeMarker.Quaternion; return true; }
        if (type == typeof(Matrix4x4)) { marker = CookedBinaryTypeMarker.Matrix4x4; return true; }
        if (type == typeof(ColorF3)) { marker = CookedBinaryTypeMarker.ColorF3; return true; }
        if (type == typeof(ColorF4)) { marker = CookedBinaryTypeMarker.ColorF4; return true; }
        if (type == typeof(Half)) { marker = CookedBinaryTypeMarker.Half; return true; }
        if (type == typeof(DateTimeOffset)) { marker = CookedBinaryTypeMarker.DateTimeOffset; return true; }
        if (type == typeof(DateOnly)) { marker = CookedBinaryTypeMarker.DateOnly; return true; }
        if (type == typeof(TimeOnly)) { marker = CookedBinaryTypeMarker.TimeOnly; return true; }
        if (type == typeof(System.Numerics.Plane)) { marker = CookedBinaryTypeMarker.Plane; return true; }
        if (type == typeof(Uri)) { marker = CookedBinaryTypeMarker.Uri; return true; }
        if (type == typeof(Segment)) { marker = CookedBinaryTypeMarker.Segment; return true; }
        if (type == typeof(Ray)) { marker = CookedBinaryTypeMarker.Ray; return true; }
        if (type == typeof(AABB)) { marker = CookedBinaryTypeMarker.AABB; return true; }
        if (type == typeof(Sphere)) { marker = CookedBinaryTypeMarker.Sphere; return true; }
        if (type == typeof(Triangle)) { marker = CookedBinaryTypeMarker.Triangle; return true; }
        if (type == typeof(Version)) { marker = CookedBinaryTypeMarker.Version; return true; }
        if (type == typeof(Complex)) { marker = CookedBinaryTypeMarker.Complex; return true; }
        if (type == typeof(IPAddress)) { marker = CookedBinaryTypeMarker.IPAddress; return true; }
        if (type == typeof(Range)) { marker = CookedBinaryTypeMarker.Range; return true; }
        if (type == typeof(Index)) { marker = CookedBinaryTypeMarker.Index; return true; }
        if (type == typeof(CultureInfo)) { marker = CookedBinaryTypeMarker.CultureInfo; return true; }
        if (type == typeof(Regex)) { marker = CookedBinaryTypeMarker.Regex; return true; }
        if (type == typeof(BitArray)) { marker = CookedBinaryTypeMarker.BitArray; return true; }
        if (type == typeof(BigInteger)) { marker = CookedBinaryTypeMarker.BigInteger; return true; }
        if (type == typeof(IPEndPoint)) { marker = CookedBinaryTypeMarker.IPEndPoint; return true; }
        if (type == typeof(Frustum)) { marker = CookedBinaryTypeMarker.Frustum; return true; }

        marker = default;
        return false;
    }

    private static int GetPrimitivePayloadSize(CookedBinaryTypeMarker marker)
        => marker switch
        {
            CookedBinaryTypeMarker.Boolean => sizeof(bool),
            CookedBinaryTypeMarker.Byte => sizeof(byte),
            CookedBinaryTypeMarker.SByte => sizeof(sbyte),
            CookedBinaryTypeMarker.Int16 => sizeof(short),
            CookedBinaryTypeMarker.UInt16 => sizeof(ushort),
            CookedBinaryTypeMarker.Int32 => sizeof(int),
            CookedBinaryTypeMarker.UInt32 => sizeof(uint),
            CookedBinaryTypeMarker.Int64 => sizeof(long),
            CookedBinaryTypeMarker.UInt64 => sizeof(ulong),
            CookedBinaryTypeMarker.Single => sizeof(float),
            CookedBinaryTypeMarker.Double => sizeof(double),
            CookedBinaryTypeMarker.Decimal => sizeof(decimal),
            CookedBinaryTypeMarker.Char => sizeof(char),
            CookedBinaryTypeMarker.DateTime => sizeof(long),
            CookedBinaryTypeMarker.TimeSpan => sizeof(long),
            CookedBinaryTypeMarker.Half => sizeof(ushort),
            CookedBinaryTypeMarker.DateOnly => sizeof(int),
            CookedBinaryTypeMarker.TimeOnly => sizeof(long),
            CookedBinaryTypeMarker.Complex => sizeof(double) * 2,
            _ => 0
        };

    private static string PreviewString(string value)
    {
        const int maxChars = 48;
        string text = value.Length <= maxChars ? value : value[..maxChars] + "...";
        return '"' + text.Replace("\r", "\\r").Replace("\n", "\\n") + '"';
    }

    private static string PreviewBytes(byte[] value)
    {
        const int maxBytes = 16;
        int count = Math.Min(value.Length, maxBytes);
        string hex = Convert.ToHexString(value, 0, count);
        return value.Length > maxBytes ? hex + "..." : hex;
    }

    private static Type? TryGetEnumerableItemType(Type type)
        => type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces()
                  .Concat([type])
                  .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                  ?.GetGenericArguments()[0];

    private static bool TryGetDictionaryTypes(Type type, out Type? keyType, out Type? valueType)
    {
        Type? dictionaryType = type.GetInterfaces()
            .Concat([type])
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (dictionaryType is not null)
        {
            Type[] args = dictionaryType.GetGenericArguments();
            keyType = args[0];
            valueType = args[1];
            return true;
        }

        keyType = null;
        valueType = null;
        return false;
    }

    private static Type GetBlendTreeSerializedModelType(Type blendTreeType)
        => blendTreeType == typeof(BlendTree1D)
            ? typeof(BlendTree1DSerializedModel)
            : blendTreeType == typeof(BlendTree2D)
                ? typeof(BlendTree2DSerializedModel)
                : blendTreeType == typeof(BlendTreeDirect)
                    ? typeof(BlendTreeDirectSerializedModel)
                    : throw new NotSupportedException($"Unsupported blend tree type '{blendTreeType.FullName}'.");
}