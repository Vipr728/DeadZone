using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlatformerPlaytest
{
    /// <summary>Creates and tracks Arenas so they can all be torn down together (editor-safe, idempotent).</summary>
    public sealed class ArenaManager
    {
        readonly List<Arena> arenas = new List<Arena>();

        /// <summary>Number of arenas currently tracked (alive since last UnloadAll). O(1). Invariant: equals the
        /// count of CreateArena calls minus the arenas cleared by UnloadAll; never negative.</summary>
        public int Count => arenas.Count;

        /// <summary>Creates and tracks an arena. O(1) amortized. Invariant: the returned arena is owned by this
        /// manager and will be unloaded by UnloadAll; callers must not Unload it independently.</summary>
        public Arena CreateArena(string name, Action<Scene> levelFactory)
        {
            Arena arena = new Arena(name, levelFactory);
            arenas.Add(arena);
            return arena;
        }

        /// <summary>
        /// Loads an authored scene additively into its own 2D physics world and tracks it as an arena.
        /// PLAY MODE ONLY — LoadSceneAsync with LoadSceneParameters is not available in Edit Mode (same
        /// restriction as CreateScene(LocalPhysicsMode), see limitations.md #3).
        ///
        /// Coroutine API: the scene is not usable until the load finishes, so callers must drive this to
        /// completion (`yield return manager.LoadSceneArena(path, a => arena = a)` inside a UnityTest/coroutine).
        ///
        /// Duplicate loads of the SAME scene path are supported: Unity appends a second copy and this resolves
        /// the arena by scene INDEX (last loaded), not by path, so each call yields a distinct scene.
        ///
        /// Hygiene: the loaded copy's Cameras and AudioListeners are disabled — arenas are simulation-only, and
        /// N live AudioListeners spam warnings. The Watch feature supplies its own follow camera.
        /// </summary>
        public IEnumerator LoadSceneArena(string scenePath, Action<Arena> onLoaded)
        {
            AsyncOperation op = SceneManager.LoadSceneAsync(
                scenePath, new LoadSceneParameters(LoadSceneMode.Additive, LocalPhysicsMode.Physics2D));
            if (op == null)
                throw new InvalidOperationException(
                    $"ArenaManager.LoadSceneArena: could not start load of '{scenePath}'. " +
                    "Is it in Build Settings, and are you in Play Mode?");

            while (!op.isDone)
                yield return null;

            Scene scene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
            DisableRenderingComponents(scene);

            Arena arena = new Arena(scene);
            arenas.Add(arena);
            onLoaded?.Invoke(arena);
        }

        static void DisableRenderingComponents(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                Behaviour[] behaviours = roots[i].GetComponentsInChildren<Behaviour>(true);
                for (int b = 0; b < behaviours.Length; b++)
                {
                    Behaviour behaviour = behaviours[b];
                    if (!behaviour)
                        continue;

                    // ponytail: URP's Light2D is matched by type name so Runtime keeps zero URP asmdef references.
                    // A second global Light2D in the same frame is a hard LogError, which fails PlayMode tests
                    // whenever two arenas of the same scene are alive. Switch to a typed check if URP is ever
                    // referenced here for another reason.
                    if (behaviour is Camera || behaviour is AudioListener || behaviour.GetType().Name == "Light2D")
                        behaviour.enabled = false;
                }
            }
        }

        public void UnloadAll()
        {
            for (int i = 0; i < arenas.Count; i++)
                arenas[i].Unload();
            arenas.Clear();
        }
    }
}
