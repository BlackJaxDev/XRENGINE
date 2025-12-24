using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Rendering
{
    /// <summary>
    /// 3--2
    /// |\ |
    /// | \|
    /// 0--1
    /// </summary>
    public class VertexQuad(Vertex v0, Vertex v1, Vertex v2, Vertex v3) : VertexPolygon(v0, v1, v2, v3)
    {
        public Vertex Vertex0 => _vertices[0];
        public Vertex Vertex1 => _vertices[1];
        public Vertex Vertex2 => _vertices[2];
        public Vertex Vertex3 => _vertices[3];
        
        public override FaceType Type => FaceType.Quads;

        /// <summary>
        /// 3--2
        /// | /|
        /// |/ |
        /// 0--1
        /// Order: 012 023
        /// </summary>
        /// <returns></returns>
        public override VertexTriangle[] ToTriangles()
            => 
            [
                new(Vertex0.HardCopy(), Vertex1.HardCopy(), Vertex2.HardCopy()),
                new(Vertex0.HardCopy(), Vertex2.HardCopy(), Vertex3.HardCopy()),
            ];

        public static VertexQuad Make(
            Vector3 bottomLeft,
            Vector3 bottomRight,
            Vector3 topRight,
            Vector3 topLeft,
            bool addAutoNormal = false,
            bool flipVerticalUVCoord = false)
        {
            if (addAutoNormal)
            {
                Vector3 normal = XRMath.CalculateNormal(bottomLeft, bottomRight, topLeft);
                return Make(bottomLeft, bottomRight, topRight, topLeft, normal, flipVerticalUVCoord);
            }
            else
                return new VertexQuad(
                    new Vertex(bottomLeft,  new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                    new Vertex(bottomRight, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                    new Vertex(topRight,    new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
                    new Vertex(topLeft,     new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
        }

        public static VertexQuad Make(
            Vector3 bottomLeft, Vector3 bottomRight, Vector3 topRight, Vector3 topLeft, Vector3 normal, bool flipVerticalUVCoord = true)
            => new(
                new Vertex(bottomLeft,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                new Vertex(bottomRight, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                new Vertex(topRight,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
                new Vertex(topLeft,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
        
        /// <summary>
        /// Generates a quad using cubemap-cross texture coordinates.
        /// </summary>
        /// <param name="bottomLeft">The bottom left position of the quad.</param>
        /// <param name="bottomRight">The bottom right position of the quad.</param>
        /// <param name="topRight">The top right position of the quad.</param>
        /// <param name="topLeft">The top left position of the quad.</param>
        /// <param name="normal">The normal value for the quad.</param>
        /// <param name="cubeMapFace">The face to retrieve UV coordinates for.</param>
        /// <param name="widthLarger">If the cubemap cross texture has a width larger than height for a sideways-oriented cross.
        /// Assumes +Y and -Y are on the left half of the image (top of the cross is on the left side).</param>
        /// <param name="bias">How much to shrink the UV coordinates inward into the cross sections
        /// to avoid sampling from the empty parts of the image.
        /// A value of 0 means exact coordinates.</param>
        /// <param name="flipVerticalUVCoord">If true, flips the vertical coordinate upside-down. 
        /// This is true by default because OpenGL uses a top-left UV origin.</param>
        /// <returns>A <see cref="VertexQuad"/> object defining a quad.</returns>
        public static VertexQuad Make(
            Vector3 bottomLeft,
            Vector3 bottomRight,
            Vector3 topRight,
            Vector3 topLeft, 
            Vector3 normal,
            ECubemapFace cubeMapFace,
            bool widthLarger,
            float bias = 0.0f,
            bool flipVerticalUVCoord = true)
        {
            Vector2 
                bottomLeftUV, 
                bottomRightUV, 
                topRightUV, 
                topLeftUV;
            
            const float zero = 0.0f;
            const float fourth = 0.25f;
            const float half = 0.5f;
            const float threeFourths = 0.75f;
            const float third = (float)(1.0 / 3.0);
            const float twoThirds = (float)(2.0 / 3.0);
            const float one = 1.0f;

            switch (cubeMapFace)
            {
                case ECubemapFace.NegX:
                    if (widthLarger)
                    {
                        bottomLeftUV = new(zero, third);
                        bottomRightUV = new(fourth, third);
                        topRightUV = new(fourth, twoThirds);
                        topLeftUV = new(zero, twoThirds);
                    }
                    else
                    {
                        bottomLeftUV = new(zero, half);
                        bottomRightUV = new(third, half);
                        topRightUV = new(third, threeFourths);
                        topLeftUV = new(zero, threeFourths);
                    }
                    break;
                case ECubemapFace.PosX:
                    if (widthLarger)
                    {
                        bottomLeftUV = new(half, third);
                        bottomRightUV = new(threeFourths, third);
                        topRightUV = new(threeFourths, twoThirds);
                        topLeftUV = new(half, twoThirds);
                    }
                    else
                    {
                        bottomLeftUV = new(twoThirds, half);
                        bottomRightUV = new(one, half);
                        topRightUV = new(one, threeFourths);
                        topLeftUV = new(twoThirds, threeFourths);
                    }
                    break;
                case ECubemapFace.NegY:
                    if (widthLarger)
                    {
                        bottomLeftUV = new(fourth, zero);
                        bottomRightUV = new(half, zero);
                        topRightUV = new(half, third);
                        topLeftUV = new(fourth, third);
                    }
                    else
                    {
                        bottomLeftUV = new(third, fourth);
                        bottomRightUV = new(twoThirds, fourth);
                        topRightUV = new(twoThirds, half);
                        topLeftUV = new(third, half);
                    }
                    break;
                case ECubemapFace.PosY:
                    if (widthLarger)
                    {
                        bottomLeftUV = new(fourth, twoThirds);
                        bottomRightUV = new(half, twoThirds);
                        topRightUV = new(half, one);
                        topLeftUV = new(fourth, one);
                    }
                    else
                    {
                        bottomLeftUV = new(third, threeFourths);
                        bottomRightUV = new(twoThirds, threeFourths);
                        topRightUV = new(twoThirds, one);
                        topLeftUV = new(third, one);
                    }
                    break;
                case ECubemapFace.NegZ:
                    if (widthLarger)
                    {
                        bottomLeftUV = new(fourth, third);
                        bottomRightUV = new(half, third);
                        topRightUV = new(half, twoThirds);
                        topLeftUV = new(fourth, twoThirds);
                    }
                    else
                    {
                        bottomLeftUV = new(third, half);
                        bottomRightUV = new(twoThirds, half);
                        topRightUV = new(twoThirds, threeFourths);
                        topLeftUV = new(third, threeFourths);
                    }
                    break;
                case ECubemapFace.PosZ:
                    if (widthLarger)
                    {
                        bottomLeftUV = new(threeFourths, third);
                        bottomRightUV = new(one, third);
                        topRightUV = new(one, twoThirds);
                        topLeftUV = new(threeFourths, twoThirds);
                    }
                    else
                    {
                        //Upside-down UVs
                        bottomLeftUV = new(third, fourth);
                        bottomRightUV = new(twoThirds, fourth);
                        topRightUV = new(twoThirds, zero);
                        topLeftUV = new(third, zero);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(cubeMapFace), cubeMapFace, null);
            }

            bottomLeftUV.X += bias;
            topLeftUV.X += bias;
            bottomRightUV.X -= bias;
            topRightUV.X -= bias;

            bottomLeftUV.Y += bias;
            bottomRightUV.Y += bias;
            topLeftUV.Y -= bias;
            topRightUV.Y -= bias;

            if (flipVerticalUVCoord)
            {
                bottomLeftUV.Y = 1.0f - bottomLeftUV.Y;
                bottomRightUV.Y = 1.0f - bottomRightUV.Y;
                topRightUV.Y = 1.0f - topRightUV.Y;
                topLeftUV.Y = 1.0f - topLeftUV.Y;
            }

            return new VertexQuad(
                new Vertex(bottomLeft, normal, bottomLeftUV),
                new Vertex(bottomRight, normal, bottomRightUV),
                new Vertex(topRight, normal, topRightUV),
                new Vertex(topLeft, normal, topLeftUV));
        }

        public static VertexQuad Make(
            Vector3 bottomLeft,     Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? bottomLeftInf,
            Vector3 bottomRight,    Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? bottomRightInf,
            Vector3 topRight,       Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? topRightInf,
            Vector3 topLeft,        Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? topLeftInf,
            Vector3 normal, bool flipVerticalUVCoord = false)
        {
            return new VertexQuad(
                new Vertex(bottomLeft,  bottomLeftInf,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                new Vertex(bottomRight, bottomRightInf, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                new Vertex(topRight,    topRightInf,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
                new Vertex(topLeft,     topLeftInf,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
        }

        public static VertexQuad Make(
           Vector3 bottomLeft,  Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? bottomLeftInf,    Vector3 bottomLeftNormal,
           Vector3 bottomRight, Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? bottomRightInf,   Vector3 bottomRightNormal,
           Vector3 topRight,    Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? topRightInf,      Vector3 topRightNormal,
           Vector3 topLeft,     Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? topLeftInf,       Vector3 topLeftNormal, bool flipVerticalUVCoord = false)
        {
            return new VertexQuad(
                new Vertex(bottomLeft,  bottomLeftInf,  bottomLeftNormal,   new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                new Vertex(bottomRight, bottomRightInf, bottomRightNormal,  new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                new Vertex(topRight,    topRightInf,    topRightNormal,     new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
                new Vertex(topLeft,     topLeftInf,     topLeftNormal,      new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
        }

        public static VertexQuad Make(
           Vector3 bottomLeft,  Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? bottomLeftInf,
           Vector3 bottomRight, Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? bottomRightInf,
           Vector3 topRight,    Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? topRightInf,
           Vector3 topLeft,     Dictionary<TransformBase, (float, Matrix4x4 invBindMatrix)>? topLeftInf,
           bool addAutoNormal = false, bool flipVerticalUVCoord = false)
        {
            if (addAutoNormal)
            {
                Vector3 normal = XRMath.CalculateNormal(bottomLeft, bottomRight, topLeft);
                return new VertexQuad(
                    new Vertex(bottomLeft,  bottomLeftInf,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                    new Vertex(bottomRight, bottomRightInf, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                    new Vertex(topRight,    topRightInf,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
                    new Vertex(topLeft,     topLeftInf,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
            }
            else
                return new VertexQuad(
                    new Vertex(bottomLeft,  bottomLeftInf,  new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                    new Vertex(bottomRight, bottomRightInf, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
                    new Vertex(topRight,    topRightInf,    new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
                    new Vertex(topLeft,     topLeftInf,     new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
        }

        /// <summary>
        /// Positive Y is facing the sky, like a floor.
        /// </summary>
        public static VertexQuad PosY(float uniformScale = 1.0f, bool bottomLeftOrigin = false, bool flipVerticalUVCoord = false) 
            => PosY(uniformScale, uniformScale, bottomLeftOrigin, flipVerticalUVCoord);
        /// <summary>
        /// Positive Y is facing the sky, like a floor.
        /// </summary>
        public static VertexQuad PosY(float xScale, float zScale, bool bottomLeftOrigin, bool flipVerticalUVCoord = false)
        {
            if (bottomLeftOrigin)
            {
                Vector3 v1 = new(0.0f,    0.0f, 0.0f);
                Vector3 v2 = new(xScale,  0.0f, 0.0f);
                Vector3 v3 = new(xScale,  0.0f, -zScale);
                Vector3 v4 = new(0.0f,    0.0f, -zScale);
                return Make(v1, v2, v3, v4, Vector3.UnitY, flipVerticalUVCoord);
            }
            else
            {
                float xHalf = xScale / 2.0f;
                float zHalf = zScale / 2.0f;
                Vector3 v1 = new(-xHalf,  0.0f, zHalf);
                Vector3 v2 = new(xHalf,   0.0f, zHalf);
                Vector3 v3 = new(xHalf,   0.0f, -zHalf);
                Vector3 v4 = new(-xHalf,  0.0f, -zHalf);
                return Make(v1, v2, v3, v4, Vector3.UnitY, flipVerticalUVCoord);
            }
        }
        /// <summary>
        /// Positive Y is facing the camera, like a wall.
        /// </summary>
        public static VertexQuad PosY(BoundingRectangleF region, bool flipVerticalUVCoord = false)
            => Make(
                new Vector3(region.BottomLeft.X, 0.0f, region.BottomLeft.Y),
                new Vector3(region.BottomRight.X, 0.0f, region.BottomRight.Y),
                new Vector3(region.TopRight.X, 0.0f, region.TopRight.Y),
                new Vector3(region.TopLeft.X, 0.0f, region.TopLeft.Y),
                Vector3.UnitY, flipVerticalUVCoord);

        /// <summary>
        /// Positive Z is facing the camera, like a wall.
        /// </summary>
        public static VertexQuad PosZ(float uniformScale = 1.0f, bool bottomLeftOrigin = false, float z = 0.0f, bool flipVerticalUVCoord = true)
            => PosZ(uniformScale, uniformScale, z, bottomLeftOrigin, flipVerticalUVCoord);
        /// <summary>
        /// Positive Z is facing the camera, like a wall.
        /// </summary>
        public static VertexQuad PosZ(float xScale, float yScale, float z, bool bottomLeftOrigin, bool flipVerticalUVCoord = true)
        {
            if (bottomLeftOrigin)
            {
                Vector3 v1 = new(0.0f,    0.0f,   z);
                Vector3 v2 = new(xScale,  0.0f,   z);
                Vector3 v3 = new(xScale,  yScale, z);
                Vector3 v4 = new(0.0f,    yScale, z);
                return Make(v1, v2, v3, v4, Vector3.UnitZ, flipVerticalUVCoord);
            }
            else
            {
                float xHalf = xScale / 2.0f;
                float yHalf = yScale / 2.0f;
                Vector3 v1 = new(-xHalf,  -yHalf, z);
                Vector3 v2 = new(xHalf,   -yHalf, z);
                Vector3 v3 = new(xHalf,   yHalf,  z);
                Vector3 v4 = new(-xHalf,  yHalf,  z);
                return Make(v1, v2, v3, v4, Vector3.UnitZ, flipVerticalUVCoord);
            }
        }
        /// <summary>
        /// Positive Z is facing the camera, like a wall.
        /// </summary>
        public static VertexQuad PosZ(BoundingRectangleF region, bool flipVerticalUVCoord = false)
            => Make(
                new Vector3(region.BottomLeft.X, region.BottomLeft.Y, 0.0f),
                new Vector3(region.BottomRight.X, region.BottomRight.Y, 0.0f),
                new Vector3(region.TopRight.X, region.TopRight.Y, 0.0f),
                new Vector3(region.TopLeft.X, region.TopLeft.Y, 0.0f),
                Vector3.UnitZ, flipVerticalUVCoord);

        /// <summary>
        /// Negative Z is away from the camera.
        /// </summary>
        public static VertexQuad NegZ(float uniformScale = 1.0f, bool bottomLeftOrigin = false, bool flipVerticalUVCoord = false)
            => NegZ(uniformScale, uniformScale, bottomLeftOrigin, flipVerticalUVCoord);
        /// <summary>
        /// Negative Z is away from the camera.
        /// </summary>
        public static VertexQuad NegZ(float xScale, float yScale, bool bottomLeftOrigin, bool flipVerticalUVCoord = false)
        {
            if (bottomLeftOrigin)
            {
                Vector3 v1 = new(0.0f,    0.0f,   0.0f);
                Vector3 v2 = new(-xScale, 0.0f,   0.0f);
                Vector3 v3 = new(-xScale, yScale, 0.0f);
                Vector3 v4 = new(0.0f,    yScale, 0.0f);
                return Make(v1, v2, v3, v4, -Vector3.UnitZ, flipVerticalUVCoord);
            }
            else
            {
                float xHalf = xScale / 2.0f;
                float yHalf = yScale / 2.0f;
                Vector3 v1 = new(xHalf,   -yHalf, 0.0f);
                Vector3 v2 = new(-xHalf,  -yHalf, 0.0f);
                Vector3 v3 = new(-xHalf,  yHalf,  0.0f);
                Vector3 v4 = new(xHalf,   yHalf,  0.0f);
                return Make(v1, v2, v3, v4, -Vector3.UnitZ, flipVerticalUVCoord);
            }
        }
    }
}
