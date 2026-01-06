using XREngine.Data.Geometry;

namespace XREngine.Data.BSP
{
    public static class BSPBoolean
    {
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