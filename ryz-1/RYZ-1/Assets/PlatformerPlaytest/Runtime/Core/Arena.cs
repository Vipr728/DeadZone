using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlatformerPlaytest
{
    /// <summary>
    /// A single isolated additive scene with its own Physics2D world. Content is populated by a caller-supplied
    /// level factory delegate; the arena itself only owns lifecycle (create/step/unload).
    /// </summary>
    public sealed class Arena
    {
        public Scene Scene { get; }
        public PhysicsScene2D PhysicsScene2D { get; }

        bool unloaded;

        public Arena(string name, Action<Scene> levelFactory)
        {
            Scene = SceneManager.CreateScene(name, new CreateSceneParameters(LocalPhysicsMode.Physics2D));
            PhysicsScene2D = Scene.GetPhysicsScene2D();
            levelFactory?.Invoke(Scene);
        }

        /// <summary>Wraps an already-loaded scene (see <see cref="ArenaManager.LoadSceneArena"/>). The scene must
        /// have been loaded with <see cref="LocalPhysicsMode.Physics2D"/> or it will share the global 2D world.</summary>
        public Arena(Scene loadedScene)
        {
            Scene = loadedScene;
            PhysicsScene2D = loadedScene.GetPhysicsScene2D();
        }

        /// <summary>Advance this arena's physics world by dt. Prefer adapter.TickSimulation for player-driven arenas.</summary>
        public void Step(float dt)
        {
            PhysicsScene2D.Simulate(dt);
        }

        public void Unload()
        {
            if (unloaded)
                return;
            unloaded = true;
            SceneManager.UnloadSceneAsync(Scene);
        }
    }
}
