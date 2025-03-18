using System.Numerics;

namespace XREngine.Animation
{
    public record struct QuaternionWeight(Quaternion Value, float Weight)
    {
        public QuaternionWeight() : this(default, default) { }
    }
}
