using Client.Communication.Manager;
using System.Threading;
using UnityEngine;

namespace Client.Utilities
{
    /// <summary>
    /// <c>Debug.Log</c> that is only sent when <c>debugMode</c> is true in the server config (<see cref="Client.Communication.Config.ServerConfig.DebugMode"/>),
    /// and is always sent on the main thread: a call from another thread (the web socket, a task) is queued for the next frame by the
    /// <see cref="MainThreadDispatcher"/>. Warnings and errors are not affected; they still use <c>Debug.LogWarning</c> and <c>Debug.LogError</c>.
    /// <para>
    /// The message string is built before <see cref="Log"/> is called, so at a place that runs often, check <see cref="Enabled"/> first
    /// (<c>if (DebugLog.Enabled) DebugLog.Log($"...");</c>) to skip the string building too when debug mode is off.
    /// </para>
    /// </summary>
    public static class DebugLog
    {
        private static int _mainThreadId = -1;
        private static volatile bool _enabled;

        /// <summary>
        /// Whether debug logging is on. Set by <see cref="Client.Communication.Config.ServerConfig"/> when it loads the config (off until then).
        /// A plain field, so any thread can read it without touching a Unity object.
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            // Runs on the main thread. Making sure a dispatcher exists lets logs from other threads reach it.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _ = MainThreadDispatcher.Instance;
        }

        /// <summary>Logs the message on the main thread if debug mode is on; does nothing otherwise.</summary>
        public static void Log(string message)
        {
            if (!_enabled)
                return;

            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                Debug.Log(message);
            else
                MainThreadDispatcher.Enqueue(() => Debug.Log(message));
        }
    }
}
