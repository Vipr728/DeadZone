using Ryz1.Contracts;

namespace Ryz1.SimCore;

internal static class DeterministicMath
{
    public static float Clamp(float value, float min, float max) => MathF.Min(MathF.Max(value, min), max);

    public static float MoveTowards(float current, float target, float maxDelta)
    {
        float delta = target - current;
        if (MathF.Abs(delta) <= maxDelta)
            return target;
        return current + MathF.Sign(delta) * maxDelta;
    }

    public static Vec2 ClampMagnitude(Vec2 value, float maxLength)
    {
        float sq = value.LengthSquared;
        if (sq <= maxLength * maxLength)
            return value;
        float length = MathF.Sqrt(sq);
        return length <= 1e-6f ? Vec2.Zero : new Vec2(value.X / length * maxLength, value.Y / length * maxLength);
    }
}
