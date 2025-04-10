
namespace XREngine.Components.Scene.Transforms
{
    public class DefaultLayers
    {
        public static string Default { get; } = "Default";
        public static Dictionary<int, string> All { get; } = new()
        {
            { 0, Default }
        };
    }
}