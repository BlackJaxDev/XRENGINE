namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class TypeReferenceCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(700, "TypeRef", "Assembly-qualified type references.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (value is not Type typeRef)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.TypeRef);
            WriteTypeName(writer, typeRef);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.TypeRef)
            {
                value = null;
                return false;
            }

            value = ReadTypeRef(reader);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not Type typeRef)
                return false;

            calculator.AddBytes(SizeOfTypeName(typeRef));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not Type typeRef)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.TypeRef.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringLeaf(node, "typeName", typeRef.AssemblyQualifiedName ?? typeRef.FullName ?? typeRef.Name);
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (type != typeof(Type) && !type.IsSubclassOf(typeof(Type)))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.TypeRef.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddUnknownLeaf(node, "typeName", "string", "7-bit UTF-8 type name");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}