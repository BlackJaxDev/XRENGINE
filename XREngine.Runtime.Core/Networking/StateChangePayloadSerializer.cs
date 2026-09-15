using System;
using MemoryPack;
using Newtonsoft.Json;

namespace XREngine.Networking
{
    public static class StateChangePayloadSerializer
    {
        /// <summary>Bounds the textual state-change envelope before JSON or base64 decoding.</summary>
        public const int MaxEncodedPayloadCharacters = 262_144;

        public static string Serialize<T>(T payload)
        {
            if (payload is null)
                return string.Empty;

            byte[] bytes = MemoryPackSerializer.Serialize(payload);
            return Convert.ToBase64String(bytes);
        }

        public static bool TryDeserialize<T>(string? data, out T? payload)
        {
            payload = default;
            if (string.IsNullOrWhiteSpace(data) || data.Length > MaxEncodedPayloadCharacters)
                return false;

            if (IsLikelyJson(data))
            {
                try
                {
                    payload = JsonConvert.DeserializeObject<T>(data);
                    return payload is not null;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(data);
                if (bytes.Length > BaseNetworkingManager.MaxInboundDecompressedBytes)
                    return false;
                payload = MemoryPackSerializer.Deserialize<T>(bytes);
                return payload is not null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLikelyJson(string data)
        {
            ReadOnlySpan<char> span = data.AsSpan().TrimStart();
            return span.StartsWith("{") || span.StartsWith("[");
        }
    }
}
