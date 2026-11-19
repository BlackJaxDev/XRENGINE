using System.Reflection.Metadata;

namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Checks encoded signature counts and depth before the metadata decoder creates arrays.</summary>
internal static class UnityApiUsageSignaturePreflight
{
    private const int MaxTypeDepth = 32;
    private const int MaxItems = 1024;

    internal static void CheckMember(MetadataReader metadata, MemberReferenceKind kind, BlobHandle signature)
    {
        BlobReader reader = metadata.GetBlobReader(signature);
        if (kind == MemberReferenceKind.Method)
            CheckMethod(ref reader, 0);
        else if (kind == MemberReferenceKind.Field)
        {
            reader.ReadSignatureHeader();
            CheckType(ref reader, 0);
        }
        else
            throw new InvalidDataException("A member reference has an unsupported signature kind.");

        if (reader.RemainingBytes != 0)
            throw new InvalidDataException("A member signature has trailing data.");
    }

    internal static void CheckTypeSpecification(MetadataReader metadata, BlobHandle signature)
    {
        BlobReader reader = metadata.GetBlobReader(signature);
        CheckType(ref reader, 0);
        if (reader.RemainingBytes != 0)
            throw new InvalidDataException("A type specification has trailing data.");
    }

    private static void CheckMethod(ref BlobReader reader, int depth)
    {
        if (depth >= MaxTypeDepth)
            throw new InvalidDataException("Function pointer signatures exceed the metadata depth limit.");
        SignatureHeader header = reader.ReadSignatureHeader();
        if (header.IsGeneric)
            ReadCount(ref reader);
        int parameterCount = ReadCount(ref reader);
        if (parameterCount > reader.RemainingBytes)
            throw new InvalidDataException("A method signature declares more parameters than its remaining bytes.");
        CheckType(ref reader, depth + 1);
        for (int index = 0; index < parameterCount; index++)
        {
            byte code = reader.ReadByte();
            if (code == 0x41)
                code = reader.ReadByte();
            CheckType(ref reader, depth + 1, code);
        }
    }

    private static void CheckType(ref BlobReader reader, int depth)
        => CheckType(ref reader, depth, reader.ReadByte());

    private static void CheckType(ref BlobReader reader, int depth, byte code)
    {
        if (depth >= MaxTypeDepth)
            throw new InvalidDataException("Type signatures exceed the metadata depth limit.");
        switch (code)
        {
            case >= 0x01 and <= 0x0e:
            case 0x16:
            case 0x18:
            case 0x19:
            case 0x1c:
                return;
            case 0x0f:
            case 0x10:
            case 0x1d:
            case 0x45:
                CheckType(ref reader, depth + 1);
                return;
            case 0x11:
            case 0x12:
                reader.ReadCompressedInteger();
                return;
            case 0x13:
            case 0x1e:
                ReadCount(ref reader);
                return;
            case 0x14:
                CheckType(ref reader, depth + 1);
                int rank = ReadCount(ref reader);
                if (rank < 1 || rank > MaxTypeDepth)
                    throw new InvalidDataException("Array rank exceeds the metadata limit.");
                int sizeCount = ReadCount(ref reader);
                if (sizeCount > rank)
                    throw new InvalidDataException("Array shape has too many sizes.");
                for (int index = 0; index < sizeCount; index++)
                    reader.ReadCompressedInteger();
                int lowerBoundCount = ReadCount(ref reader);
                if (lowerBoundCount > rank)
                    throw new InvalidDataException("Array shape has too many lower bounds.");
                for (int index = 0; index < lowerBoundCount; index++)
                    reader.ReadCompressedSignedInteger();
                return;
            case 0x15:
                byte kind = reader.ReadByte();
                if (kind is not (0x11 or 0x12))
                    throw new InvalidDataException("Generic type signatures need a type handle.");
                reader.ReadCompressedInteger();
                int typeArgumentCount = ReadCount(ref reader);
                if (typeArgumentCount > reader.RemainingBytes)
                    throw new InvalidDataException("A generic type declares more arguments than its remaining bytes.");
                for (int index = 0; index < typeArgumentCount; index++)
                    CheckType(ref reader, depth + 1);
                return;
            case 0x1b:
                CheckMethod(ref reader, depth + 1);
                return;
            case 0x1f:
            case 0x20:
                reader.ReadCompressedInteger();
                CheckType(ref reader, depth + 1);
                return;
            default:
                throw new InvalidDataException("A type signature has an unsupported element code.");
        }
    }

    private static int ReadCount(ref BlobReader reader)
    {
        int count = reader.ReadCompressedInteger();
        if (count < 0 || count > MaxItems)
            throw new InvalidDataException("A signature count exceeds the metadata limit.");
        return count;
    }
}
