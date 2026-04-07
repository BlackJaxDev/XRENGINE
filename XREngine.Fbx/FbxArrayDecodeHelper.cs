using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace XREngine.Fbx;

internal static class FbxArrayDecodeHelper
{
    public static int[] ReadInt32ArrayDirect(FbxStructuralDocument document, FbxArrayWorkItem workItem)
    {
        int[] values = GC.AllocateUninitializedArray<int>(checked((int)workItem.ArrayLength));
        FbxStructuralParser.DecodeArrayPayload(document, workItem, MemoryMarshal.AsBytes(values.AsSpan()));
        SwapEndiannessIfNeeded(document.Header.IsBigEndian, values.AsSpan());
        return values;
    }

    public static long[] ReadInt64ArrayDirect(FbxStructuralDocument document, FbxArrayWorkItem workItem)
    {
        long[] values = GC.AllocateUninitializedArray<long>(checked((int)workItem.ArrayLength));
        FbxStructuralParser.DecodeArrayPayload(document, workItem, MemoryMarshal.AsBytes(values.AsSpan()));
        SwapEndiannessIfNeeded(document.Header.IsBigEndian, values.AsSpan());
        return values;
    }

    public static float[] ReadFloat32ArrayDirect(FbxStructuralDocument document, FbxArrayWorkItem workItem)
    {
        float[] values = GC.AllocateUninitializedArray<float>(checked((int)workItem.ArrayLength));
        FbxStructuralParser.DecodeArrayPayload(document, workItem, MemoryMarshal.AsBytes(values.AsSpan()));
        SwapEndiannessIfNeeded(document.Header.IsBigEndian, values.AsSpan());
        return values;
    }

    public static double[] ReadFloat64ArrayDirect(FbxStructuralDocument document, FbxArrayWorkItem workItem)
    {
        double[] values = GC.AllocateUninitializedArray<double>(checked((int)workItem.ArrayLength));
        FbxStructuralParser.DecodeArrayPayload(document, workItem, MemoryMarshal.AsBytes(values.AsSpan()));
        SwapEndiannessIfNeeded(document.Header.IsBigEndian, values.AsSpan());
        return values;
    }

    private static void SwapEndiannessIfNeeded(bool documentBigEndian, Span<int> values)
    {
        if (!NeedsByteSwap(documentBigEndian, sizeof(int)))
            return;

        for (int index = 0; index < values.Length; index++)
            values[index] = BinaryPrimitives.ReverseEndianness(values[index]);
    }

    private static void SwapEndiannessIfNeeded(bool documentBigEndian, Span<long> values)
    {
        if (!NeedsByteSwap(documentBigEndian, sizeof(long)))
            return;

        for (int index = 0; index < values.Length; index++)
            values[index] = BinaryPrimitives.ReverseEndianness(values[index]);
    }

    private static void SwapEndiannessIfNeeded(bool documentBigEndian, Span<float> values)
    {
        if (!NeedsByteSwap(documentBigEndian, sizeof(float)))
            return;

        Span<int> raw = MemoryMarshal.Cast<float, int>(values);
        SwapEndiannessIfNeeded(documentBigEndian, raw);
    }

    private static void SwapEndiannessIfNeeded(bool documentBigEndian, Span<double> values)
    {
        if (!NeedsByteSwap(documentBigEndian, sizeof(double)))
            return;

        Span<long> raw = MemoryMarshal.Cast<double, long>(values);
        SwapEndiannessIfNeeded(documentBigEndian, raw);
    }

    private static bool NeedsByteSwap(bool documentBigEndian, int elementSize)
        => elementSize > 1 && documentBigEndian == BitConverter.IsLittleEndian;
}
