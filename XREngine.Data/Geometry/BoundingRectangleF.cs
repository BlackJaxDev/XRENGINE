using System.ComponentModel;
using System.Drawing;
using System.Numerics;
using XREngine.Data.Vectors;
using YamlDotNet.Serialization;

namespace XREngine.Data.Geometry
{
    /// <summary>
    /// Axis-aligned rectangle struct. Supports position, size, and a local origin. All translations are relative to the bottom left (0, 0), like a graph.
    /// </summary>
    public struct BoundingRectangleF(Vector2 translation, Vector2 bounds, Vector2 localOriginPercentage) : IEquatable<BoundingRectangleF>
    {
        /// <summary>
        /// A rectangle with a location at 0,0 (bottom left), a size of 0, and a local origin at the bottom left.
        /// </summary>
        public static readonly BoundingRectangleF Empty = new();

        private Vector2 _translation = translation - localOriginPercentage * bounds;
        private Vector2 _bounds = bounds;
        private Vector2 _localOriginPercentage = localOriginPercentage;

        /// <summary>
        /// The relative translation of the origin from the bottom left, as a percentage.
        /// 0,0 is bottom left, 0.5,0.5 is center, 1,1 is top right.
        /// </summary>
        [Description(@"The relative translation of the origin from the bottom left, as a percentage.
0,0 is bottom left, 0.5,0.5 is center, 1,1 is top right.")]
        public Vector2 LocalOriginPercentage
        {
            readonly get => _localOriginPercentage;
            set
            {
                Vector2 diff = value - _localOriginPercentage;
                _translation.X += diff.X * Width;
                _translation.Y += diff.Y * Height;
                _localOriginPercentage = value;
            }
        }
        /// <summary>
        /// The actual translation of the origin of this rectangle, relative to the bottom left.
        /// </summary>
        [YamlIgnore]
        [Description(@"The actual translation of the origin of this rectangle, relative to the bottom left.")]
        public Vector2 LocalOrigin
        {
            readonly get => _localOriginPercentage * _bounds;
            set => _localOriginPercentage = value / _bounds;
        }
        /// <summary>
        /// The location of the origin of this rectangle as a world point relative to the bottom left (0, 0).
        /// Bottom left point of this rectangle is Position - LocalOrigin.
        /// </summary>
        [YamlIgnore]
        [Description(@"The location of the origin of this rectangle as a world point relative to the bottom left (0, 0).
Bottom left point of this rectangle is Position - LocalOrigin.")]
        public Vector2 OriginTranslation
        {
            readonly get => _translation + LocalOrigin;
            set => _translation = value - LocalOrigin;
        }

        public BoundingRectangleF(float x, float y, float width, float height, float localOriginPercentageX, float localOriginPercentageY)
            : this(new Vector2(x, y), new Vector2(width, height), new Vector2(localOriginPercentageX, localOriginPercentageY)) { }
        public BoundingRectangleF(float x, float y, float width, float height)
            : this(new Vector2(x, y), new Vector2(width, height)) { }
        public BoundingRectangleF(Vector2 translation, Vector2 bounds)
            : this(translation, bounds, Vector2.Zero) { }

        public static BoundingRectangleF FromMinMaxSides(
            float minX, float maxX,
            float minY, float maxY,
            float localOriginPercentageX, float localOriginPercentageY)
            => new(minX, minY, maxX - minX, maxY - minY, localOriginPercentageX, localOriginPercentageY);

        /// <summary>
        /// The horizontal translation of this rectangle's position. 0 is fully left, positive values are right.
        /// </summary>
        [YamlIgnore]
        public float X
        {
            readonly get => OriginTranslation.X;
            set => _translation.X = value - LocalOrigin.X;
        }

        /// <summary>
        /// The vertical translation of this rectangle's position. 0 is fully down, positive values are up.
        /// </summary>
        [YamlIgnore]
        public float Y
        {
            readonly get => OriginTranslation.Y;
            set => _translation.Y = value - LocalOrigin.Y;
        }

        /// <summary>
        /// The width of this rectangle.
        /// </summary>
        [YamlIgnore]
        public float Width
        {
            readonly get => _bounds.X;
            set
            {
                _bounds.X = value;
                CheckProperDimensions();
            }
        }

        /// <summary>
        /// The height of this rectangle.
        /// </summary>
        [YamlIgnore]
        public float Height
        {
            readonly get => _bounds.Y;
            set
            {
                _bounds.Y = value;
                CheckProperDimensions();
            }
        }

        /// <summary>
        /// The X value of the right boundary line.
        /// Only moves the right edge by resizing width.
        /// </summary>
        [YamlIgnore]
        public float MaxX
        {
            readonly get => _translation.X + (Width < 0 ? 0 : Width);
            set
            {
                CheckProperDimensions();
                _bounds.X = value - _translation.X;
            }
        }

        /// <summary>
        /// The Y value of the top boundary line.
        /// Only moves the top edge by resizing height.
        /// </summary>
        [YamlIgnore]
        public float MaxY
        {
            readonly get => _translation.Y + (Height < 0 ? 0 : Height);
            set
            {
                CheckProperDimensions();
                _bounds.Y = value - _translation.Y;
            }
        }

        /// <summary>
        /// The X value of the left boundary line.
        /// Only moves the left edge by resizing width.
        /// </summary>
        [YamlIgnore]
        public float MinX
        {
            readonly get => _translation.X + (Width < 0 ? Width : 0);
            set
            {
                CheckProperDimensions();
                float origX = _bounds.X;
                _translation.X = value;
                _bounds.X = origX - _translation.X;
            }
        }

        [YamlIgnore]
        public Vector2 Min
        {
            readonly get => _translation + new Vector2(Width < 0 ? Width : 0, Height < 0 ? Height : 0);
            set
            {
                CheckProperDimensions();
                Vector2 orig = _bounds;
                _translation = value;
                _bounds = orig - _translation;
            }
        }

        [YamlIgnore]
        public Vector2 Max
        {
            readonly get => _translation + new Vector2(Width > 0 ? Width : 0, Height > 0 ? Height : 0);
            set
            {
                CheckProperDimensions();
                _bounds = value - _translation;
            }
        }

        [YamlIgnore]
        /// <summary>
        /// The Y value of the bottom boundary line.
        /// Only moves the bottom edge by resizing height.
        /// </summary>
        public float MinY
        {
            readonly get => _translation.Y + (Height < 0 ? Height : 0);
            set
            {
                CheckProperDimensions();
                float origY = Height;
                _translation.Y = value;
                Height = origY - _translation.Y;
            }
        }

        [YamlIgnore]
        /// <summary>
        /// The world position of the center point of the rectangle (regardless of the local origin).
        /// </summary>
        public Vector2 Center
        {
            readonly get => _translation + (_bounds / 2.0f);
            set => _translation = value - (_bounds / 2.0f);
        }

        /// <summary>
        /// The width and height of this rectangle.
        /// </summary>
        public Vector2 Extents
        {
            readonly get => _bounds;
            set => _bounds = value;
        }

        /// <summary>
        /// The location of this rectangle's bottom left point (top right if both width and height are negative). 0 is fully left/down, positive values are right/up.
        /// </summary>
        public Vector2 Translation
        {
            readonly get => _translation;
            set => _translation = value;
        }

        /// <summary>
        /// Bottom left point in world space regardless of width or height being negative.
        /// </summary>
        [YamlIgnore]
        public Vector2 BottomLeft
        {
            readonly get => new(MinX, MinY);
            set
            {
                CheckProperDimensions();
                Vector2 upper = TopRight;
                _translation = value;
                _bounds = upper - _translation;
            }
        }

        [YamlIgnore]
        /// <summary>
        /// Top right point in world space regardless of width or height being negative.
        /// </summary>
        public Vector2 TopRight
        {
            readonly get => new(MaxX, MaxY);
            set
            {
                CheckProperDimensions();
                _bounds = value - _translation;
            }
        }

        [YamlIgnore]
        /// <summary>
        /// Bottom right point in world space regardless of width or height being negative.
        /// </summary>
        public Vector2 BottomRight
        {
            readonly get => new(MaxX, MinY);
            set
            {
                CheckProperDimensions();
                float upperY = _translation.Y + _bounds.Y;
                _translation.Y = value.Y;
                _bounds.X = value.X - _translation.X;
                _bounds.Y = upperY - value.Y;
            }
        }

        public readonly RectangleF AsRectangleF(float containerHeight)
        {
            Vector2 pos = TopLeft;
            return new RectangleF(pos.X, containerHeight - pos.Y, Width, Height);
        }

        /// <summary>
        /// Top left point in world space regardless of width or height being negative.
        /// </summary>
        [YamlIgnore]
        public Vector2 TopLeft
        {
            readonly get => new(MinX, MaxY);
            set
            {
                CheckProperDimensions();
                float upperX = _translation.X + _bounds.X;
                _translation.X = value.X;
                _bounds.X = upperX - value.X;
                _bounds.Y = value.Y - _translation.Y;
            }
        }

        /// <summary>
        /// The horizontal location of this rectangle's origin, as an integer value floored from float value. 0 is fully left, positive values are right.
        /// </summary>
        [YamlIgnore]
        public int IntX
        {
            readonly get => (int)X;
            set => X = value;
        }

        /// <summary>
        /// The vertical location of this rectangle's origin, as an integer value floored from float value. 0 is fully down, positive values are up.
        /// </summary>
        [YamlIgnore]
        public int IntY
        {
            readonly get => (int)Y;
            set => Y = value;
        }

        /// <summary>
        /// The width of this rectangle, as an integer value floored from float value.
        /// </summary>
        [YamlIgnore]
        public int IntWidth
        {
            readonly get => (int)Width;
            set => Width = value;
        }

        /// <summary>
        /// The height of this rectangle, as an integer value floored from float value.
        /// </summary>
        [YamlIgnore]
        public int IntHeight
        {
            readonly get => (int)Height;
            set => Height = value;
        }

        /// <summary>
        /// Translates this rectangle relative to the current translation using an offset.
        /// </summary>
        /// <param name="offset">The translation delta to add to the current translation.</param>
        public void Translate(Vector2 offset)
            => _translation += offset;
        public void Translate(float x, float y)
            => _translation += new Vector2(x, y);
        public readonly BoundingRectangleF Translated(Vector2 offset)
            => new(_translation + offset, _bounds, _localOriginPercentage);
        public readonly BoundingRectangleF Translated(float x, float y)
            => new(_translation + new Vector2(x, y), _bounds, _localOriginPercentage);

        /// <summary>
        /// Checks that the width and height are positive values. Will move the location of the rectangle to fix this.
        /// </summary>
        public void CheckProperDimensions()
        {
            if (Width < 0)
            {
                _translation.X += Width;
                Width = -Width;
            }
            if (Height < 0)
            {
                _translation.Y += Height;
                Height = -Height;
            }
        }

        /// <summary>
        /// Checks if the point is contained within this rectangle.
        /// </summary>
        /// <param name="point">The point to check.</param>
        /// <returns>True if the point is contained within this rectangle.</returns>
        public readonly bool Contains(Vector2 point)
        {
            Vector2 local = point - BottomLeft;
            return 
                local.X >= 0 && 
                local.Y >= 0 && 
                local.X <= Width && 
                local.Y <= Height;
        }

        /// <summary>
        /// Determines if this rectangle is contained within another.
        /// </summary>
        /// <param name="other">The other rectangle.</param>
        /// <returns>EContainment.Disjoint if not intersecting. EContainment.Intersecting if intersecting, but not fully contained. EContainment.Contains if fully contained.</returns>
        public readonly EContainment ContainmentWithin(BoundingRectangleF other)
            => other.ContainmentOf(this);

        /// <summary>
        /// Determines if this rectangle contains another.
        /// </summary>
        /// <param name="other">The other rectangle.</param>
        /// <returns>EContainment.Disjoint if not intersecting. EContainment.Intersecting if intersecting, but not fully contained. EContainment.Contains if fully contained.</returns>
        public readonly EContainment ContainmentOf(BoundingRectangleF other) =>
            Intersects(other) 
            ? EContainment.Intersects 
            : Contains(other) 
                ? EContainment.Contains
                : EContainment.Disjoint;

        public readonly bool DisjointWith(float width, float height)
        {
            //Can't just negate contains operation, because that would also include intersection.
            return
                0.0f > MaxX ||
                width < MinX ||
                0.0f > MaxY ||
                height < MinY;
        }

        /// <summary>
        /// Returns true if this rectangle and the given rectangle are not touching or contained within another.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public readonly bool DisjointWith(BoundingRectangleF other)
        {
            //Can't just negate contains operation, because that would also include intersection.
            return
                other.MinX > MaxX ||
                other.MaxX < MinX ||
                other.MinY > MaxY ||
                other.MaxY < MinY;
        }

        /// <summary>
        /// Returns true if full contains the given rectangle. If intersecting at all (including a same edge) or disjoint, returns false.
        /// </summary>
        public readonly bool Contains(BoundingRectangleF other)
        {
            return
                other.MaxX <= MaxX &&
                other.MinX >= MinX &&
                other.MaxY <= MaxY &&
                other.MinY >= MinY;
        }

        /// <summary>
        /// Returns true if intersecting at all (including a same edge). If no edges are touching, returns false.
        /// </summary>
        public readonly bool Intersects(BoundingRectangleF other)
        {
            return !Contains(other) && !DisjointWith(other);
            //MinX <= other.MaxX &&
            //MaxX >= other.MinX &&
            //MinY <= other.MaxY &&
            //MaxY >= other.MinY;
        }

        public override readonly string ToString()
            => string.Format("[X:{0} Y:{1} W:{2} H:{3}]", OriginTranslation.X, OriginTranslation.Y, Width, Height);

        public readonly Vector2 ClosestPoint(Vector2 point)
            => new(
                Math.Clamp(point.X, MinX, MaxX),
                Math.Clamp(point.Y, MinY, MaxY));

        public readonly bool IsEmpty()
            => IntHeight == 0 || IntWidth == 0;

        public readonly BoundingRectangle AsBoundingRectangle()
            => new((IVector2)_translation, (IVector2)_bounds, _localOriginPercentage);

        public readonly bool Equals(BoundingRectangleF other)
            => _translation.Equals(other._translation) &&
                _bounds.Equals(other._bounds) &&
                _localOriginPercentage.Equals(other._localOriginPercentage);
        public override readonly bool Equals(object? obj)
            => obj is BoundingRectangleF other && Equals(other);
        public override readonly int GetHashCode()
            => HashCode.Combine(_translation, _bounds, _localOriginPercentage);
        public static bool operator ==(BoundingRectangleF left, BoundingRectangleF right)
            => left.Equals(right);
        public static bool operator !=(BoundingRectangleF left, BoundingRectangleF right)
            => !left.Equals(right);
    }
}
