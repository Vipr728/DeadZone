using System.Text.Json.Serialization;

namespace Ryz1.Contracts;

public readonly record struct Vec2(float X, float Y)
{
    public static readonly Vec2 Zero = new(0f, 0f);

    [JsonIgnore]
    public float LengthSquared => X * X + Y * Y;

    public Vec2 Normalized()
    {
        float length = MathF.Sqrt(LengthSquared);
        return length <= 1e-6f ? Zero : new Vec2(X / length, Y / length);
    }

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);
}

public readonly record struct Rect2(float X, float Y, float Width, float Height)
{
    public float MinX => X;
    public float MinY => Y;
    public float MaxX => X + Width;
    public float MaxY => Y + Height;

    public bool Contains(Vec2 point) =>
        point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;

    public bool Intersects(Rect2 other) =>
        MinX <= other.MaxX && MaxX >= other.MinX && MinY <= other.MaxY && MaxY >= other.MinY;
}
