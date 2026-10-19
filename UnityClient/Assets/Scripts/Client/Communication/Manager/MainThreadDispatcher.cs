using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace Client.Communication.Manager
{
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly object _lock = new object();
        private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            GameObject obj = new GameObject("MainThreadDispatcher");
                            _instance = obj.AddComponent<MainThreadDispatcher>();
                            DontDestroyOnLoad(obj);
                        }
                    }
                }
                return _instance;
            }
        }

        public static void Enqueue(Action action)
        {
            _executionQueue.Enqueue(action);
        }

        private void Update()
        {
            while (_executionQueue.TryDequeue(out var action))
            {
                action.Invoke();
            }
        }
    }
}
