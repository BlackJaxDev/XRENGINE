using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Data.BSP
{
    public class BSPNode
    {
        private const float Epsilon = 0.00001f;

        public System.Numerics.Plane? Plane;
        public BSPNode? Front;
        public BSPNode? Back;
        public List<Triangle> Triangles;

        public BSPNode()
            => Triangles = [];

        public BSPNode(List<Triangle> triangles)
            => Triangles = triangles;

        public void Build(List<Triangle> triangles)
        {
            if (triangles.Count == 0)
                return;

            Plane ??= triangles[0].GetPlane();

            List<Triangle> front = [];
            List<Triangle> back = [];

            foreach (Triangle triangle in triangles)
                SplitTriangle(Plane.Value, triangle, Triangles, front, back);
            
            if (front.Count > 0)
            {
                Front ??= new BSPNode();
                Front.Build(front);
            }

            if (back.Count > 0)
            {
                Back ??= new BSPNode();
                Back.Build(back);
            }
        }

        private void SplitTriangle(System.Numerics.Plane plane, Triangle triangle, List<Triangle>? coplanar, List<Triangle>? front, List<Triangle>? back)
        {
            float da = SignedDistanceToPlane(plane, triangle.A);
            float db = SignedDistanceToPlane(plane, triangle.B);
            float dc = SignedDistanceToPlane(plane, triangle.C);

            int sa = ClassifyDistance(da);
            int sb = ClassifyDistance(db);
            int sc = ClassifyDistance(dc);

            // Coplanar
            if (sa == 0 && sb == 0 && sc == 0)
            {
                if (coplanar != null)
                {
                    coplanar.Add(triangle);
                }
                else
                {
                    // Clipping context: treat coplanar tris as front/back based on orientation.
                    // This matches common BSP/CSG behavior (coplanars facing opposite are "inside").
                    float alignment = Vector3.Dot(plane.Normal, triangle.GetNormal());
                    if (alignment >= 0.0f)
                        front?.Add(triangle);
                    else
                        back?.Add(triangle);
                }
                return;
            }

            // Entirely on one side
            if (sa >= 0 && sb >= 0 && sc >= 0)
            {
                front?.Add(triangle);
                return;
            }
            if (sa <= 0 && sb <= 0 && sc <= 0)
            {
                back?.Add(triangle);
                return;
            }

            // Spanning: split triangle into front/back polygons, then triangulate
            SpanSplitTriangle(plane, triangle, front, back);
        }

        private static float SignedDistanceToPlane(System.Numerics.Plane plane, Vector3 point)
            => Vector3.Dot(plane.Normal, point) + plane.D;

        private static int ClassifyDistance(float d)
        {
            if (d > Epsilon) return 1;
            if (d < -Epsilon) return -1;
            return 0;
        }

        private static void SpanSplitTriangle(System.Numerics.Plane plane, Triangle triangle, List<Triangle>? front, List<Triangle>? back)
        {
            SpanSplitTriangle(plane, triangle.A, triangle.B, triangle.C, front, back);
        }

        private static void SpanSplitTriangle(System.Numerics.Plane plane, Vector3 a, Vector3 b, Vector3 c, List<Triangle>? front, List<Triangle>? back)
        {
            // Sutherland–Hodgman style clipping against a plane.
            Vector3[] v = [a, b, c];
            float[] d =
            [
                SignedDistanceToPlane(plane, a),
                SignedDistanceToPlane(plane, b),
                SignedDistanceToPlane(plane, c)
            ];

            List<Vector3> f = [];
            List<Vector3> bk = [];

            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;

                Vector3 vi = v[i];
                Vector3 vj = v[j];
                float di = d[i];
                float dj = d[j];

                int si = ClassifyDistance(di);
                int sj = ClassifyDistance(dj);

                // keep vertex on front
                if (si >= 0)
                    f.Add(vi);
                // keep vertex on back
                if (si <= 0)
                    bk.Add(vi);

                // edge crosses plane?
                if ((si > 0 && sj < 0) || (si < 0 && sj > 0))
                {
                    float t = di / (di - dj); // safe: di and dj have opposite signs
                    Vector3 p = vi + (vj - vi) * t;
                    f.Add(p);
                    bk.Add(p);
                }
            }

            TriangulateFan(f, front);
            TriangulateFan(bk, back);
        }

        private static void TriangulateFan(List<Vector3> polygon, List<Triangle>? output)
        {
            if (output == null)
                return;

            if (polygon.Count < 3)
                return;

            Vector3 p0 = polygon[0];
            for (int i = 1; i < polygon.Count - 1; i++)
                output.Add(new Triangle(p0, polygon[i], polygon[i + 1]));
        }

        public void Invert()
        {
            Plane = Flip(Plane);
            Triangles.ForEach(t => t.Flip());

            Front?.Invert();
            Back?.Invert();

            (Back, Front) = (Front, Back);
        }

        private static System.Numerics.Plane? Flip(System.Numerics.Plane? plane)
        {
            if (plane is null)
                return null;

            return new System.Numerics.Plane(-plane.Value.Normal, -plane.Value.D);
        }

        public void ClipTo(BSPNode node)
        {
            if (Triangles.Count == 0)
                return;

            Triangles = node.ClipTriangles(Triangles);
            Front?.ClipTo(node);
            Back?.ClipTo(node);
        }

        public List<Triangle> ClipTriangles(List<Triangle> triangles)
        {
            if (Plane == null)
                return new List<Triangle>(triangles);

            List<Triangle> front = [];
            List<Triangle> back = [];

            foreach (Triangle triangle in triangles)
                SplitTriangle(Plane.Value, triangle, null, front, back);
            
            if (Front != null)
                front = Front.ClipTriangles(front);

            // If there's no back child, back-side geometry is considered "inside" and discarded.
            if (Back != null)
                back = Back.ClipTriangles(back);
            else
                back.Clear();

            front.AddRange(back);
            return front;
        }

        public void GetAllTriangles(List<Triangle> triangles)
        {
            triangles.AddRange(Triangles);
            Front?.GetAllTriangles(triangles);
            Back?.GetAllTriangles(triangles);
        }

        public BSPNode Clone()
        {
            BSPNode cloneNode = new();

            if (Plane != null)
                cloneNode.Plane = new System.Numerics.Plane(Plane.Value.Normal, Plane.Value.D);
            
            if (Front != null)
                cloneNode.Front = Front.Clone();
            
            if (Back != null)
                cloneNode.Back = Back.Clone();
            
            foreach (Triangle triangle in Triangles)
                cloneNode.Triangles.Add(new Triangle(triangle.A, triangle.B, triangle.C));
            
            return cloneNode;
        }
    }
}