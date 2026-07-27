using System;
using System.Collections.Generic;
using PlatformerPlaytest.Solver;
using UnityEditor;
using UnityEngine;

namespace PlatformerPlaytest.Editor
{
    /// <summary>
    /// T12: "solve once, cache forever" for the Watch tab / batch runner. Checks SolutionCache first (instant on
    /// hit); on a miss, runs the segmented solver behind a cancelable progress bar driven by
    /// SegmentedSolver.OnSegment, then saves the result. Never fabricates success: a cancel or a partial solve is
    /// reported with the segment/x it stalled at, and nothing is written to the cache in either case.
    /// </summary>
    static class CachedSolver
    {
        sealed class CancelledException : Exception { }

        public static bool TrySolve(IGameAdapter adapter, ScenarioConfig scenario, string scenarioId,
            SolverConfig solverConfig, out List<PlayerAction> stream, out string status)
        {
            string key = SolutionCache.MakeKey(scenario.CacheIdentity(scenarioId), solverConfig);

            if (SolutionCache.TryLoad(key, out stream))
            {
                status = $"Loaded cached solution for '{scenarioId}' ({stream.Count} ticks).";
                return true;
            }

            SegmentedSolver solver = new SegmentedSolver();
            solver.OnSegment = (i, n) =>
            {
                bool cancel = EditorUtility.DisplayCancelableProgressBar(
                    $"Solving {scenarioId}",
                    $"Solving {scenarioId} (one-time, cached after this)... segment {i + 1}/{n}",
                    n > 0 ? (float)i / n : 0f);
                if (cancel)
                    throw new CancelledException();
            };

            try
            {
                SegmentedSolveResult result = solver.Solve(adapter, scenario, solverConfig);

                if (!result.Solved)
                {
                    stream = null;
                    status = $"Solve did not complete '{scenarioId}': stalled at segment " +
                             $"{result.FailedSegment}/{result.SegmentCount} (furthest x={result.FurthestX:F2}). " +
                             $"{result.Diagnostic}";
                    return false;
                }

                SolutionCache.Save(key, result.ActionStream);
                stream = result.ActionStream;
                status = $"Solved '{scenarioId}' and cached ({stream.Count} ticks, {result.ElapsedMs} ms).";
                return true;
            }
            catch (CancelledException)
            {
                stream = null;
                status = $"Solve cancelled for '{scenarioId}'. Nothing cached.";
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>Deletes the cached solution for (scenarioId, solverConfig), if any.</summary>
        public static bool Clear(string scenarioId, SolverConfig solverConfig) =>
            SolutionCache.ClearScenario(scenarioId);
    }
}
