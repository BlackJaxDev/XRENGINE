namespace XREngine.Components
{
    public static class Audio2Face3DRegistry
    {
        public const int Count = 6;

        public static readonly string[] Names =
        [
            "angry",
            "disgust",
            "fear",
            "happy",
            "neutral",
            "sad",
        ];

        public static bool TryGetIndex(string? emotionName, out int index)
        {
            if (string.IsNullOrWhiteSpace(emotionName))
            {
                index = -1;
                return false;
            }

            for (int i = 0; i < Names.Length; i++)
            {
                if (string.Equals(Names[i], emotionName, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }
        public const string MissingAdapterMessage = "No Audio2Face-3D live client adapter is registered. Add an Audio2Face3DNativeBridgeComponent beside the runtime component or register a custom adapter through Audio2Face3DRegistry.Adapter.";

        public static IAudio2Face3DLiveClientAdapter? Adapter { get; set; }
        public static bool HasAdapter => Adapter is not null;
    }
}
