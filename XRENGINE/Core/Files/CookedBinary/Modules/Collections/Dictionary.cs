using System.Collections;
using System.Globalization;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class DictionaryCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1000, "Dictionaries", "Runtime-type-prefixed IDictionary entries.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (value is not IDictionary dictionary)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.Dictionary);
            WriteTypeName(writer, runtimeType);
            writer.Write(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
            {
                WriteValue(writer, entry.Key, allowCustom, callbacks);
                WriteValue(writer, entry.Value, allowCustom, callbacks);
            }
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.Dictionary)
            {
                value = null;
                return false;
            }

            value = ReadDictionary(reader, callbacks);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not IDictionary dictionary)
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType));
            calculator.AddBytes(sizeof(int));
            foreach (DictionaryEntry entry in dictionary)
            {
                calculator.AddValue(entry.Key, allowCustom);
                calculator.AddValue(entry.Value, allowCustom);
            }
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not IDictionary dictionary)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.Dictionary.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringLeaf(node, "dictionaryType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);
            builder.AddFixedLeaf(node, "count", "count", sizeof(int), dictionary.Count.ToString(CultureInfo.InvariantCulture));

            Type keyType = typeof(object);
            Type valueType = typeof(object);
            if (TryGetDictionaryTypes(runtimeType, out Type? resolvedKeyType, out Type? resolvedValueType))
            {
                keyType = resolvedKeyType!;
                valueType = resolvedValueType!;
            }

            int index = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                var item = builder.NewNode($"entry{index}", "dictionaryEntry", runtimeType.FullName ?? runtimeType.Name);
                item.MutableChildren.Add(builder.BuildValueNode("key", keyType, entry.Key, allowCustom));
                item.MutableChildren.Add(builder.BuildValueNode("value", valueType, entry.Value, allowCustom));
                node.MutableChildren.Add(builder.FinalizeNode(item));
                index++;
            }

            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!typeof(IDictionary).IsAssignableFrom(type))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.Dictionary.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringTemplateLeaf(node, "dictionaryType", type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
            builder.AddFixedLeaf(node, "count", "count", sizeof(int), "variable");

            Type keyType = typeof(object);
            Type valueType = typeof(object);
            if (TryGetDictionaryTypes(type, out Type? resolvedKeyType, out Type? resolvedValueType))
            {
                keyType = resolvedKeyType!;
                valueType = resolvedValueType!;
            }

            var item = builder.NewNode("entry", "dictionaryEntry", type.FullName ?? type.Name);
            item.MutableChildren.Add(builder.BuildTypeNode("key", keyType, allowCustom));
            item.MutableChildren.Add(builder.BuildTypeNode("value", valueType, allowCustom));
            node.MutableChildren.Add(builder.FinalizeNode(item, allowUnknownChildren: true));
            node.Notes = "repeated for each dictionary entry";
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}