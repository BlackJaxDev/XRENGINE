using System.Collections.Immutable;

namespace XREngine.Networking;

/// <summary>Explicit AOT-safe game input registration; no reflection or dynamic activation is used.</summary>
public static class GameInputSchemaRegistry
{
    public const int MaxPayloadBytes = 512;
    private static ImmutableDictionary<(ushort Id, ushort Version), IGameInputSchema> _schemas = ImmutableDictionary<(ushort, ushort), IGameInputSchema>.Empty;

    public static void Register(IGameInputSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.SchemaId == 0 || schema.SchemaVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(schema));
        ImmutableInterlocked.Update(ref _schemas, schemas => schemas.Add((schema.SchemaId, schema.SchemaVersion), schema));
    }

    public static bool TryValidate(GameInputSnapshot input, out IGameInputSchema? schema)
    {
        schema = null;
        if (input.SchemaId == 0 || input.SchemaVersion == 0 || input.Payload is null || input.Payload.Length > MaxPayloadBytes
            || !_schemas.TryGetValue((input.SchemaId, input.SchemaVersion), out schema))
            return false;
        return schema.Validate(input.Payload);
    }

    public static bool TryApply(GameInputSnapshot input, PlayerInputSnapshot snapshot)
        => TryValidate(input, out IGameInputSchema? schema) && schema!.TryApply(input.Payload, snapshot);
}

public interface IGameInputSchema
{
    ushort SchemaId { get; }
    ushort SchemaVersion { get; }
    bool Validate(ReadOnlySpan<byte> payload);
    bool TryApply(ReadOnlySpan<byte> payload, PlayerInputSnapshot snapshot);
}
