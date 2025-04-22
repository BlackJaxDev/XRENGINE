using System.Numerics;

namespace XREngine.Animation
{
    public record struct Vector3Weight(Vector3 Value, float Weight)
    {
        public Vector3Weight() : this(default, default) { }
    }
}
