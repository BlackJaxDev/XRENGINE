using System.Globalization;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class ObjectCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1800, "ObjectFallback", "Reflection or MemoryPack object fallback for all remaining serializable types.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            writer.Write((byte)CookedBinaryTypeMarker.Object);
            WriteTypeName(writer, runtimeType);
            if (TrySerializeWithMemoryPack(value, runtimeType, out byte[]? memPackBytes))
            {
                writer.Write((byte)CookedBinaryObjectEncoding.MemoryPack);
                writer.Write(memPackBytes!.Length);
                writer.Write(memPackBytes);
            }
            else
            {
                writer.Write((byte)CookedBinaryObjectEncoding.Reflection);
                using var scope = EnterReflectionScope(value, out bool isCycle);
                if (!isCycle)
                    WriteObjectContent(writer, value, runtimeType, allowCustom, callbacks);
                else
                    writer.Write(0);
            }

            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker == CookedBinaryTypeMarker.Null)
            {
                value = null;
                return true;
            }

            if (marker != CookedBinaryTypeMarker.Object)
            {
                value = null;
                return false;
            }

            value = ReadObject(reader, callbacks);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            calculator.AddBytes(SizeOfTypeName(runtimeType));
            if (TryGetMemoryPackLength(value, runtimeType, out int memoryPackLength))
            {
                calculator.AddBytes(1 + sizeof(int) + memoryPackLength);
                return true;
            }

            calculator.AddBytes(1);
            using var scope = EnterReflectionScope(value, out bool isCycle);
            if (isCycle)
            {
                calculator.AddBytes(sizeof(int));
                return true;
            }

            calculator.AddObjectContent(value, runtimeType, allowCustom);
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.Object.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringLeaf(node, "runtimeType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);

            if (TryGetMemoryPackLength(value, runtimeType, out int memoryPackLength))
            {
                builder.AddFixedLeaf(node, "encoding", "encoding", 1, CookedBinaryObjectEncoding.MemoryPack.ToString());
                builder.AddFixedLeaf(node, "payloadLength", "length", sizeof(int), memoryPackLength.ToString(CultureInfo.InvariantCulture));
                builder.AddFixedLeaf(node, "payload", "blob", memoryPackLength, $"{memoryPackLength} bytes", "MemoryPack payload");
                return builder.FinalizeNode(node);
            }

            builder.AddFixedLeaf(node, "encoding", "encoding", 1, CookedBinaryObjectEncoding.Reflection.ToString());
            using var scope = EnterReflectionScope(value, out bool isCycle);
            if (isCycle)
            {
                builder.AddFixedLeaf(node, "memberCount", "count", sizeof(int), "0", "reference cycle suppressed");
                return builder.FinalizeNode(node);
            }

            TypeMetadata metadata = TypeMetadataCache.Get(runtimeType);
            builder.AddFixedLeaf(node, "memberCount", "count", sizeof(int), metadata.Members.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var member in metadata.Members)
            {
                object? memberValue = null;
                try
                {
                    memberValue = member.GetValue(value);
                }
                catch (Exception ex)
                {
                    node.MutableChildren.Add(new CookedBinarySchemaNode(member.Name, "member")
                    {
                        TypeName = member.MemberType.FullName ?? member.MemberType.Name,
                        Notes = $"getter failed: {ex.Message}",
                        Size = null,
                        SizeDescription = "unknown"
                    });
                    continue;
                }

                node.MutableChildren.Add(builder.BuildMemberEntryNode(member, memberValue, allowCustom));
            }

            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.Object.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringTemplateLeaf(node, "runtimeType", type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
            builder.AddFixedLeaf(node, "encoding", "encoding", 1, CookedBinaryObjectEncoding.Reflection.ToString(), "runtime may use MemoryPack when supported");
            TypeMetadata metadata = TypeMetadataCache.Get(type);
            builder.AddFixedLeaf(node, "memberCount", "count", sizeof(int), metadata.Members.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var member in metadata.Members)
                node.MutableChildren.Add(builder.BuildMemberSchemaEntryNode(member, allowCustom));
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}