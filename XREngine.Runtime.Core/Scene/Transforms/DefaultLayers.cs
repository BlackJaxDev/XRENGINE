namespace XREngine.Components.Scene.Transforms
{
    public class DefaultLayers
    {
        public const int DynamicIndex = 0;
        public const int StaticIndex = 1;
        /// <summary>Default layer for character, avatar, and attached VR controller or tracker meshes.</summary>
        public const int CharactersIndex = 2;
        /// <summary>Engine-owned layer for a local VR first-person head-chopped avatar proxy, hidden from mirrors and captures.</summary>
        public const int LocalVrFirstPersonIndex = 29;
        /// <summary>Engine-owned layer for a local VR avatar, hidden by first-person cameras.</summary>
        public const int LocalVrAvatarIndex = 30;
        public const int GizmosIndex = 31;

        public static string Dynamic { get; } = "Dynamic";
        public static string Static { get; } = "Static";
        public static string Characters { get; } = "Characters";
        public static string LocalVrFirstPerson { get; } = "Local VR First Person";
        public static string LocalVrAvatar { get; } = "Local VR Avatar";
        public static string Gizmos { get; } = "Gizmos";

        public static Dictionary<int, string> All { get; } = new()
        {
            { DynamicIndex, Dynamic },
            { StaticIndex, Static },
            { CharactersIndex, Characters },
            { LocalVrFirstPersonIndex, LocalVrFirstPerson },
            { LocalVrAvatarIndex, LocalVrAvatar },
            { GizmosIndex, Gizmos }
        };

        /// <summary>Returns a layer mask that includes only dynamic world geometry.</summary>
        public static int DynamicWorld => 1 << DynamicIndex;

        /// <summary>Returns a layer mask that includes only static world geometry.</summary>
        public static int StaticWorld => 1 << StaticIndex;

        /// <summary>Returns a layer mask that includes static and dynamic world geometry.</summary>
        public static int World => DynamicWorld | StaticWorld;

        /// <summary>
        /// Returns a layer mask that includes character meshes and the full local VR avatar,
        /// excluding first-person head-chopped proxies.
        /// </summary>
        public static int CharactersMask => (1 << CharactersIndex) | (1 << LocalVrAvatarIndex);

        /// <summary>Returns a layer mask that includes only the full local VR avatar layer.</summary>
        public static int LocalVrAvatarOnly => 1 << LocalVrAvatarIndex;

        /// <summary>
        /// Returns a layer mask with all layers except Gizmos and local first-person-only VR proxies.
        /// Mirrors and scene capture cameras use this mask so reflections show the full local avatar.
        /// </summary>
        public static int EverythingExceptGizmos => ~((1 << GizmosIndex) | (1 << LocalVrFirstPersonIndex));

        /// <summary>First-person view mask; spectator and mirror cameras retain the local avatar layer.</summary>
        public static int FirstPersonVr => ~(1 << LocalVrAvatarIndex);
    }
}
