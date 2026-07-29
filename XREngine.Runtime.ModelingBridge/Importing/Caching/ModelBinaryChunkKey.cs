namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Identifies a singleton or instance-scoped chunk within one container.
/// </summary>
internal readonly record struct ModelBinaryChunkKey(uint TypeId, ulong InstanceId);
