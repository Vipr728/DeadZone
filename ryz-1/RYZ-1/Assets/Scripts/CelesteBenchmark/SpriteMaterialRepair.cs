using UnityEngine;
using UnityEngine.SceneManagement;

namespace CelesteBenchmark
{
    /// <summary>
    /// Keeps the benchmark playable when Unity has stale URP 2D material references
    /// in a scene or when the URP package is still reimporting.
    /// </summary>
    static class SpriteMaterialRepair
    {
        static Material fallbackMaterial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ApplyToScene();
        }

        static void ApplyToScene()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (!shader)
            {
                Debug.LogError("Celeste Benchmark could not find the built-in Sprites/Default shader.");
                return;
            }

            if (!fallbackMaterial)
            {
                fallbackMaterial = new Material(shader)
                {
                    name = "Celeste Benchmark Sprite Fallback"
                };
            }

            SpriteRenderer[] renderers = Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
                renderers[i].sharedMaterial = fallbackMaterial;
        }
    }
}
