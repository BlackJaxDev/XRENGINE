using System;
using System.IO;
using System.Reflection;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class XRMeshYamlTypeConverter : IYamlTypeConverter
{
    private const string FailedCookedMeshCategory = "XRMesh Cooked Payload";

    public bool Accepts(Type type)
        => type == typeof(XRMesh);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (scalar.Value is null || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            throw new YamlException($"Unexpected scalar while deserializing {nameof(XRMesh)}: '{scalar.Value}'.");
        }

        XRMeshYamlEnvelope? envelope = rootDeserializer(typeof(XRMeshYamlEnvelope)) as XRMeshYamlEnvelope;
        if (envelope?.ID is Guid referenceId
            && referenceId != Guid.Empty
            && (envelope.Payload is null || envelope.Payload.Length == 0))
            return ResolveExternalReference(referenceId);

        if (envelope?.Payload is null || envelope.Payload.Length == 0)
            return new XRMesh();

        byte[] payload = envelope.Payload.GetBytes();
        if (TryExtractRuntimeCookedPayload(payload, out byte[]? runtimePayload))
            payload = runtimePayload!;

        XRMesh? mesh;
        try
        {
            mesh = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
                () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), payload) as XRMesh);
        }
        catch (Exception ex) when (IsRecoverableCookedMeshPayloadException(ex))
        {
            return CreateFailedCookedMeshPlaceholder(envelope, payload.Length, ex);
        }

        if (mesh is null)
        {
            return CreateFailedCookedMeshPlaceholder(
                envelope,
                payload.Length,
                new InvalidOperationException("Cooked mesh payload did not deserialize to an XRMesh instance."));
        }

        return mesh;
    }

    private static bool IsRecoverableCookedMeshPayloadException(Exception ex)
        => ex is EndOfStreamException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or FormatException
            or InvalidCastException
            or OverflowException;

    private static XRMesh CreateFailedCookedMeshPlaceholder(XRMeshYamlEnvelope envelope, int payloadLength, Exception ex)
    {
        Guid id = envelope.ID.GetValueOrDefault();
        string diagnosticPath = GetFailedCookedMeshDiagnosticPath(id);
        string context = GetFailedCookedMeshContext(envelope, payloadLength, ex);

        AssetDiagnostics.RecordMissingAsset(diagnosticPath, FailedCookedMeshCategory, context);
        Debug.MeshesWarningEvery(
            string.Concat(nameof(XRMeshYamlTypeConverter), ":", diagnosticPath),
            TimeSpan.FromMinutes(1),
            "Cooked XRMesh payload failed to deserialize for '{0}'. The model will load with a placeholder mesh. Reimport the source FBX/model or clear generated asset cache. {1}: {2}",
            diagnosticPath,
            ex.GetType().Name,
            ex.Message);

        string idSuffix = id == Guid.Empty ? "unknown" : id.ToString("N")[..8];
        XRMesh placeholder = new()
        {
            Name = string.Concat("FailedCookedMesh_", idSuffix)
        };
        if (id != Guid.Empty)
            TryAssignMeshId(placeholder, id);

        return placeholder;
    }

    private static string GetFailedCookedMeshDiagnosticPath(Guid id)
    {
        string? ownerPath = AssetDeserializationContext.CurrentFilePath;
        if (!string.IsNullOrWhiteSpace(ownerPath))
            return ownerPath;

        string idText = id == Guid.Empty ? "no-id" : id.ToString("D");
        return string.Concat("<inline XRMesh payload ", idText, ">");
    }

    private static string GetFailedCookedMeshContext(XRMeshYamlEnvelope envelope, int payloadLength, Exception ex)
    {
        Guid id = envelope.ID.GetValueOrDefault();
        string idText = id == Guid.Empty ? "<none>" : id.ToString("D");
        return string.Concat(
            nameof(XRMeshYamlTypeConverter),
            ".",
            nameof(ReadYaml),
            " failed to deserialize cooked mesh payload ",
            "(id='",
            idText,
            "', bytes=",
            payloadLength,
            ", format='",
            envelope.Format,
            "', version=",
            envelope.Version,
            "). Reimport the source FBX/model or clear generated mesh cache. ",
            ex.GetType().Name,
            ": ",
            ex.Message);
    }

    private static void TryAssignMeshId(XRMesh mesh, Guid id)
    {
        try
        {
            PropertyInfo? idProperty = mesh.GetType().GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
            if (idProperty?.SetMethod is not null)
                idProperty.SetValue(mesh, id);
        }
        catch
        {
            // The placeholder is still useful even if a non-public ID setter cannot be reached.
        }
    }

    private static bool TryExtractRuntimeCookedPayload(byte[] payload, out byte[]? runtimePayload)
    {
        const byte cookedBinaryCustomObjectMarker = 24;

        runtimePayload = null;
        if (payload.Length < sizeof(byte) + sizeof(int) || payload[0] != cookedBinaryCustomObjectMarker)
            return false;

        try
        {
            using MemoryStream stream = new(payload, writable: false);
            using BinaryReader reader = new(stream, System.Text.Encoding.UTF8);
            if (reader.ReadByte() != cookedBinaryCustomObjectMarker)
                return false;

            string typeName = reader.ReadString();
            if (!IsSerializedMeshType(typeName))
                return false;

            int payloadLength = reader.ReadInt32();
            long remaining = stream.Length - stream.Position;
            if (payloadLength <= 0 || payloadLength > remaining)
                return false;

            runtimePayload = reader.ReadBytes(payloadLength);
            return runtimePayload.Length == payloadLength;
        }
        catch (Exception ex) when (
            ex is EndOfStreamException or
            IOException or
            ArgumentException or
            FormatException or
            OverflowException)
        {
            runtimePayload = null;
            return false;
        }
    }

    private static bool IsSerializedMeshType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        Type? resolved = Type.GetType(typeName, throwOnError: false);
        if (resolved is not null)
            return typeof(XRMesh).IsAssignableFrom(resolved);

        int assemblySeparator = typeName.IndexOf(',');
        string fullName = assemblySeparator >= 0
            ? typeName[..assemblySeparator].Trim()
            : typeName.Trim();
        return string.Equals(fullName, typeof(XRMesh).FullName, StringComparison.Ordinal);
    }

    private static XRMesh? ResolveExternalReference(Guid id)
    {
        IRenderAssetSerializationServices services = RenderAssetSerializationServices.Current;
        if (services.TryGetAssetById(id, out XRAsset? loadedAsset) && loadedAsset is XRMesh loadedMesh)
            return loadedMesh;

        string? referenceAssetPath = AssetDeserializationContext.CurrentFilePath;
        if (!services.TryResolveAssetPathById(id, referenceAssetPath, out string? assetPath) || string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            return null;

        if (DeferredAssetReferenceContext.TryDeferAssetLoad(assetPath, typeof(XRMesh), out XRAsset? deferredAsset))
            return deferredAsset as XRMesh;

        return services.LoadImmediate(assetPath, typeof(XRMesh)) as XRMesh;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not XRMesh mesh)
            throw new YamlException($"Expected {nameof(XRMesh)} but got '{value.GetType()}'.");

        if (TryWriteAsReference.TryEmitReference(emitter, mesh))
            return;

        byte[] payloadBytes = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => RuntimeCookedBinarySerializer.Serialize(mesh));
        XRMeshYamlEnvelope envelope = new()
        {
            ID = mesh.ID,
            AssetType = mesh.GetType().FullName ?? mesh.GetType().Name,
            Format = "CookedBinary",
            Version = 1,
            Payload = new DataSource(payloadBytes) { PreferCompressedYaml = true }
        };

        serializer(envelope, typeof(XRMeshYamlEnvelope));
    }

    private sealed class XRMeshYamlEnvelope
    {
        public Guid? ID { get; set; }

        [YamlMember(Alias = "__assetType", Order = -100)]
        public string? AssetType { get; set; }

        public string Format { get; set; } = "CookedBinary";

        public int Version { get; set; } = 1;

        public DataSource? Payload { get; set; }
    }
}
