using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Data.BSP
{
    public static class BSPShapeExtensions
    {
        public static BSPNode ToBSPNode(this BSPShape shape)
        {
            List<Triangle> triangles = [];
            for (int i = 0; i < shape.Indices.Count; i += 3)
            {
                Vector3 a = shape.Vertices[shape.Indices[i]];
                Vector3 b = shape.Vertices[shape.Indices[i + 1]];
                Vector3 c = shape.Vertices[shape.Indices[i + 2]];
                triangles.Add(new Triangle(a, b, c));
            }

            BSPNode node = new();
            node.Build(triangles);
            return node;
        }

        public static void FromBSPNode(this BSPShape shape, BSPNode node)
        {
            List<Triangle> triangles = [];
            node.GetAllTriangles(triangles);

            shape.Vertices.Clear();
            shape.Indices.Clear();
            shape.Normals.Clear();

            foreach (Triangle triangle in triangles)
            {
                int aIndex = shape.Vertices.Count;
                int bIndex = shape.Vertices.Count + 1;
                int cIndex = shape.Vertices.Count + 2;

                shape.Vertices.Add(triangle.A);
                shape.Vertices.Add(triangle.B);
                shape.Vertices.Add(triangle.C);

                shape.Indices.Add(aIndex);
                shape.Indices.Add(bIndex);
                shape.Indices.Add(cIndex);

                Vector3 normal = triangle.GetNormal();
                shape.Normals.Add(normal);
                shape.Normals.Add(normal);
                shape.Normals.Add(normal);
            }
        }

        // ────────── Boolean operations (merged from BSPBoolean) ──────────

        public static List<Triangle> Union(BSPNode a, BSPNode b)
        {
            a = a.Clone();
            b = b.Clone();

            a.ClipTo(b);
            b.ClipTo(a);
            b.Invert();
            b.ClipTo(a);
            b.Invert();

            List<Triangle> bTriangles = [];
            b.GetAllTriangles(bTriangles);
            a.Build(bTriangles);

            List<Triangle> result = [];
            a.GetAllTriangles(result);
            return result;
        }

        public static List<Triangle> Intersect(BSPNode a, BSPNode b)
        {
            a = a.Clone();
            b = b.Clone();

            a.Invert();
            b.ClipTo(a);
            b.Invert();
            a.ClipTo(b);
            b.ClipTo(a);
            List<Triangle> bTriangles = [];
            b.GetAllTriangles(bTriangles);
            a.Build(bTriangles);
            a.Invert();

            List<Triangle> result = [];
            a.GetAllTriangles(result);
            return result;
        }

        public static List<Triangle> Subtract(BSPNode a, BSPNode b)
        {
            a = a.Clone();
            b = b.Clone();

            a.Invert();
            a.ClipTo(b);
            b.ClipTo(a);
            b.Invert();
            b.ClipTo(a);
            b.Invert();
            List<Triangle> bTriangles = [];
            b.GetAllTriangles(bTriangles);
            a.Build(bTriangles);
            a.Invert();

            List<Triangle> result = [];
            a.GetAllTriangles(result);
            return result;
        }

        public static List<Triangle> XOR(BSPNode a, BSPNode b)
            => Subtract(new(Union(a, b)), new(Intersect(a, b)));
    }
}