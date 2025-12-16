
namespace XREngine.Components.Scene.Transforms
{
    public class DefaultLayers
    {
        public const int DynamicIndex = 0;
        public const int StaticIndex = 1;
        public const int GizmosIndex = 31;

        public static string Dynamic { get; } = "Dynamic";
        public static string Static { get; } = "Static";
        public static string Gizmos { get; } = "Gizmos";

        public static Dictionary<int, string> All { get; } = new()
        {
            { DynamicIndex, Dynamic },
            { StaticIndex, Static },
            { GizmosIndex, Gizmos }
        };

        /// <summary>
        /// Returns a layer mask with all layers except Gizmos.
        /// Useful for scene capture cameras that should not capture debug visuals.
        /// </summary>
        public static int EverythingExceptGizmos => ~(1 << GizmosIndex);
    }
}