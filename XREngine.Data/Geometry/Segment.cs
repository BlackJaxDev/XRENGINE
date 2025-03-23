using Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct Segment(Vector3 start, Vector3 end)
    {
        private Vector3 _start = start;
        private Vector3 _end = end;

        public Vector3 Start
        {
            readonly get => _start;
            set => _start = value;
        }

        public Vector3 End
        {
            readonly get => _end;
            set => _end = value;
        }

        public readonly Vector3 Direction
            => (_end - _start).Normalized();

        public float Length
        {
            readonly get => Vector3.Distance(_start, _end);
            set => _end = _start + Vector3.Normalize(_end - _start) * value;
        }

        public readonly Vector3 ClosestPoint(Vector3 point)
        {
            Vector3 direction = _end - _start;
            float t = Vector3.Dot(point - _start, direction) / Vector3.Dot(direction, direction);
            return _start + t * direction;
        }

        public readonly float DistanceToPoint(Vector3 point)
            => Vector3.Cross(_end - _start, (point - _start)).Length() / (_end - _start).Length();

        public readonly float DistanceToSegment(Segment segment)
            => DistanceToSegment(segment.Start, segment.End);

        public readonly float DistanceToSegment(Vector3 start, Vector3 end)
        {
            Vector3 direction = end - start;
            Vector3 diff = start - _start;
            float a = Vector3.Dot(direction, direction);
            float b = Vector3.Dot(diff, direction);
            float c = Vector3.Dot(diff, diff);
            float d = a * c - b * b;
            if (d > 0.0f)
            {
                float t = Math.Max(0.0f, Math.Min(1.0f, (b / a)));
                Vector3 closest = start + t * direction;
                return (closest - _start).Length();
            }
            return (start - _start).Length();
        }

        /// <summary>
        /// Returns a colinear point the given distance from the start point of this segment.
        /// </summary>
        public readonly Vector3 PointAtLineDistance(float distance)
            => PointAtLineDistance(Start, End, distance);
        /// <summary>
        /// Returns a 
        /// </summary>
        /// <param name="startPoint"></param>
        /// <param name="endPoint"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        public static Vector3 PointAtLineDistance(Vector3 startPoint, Vector3 endPoint, float distance)
        {
            Vector3 diff = endPoint - startPoint;
            return startPoint + (diff * (distance / diff.Length()));
        }

        public readonly Segment TransformedBy(Matrix4x4 transform)
            => new(Vector3.Transform(_start, transform), Vector3.Transform(_end, transform));
    }
}