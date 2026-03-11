using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Core.Files;

public readonly record struct CookedBinarySerializationModuleInfo(int Priority, string Name, string Description);

public static partial class CookedBinarySerializer
{
    private static readonly CookedBinaryModule[] SerializationModules =
    [
        new PrimitiveCookedBinaryModule(),
        new ByteArrayCookedBinaryModule(),
        new DataSourceCookedBinaryModule(),
        new XREventCookedBinaryModule(),
        new NullableCookedBinaryModule(),
        new ValueTupleCookedBinaryModule(),
        new TypeReferenceCookedBinaryModule(),
        new EnumCookedBinaryModule(),
        new ArrayCookedBinaryModule(),
        new DictionaryCookedBinaryModule(),
        new HashSetCookedBinaryModule(),
        new ListCookedBinaryModule(),
        new AnimationClipCookedBinaryModule(),
        new BlendTreeCookedBinaryModule(),
        new AnimStateMachineCookedBinaryModule(),
        new CustomSerializableCookedBinaryModule(),
        new BlittableStructCookedBinaryModule(),
        new ObjectCookedBinaryModule(),
    ];

    public static IReadOnlyList<CookedBinarySerializationModuleInfo> GetSerializationModuleChecklist()
        => [.. SerializationModules.Select(static module => module.Info)];

    private static Type? TryResolveSerializedTypeName(string typeName)
    {
        try
        {
            return ResolveType(typeName);
        }
        catch
        {
            return Type.GetType(typeName, throwOnError: false);
        }
    }

    private abstract class CookedBinaryModule
    {
        public abstract CookedBinarySerializationModuleInfo Info { get; }

        public virtual bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
            => false;

        public virtual bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            value = null;
            return false;
        }

        public virtual bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
            => false;

        public virtual CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
            => null;

        public virtual CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
            => null;
    }
}