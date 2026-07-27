using System;
using System.Collections;
using UnityEditor;

namespace PlatformerPlaytest.Editor
{
    /// <summary>
    /// T12: steps a runtime IEnumerator (e.g. ArenaManager.LoadSceneArena) from EditorApplication.update, since
    /// there is no MonoBehaviour to StartCoroutine on in editor code. One instance drives one routine to
    /// completion or cancellation; unsubscribe is idempotent (guarded by `done`) so repeated Cancel() calls from
    /// both the caller and a play-mode-exit handler never double-remove or leak the update hook.
    /// </summary>
    public sealed class EditorCoroutinePump
    {
        readonly IEnumerator routine;
        readonly Action onComplete;
        readonly Action<Exception> onError;
        bool done;

        EditorCoroutinePump(IEnumerator routine, Action onComplete, Action<Exception> onError)
        {
            this.routine = routine;
            this.onComplete = onComplete;
            this.onError = onError;
        }

        public static EditorCoroutinePump Run(IEnumerator routine, Action onComplete, Action<Exception> onError = null)
        {
            EditorCoroutinePump pump = new EditorCoroutinePump(routine, onComplete, onError);
            EditorApplication.update += pump.Tick;
            return pump;
        }

        void Tick()
        {
            bool moved;
            try
            {
                moved = routine.MoveNext();
            }
            catch (Exception ex)
            {
                Cancel();
                onError?.Invoke(ex);
                return;
            }

            if (!moved)
            {
                Cancel();
                onComplete?.Invoke();
            }
        }

        /// <summary>Stops pumping. Safe to call multiple times (window close + play-mode-exit both call this).</summary>
        public void Cancel()
        {
            if (done)
                return;
            done = true;
            EditorApplication.update -= Tick;
        }
    }
}
