using System;
using UnityEngine;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Deterministic 64-bit FNV-1a hash over a quantized slice of player state — the keyframe fingerprint used to
    /// detect replay desync (ADR-006). Floats are quantized to a 1e-4 grid before hashing so that bit-level noise
    /// below the grid never changes the hash, and identical runs on the same machine produce identical hashes.
    ///
    /// Invariants:
    ///  - Determinism: same (pos, vel, flags, dashes) → same hash, on every machine/run. No wall-clock, no RNG.
    ///  - Quantization: q(v) = round(v * 10000) away-from-zero, as a 64-bit integer. Two values within &lt;5e-5 of
    ///    the same grid point hash identically.
    ///  - NaN / Infinity guard: non-finite floats quantize to 0, so a corrupt frame cannot throw or produce a
    ///    platform-dependent cast result.
    ///  - Signed-zero: -0f and +0f both quantize to 0 (round(-0*10000)=-0.0, (long)(-0.0)=0), so they hash equal.
    ///
    /// Complexity: O(1) — six 8-byte mixes, no allocation.
    /// </summary>
    public static class StateHash
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        /// <summary>Quantize one float to the 1e-4 grid as a 64-bit integer. Non-finite → 0. Away-from-zero rounding.</summary>
        public static long Quantize(float v)
        {
            double d = v;
            if (double.IsNaN(d) || double.IsInfinity(d))
                return 0L;
            return (long)Math.Round(d * 10000.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>FNV-1a 64 over quantized pos.x, pos.y, vel.x, vel.y, then flags bitfield and dashesRemaining.</summary>
        public static ulong Compute(Vector2 pos, Vector2 vel, int flags, int dashesRemaining)
        {
            ulong h = FnvOffset;
            h = Mix(h, Quantize(pos.x));
            h = Mix(h, Quantize(pos.y));
            h = Mix(h, Quantize(vel.x));
            h = Mix(h, Quantize(vel.y));
            h = Mix(h, flags);
            h = Mix(h, dashesRemaining);
            return h;
        }

        static ulong Mix(ulong hash, long value)
        {
            ulong u = (ulong)value;
            for (int i = 0; i < 8; i++)
            {
                hash ^= u & 0xFFUL;
                hash *= FnvPrime;
                u >>= 8;
            }
            return hash;
        }
    }
}
