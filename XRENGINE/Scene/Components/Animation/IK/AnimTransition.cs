
namespace XREngine.Scene.Components.Animation
{
    public class AnimTransition
    {
        public string? Name { get; set; }

        public bool NameEquals(string name)
            => string.Equals(Name, name, StringComparison.InvariantCulture);
    }
}