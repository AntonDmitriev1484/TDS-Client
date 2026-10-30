using System.Collections;
using System.IO;
using UnityEngine;

namespace Client.Communication.Config
{
    // A simple class to match our JSON structure
    [System.Serializable]
    public class ConfigData
    {
        public string host;
        public int port;
        public float reconnectDelay;
        public string username;
        public string password;
    }

    /// <summary>Matches runconfig.json in the Unity project root.</summary>
    [System.Serializable]
    public class RunConfigData
    {
        public string run_mode;
    }

    public enum RunMode
    {
        /// <summary>Exchange messages with the in-app OfflineGameStateManager over loopback TCP.</summary>
        LocalTest,
        /// <summary>Log in to and play on the game server at the configured host.</summary>
        Remote,
    }

    public class ServerConfig : MonoBehaviour
    {
        /// <summary>
        /// runconfig.json lives in the Unity project root. The Editor reads it there; a build step
        /// copies it into StreamingAssets so device builds can read it too.
        /// </summary>
        public const string RunConfigFileName = "runconfig.json";

        public bool Initialized = false;
        private string configPath;
        private string serverUrl;
        private string host = "localhost"; // Fallback default
        private int port = 5000; // Fallback default
        private float retryDelay = 1f; // Fallback default
        private string username = "Chitty"; // Fallback default
        private string password = "ChittyPassword"; // Fallback default
        private RunMode runMode = RunMode.Remote; // Fallback default

        public RunMode RunMode => runMode;
        public bool IsLocalTest => runMode == RunMode.LocalTest;
        public string ServerUrl => serverUrl;
        public string Host => host;
        public int Port => port;
        public float RetryDelay => retryDelay;
        public string Username => username;
        public string Password => password;


        public static ServerConfig Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                StartCoroutine(LoadConfigCoroutine());
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private IEnumerator LoadConfigCoroutine()
        {
            string configPath = Path.Combine(Application.streamingAssetsPath, "serverconfig.json");

            using (var request = UnityEngine.Networking.UnityWebRequest.Get(configPath))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    try
                    {
                        ConfigData data = JsonUtility.FromJson<ConfigData>(request.downloadHandler.text);

                        if (!string.IsNullOrEmpty(data.host)) host = data.host;
                        if (data.port != 0) port = data.port;
                        if (data.reconnectDelay != 0) retryDelay = data.reconnectDelay;
                        if (!string.IsNullOrEmpty(data.username)) username = data.username;
                        if (!string.IsNullOrEmpty(data.password)) password = data.password;

                        serverUrl = $"https://{host}:{port}";
                        Debug.Log($"[ServerConfig] Loaded config. URL: {serverUrl}");
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[ServerConfig] Failed to parse config: {e.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ServerConfig] Could not load config: {request.error}. Using defaults.");
                    serverUrl = $"https://{host}:{port}";
                }
            }

            yield return LoadRunConfigCoroutine();

            Initialized = true;
        }

        private IEnumerator LoadRunConfigCoroutine()
        {
            string json = null;

#if UNITY_EDITOR
            // Application.dataPath is <project>/Assets.
            string runConfigPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), RunConfigFileName);
            if (File.Exists(runConfigPath))
                json = File.ReadAllText(runConfigPath);
#else
            string runConfigPath = Path.Combine(Application.streamingAssetsPath, RunConfigFileName);
            using (var request = UnityEngine.Networking.UnityWebRequest.Get(runConfigPath))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    json = request.downloadHandler.text;
            }
#endif

            if (string.IsNullOrWhiteSpace(json))
            {
                Debug.LogWarning($"[ServerConfig] Could not load {runConfigPath}. Using run mode {runMode}.");
                yield break;
            }

            try
            {
                RunConfigData data = JsonUtility.FromJson<RunConfigData>(json);

                switch (data.run_mode)
                {
                    case "local_test": runMode = RunMode.LocalTest; break;
                    case "remote": runMode = RunMode.Remote; break;
                    default:
                        Debug.LogError($"[ServerConfig] Unknown run_mode '{data.run_mode}' in {runConfigPath}; expected 'local_test' or 'remote'. Using run mode {runMode}.");
                        yield break;
                }

                Debug.Log($"[ServerConfig] Run mode: {runMode}.");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ServerConfig] Failed to parse {runConfigPath}: {e.Message}");
            }
        }
    }
}
