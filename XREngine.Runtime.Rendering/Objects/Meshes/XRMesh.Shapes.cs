using Extensions;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using static System.MathF;

namespace XREngine.Rendering
{
    public partial class XRMesh
    {
        public static class Shapes
        {
            public static VertexLineStrip CircleLineStrip(float radius, Vector3 normal, Vector3 center, int sides)
            {
                Vertex[] points = CirclePoints(radius, normal, center, sides);
                return new VertexLineStrip(true, points);
            }
            public static Vertex[] CirclePoints(float radius, Vector3 normal, Vector3 center, int sides)
            {
                if (sides < 3)
                    throw new Exception("A (very low res) circle needs at least 3 sides.");

                normal = normal.Normalized();

                Quaternion offset = XRMath.RotationBetweenVectors(Globals.Up, normal);

                Vertex[] points = new Vertex[sides];
                float angleInc = PI * 2.0f / sides;
                float rad = 0.0f;
                for (int i = 0; i < sides; ++i, rad += angleInc)
                {
                    Vector2 coord = new(MathF.Cos(rad), MathF.Sin(rad));
                    Vector3 v = new(coord.X, 0.0f, coord.Y);
                    points[i] = new(
                        center + Vector3.Transform(radius * v, offset),
                        normal,
                        coord * 0.5f + new Vector2(0.5f));
                }
                return points;
            }
            public static XRMesh SolidSphere(Vector3 center, float radius, uint precision)
            {
                float halfPI = PI * 0.5f;
                float invPrecision = 1.0f / precision;
                float twoPIThroughPrecision = PI * 2.0f * invPrecision;

                float theta1, theta2, theta3;
                Vector3 norm, pos;
                Vector2 uv;

                List<VertexTriangleStrip> strips = [];
                for (uint j = 0; j < precision * 0.5f; j++)
                {
                    theta1 = (j * twoPIThroughPrecision) - halfPI;
                    theta2 = ((j + 1) * twoPIThroughPrecision) - halfPI;

                    Vertex[] stripVertices = new Vertex[((int)precision + 1) * 2];
                    int x = 0;
                    for (uint i = 0; i <= precision; i++)
                    {
                        theta3 = i * twoPIThroughPrecision;

                        norm.X = -(float)(Cos(theta2) * Cos(theta3));
                        norm.Y = -(float)Sin(theta2);
                        norm.Z = -(float)(Cos(theta2) * Sin(theta3));
                        pos = center + radius * norm;
                        uv.X = i * invPrecision;
                        uv.Y = 2.0f * (j + 1) * invPrecision;

                        stripVertices[x++] = new Vertex(pos, norm, uv);

                        norm.X = -(float)(Cos(theta1) * Cos(theta3));
                        norm.Y = -(float)Sin(theta1);
                        norm.Z = -(float)(Cos(theta1) * Sin(theta3));
                        pos = center + radius * norm;
                        uv.X = i * invPrecision;
                        uv.Y = 2.0f * j * invPrecision;

                        stripVertices[x++] = new Vertex(pos, norm, uv);
                    }
                    strips.Add(new VertexTriangleStrip(stripVertices));
                }

                return Create(strips.SelectMany(x => x.ToTriangles()));
            }
            public static XRMesh WireframeSphere(Vector3 center, float radius, int pointCount)
            {
                VertexLineStrip d1 = CircleLineStrip(radius, Globals.Forward, center, pointCount);
                VertexLineStrip d2 = CircleLineStrip(radius, Globals.Up, center, pointCount);
                VertexLineStrip d3 = CircleLineStrip(radius, Globals.Right, center, pointCount);
                return Create(d1, d2, d3);
            }
            public static XRMesh SolidSphere(Vector3 center, float radius, int slices, int stacks)
            {
                List<Vertex> v = [];
                float twoPi = PI * 2.0f;
                for (int i = 0; i <= stacks; ++i)
                {
                    // V texture coordinate.
                    float V = i / (float)stacks;
                    float phi = V * PI;

                    for (int j = 0; j <= slices; ++j)
                    {
                        // U texture coordinate.
                        float U = j / (float)slices;
                        float theta = U * twoPi;

                        float X = (float)Cos(theta) * (float)Sin(phi);
                        float Y = (float)Cos(phi);
                        float Z = (float)Sin(theta) * (float)Sin(phi);

                        Vector3 normal = new(X, Y, Z);
                        v.Add(new Vertex(center + normal * radius, normal, new Vector2(U, V)));
                    }
                }
                List<VertexTriangle> triangles = [];
                for (int i = 0; i < slices * stacks + slices; ++i)
                {
                    triangles.Add(new VertexTriangle(v[i], v[i + slices + 1], v[i + slices]));
                    triangles.Add(new VertexTriangle(v[i + slices + 1], v[i], v[i + 1]));
                }
                return Create(triangles);
            }
            public static XRMesh WireframeCone(Vector3 center, Vector3 up, float height, float radius, int sides)
            {
                up = up.Normalized();

                VertexLine[] lines = new VertexLine[sides * 3];

                Vector3 topPoint = center + (up * (height / 2.0f));
                Vector3 bottomPoint = center - (up * (height / 2.0f));

                Vertex[] sidePoints = CirclePoints(radius, up, bottomPoint, sides);

                for (int i = 0, x = 0; i < sides; ++i)
                {
                    Vertex sidePoint = sidePoints[i];
                    lines[x++] = new VertexLine(bottomPoint, sidePoint.Position);
                    lines[x++] = new VertexLine(topPoint, sidePoint.Position);
                    lines[x++] = new VertexLine(sidePoints[i + 1 == sides ? 0 : i + 1], sidePoint);
                }

                return Create(lines);
            }
            public static XRMesh SolidCone(Vector3 center, Vector3 up, float height, float radius, int sides, bool closeBottom)
            {
                up = up.Normalized();

                List<VertexTriangle> tris = new((sides * 3) * (closeBottom ? 2 : 1));

                Vector3 topPoint = center + (up * (height / 2.0f));
                Vector3 bottomPoint = center - (up * (height / 2.0f));

                Vertex[] sidePoints = CirclePoints(radius, up, bottomPoint, sides);

                Vector3 diff, normal;
                Vertex topVertex;

                for (int i = 0; i < sides; ++i)
                {
                    diff = (topPoint - sidePoints[i].Position).Normalized();
                    normal = Vector3.Cross(diff, Vector3.Cross(up, diff));
                    sidePoints[i].Normal = normal;

                    topVertex = new Vertex(topPoint, up, new Vector2(0.5f));
                    tris.Add(new VertexTriangle(
                        sidePoints[i + 1 == sides ? 0 : i + 1],
                        sidePoints[i],
                        topVertex));

                    if (tris.Count - 2 >= 0)
                    {
                        VertexTriangle lastTri = tris[^2];
                        Vector3 startNormal = lastTri.Vertex0.Normal ?? Vector3.Zero;
                        lastTri.Vertex0.Normal = (startNormal + normal).Normalized();
                    }
                }

                if (closeBottom)
                {
                    List<Vertex> list = new(sidePoints.Length + 1)
                    {
                        new(bottomPoint, -up, new Vector2(0.5f))
                    };
                    for (int i = 0; i < sidePoints.Length; ++i)
                    {
                        Vertex v2 = sidePoints[i].HardCopy();
                        v2.Normal = -up;
                        list.Add(v2);
                    }
                    Vertex v3 = sidePoints[0].HardCopy();
                    v3.Normal = -up;
                    list.Add(v3);
                    tris.AddRange(new VertexTriangleFan(list).ToTriangles());
                }

                return Create(tris);
            }
            public static XRMesh WireframeCapsule(Vector3 center, Vector3 upAxis, float radius, float halfHeight, int pointCountHalfCircle)
            {
                upAxis = upAxis.Normalized();

                Vector3 topPoint = center + upAxis * halfHeight;
                Vector3 bottomPoint = center - upAxis * halfHeight;

                Vector3 forwardNormal, rightNormal;
                if (upAxis == Globals.Right)
                {
                    forwardNormal = Globals.Forward;
                    rightNormal = Globals.Up;
                }
                else if (upAxis == -Globals.Right)
                {
                    forwardNormal = Globals.Forward;
                    rightNormal = -Globals.Up;
                }
                else if (upAxis == Globals.Forward)
                {
                    forwardNormal = Globals.Up;
                    rightNormal = Globals.Right;
                }
                else if (upAxis == -Globals.Forward)
                {
                    forwardNormal = -Globals.Up;
                    rightNormal = Globals.Right;
                }
                else
                {
                    forwardNormal = Vector3.Cross(Globals.Right, upAxis);
                    rightNormal = Vector3.Cross(Globals.Forward, upAxis);
                }

                int pts = pointCountHalfCircle + 1;
                Vertex[] topPoints1 = new Vertex[pts], topPoints2 = new Vertex[pts];
                Vertex[] botPoints1 = new Vertex[pts], botPoints2 = new Vertex[pts];

                float angleInc = PI / pointCountHalfCircle;
                float angle = 0.0f;
                for (int i = 0; i < pts; ++i, angle += angleInc)
                {
                    Vector3 v1 = new(Cos(angle), Sin(angle), 0.0f);
                    Vector3 v2 = new(0.0f, Sin(angle), Cos(angle));
                    topPoints1[i] = new Vertex(topPoint + radius * v1);
                    topPoints2[i] = new Vertex(topPoint + radius * v2);
                    botPoints1[i] = new Vertex(bottomPoint - radius * v1);
                    botPoints2[i] = new Vertex(bottomPoint - radius * v2);
                }

                VertexLineStrip topCircleUp = CircleLineStrip(radius, upAxis, topPoint, pointCountHalfCircle * 2);
                VertexLineStrip topHalfCircleToward = new(false, topPoints1);
                VertexLineStrip topHalfCircleRight = new(false, topPoints2);

                VertexLineStrip bottomCircleDown = CircleLineStrip(radius, -upAxis, bottomPoint, pointCountHalfCircle * 2);
                VertexLineStrip bottomHalfCircleAway = new(false, botPoints1);
                VertexLineStrip bottomHalfCircleRight = new(false, botPoints2);

                VertexLineStrip right = new(false,
                    new Vertex(bottomPoint + rightNormal * radius),
                    new Vertex(topPoint + rightNormal * radius));
                VertexLineStrip left = new(false,
                    new Vertex(bottomPoint - rightNormal * radius),
                    new Vertex(topPoint - rightNormal * radius));
                VertexLineStrip front = new(false,
                    new Vertex(bottomPoint + forwardNormal * radius),
                    new Vertex(topPoint + forwardNormal * radius));
                VertexLineStrip back = new(false,
                    new Vertex(bottomPoint - forwardNormal * radius),
                    new Vertex(topPoint - forwardNormal * radius));

                return Create(
                    topCircleUp, topHalfCircleToward, topHalfCircleRight,
                    bottomCircleDown, bottomHalfCircleAway, bottomHalfCircleRight,
                    right, left, front, back);
            }
            public static void SolidCapsuleParts(
                Vector3 center, Vector3 upAxis, float radius, float halfHeight, int pointCountHalfCircle,
                out XRMesh cylinder, out XRMesh topSphereHalf, out XRMesh bottomSphereHalf)
            {
                upAxis = upAxis.Normalized();

                Vector3 topPoint = center + upAxis * halfHeight;
                Vector3 bottomPoint = center - upAxis * halfHeight;

                VertexTriangle[] cylinderTriangles = new VertexTriangle[pointCountHalfCircle * 2];
                VertexTriangle[] topSphereTriangles = new VertexTriangle[pointCountHalfCircle * pointCountHalfCircle];
                VertexTriangle[] bottomSphereTriangles = new VertexTriangle[pointCountHalfCircle * pointCountHalfCircle];

                Quaternion offset = XRMath.RotationBetweenVectors(Globals.Up, upAxis);

                float angleInc = PI / pointCountHalfCircle;
                float rad = 0.0f;

                //Create the cylinder
                for (int i = 0, x = 0; i < pointCountHalfCircle; ++i, rad += angleInc)
                {
                    Vector3 v1 = new(Cos(rad), Sin(rad), 0.0f);
                    Vector3 v2 = new(0.0f, Sin(rad), Cos(rad));
                    Vertex top1 = new(Vector3.Transform(radius * v1, offset) + topPoint);
                    Vertex top2 = new(Vector3.Transform(radius * v2, offset) + topPoint);
                    Vertex bot1 = new(-Vector3.Transform(radius * v1, offset) + bottomPoint);
                    Vertex bot2 = new(-Vector3.Transform(radius * v2, offset) + bottomPoint);

                    cylinderTriangles[x++] = new VertexTriangle(top1, top2, bot1);
                    cylinderTriangles[x++] = new VertexTriangle(top2, bot2, bot1);
                }

                //Create the top sphere
                for (int i = 0, x = 0; i < pointCountHalfCircle; ++i, rad += angleInc)
                {
                    Vector3 v1 = new(Cos(rad), Sin(rad), 0.0f);
                    Vector3 v2 = new(0.0f, Sin(rad), Cos(rad));
                    Vertex top1 = new(Vector3.Transform(radius * v1, offset) + topPoint);
                    Vertex top2 = new(Vector3.Transform(radius * v2, offset) + topPoint);

                    for (int j = 0; j < pointCountHalfCircle; ++j)
                    {
                        float theta = j * PI / pointCountHalfCircle;
                        Vector3 normal = new(Cos(theta) * Cos(rad), Sin(theta), Cos(theta) * Sin(rad));
                        Vertex v = new(top1.Position, normal);
                        topSphereTriangles[x++] = new VertexTriangle(top1, top2, v);
                        top1 = top2;
                        top2 = v;
                    }
                }

                //Create the bottom sphere
                for (int i = 0, x = 0; i < pointCountHalfCircle; ++i, rad += angleInc)
                {
                    Vector3 v1 = new(Cos(rad), Sin(rad), 0.0f);
                    Vector3 v2 = new(0.0f, Sin(rad), Cos(rad));
                    Vertex bot1 = new(-Vector3.Transform(radius * v1, offset) + bottomPoint);
                    Vertex bot2 = new(-Vector3.Transform(radius * v2, offset) + bottomPoint);

                    for (int j = 0; j < pointCountHalfCircle; ++j)
                    {
                        float theta = j * PI / pointCountHalfCircle;
                        Vector3 normal = new(Cos(theta) * Cos(rad), Sin(theta), Cos(theta) * Sin(rad));
                        Vertex v = new(bot1.Position, normal);
                        bottomSphereTriangles[x++] = new VertexTriangle(bot1, bot2, v);
                        bot1 = bot2;
                        bot2 = v;
                    }
                }

                cylinder = Create(cylinderTriangles);
                topSphereHalf = Create(topSphereTriangles);
                bottomSphereHalf = Create(bottomSphereTriangles);
            }
            public static void WireframeCapsuleParts(
                Vector3 center, Vector3 upAxis, float radius, float halfHeight, int pointCountHalfCircle,
                out XRMesh cylinder, out XRMesh topSphereHalf, out XRMesh bottomSphereHalf)
            {
                upAxis = upAxis.Normalized();

                Vector3 topPoint = center + upAxis * halfHeight;
                Vector3 bottomPoint = center - upAxis * halfHeight;

                Vector3 forwardNormal, rightNormal;
                if (upAxis == Globals.Right)
                {
                    forwardNormal = Globals.Forward;
                    rightNormal = Globals.Up;
                }
                else if (upAxis == -Globals.Right)
                {
                    forwardNormal = Globals.Forward;
                    rightNormal = -Globals.Up;
                }
                else if (upAxis == Globals.Forward)
                {
                    forwardNormal = Globals.Up;
                    rightNormal = Globals.Right;
                }
                else if (upAxis == -Globals.Forward)
                {
                    forwardNormal = -Globals.Up;
                    rightNormal = Globals.Right;
                }
                else
                {
                    forwardNormal = Vector3.Cross(Globals.Right, upAxis);
                    rightNormal = Vector3.Cross(Globals.Forward, upAxis);
                }

                int pts = pointCountHalfCircle + 1;

                Vertex[] topPoints1 = new Vertex[pts], topPoints2 = new Vertex[pts];
                Vertex[] botPoints1 = new Vertex[pts], botPoints2 = new Vertex[pts];

                Quaternion offset = XRMath.RotationBetweenVectors(Globals.Up, upAxis);

                float angleInc = PI / pointCountHalfCircle;
                float rad = 0.0f;
                for (int i = 0; i < pts; ++i, rad += angleInc)
                {
                    Vector3 v1 = new(Cos(rad), Sin(rad), 0.0f);
                    Vector3 v2 = new(0.0f, Sin(rad), Cos(rad));
                    topPoints1[i] = new Vertex(Vector3.Transform(radius * v1, offset));
                    topPoints2[i] = new Vertex(Vector3.Transform(radius * v2, offset));
                    botPoints1[i] = new Vertex(-Vector3.Transform(radius * v1, offset));
                    botPoints2[i] = new Vertex(-Vector3.Transform(radius * v2, offset));
                }

                VertexLineStrip topCircleUp = CircleLineStrip(radius, upAxis, topPoint, pointCountHalfCircle * 2);
                VertexLineStrip topHalfCircleToward = new(false, topPoints1);
                VertexLineStrip topHalfCircleRight = new(false, topPoints2);

                VertexLineStrip bottomCircleDown = CircleLineStrip(radius, -upAxis, bottomPoint, pointCountHalfCircle * 2);
                VertexLineStrip bottomHalfCircleAway = new(false, botPoints1);
                VertexLineStrip bottomHalfCircleRight = new(false, botPoints2);

                VertexLineStrip right = new(false,
                    new Vertex(bottomPoint + rightNormal * radius),
                    new Vertex(topPoint + rightNormal * radius));
                VertexLineStrip left = new(false,
                    new Vertex(bottomPoint - rightNormal * radius),
                    new Vertex(topPoint - rightNormal * radius));
                VertexLineStrip front = new(false,
                    new Vertex(bottomPoint + forwardNormal * radius),
                    new Vertex(topPoint + forwardNormal * radius));
                VertexLineStrip back = new(false,
                    new Vertex(bottomPoint - forwardNormal * radius),
                    new Vertex(topPoint - forwardNormal * radius));

                cylinder = Create(
                    topCircleUp, bottomCircleDown, right, left, front, back);

                topSphereHalf = Create(
                    topHalfCircleToward, topHalfCircleRight);

                bottomSphereHalf = Create(
                    bottomHalfCircleAway, bottomHalfCircleRight);
            }

            public static XRMesh WireframeBox(Vector3 min, Vector3 max)
            {
                VertexLine
                    topFront, topRight, topBack, topLeft,
                    frontLeft, frontRight, backLeft, backRight,
                    bottomFront, bottomRight, bottomBack, bottomLeft;

                AABB.GetCorners(min, max,
                    out Vector3 TBL,
                    out Vector3 TBR,
                    out Vector3 TFL,
                    out Vector3 TFR,
                    out Vector3 BBL,
                    out Vector3 BBR,
                    out Vector3 BFL,
                    out Vector3 BFR);

                topFront = new VertexLine(new Vertex(TFL), new Vertex(TFR));
                topBack = new VertexLine(new Vertex(TBL), new Vertex(TBR));
                topLeft = new VertexLine(new Vertex(TFL), new Vertex(TBL));
                topRight = new VertexLine(new Vertex(TFR), new Vertex(TBR));

                bottomFront = new VertexLine(new Vertex(BFL), new Vertex(BFR));
                bottomBack = new VertexLine(new Vertex(BBL), new Vertex(BBR));
                bottomLeft = new VertexLine(new Vertex(BFL), new Vertex(BBL));
                bottomRight = new VertexLine(new Vertex(BFR), new Vertex(BBR));

                frontLeft = new VertexLine(new Vertex(TFL), new Vertex(BFL));
                frontRight = new VertexLine(new Vertex(TFR), new Vertex(BFR));
                backLeft = new VertexLine(new Vertex(TBL), new Vertex(BBL));
                backRight = new VertexLine(new Vertex(TBR), new Vertex(BBR));

                return Create(
                    topFront, topRight, topBack, topLeft,
                    frontLeft, frontRight, backLeft, backRight,
                    bottomFront, bottomRight, bottomBack, bottomLeft);
            }
            public enum ECubemapTextureUVs
            {
                /// <summary>
                /// Specifies that each quad should have normal corner-to-corner UVs.
                /// </summary>
                None,
                /// <summary>
                /// Specifies that each quad should be mapped for a cubemap texture that has a larger width (4x3).
                /// </summary>
                WidthLarger,
                /// <summary>
                /// Specifies that each quad should be mapped for a cubemap texture that has a larger height (3x4).
                /// </summary>
                HeightLarger,
            }
            /// <summary>
            /// Generates a bounding box mesh.
            /// </summary>
            /// <param name="min">Minimum axis-aligned bound of the box.</param>
            /// <param name="max">Maximum axis-aligned bound of the box.</param>
            /// <param name="inwardFacing">If the faces' fronts should face inward instead of outward.</param>
            /// <param name="cubemapUVs">If each quad should use UVs for </param>
            /// <returns></returns>
            public static XRMesh SolidBox(Vector3 min, Vector3 max, bool inwardFacing = false, ECubemapTextureUVs cubemapUVs = ECubemapTextureUVs.None, float bias = 0.0f)
            {
                VertexQuad left, right, top, bottom, front, back;

                AABB.GetCorners(min, max,
                    out Vector3 TBL,
                    out Vector3 TBR,
                    out Vector3 TFL,
                    out Vector3 TFR,
                    out Vector3 BBL,
                    out Vector3 BBR,
                    out Vector3 BFL,
                    out Vector3 BFR);

                Vector3 rightNormal = inwardFacing ? Globals.Left : Globals.Right;
                Vector3 frontNormal = inwardFacing ? Globals.Forward : Globals.Backward;
                Vector3 topNormal = inwardFacing ? Globals.Down : Globals.Up;
                Vector3 leftNormal = -rightNormal;
                Vector3 backNormal = -frontNormal;
                Vector3 bottomNormal = -topNormal;

                if (cubemapUVs == ECubemapTextureUVs.None)
                {
                    left = inwardFacing ?
                        VertexQuad.Make(BFL, BBL, TBL, TFL, leftNormal) :
                        VertexQuad.Make(BBL, BFL, TFL, TBL, leftNormal);
                    right = inwardFacing ?
                        VertexQuad.Make(BBR, BFR, TFR, TBR, rightNormal) :
                        VertexQuad.Make(BFR, BBR, TBR, TFR, rightNormal);
                    top = inwardFacing ?
                        VertexQuad.Make(TBL, TBR, TFR, TFL, topNormal) :
                        VertexQuad.Make(TFL, TFR, TBR, TBL, topNormal);
                    bottom = inwardFacing ?
                        VertexQuad.Make(BFL, BFR, BBR, BBL, bottomNormal) :
                        VertexQuad.Make(BBL, BBR, BFR, BFL, bottomNormal);
                    front = inwardFacing ?
                        VertexQuad.Make(BFR, BFL, TFL, TFR, frontNormal) :
                        VertexQuad.Make(BFL, BFR, TFR, TFL, frontNormal);
                    back = inwardFacing ?
                        VertexQuad.Make(BBL, BBR, TBR, TBL, backNormal) :
                        VertexQuad.Make(BBR, BBL, TBL, TBR, backNormal);
                }
                else
                {
                    bool widthLarger = cubemapUVs == ECubemapTextureUVs.WidthLarger;
                    left = inwardFacing ?
                        VertexQuad.Make(BFL, BBL, TBL, TFL, leftNormal, ECubemapFace.NegX, widthLarger, bias) :
                        VertexQuad.Make(BBL, BFL, TFL, TBL, leftNormal, ECubemapFace.NegX, widthLarger, bias);

                    right = inwardFacing ?
                        VertexQuad.Make(BBR, BFR, TFR, TBR, rightNormal, ECubemapFace.PosX, widthLarger, bias) :
                        VertexQuad.Make(BFR, BBR, TBR, TFR, rightNormal, ECubemapFace.PosX, widthLarger, bias);

                    top = inwardFacing ?
                        VertexQuad.Make(TBL, TBR, TFR, TFL, topNormal, ECubemapFace.PosY, widthLarger, bias) :
                        VertexQuad.Make(TFL, TFR, TBR, TBL, topNormal, ECubemapFace.PosY, widthLarger, bias);

                    bottom = inwardFacing ?
                        VertexQuad.Make(BFL, BFR, BBR, BBL, bottomNormal, ECubemapFace.NegY, widthLarger, bias) :
                        VertexQuad.Make(BBL, BBR, BFR, BFL, bottomNormal, ECubemapFace.NegY, widthLarger, bias);

                    front = inwardFacing ?
                        VertexQuad.Make(BFR, BFL, TFL, TFR, frontNormal, ECubemapFace.PosZ, widthLarger, bias) :
                        VertexQuad.Make(BFL, BFR, TFR, TFL, frontNormal, ECubemapFace.PosZ, widthLarger, bias);

                    back = inwardFacing ?
                        VertexQuad.Make(BBL, BBR, TBR, TBL, backNormal, ECubemapFace.NegZ, widthLarger, bias) :
                        VertexQuad.Make(BBR, BBL, TBL, TBR, backNormal, ECubemapFace.NegZ, widthLarger, bias);
                }

                return Create(left, right, top, bottom, front, back);
            }

            /// <summary>
            /// Creates a mesh representing this bounding box.
            /// </summary>
            /// <param name="includeTranslation">If true, makes mesh with minimum and maximum coordinates.
            /// If false, makes the mesh about the origin.</param>
            public static XRMesh GetSolidAABB(AABB aabb, bool includeTranslation)
                => includeTranslation
                    ? SolidBox(aabb.Min, aabb.Max)
                    : SolidBox(-aabb.HalfExtents, aabb.HalfExtents);

            /// <summary>
            /// Creates a mesh representing this bounding box.
            /// </summary>
            /// <param name="includeTranslation">If true, makes mesh with minimum and maximum coordinates.
            /// If false, makes the mesh about the origin.</param>
            public static XRMesh GetWireframeAABB(AABB aabb, bool includeTranslation)
                => includeTranslation
                    ? WireframeBox(aabb.Min, aabb.Max)
                    : WireframeBox(-aabb.HalfExtents, aabb.HalfExtents);

            public static XRMesh WireframePlane(Vector3 position, Vector3 normal, float xExtent, float yExtent)
            {
                PlaneHelper.GetPlanePoints(
                    position, normal, xExtent, yExtent,
                    out Vector3 bottomLeft, out Vector3 bottomRight, out Vector3 topLeft, out Vector3 topRight);

                return Create(new VertexLineStrip(true, bottomLeft, bottomRight, topRight, topLeft));
            }

            public static XRMesh SolidPlane(Vector3 position, Vector3 normal, float xExtent, float yExtent)
            {
                PlaneHelper.GetPlanePoints(
                    position, normal, xExtent, yExtent,
                    out Vector3 bottomLeft, out Vector3 bottomRight, out Vector3 topLeft, out Vector3 topRight);

                return Create(VertexQuad.Make(bottomLeft, bottomRight, topRight, topLeft, normal));
            }

            public static XRMesh SolidCircle(float radius, Vector3 normal, Vector3 center, int sides)
            {
                if (sides < 3)
                    throw new Exception("A (very low res) circle needs at least 3 sides.");

                normal = normal.Normalized();

                List<Vertex> points = [.. CirclePoints(radius, normal, center, sides)];
                points.Insert(0, new Vertex(center, normal, new Vector2(0.5f)));
                VertexTriangleFan fan = new([.. points]);

                return Create(fan);
            }

            public static XRMesh WireframeCircle(float radius, Vector3 normal, Vector3 center, int sides)
                => Create(CircleLineStrip(radius, normal, center, sides));

            public static XRMesh SolidFrustum(Frustum frustum)
            {
                Vector3 ltf = frustum.LeftTopFar;
                Vector3 lbf = frustum.LeftBottomFar;
                Vector3 rtf = frustum.RightTopFar;
                Vector3 rbf = frustum.RightBottomFar;

                Vector3 ltn = frustum.LeftTopNear;
                Vector3 lbn = frustum.LeftBottomNear;
                Vector3 rtn = frustum.RightTopNear;
                Vector3 rbn = frustum.RightBottomNear;

                VertexTriangle[] triangles =
                [
                    new(ltf, rtf, lbf),
                    new(rtf, rbf, lbf),
                    new(ltn, lbn, rbn),
                    new(ltn, rbn, rtn),
                    new(ltf, ltn, rtf),
                    new(ltn, rtn, rtf),
                    new(lbf, rbf, lbn),
                    new(rbf, rbn, lbn),
                    new(ltf, lbf, ltn),
                    new(lbf, lbn, ltn),
                    new(rtf, rtn, rbf),
                    new(rbf, rtn, rbn),
                ];

                return Create(triangles);
            }

            public static XRMesh WireframeFrustum(Frustum frustum)
            {
                Vector3 ltf = frustum.LeftTopFar;
                Vector3 lbf = frustum.LeftBottomFar;
                Vector3 rtf = frustum.RightTopFar;
                Vector3 rbf = frustum.RightBottomFar;

                Vector3 ltn = frustum.LeftTopNear;
                Vector3 lbn = frustum.LeftBottomNear;
                Vector3 rtn = frustum.RightTopNear;
                Vector3 rbn = frustum.RightBottomNear;

                VertexLine[] lines =
                [
                    new(ltf, rtf),
                    new(rtf, rbf),
                    new(rbf, lbf),
                    new(lbf, ltf),
                    new(ltn, lbn),
                    new(lbn, rbn),
                    new(rbn, rtn),
                    new(rtn, ltn),
                    new(ltf, ltn),
                    new(rtf, rtn),
                    new(rbf, rbn),
                    new(lbf, lbn),
                ];

                return Create(lines);
            }

            public static XRMesh? FromVolume(IVolume volume, bool wireframe) 
                => volume switch
                {
                    AABB box => wireframe
                        ? GetWireframeAABB(box, true)
                        : GetSolidAABB(box, true),
                    Sphere sphere => wireframe
                        ? WireframeSphere(sphere.Center, sphere.Radius, 32)
                        : SolidSphere(sphere.Center, sphere.Radius, 32),
                    Cone cone => wireframe
                        ? WireframeCone(cone.Center, cone.Up, cone.Height, cone.Radius, 32)
                        : SolidCone(cone.Center, cone.Up, cone.Height, cone.Radius, 32, false),
                    Capsule capsule => wireframe
                        ? WireframeCapsule(capsule.Center, capsule.UpAxis, capsule.Radius, capsule.HalfHeight, 32)
                        : SolidCone(capsule.Center, capsule.UpAxis, capsule.HalfHeight, capsule.Radius, 32, true),
                    Frustum frustum => wireframe
                        ? WireframeFrustum(frustum)
                        : SolidFrustum(frustum),
                    _ => null,
                };
        }
    }
}
