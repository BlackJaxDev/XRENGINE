using System.Collections;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class PrimitiveCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(100, "Primitives", "Built-in scalar, math, geometry, and string-like cooked value types.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!PrimitiveWriters.TryGetValue(runtimeType, out var primitiveWriter))
                return false;

            primitiveWriter(writer, value);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            value = marker switch
            {
                CookedBinaryTypeMarker.Boolean => reader.ReadBoolean(),
                CookedBinaryTypeMarker.Byte => reader.ReadByte(),
                CookedBinaryTypeMarker.SByte => reader.ReadSByte(),
                CookedBinaryTypeMarker.Int16 => reader.ReadInt16(),
                CookedBinaryTypeMarker.UInt16 => reader.ReadUInt16(),
                CookedBinaryTypeMarker.Int32 => reader.ReadInt32(),
                CookedBinaryTypeMarker.UInt32 => reader.ReadUInt32(),
                CookedBinaryTypeMarker.Int64 => reader.ReadInt64(),
                CookedBinaryTypeMarker.UInt64 => reader.ReadUInt64(),
                CookedBinaryTypeMarker.Single => reader.ReadSingle(),
                CookedBinaryTypeMarker.Double => reader.ReadDouble(),
                CookedBinaryTypeMarker.Decimal => reader.ReadDecimal(),
                CookedBinaryTypeMarker.Char => reader.ReadChar(),
                CookedBinaryTypeMarker.String => reader.ReadString(),
                CookedBinaryTypeMarker.Guid => new Guid(reader.ReadBytes(16)),
                CookedBinaryTypeMarker.DateTime => DateTime.FromBinary(reader.ReadInt64()),
                CookedBinaryTypeMarker.TimeSpan => new TimeSpan(reader.ReadInt64()),
                CookedBinaryTypeMarker.Vector2 => new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                CookedBinaryTypeMarker.Vector3 => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                CookedBinaryTypeMarker.Vector4 => new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                CookedBinaryTypeMarker.Quaternion => new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                CookedBinaryTypeMarker.Matrix4x4 => ReadMatrix4x4(reader),
                CookedBinaryTypeMarker.ColorF3 => new ColorF3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                CookedBinaryTypeMarker.ColorF4 => new ColorF4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                CookedBinaryTypeMarker.Half => BitConverter.UInt16BitsToHalf(reader.ReadUInt16()),
                CookedBinaryTypeMarker.DateTimeOffset => new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16())),
                CookedBinaryTypeMarker.DateOnly => DateOnly.FromDayNumber(reader.ReadInt32()),
                CookedBinaryTypeMarker.TimeOnly => new TimeOnly(reader.ReadInt64()),
                CookedBinaryTypeMarker.Plane => new System.Numerics.Plane(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                CookedBinaryTypeMarker.Uri => new Uri(reader.ReadString()),
                CookedBinaryTypeMarker.Segment => new Segment(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())),
                CookedBinaryTypeMarker.Ray => new Ray(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())),
                CookedBinaryTypeMarker.AABB => new AABB(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())),
                CookedBinaryTypeMarker.Sphere => new Sphere(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle()),
                CookedBinaryTypeMarker.Triangle => new Triangle(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())),
                CookedBinaryTypeMarker.Frustum => ReadFrustum(reader),
                CookedBinaryTypeMarker.Version => Version.Parse(reader.ReadString()),
                CookedBinaryTypeMarker.BigInteger => ReadBigInteger(reader),
                CookedBinaryTypeMarker.Complex => new Complex(reader.ReadDouble(), reader.ReadDouble()),
                CookedBinaryTypeMarker.IPAddress => ReadIPAddress(reader),
                CookedBinaryTypeMarker.IPEndPoint => ReadIPEndPoint(reader),
                CookedBinaryTypeMarker.Range => new Range(new Index(reader.ReadInt32(), reader.ReadBoolean()), new Index(reader.ReadInt32(), reader.ReadBoolean())),
                CookedBinaryTypeMarker.Index => new Index(reader.ReadInt32(), reader.ReadBoolean()),
                CookedBinaryTypeMarker.BitArray => ReadBitArray(reader),
                CookedBinaryTypeMarker.CultureInfo => CultureInfo.GetCultureInfo(reader.ReadString()),
                CookedBinaryTypeMarker.Regex => new Regex(reader.ReadString(), (RegexOptions)reader.ReadInt32()),
                _ => null
            };

            return value is not null || marker is CookedBinaryTypeMarker.String;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            switch (value)
            {
                case bool:
                    calculator.AddBytes(sizeof(bool));
                    return true;
                case byte or sbyte:
                    calculator.AddBytes(1);
                    return true;
                case short or ushort:
                    calculator.AddBytes(2);
                    return true;
                case int or uint:
                    calculator.AddBytes(4);
                    return true;
                case long or ulong:
                    calculator.AddBytes(8);
                    return true;
                case float:
                    calculator.AddBytes(4);
                    return true;
                case double:
                    calculator.AddBytes(8);
                    return true;
                case decimal:
                    calculator.AddBytes(16);
                    return true;
                case char:
                    calculator.AddBytes(sizeof(char));
                    return true;
                case string s:
                    calculator.AddBytes(SizeOfString(s));
                    return true;
                case Guid:
                    calculator.AddBytes(16);
                    return true;
                case DateTime or TimeSpan:
                    calculator.AddBytes(8);
                    return true;
                case Vector2:
                    calculator.AddBytes(sizeof(float) * 2);
                    return true;
                case Vector3:
                    calculator.AddBytes(sizeof(float) * 3);
                    return true;
                case Vector4 or Quaternion:
                    calculator.AddBytes(sizeof(float) * 4);
                    return true;
                case Matrix4x4:
                    calculator.AddBytes(sizeof(float) * 16);
                    return true;
                case ColorF3:
                    calculator.AddBytes(sizeof(float) * 3);
                    return true;
                case ColorF4:
                    calculator.AddBytes(sizeof(float) * 4);
                    return true;
                case Half:
                    calculator.AddBytes(2);
                    return true;
                case DateTimeOffset:
                    calculator.AddBytes(sizeof(long) + sizeof(short));
                    return true;
                case DateOnly:
                    calculator.AddBytes(sizeof(int));
                    return true;
                case TimeOnly:
                    calculator.AddBytes(sizeof(long));
                    return true;
                case System.Numerics.Plane:
                    calculator.AddBytes(sizeof(float) * 4);
                    return true;
                case Uri uri:
                    calculator.AddBytes(SizeOfString(uri.OriginalString));
                    return true;
                case Segment or Ray or AABB:
                    calculator.AddBytes(sizeof(float) * 6);
                    return true;
                case Sphere:
                    calculator.AddBytes(sizeof(float) * 4);
                    return true;
                case Triangle:
                    calculator.AddBytes(sizeof(float) * 9);
                    return true;
                case Frustum:
                    calculator.AddBytes(sizeof(float) * 8 * 3);
                    return true;
                case Version version:
                    calculator.AddBytes(SizeOfString(version.ToString()));
                    return true;
                case BigInteger bigInt:
                {
                    byte[] bytes = bigInt.ToByteArray();
                    calculator.AddBytes(sizeof(int) + bytes.Length);
                    return true;
                }
                case Complex:
                    calculator.AddBytes(sizeof(double) * 2);
                    return true;
                case IPAddress ip:
                    calculator.AddBytes(1 + ip.GetAddressBytes().Length);
                    return true;
                case IPEndPoint endPoint:
                    calculator.AddBytes(1 + endPoint.Address.GetAddressBytes().Length + sizeof(int));
                    return true;
                case Range:
                    calculator.AddBytes(sizeof(int) + sizeof(bool) + sizeof(int) + sizeof(bool));
                    return true;
                case Index:
                    calculator.AddBytes(sizeof(int) + sizeof(bool));
                    return true;
                case BitArray bitArray:
                    calculator.AddBytes(sizeof(int) + (bitArray.Length + 7) / 8);
                    return true;
                case CultureInfo culture:
                    calculator.AddBytes(SizeOfString(culture.Name));
                    return true;
                case Regex regex:
                    calculator.AddBytes(SizeOfString(regex.ToString()) + sizeof(int));
                    return true;
                default:
                    return false;
            }
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            var node = builder.NewNode(name, "value", declaredType?.FullName ?? declaredType?.Name);
            node.TypeName = runtimeType.FullName ?? runtimeType.Name;
            return builder.TryBuildPrimitiveValueNode(node, runtimeType, value)
                ? builder.FinalizeNode(node)
                : null;
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            return builder.TryBuildPrimitiveTypeNode(node, type)
                ? builder.FinalizeNode(node, allowUnknownChildren: true)
                : null;
        }
    }
}