using MemoryPack;
using XREngine.Core.Files;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;

namespace XREngine.Rendering;

public partial class XRMesh
{
    static XRMesh()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<XRMesh>())
            MemoryPackFormatterProvider.Register(new XRMeshMemoryPackFormatter());
    }

    private sealed class XRMeshMemoryPackFormatter : MemoryPackFormatter<XRMesh>
    {
        public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref XRMesh? value)
        {
            if (value is null)
            {
                writer.WriteNullObjectHeader();
                return;
            }

            writer.WriteObjectHeader(1);
            XRMesh mesh = value;
            byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(mesh));
            writer.WriteUnmanagedArray(payload);
        }

        public override void Deserialize(ref MemoryPackReader reader, scoped ref XRMesh? value)
        {
            if (!reader.TryReadObjectHeader(out byte count))
            {
                value = null;
                return;
            }

            if (count != 1)
                MemoryPackSerializationException.ThrowInvalidPropertyCount(1, count);

            byte[]? payload = reader.ReadUnmanagedArray<byte>();
            if (payload is null || payload.Length == 0)
            {
                value = new XRMesh();
                return;
            }

            XRMesh? mesh = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
                () => CookedBinarySerializer.Deserialize(typeof(XRMesh), payload) as XRMesh);

            value = mesh ?? new XRMesh();
        }
    }
}
