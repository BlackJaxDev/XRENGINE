using Extensions;
using System.Numerics;

namespace XREngine.Data.BSP
{
    public class BSPSphere : BSPShape
    {
        private readonly float _radius;
        private readonly int _segments;

        public BSPSphere(float radius, int segments)
        {
            _radius = radius;
            _segments = segments;
            GenerateMeshData();
        }

        protected override void GenerateVertices()
        {
            //Isophere vertices
            for (int i = 0; i < _segments; i++)
            {
                float lat = MathF.PI * (-0.5f + (i - 1) / (float)_segments);
                float z = MathF.Sin(lat) * _radius;
                float radiusAtLatitude = MathF.Cos(lat) * _radius;

                for (int j = 0; j < _segments; j++)
                {
                    float lon = 2 * MathF.PI * (j - 1) / _segments;
                    float x = MathF.Cos(lon) * radiusAtLatitude;
                    float y = MathF.Sin(lon) * radiusAtLatitude;

                    Vector3 vertex = new(x, y, z);
                    Vertices.Add(vertex);
                }
            }
        }

        protected override void GenerateIndices()
        {
            for (int i = 0; i < _segments; i++)
            {
                for (int j = 0; j < _segments; j++)
                {
                    int nextI = (i + 1) % _segments;
                    int nextJ = (j + 1) % _segments;

                    int a = i * _segments + j;
                    int b = nextI * _segments + j;
                    int c = nextI * _segments + nextJ;
                    int d = i * _segments + nextJ;

                    Indices.Add(a);
                    Indices.Add(b);
                    Indices.Add(c);

                    Indices.Add(a);
                    Indices.Add(c);
                    Indices.Add(d);
                }
            }
        }

        protected override void GenerateNormals()
        {
            for (int i = 0; i < Vertices.Count; i++)
                Normals.Add(Vertices[i].Normalized());
        }
    }
}