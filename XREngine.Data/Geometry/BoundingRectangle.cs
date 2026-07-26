using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Vectors;
using Rectangle = System.Drawing.Rectangle;

namespace XREngine.Data.Geometry
{
    /// <summary>
    /// Axis-aligned rectangle struct. Supports position, size, and a local origin. All translations are relative to the bottom left (0, 0), like a graph.
    /// </summary>
    [Serializable]
    public struct BoundingRectangle(IVector2 translation, IVector2 bounds, Vector2 localOriginPercentage)
        : IEquatable<BoundingRectangle>
    {
        /// <summary>
        /// A rectangle with a location at 0,0 (bottom left), a size of 0, and a local origin at the bottom left.
        /// </summary>
        public static readonly BoundingRectangle Empty = new();

        private IVector2 _translation = (IVector2)(translation - (localOriginPercentage * bounds));
        private IVector2 _bounds = bounds;
        private Vector2 _localOriginPercentage = localOriginPercentage;

        /// <summary>
        /// The relative translation of the origin from the bottom left, as a percentage.
        /// 0,0 is bottom left, 0.5,0.5 is center, 1,1 is top right.
        /// </summary>
        [Description(
            "The relative translation of the origin from the bottom left, as a percentage." +
            "0,0 is bottom left, 0.5,0.5 is center, 1,1 is top right.")]
        public Vector2 LocalOriginPercentage
        {
            readonly get => _localOriginPercentage;
            set
            {
                Vector2 diff = value - _localOriginPercentage;

                Vector2 trans = _translation;
                trans += diff * _bounds;
                _translation = (IVector2)trans;

                _localOriginPercentage = value;
            }
        }

        /// <summary>
        /// The actual translation of the origin of this rectangle, relative to the bottom left.
        /// </summary>
        [Description(@"The actual translation of the origin of this rectangle, relative to the bottom left.")]
        public IVector2 LocalOrigin
        {
            readonly get => (IVector2)(_localOriginPercentage * _bounds);
            set => _localOriginPercentage = value / _bounds;
        }
        /// <summary>
        /// The location of the origin of this rectangle as a world point relative to the bottom left (0, 0).
        /// Bottom left point of this rectangle is Position - LocalOrigin.
        /// </summary>
        [Description(@"The location of the origin of this rectangle as a world point relative to the bottom left (0, 0). Bottom left point of this rectangle is Position - LocalOrigin.")]
        public IVector2 OriginTranslation
        {
            readonly get => _translation + LocalOrigin;
            set => _translation = value - LocalOrigin;
        }

        public BoundingRectangle(int x, int y, int width, int height, float localOriginPercentageX, float localOriginPercentageY)
            : this(new IVector2(x, y), new IVector2(width, height), new Vector2(localOriginPercentageX, localOriginPercentageY)) { }

        public BoundingRectangle(int x, int y, int width, int height)
            : this(new IVector2(x, y), new IVector2(width, height)) { }

        public BoundingRectangle(IVector2 translation, IVector2 bounds)
            : this(translation, bounds, Vector2.Zero) { }

        public BoundingRectangle(int width, int height)
            : this(0, 0, width, height) { }

        public static BoundingRectangle FromMinMaxSides(
            int minX, int maxX,
            int minY, int maxY,
            float localOriginPercentageX, float localOriginPercentageY)
            => new(minX, minY, maxX - minX, maxY - minY, localOriginPercentageX, localOriginPercentageY);

        /// <summary>
        /// The horizontal translation of this rectangle's position. 0 is fully left, positive values are right.
        /// </summary>
        public int X
        {
            readonly get => OriginTranslation.X;
            set => _translation.X = value - LocalOrigin.X;
        }

        /// <summary>
        /// The vertical translation of this rectangle's position. 0 is fully down, positive values are up.
        /// </summary>
        public int Y
        {
            readonly get => OriginTranslation.Y;
            set => _translation.Y = value - LocalOrigin.Y;
        }

        /// <summary>
        /// The width of this rectangle.
        /// </summary>
        public int Width
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
        public int Height
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
        public int MaxX
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
        public int MaxY
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
        public int MinX
        {
            readonly get => _translation.X + (Width < 0 ? Width : 0);
            set
            {
                CheckProperDimensions();
                int origX = _bounds.X;
                _translation.X = value;
                _bounds.X = origX - _translation.X;
            }
        }

        /// <summary>
        /// The Y value of the bottom boundary line.
        /// Only moves the bottom edge by resizing height.
        /// </summary>
        public int MinY
        {
            readonly get => _translation.Y + (Height < 0 ? Height : 0);
            set
            {
                CheckProperDimensions();
                int origY = Height;
                _translation.Y = value;
                Height = origY - _translation.Y;
            }
        }

        /// <summary>
        /// The world position of the center point of the rectangle (regardless of the local origin).
        /// </summary>
        public IVector2 Center
        {
            readonly get => (IVector2)(_translation + (_bounds / 2));
            set => _translation = (IVector2)(value - (_bounds / 2));
        }
        /// <summary>
        /// The width and height of this rectangle.
        /// </summary>
        public IVector2 Size
        {
            readonly get => _bounds;
            set => _bounds = value;
        }

        /// <summary>
        /// The location of this rectangle's bottom left point (top right if both width and height are negative). 0 is fully left/down, positive values are right/up.
        /// </summary>
        public IVector2 Translation
        {
            readonly get => _translation;
            set => _translation = value;
        }

        /// <summary>
        /// Bottom left point in world space regardless of width or height being negative.
        /// </summary>
        public IVector2 BottomLeft
        {
            readonly get => new(MinX, MinY);
            set
            {
                CheckProperDimensions();
                IVector2 oldTopRight = TopRight;
                _translation = value;
                _bounds = oldTopRight - _translation;
            }
        }

        /// <summary>
        /// Top right point in world space regardless of width or height being negative.
        /// </summary>
        public IVector2 TopRight
        {
            readonly get => new(MaxX, MaxY);
            set
            {
                CheckProperDimensions();
                _bounds = value - _translation;
            }
        }

        /// <summary>
        /// Bottom right point in world space regardless of width or height being negative.
        /// </summary>
        public IVector2 BottomRight
        {
            readonly get => new(MaxX, MinY);
            set
            {
                CheckProperDimensions();
                int upperY = _translation.Y + _bounds.Y;
                _translation.Y = value.Y;
                _bounds.X = value.X - _translation.X;
                _bounds.Y = upperY - value.Y;
            }
        }

        public readonly Rectangle AsRectangle(int containerHeight)
        {
            IVector2 pos = TopLeft;
            return new Rectangle(pos.X, containerHeight - pos.Y, Width, Height);
        }

        /// <summary>
        /// Top left point in world space regardless of width or height being negative.
        /// </summary>
        public IVector2 TopLeft
        {
            readonly get => new(MinX, MaxY);
            set
            {
                CheckProperDimensions();
                int upperX = _translation.X + _bounds.X;
                _translation.X = value.X;
                _bounds.X = upperX - value.X;
                _bounds.Y = value.Y - _translation.Y;
            }
        }

        /// <summary>
        /// Translates this rectangle relative to the current translation using an offset.
        /// </summary>
        /// <param name="offset">The translation delta to add to the current translation.</param>
        public void Translate(IVector2 offset)
            => _translation += offset;

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
        public readonly bool Contains(IVector2 point)
            => _bounds.Contains(point - BottomLeft);

        /// <summary>
        /// Determines if this rectangle is contained within another.
        /// </summary>
        /// <param name="other">The other rectangle.</param>
        /// <returns>EContainment.Disjoint if not intersecting. EContainment.Intersecting if intersecting, but not fully contained. EContainment.Contains if fully contained.</returns>
        public readonly EContainment ContainmentWithin(BoundingRectangle other)
            => other.ContainmentOf(this);

        /// <summary>
        /// Determines if this rectangle contains another.
        /// </summary>
        /// <param name="other">The other rectangle.</param>
        /// <returns>EContainment.Disjoint if not intersecting. EContainment.Intersecting if intersecting, but not fully contained. EContainment.Contains if fully contained.</returns>
        public readonly EContainment ContainmentOf(BoundingRectangle other)
            => Intersects(other)
                ? EContainment.Intersects
                : Contains(other)
                    ? EContainment.Contains
                    : EContainment.Disjoint;
        
        public readonly bool DisjointWith(float width, float height)
            => //Can't just negate contains operation, because that would also include intersection.
            0 > MaxX || width < MinX ||
            0 > MaxY || height < MinY;

        /// <summary>
        /// Returns true if this rectangle and the given rectangle are not touching or contained within another.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public readonly bool DisjointWith(BoundingRectangle other)
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
        public readonly bool Contains(BoundingRectangle other)
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
        public readonly bool Intersects(BoundingRectangle other)
        {
            return !Contains(other) && !DisjointWith(other);
            //MinX <= other.MaxX &&
            //MaxX >= other.MinX &&
            //MinY <= other.MaxY &&
            //MaxY >= other.MinY;
        }

        public override readonly string ToString()
            => string.Format("[X:{0} Y:{1} W:{2} H:{3}]", OriginTranslation.X, OriginTranslation.Y, Width, Height);

        public readonly bool Equals(BoundingRectangle other)
            => _translation.X == other._translation.X &&
               _translation.Y == other._translation.Y &&
               _bounds.X == other._bounds.X &&
               _bounds.Y == other._bounds.Y &&
               _localOriginPercentage.Equals(other._localOriginPercentage);

        public override readonly bool Equals(object? obj)
            => obj is BoundingRectangle other && Equals(other);

        public override readonly int GetHashCode()
            => HashCode.Combine(
                _translation.X,
                _translation.Y,
                _bounds.X,
                _bounds.Y,
                _localOriginPercentage);

        public static bool operator ==(BoundingRectangle left, BoundingRectangle right)
            => left.Equals(right);

        public static bool operator !=(BoundingRectangle left, BoundingRectangle right)
            => !left.Equals(right);

        public readonly IVector2 ClosestPoint(IVector2 point)
            => point.Clamped(BottomLeft, TopRight);

        public readonly bool IsEmpty()
            => Height == 0 || Width == 0;
    }
}
