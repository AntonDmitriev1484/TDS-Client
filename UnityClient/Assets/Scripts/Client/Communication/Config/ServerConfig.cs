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

    public class ServerConfig : MonoBehaviour
    {
        public bool Initialized = false;
        private string configPath;
        private string serverUrl;
        private string host = "localhost"; // Fallback default
        private int port = 5000; // Fallback default
        private float retryDelay = 1f; // Fallback default
        private string username = "Chitty"; // Fallback default
        private string password = "ChittyPassword"; // Fallback default

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

            Initialized = true;
        }
    }
}
