using MemoryPack;
using System.ComponentModel;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;
using YamlDotNet.Serialization;

namespace XREngine.Data.Vectors;

[Serializable]
[MemoryPackable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe partial struct IVector2(int x, int y) : IBufferable, IUniformable
{
    public static readonly IVector2 Zero = new(0, 0);
    public static readonly IVector2 One = new(1, 1);

    public int X
    {
        readonly get => _x;
        set => _x = value;
    }

    public int Y
    {
        readonly get => _y;
        set => _y = value;
    }

    private int
        _x = x,
        _y = y;

    //[JsonIgnore]
    //[YamlIgnore]
    [Browsable(false)]
    public int* Data { get { fixed (void* ptr = &this) return (int*)ptr; } }

    //[JsonIgnore]
    //[YamlIgnore]
    public EComponentType ComponentType { get; } = EComponentType.Int;
    //[JsonIgnore]
    //[YamlIgnore]
    public uint ComponentCount { get; } = 2;
    //[JsonIgnore]
    //[YamlIgnore]
    public bool Normalize { get; } = false;

    public void Write(VoidPtr address)
    {
        int* dPtr = (int*)address;
        for (int i = 0; i < 2; ++i)
            *dPtr++ = Data[i];
    }

    public void Read(VoidPtr address)
    {
        int* data = (int*)address;
        for (int i = 0; i < 2; ++i)
            Data[i] = *data++;
    }

    public int this[int index]
    {
        get
        {
            if (index < 0 || index > 1)
                throw new IndexOutOfRangeException("Cannot access vector at index " + index);
            return Data[index];
        }
        set
        {
            if (index < 0 || index > 1)
                throw new IndexOutOfRangeException("Cannot access vector at index " + index);
            Data[index] = value;
        }
    }

    public static bool operator ==(IVector2 left, IVector2 right)
        => left.Equals(right);

    public static bool operator !=(IVector2 left, IVector2 right)
        => !left.Equals(right);

    private static string ListSeparator = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator;

    public override readonly string ToString()
        => string.Format("({0}{2} {1})", X, Y, ListSeparator);

    public override readonly int GetHashCode()
    {
        unchecked
        {
            return (X.GetHashCode() * 397) ^ Y.GetHashCode();
        }
    }

    public override readonly bool Equals(object? obj)
        => obj is IVector2 vector && Equals(vector);

    public readonly bool Equals(IVector2 other) =>
        X == other.X &&
        Y == other.Y;

    public static IVector2 operator +(IVector2 left, IVector2 right)
        => new(left.X + right.X, left.Y + right.Y);
    public static IVector2 operator -(IVector2 left, IVector2 right)
        => new(left.X - right.X, left.Y - right.Y);
    public static IVector2 operator *(IVector2 left, IVector2 right)
        => new(left.X * right.X, left.Y * right.Y);
    public static Vector2 operator /(IVector2 left, IVector2 right)
        => new((float)left.X / right.X, (float)left.Y / right.Y);

    public static Vector2 operator /(IVector2 left, int right)
        => new((float)left.X / right, (float)left.Y / right);
    public static IVector2 operator *(IVector2 left, int right)
        => new(left.X * right, left.Y * right);
    public static IVector2 operator +(IVector2 left, int right)
        => new(left.X + right, left.Y + right);
    public static IVector2 operator -(IVector2 left, int right)
        => new(left.X - right, left.Y - right);

    public static Vector2 operator /(IVector2 left, float right)
        => new(left.X / right, left.Y / right);
    public static Vector2 operator *(IVector2 left, float right)
        => new(left.X * right, left.Y * right);
    public static Vector2 operator +(IVector2 left, float right)
        => new(left.X + right, left.Y + right);
    public static Vector2 operator -(IVector2 left, float right)
        => new(left.X - right, left.Y - right);

    public static Vector2 operator *(Vector2 left, IVector2 right)
        => new(left.X * right.X, left.Y * right.Y);
    public static Vector2 operator *(IVector2 left, Vector2 right)
        => new(left.X * right.X, left.Y * right.Y);

    public static explicit operator IVector2(Vector2 v) => new((int)v.X, (int)v.Y);
    public static implicit operator Vector2(IVector2 v) => new(v.X, v.Y);

    public static implicit operator Size(IVector2 v) => new(v.X, v.Y);
    public static implicit operator IVector2(Size v) => new(v.Width, v.Height);

    public readonly bool Contains(IVector2 point) =>
        point.X <= X &&
        point.Y <= Y &&
        point.X >= 0 &&
        point.Y >= 0;

    public IVector2 Clamped(IVector2 min, IVector2 max)
        => new()
        {
            X = X < min.X ? min.X : X > max.X ? max.X : X,
            Y = Y < min.Y ? min.Y : Y > max.Y ? max.Y : Y
        };
}
