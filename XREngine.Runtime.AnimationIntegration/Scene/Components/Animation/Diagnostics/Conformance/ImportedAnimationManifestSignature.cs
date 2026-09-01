using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Computes a content-addressed, path-independent identity for the complete
/// semantic result of importing a Unity animation clip.
/// </summary>
/// <remarks>
/// This signature intentionally covers durable importer output rather than an
/// asset path or object name. Collections are sorted by all of their semantic
/// fields, so equivalent manifests retain one identity regardless of importer
/// traversal order. Values use tagged, length-prefixed UTF-8 encoding to avoid
/// delimiter and concatenation ambiguities.
/// </remarks>
public static class ImportedAnimationManifestSignature
{
    private const int SignatureSchemaVersion = 1;

    /// <summary>
    /// Computes an uppercase SHA-256 identity for <paramref name="manifest"/>.
    /// </summary>
    public static string ComputeSha256(ImportedAnimationImportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var writer = new CanonicalWriter();
        writer.WriteInt32(SignatureSchemaVersion);
        writer.WriteInt32(manifest.SchemaVersion);
        writer.WriteInt32(manifest.CapabilityContractVersion);
        WriteSourceIdentity(writer, manifest.SourceIdentity);
        WriteCoordinateContract(writer, manifest.CoordinateContract);
        WriteDomains(writer, manifest.Domains);
        WriteBindings(writer, manifest.Bindings);
        WritePreservedPayloads(writer, manifest.PreservedPayloads);
        return Convert.ToHexString(SHA256.HashData(writer.WrittenSpan));
    }

    private static void WriteSourceIdentity(CanonicalWriter writer, ImportedAnimationSourceIdentity? identity)
    {
        writer.WriteObjectMarker(identity is not null);
        if (identity is null)
            return;

        writer.WriteString(identity.SourceFormat);
        writer.WriteInt32(identity.SerializedVersion);
        writer.WriteString(identity.SourceContentSha256);
        writer.WriteString(identity.ImportSettingsSha256);
    }

    private static void WriteCoordinateContract(CanonicalWriter writer, ImportedAnimationCoordinateContract? contract)
    {
        writer.WriteObjectMarker(contract is not null);
        if (contract is null)
            return;

        writer.WriteString(contract.ContractId);
        writer.WriteString(contract.GenericTransformRule);
        writer.WriteString(contract.HumanoidPositionRule);
        writer.WriteString(contract.HumanoidBodyPositionRule);
        writer.WriteString(contract.HumanoidRotationRule);
        writer.WriteString(contract.MuscleRule);
    }

    private static void WriteDomains(CanonicalWriter writer, ImportedAnimationDomainCapability[]? domains)
    {
        ImportedAnimationDomainCapability[] ordered = domains is null ? [] : [.. domains];
        Array.Sort(ordered, CompareDomains);
        writer.WriteInt32(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            ImportedAnimationDomainCapability domain = ordered[i];
            writer.WriteInt32((int)domain.Domain);
            writer.WriteInt32((int)domain.State);
            writer.WriteInt32(domain.SourceItemCount);
            writer.WriteInt32(domain.AppliedItemCount);
            writer.WriteInt32(domain.DiscardedItemCount);
            writer.WriteInt32(domain.PreservedItemCount);

            string[] diagnostics = domain.Diagnostics is null ? [] : [.. domain.Diagnostics];
            Array.Sort(diagnostics, StringComparer.Ordinal);
            writer.WriteInt32(diagnostics.Length);
            for (int diagnosticIndex = 0; diagnosticIndex < diagnostics.Length; diagnosticIndex++)
                writer.WriteString(diagnostics[diagnosticIndex]);
        }
    }

    private static void WriteBindings(CanonicalWriter writer, ImportedAnimationSourceBinding[]? bindings)
    {
        ImportedAnimationSourceBinding[] ordered = bindings is null ? [] : [.. bindings];
        Array.Sort(ordered, CompareBindings);
        writer.WriteInt32(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            ImportedAnimationSourceBinding binding = ordered[i];
            writer.WriteInt32((int)binding.Domain);
            writer.WriteInt32((int)binding.State);
            writer.WriteString(binding.SourceField);
            writer.WriteString(binding.NodePath);
            writer.WriteString(binding.Attribute);
            writer.WriteNullableInt32(binding.ClassId);
            writer.WriteString(binding.RuntimeTarget);
            writer.WriteString(binding.Diagnostic);
        }
    }

    private static void WritePreservedPayloads(CanonicalWriter writer, ImportedAnimationPreservedPayload[]? payloads)
    {
        ImportedAnimationPreservedPayload[] ordered = payloads is null ? [] : [.. payloads];
        Array.Sort(ordered, ComparePayloads);
        writer.WriteInt32(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            ImportedAnimationPreservedPayload payload = ordered[i];
            writer.WriteInt32((int)payload.Domain);
            writer.WriteString(payload.SourceLocation);
            writer.WriteInt32(payload.SerializedPayloadByteCount);
            writer.WriteString(payload.SerializedPayloadSha256);
            writer.WriteBoolean(payload.ContentOmitted);
        }
    }

    private static int CompareDomains(ImportedAnimationDomainCapability left, ImportedAnimationDomainCapability right)
    {
        int comparison = ((int)left.Domain).CompareTo((int)right.Domain);
        if (comparison != 0)
            return comparison;
        comparison = ((int)left.State).CompareTo((int)right.State);
        if (comparison != 0)
            return comparison;
        comparison = left.SourceItemCount.CompareTo(right.SourceItemCount);
        if (comparison != 0)
            return comparison;
        comparison = left.AppliedItemCount.CompareTo(right.AppliedItemCount);
        if (comparison != 0)
            return comparison;
        comparison = left.DiscardedItemCount.CompareTo(right.DiscardedItemCount);
        if (comparison != 0)
            return comparison;
        comparison = left.PreservedItemCount.CompareTo(right.PreservedItemCount);
        return comparison != 0
            ? comparison
            : CompareDiagnostics(left.Diagnostics, right.Diagnostics);
    }

    private static int CompareBindings(ImportedAnimationSourceBinding left, ImportedAnimationSourceBinding right)
    {
        int comparison = ((int)left.Domain).CompareTo((int)right.Domain);
        if (comparison != 0)
            return comparison;
        comparison = ((int)left.State).CompareTo((int)right.State);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.SourceField, right.SourceField);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.NodePath, right.NodePath);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Attribute, right.Attribute);
        if (comparison != 0)
            return comparison;
        comparison = Nullable.Compare(left.ClassId, right.ClassId);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.RuntimeTarget, right.RuntimeTarget);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Diagnostic, right.Diagnostic);
    }

    private static int ComparePayloads(ImportedAnimationPreservedPayload left, ImportedAnimationPreservedPayload right)
    {
        int comparison = ((int)left.Domain).CompareTo((int)right.Domain);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.SourceLocation, right.SourceLocation);
        if (comparison != 0)
            return comparison;
        comparison = left.SerializedPayloadByteCount.CompareTo(right.SerializedPayloadByteCount);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.SerializedPayloadSha256, right.SerializedPayloadSha256);
        if (comparison != 0)
            return comparison;
        return left.ContentOmitted.CompareTo(right.ContentOmitted);
    }

    private static int CompareDiagnostics(string[]? left, string[]? right)
    {
        string[] leftOrdered = left is null ? [] : [.. left];
        string[] rightOrdered = right is null ? [] : [.. right];
        Array.Sort(leftOrdered, StringComparer.Ordinal);
        Array.Sort(rightOrdered, StringComparer.Ordinal);
        int length = Math.Min(leftOrdered.Length, rightOrdered.Length);
        for (int i = 0; i < length; i++)
        {
            int comparison = StringComparer.Ordinal.Compare(leftOrdered[i], rightOrdered[i]);
            if (comparison != 0)
                return comparison;
        }

        return leftOrdered.Length.CompareTo(rightOrdered.Length);
    }

    private sealed class CanonicalWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

        public void WriteObjectMarker(bool present)
            => WriteBoolean(present);

        public void WriteBoolean(bool value)
        {
            Span<byte> destination = _buffer.GetSpan(1);
            destination[0] = value ? (byte)1 : (byte)0;
            _buffer.Advance(1);
        }

        public void WriteInt32(int value)
        {
            Span<byte> destination = _buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(destination, value);
            _buffer.Advance(sizeof(int));
        }

        public void WriteNullableInt32(int? value)
        {
            WriteBoolean(value.HasValue);
            if (value.HasValue)
                WriteInt32(value.Value);
        }

        public void WriteString(string? value)
        {
            WriteBoolean(value is not null);
            if (value is null)
                return;

            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteInt32(byteCount);
            Span<byte> destination = _buffer.GetSpan(byteCount);
            int written = Encoding.UTF8.GetBytes(value, destination);
            _buffer.Advance(written);
        }
    }
}
