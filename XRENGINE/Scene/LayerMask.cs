namespace XREngine.Scene
{
    public struct LayerMask
    {
        private int _mask;

        public LayerMask() { }
        public LayerMask(int mask) : this()
        {
            _mask = mask;
        }

        //
        // Summary:
        //     Converts a layer mask value to an integer value.
        public int Value
        {
            readonly get => _mask;
            set => _mask = value;
        }

        public static implicit operator int(LayerMask mask)
            => mask._mask;

        public static implicit operator LayerMask(int intVal)
        {
            LayerMask result = default;
            result._mask = intVal;
            return result;
        }

        public static string LayerToName(int layer)
            => Engine.GameSettings.LayerNames.TryGetValue(layer, out string? name) ? name : string.Empty;

        public static int NameToLayer(string layerName)
            => Engine.GameSettings.LayerNames.FirstOrDefault(x => string.Equals(x.Value, layerName, StringComparison.Ordinal)).Key;

        public static LayerMask GetMask(params string[] layerNames)
        {
            ArgumentNullException.ThrowIfNull(layerNames);

            int num = 0;
            foreach (string layerName in layerNames)
            {
                int num2 = NameToLayer(layerName);
                if (num2 != -1)
                    num |= 1 << num2;
            }

            return num;
        }
    }
}
