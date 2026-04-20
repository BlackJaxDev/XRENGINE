using MemoryPack;
using XREngine.Core.Files;

namespace XREngine;

internal sealed class SerializedAssetSupport
{
    private SerializedAssetSupport()
    {
    }

    public static void WriteModel<TAsset, TModel>(CookedBinaryWriter writer, TAsset asset, Func<TAsset, TModel> createModel)
        where TAsset : class
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(createModel);

        writer.WriteValue(createModel(asset));
    }

    public static TAsset ReadModel<TAsset, TModel>(CookedBinaryReader reader, Func<TAsset> createAsset, Action<TAsset, TModel?> applyModel)
        where TAsset : class
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(createAsset);
        ArgumentNullException.ThrowIfNull(applyModel);

        TModel? model = reader.ReadValue<TModel>();
        TAsset asset = createAsset();
        applyModel(asset, model);
        return asset;
    }

    public static long CalculateModelSize<TAsset, TModel>(TAsset asset, Func<TAsset, TModel> createModel)
        where TAsset : class
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(createModel);

        return CookedBinarySerializer.CalculateSize(createModel(asset));
    }

    public static void RegisterFormatter<TAsset>(MemoryPackFormatter<TAsset> formatter)
        where TAsset : class
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!MemoryPackFormatterProvider.IsRegistered<TAsset>())
            MemoryPackFormatterProvider.Register(formatter);
    }

    public static byte[] SerializePayload<TAsset>(TAsset value)
        where TAsset : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(value));
    }

    public static TAsset? DeserializePayload<TAsset>(byte[]? payload, Func<TAsset>? emptyFactory = null)
        where TAsset : class
    {
        if (payload is null || payload.Length == 0)
            return emptyFactory?.Invoke();

        return CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Deserialize(typeof(TAsset), payload) as TAsset)
            ?? emptyFactory?.Invoke();
    }

    internal sealed class CookedBinaryMemoryPackFormatter<TAsset>(Func<byte[]?, TAsset?> deserializePayload) : MemoryPackFormatter<TAsset>
        where TAsset : class
    {
        public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref TAsset? value)
        {
            if (value is null)
            {
                writer.WriteNullObjectHeader();
                return;
            }

            writer.WriteObjectHeader(1);
            writer.WriteUnmanagedArray(SerializePayload(value));
        }

        public override void Deserialize(ref MemoryPackReader reader, scoped ref TAsset? value)
        {
            if (!reader.TryReadObjectHeader(out byte count))
            {
                value = null;
                return;
            }

            if (count != 1)
                MemoryPackSerializationException.ThrowInvalidPropertyCount(1, count);

            byte[]? payload = reader.ReadUnmanagedArray<byte>();
            value = deserializePayload(payload);
        }
    }
}