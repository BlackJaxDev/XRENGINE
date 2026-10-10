using System.Buffers.Binary;
using System.Text;

namespace XREngine.Core.Files;

/// <summary>Preflights the engine's fixed string/byte-array MemoryPack envelopes before generated allocation.</summary>
internal static class CookedMemoryPackEnvelope
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Validate(ReadOnlySpan<byte> data, bool hasFormat)
    {
        if (!CookedBinaryReadBudget.IsActive)
            return;
        int offset = 0;
        Require(data.Length >= 1 && data[offset++] == (hasFormat ? 3 : 2), "unsupported object header");
        int stringHeader = ReadInt32(data, ref offset);
        Require(stringHeader != -1, "missing type identity");
        int characterCount;
        int stringBytes;
        if (stringHeader >= 0)
        {
            characterCount = stringHeader;
            Require(characterCount <= (data.Length - offset) / sizeof(char), "truncated UTF-16 identity");
            stringBytes = characterCount * sizeof(char);
        }
        else
        {
            stringBytes = ~stringHeader;
            characterCount = ReadInt32(data, ref offset);
            Require(characterCount >= 0 && stringBytes <= data.Length - offset, "truncated UTF-8 identity");
            Require(StrictUtf8.GetCharCount(data.Slice(offset, stringBytes)) == characterCount, "incorrect UTF-8 character count");
        }
        CookedBinaryReadBudget.Reserve(characterCount, sizeof(char));
        offset += stringBytes;
        if (hasFormat)
        {
            Require(offset < data.Length, "missing format");
            offset++;
        }
        int payloadLength = ReadInt32(data, ref offset);
        Require(payloadLength >= 0 && payloadLength == data.Length - offset, "invalid payload length");
        CookedBinaryReadBudget.Reserve(payloadLength);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(offset <= data.Length - sizeof(int), "truncated length");
        int result = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return result;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidDataException($"CookedBinary.InvalidEnvelope: {reason}.");
    }
}
