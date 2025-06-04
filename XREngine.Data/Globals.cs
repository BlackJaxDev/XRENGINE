using System.Drawing;
using System.Numerics;

namespace XREngine
{
    public static class Globals
    {
        public static Color InvalidColor { get; } = Color.Magenta;

        public static readonly Vector3 Up = Vector3.UnitY;
        public static readonly Vector3 Down = -Vector3.UnitY;
        public static readonly Vector3 Left = -Vector3.UnitX;
        public static readonly Vector3 Right = Vector3.UnitX;
        public static readonly Vector3 Backward = -Vector3.UnitZ;
        public static readonly Vector3 Forward = Vector3.UnitZ;
        public static readonly Vector3 TowardsCamera = Forward;
        public static readonly Vector3 AwayFromCamera = Backward;

        public static class Min
        {
            public static readonly Vector2 Vector2 = new(float.MinValue);
            public static readonly Vector3 Vector3 = new(float.MinValue);
            public static readonly Vector4 Vector4 = new(float.MinValue);
        }
        public static class Max
        {
            public static readonly Vector2 Vector2 = new(float.MaxValue);
            public static readonly Vector3 Vector3 = new(float.MaxValue);
            public static readonly Vector4 Vector4 = new(float.MaxValue);
        }
    }
}
