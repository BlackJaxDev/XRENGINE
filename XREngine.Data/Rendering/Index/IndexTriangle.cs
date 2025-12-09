using Extensions;
using YamlDotNet.Serialization;

namespace XREngine.Data.Rendering
{
    public class IndexTriangle : IndexPolygon
    {
        [YamlIgnore]
        public override FaceType Type => FaceType.Triangles;

        [YamlIgnore]
        public int Point0 => _points.TryGet(0, out int v) ? v : -1;
        [YamlIgnore]
        public int Point1 => _points.TryGet(1, out int v) ? v : -1;
        [YamlIgnore]
        public int Point2 => _points.TryGet(2, out int v) ? v : -1;

        public IndexTriangle() { }
        /// <summary>
        /// Counter-Clockwise winding
        ///     2
        ///    / \
        ///   /   \
        ///  0-----1
        /// </summary>
        public IndexTriangle(int point0, int point1, int point2)
        {
            _points.Add(point0);
            _points.Add(point1);
            _points.Add(point2);
        }

        public override List<IndexTriangle> ToTriangles()
            => [this];

        public override bool Equals(object? obj)
            => obj is IndexTriangle t && 
            t.Point0 == Point0 && 
            t.Point1 == Point1 && 
            t.Point2 == Point2;

        public override int GetHashCode()
            => base.GetHashCode();

        public string WriteToString()
            => $"{Point0} {Point1} {Point2}";

        private static readonly char[] Separator = [' '];
        public void ReadFromString(string str)
        {
            string[] indices = str.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            static int ToInt(string s) => int.TryParse(s, out int value) ? value : 0;
            _points =
            [
                indices.IndexInRangeArrayT(0) ? ToInt(indices[0]) : 0,
                indices.IndexInRangeArrayT(1) ? ToInt(indices[1]) : 0,
                indices.IndexInRangeArrayT(2) ? ToInt(indices[2]) : 0
            ];
        }
    }
}
