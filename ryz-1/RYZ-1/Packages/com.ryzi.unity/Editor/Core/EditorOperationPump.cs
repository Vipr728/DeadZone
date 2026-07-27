using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Ryzi.Editor
{
    public sealed class EditorOperationPump
    {
        readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
        readonly Action completed;
        readonly Action<Exception> failed;
        AsyncOperation waiting;
        bool stopped;

        EditorOperationPump(IEnumerator operation, Action completed, Action<Exception> failed)
        {
            stack.Push(operation ?? throw new ArgumentNullException(nameof(operation)));
            this.completed = completed;
            this.failed = failed;
        }

        public static EditorOperationPump Start(
            IEnumerator operation,
            Action completed,
            Action<Exception> failed)
        {
            EditorOperationPump pump = new EditorOperationPump(operation, completed, failed);
            EditorApplication.update += pump.Tick;
            return pump;
        }

        public void Cancel()
        {
            if (stopped)
                return;
            stopped = true;
            EditorApplication.update -= Tick;
        }

        void Tick()
        {
            if (stopped)
                return;
            try
            {
                if (waiting != null)
                {
                    if (!waiting.isDone)
                        return;
                    waiting = null;
                }

                while (stack.Count > 0)
                {
                    IEnumerator current = stack.Peek();
                    if (!current.MoveNext())
                    {
                        stack.Pop();
                        continue;
                    }

                    if (current.Current is IEnumerator nested)
                    {
                        stack.Push(nested);
                        continue;
                    }
                    if (current.Current is AsyncOperation asyncOperation)
                        waiting = asyncOperation;
                    return;
                }

                Cancel();
                completed?.Invoke();
            }
            catch (Exception ex)
            {
                Cancel();
                failed?.Invoke(ex);
            }
        }
    }
}
