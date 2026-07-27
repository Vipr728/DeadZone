namespace PlatformerPlaytest
{
    /// <summary>Pure helper math shared by the adapter and testable in isolation, no LINQ, no allocation.</summary>
    public static class ProgressMath
    {
        /// <summary>Clamp01 of (x - spawnX) / (goalX - spawnX). Returns 0 if goalX == spawnX.</summary>
        public static float Progress(float x, float spawnX, float goalX)
        {
            float span = goalX - spawnX;
            if (span == 0f)
                return 0f;
            float t = (x - spawnX) / span;
            if (t < 0f) return 0f;
            if (t > 1f) return 1f;
            return t;
        }

        /// <summary>Linear scan for the index of the last boundary the given x has passed; -1 if bounds is empty or x is before the first boundary.</summary>
        public static int SectionIndexFor(float x, float[] bounds)
        {
            if (bounds == null || bounds.Length == 0)
                return 0;

            int index = 0;
            for (int i = 0; i < bounds.Length; i++)
            {
                if (x >= bounds[i])
                    index = i + 1;
                else
                    break;
            }
            return index;
        }

        /// <summary>Same scan over authored checkpoints; only the x component participates.</summary>
        public static int SectionIndexFor(float x, UnityEngine.Vector2[] bounds)
        {
            if (bounds == null || bounds.Length == 0)
                return 0;

            int index = 0;
            for (int i = 0; i < bounds.Length; i++)
            {
                if (x >= bounds[i].x)
                    index = i + 1;
                else
                    break;
            }
            return index;
        }
    }
}
