using System.Collections.Generic;
using MemoryPack;

namespace System.Collections.Generic
{
    // MemoryPack formatter to serialize EventList<T> without emitting its event wiring.
    public partial class EventList<T> : IMemoryPackable<EventList<T>>
    {
        static void IMemoryPackFormatterRegister.RegisterFormatter() { }

        static void IMemoryPackable<EventList<T>>.Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref EventList<T>? value)
        {
            if (value is null)
            {
                writer.WriteNullObjectHeader();
                return;
            }

            // Persist flags first to recreate list semantics.
            writer.WriteUnmanaged(value._allowDuplicates);
            writer.WriteUnmanaged(value._allowNull);

            // Persist items as a standard collection.
            var list = value._list;
            writer.WriteCollectionHeader(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                writer.WriteValue(list[i]);
            }
        }

        static void IMemoryPackable<EventList<T>>.Deserialize(ref MemoryPackReader reader, scoped ref EventList<T>? value)
        {
            if (!reader.TryReadObjectHeader(out _))
            {
                value = null;
                return;
            }

            bool allowDuplicates = reader.ReadUnmanaged<bool>();
            bool allowNull = reader.ReadUnmanaged<bool>();

            if (!reader.TryReadCollectionHeader(out int count))
            {
                value = null;
                return;
            }
            var list = new List<T>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(reader.ReadValue<T>()!);
            }

            value = new EventList<T>(list, allowDuplicates, allowNull);
        }
    }
}
