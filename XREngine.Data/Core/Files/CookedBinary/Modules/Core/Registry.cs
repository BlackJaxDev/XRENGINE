using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace XREngine.Core.Files;

public readonly record struct CookedBinarySerializationModuleInfo(int Priority, string Name, string Description);

/// <summary>
/// Feature-owned codec plugged into the format-neutral cooked-binary serializer by an
/// application composition root. Implementations live with the domain type they encode.
/// </summary>
public interface ICookedBinaryFeatureCodec
{
    CookedBinarySerializationModuleInfo Info { get; }

    bool CanHandle(Type type);

    void Write(CookedBinaryWriter writer, object value);

    object? Read(Type targetType, CookedBinaryReader reader);

    long CalculateSize(object value);

    object CreateSchemaModel(object value);

    Type GetSchemaModelType(Type runtimeType);
}

public static partial class CookedBinarySerializer
{
    private static readonly CookedBinaryModule[] BuiltInSerializationModules =
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
        new CustomSerializableCookedBinaryModule(),
        new BlittableStructCookedBinaryModule(),
        new ObjectCookedBinaryModule(),
    ];

    private static readonly object ModuleSync = new();
    private static CookedBinaryModule[] _featureSerializationModules = [];

    private static CookedBinaryModule[] SerializationModules
    {
        get
        {
            CookedBinaryModule[] featureModules = Volatile.Read(ref _featureSerializationModules);
            if (featureModules.Length == 0)
                return BuiltInSerializationModules;

            CookedBinaryModule[] modules = new CookedBinaryModule[BuiltInSerializationModules.Length + featureModules.Length];
            int featureIndex = 0;
            int builtInIndex = 0;
            int outputIndex = 0;
            while (featureIndex < featureModules.Length && builtInIndex < BuiltInSerializationModules.Length)
            {
                if (featureModules[featureIndex].Info.Priority < BuiltInSerializationModules[builtInIndex].Info.Priority)
                    modules[outputIndex++] = featureModules[featureIndex++];
                else
                    modules[outputIndex++] = BuiltInSerializationModules[builtInIndex++];
            }

            while (featureIndex < featureModules.Length)
                modules[outputIndex++] = featureModules[featureIndex++];
            while (builtInIndex < BuiltInSerializationModules.Length)
                modules[outputIndex++] = BuiltInSerializationModules[builtInIndex++];
            return modules;
        }
    }

    public static IReadOnlyList<CookedBinarySerializationModuleInfo> GetSerializationModuleChecklist()
        => [.. SerializationModules.Select(static module => module.Info)];

    /// <summary>
    /// Installs a feature-owned cooked-binary module until the returned lease is disposed.
    /// The lease makes optional serializer composition deterministic across tests and editor reloads.
    /// </summary>
    public static IDisposable InstallFeatureCodec(ICookedBinaryFeatureCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        CookedBinaryModule module = new FeatureCodecModule(codec);

        lock (ModuleSync)
        {
            CookedBinaryModule[] current = _featureSerializationModules;
            if (current.Any(existing => string.Equals(existing.Info.Name, module.Info.Name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Cooked-binary module '{module.Info.Name}' is already installed.");

            CookedBinaryModule[] updated = [.. current, module];
            Array.Sort(updated, static (left, right) => left.Info.Priority.CompareTo(right.Info.Priority));
            Volatile.Write(ref _featureSerializationModules, updated);
        }

        return new FeatureModuleLease(module);
    }

    private sealed class FeatureCodecModule(ICookedBinaryFeatureCodec codec) : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => codec.Info;

        public override bool TryWrite(
            CookedBinaryWriter writer,
            object value,
            Type runtimeType,
            bool allowCustom,
            CookedBinarySerializationCallbacks? callbacks)
        {
            if (!allowCustom || !codec.CanHandle(runtimeType))
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, runtimeType);
            codec.Write(writer, value);
            return true;
        }

        public override bool TryRead(
            CookedBinaryTypeMarker marker,
            CookedBinaryReader reader,
            Type? expectedType,
            CookedBinarySerializationCallbacks? callbacks,
            out object? value)
        {
            if (marker != CookedBinaryTypeMarker.CustomObject)
            {
                value = null;
                return false;
            }

            long rewind = reader.Position;
            string typeName = reader.ReadString();
            Type? targetType = TryResolveSerializedTypeName(typeName) ?? expectedType;
            if (targetType is null || !codec.CanHandle(targetType))
            {
                reader.Position = rewind;
                value = null;
                return false;
            }

            value = codec.Read(targetType, reader);
            return true;
        }

        public override bool TryAddSize(
            CookedBinarySizeCalculator calculator,
            object value,
            Type runtimeType,
            bool allowCustom)
        {
            if (!allowCustom || !codec.CanHandle(runtimeType))
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType) + codec.CalculateSize(value));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(
            CookedBinarySchemaBuilder builder,
            string name,
            Type? declaredType,
            object value,
            Type runtimeType,
            bool allowCustom)
        {
            if (!allowCustom || !codec.CanHandle(runtimeType))
                return null;

            object model = codec.CreateSchemaModel(value);
            CookedBinarySchemaNode node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddExpandedCustomModelValueNode(
                node,
                runtimeType,
                model,
                model.GetType(),
                $"{codec.Info.Name} feature codec writes its serialized model via WriteValue");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(
            CookedBinarySchemaBuilder builder,
            string name,
            Type type,
            bool allowCustom)
        {
            if (!allowCustom || !codec.CanHandle(type))
                return null;

            CookedBinarySchemaNode node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddExpandedCustomModelSchemaNode(
                node,
                type,
                codec.GetSchemaModelType(type),
                $"{codec.Info.Name} feature codec writes its serialized model via WriteValue");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }

    private sealed class FeatureModuleLease(CookedBinaryModule module) : IDisposable
    {
        private CookedBinaryModule? _module = module;

        public void Dispose()
        {
            CookedBinaryModule? installed = Interlocked.Exchange(ref _module, null);
            if (installed is null)
                return;

            lock (ModuleSync)
            {
                CookedBinaryModule[] current = _featureSerializationModules;
                int index = Array.IndexOf(current, installed);
                if (index < 0)
                    return;

                CookedBinaryModule[] updated = new CookedBinaryModule[current.Length - 1];
                if (index > 0)
                    Array.Copy(current, 0, updated, 0, index);
                if (index < current.Length - 1)
                    Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
                Volatile.Write(ref _featureSerializationModules, updated);
            }
        }
    }

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
