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
                LoadConfig();
                Initialized = true;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void LoadConfig()
        {
            // Path to StreamingAssets/serverconfig.json
            configPath = Path.Combine(Application.streamingAssetsPath, "serverconfig.json");

            if (File.Exists(configPath))
            {
                try
                {
                    string jsonContents = File.ReadAllText(configPath);
                    ConfigData data = JsonUtility.FromJson<ConfigData>(jsonContents);

                    if (!string.IsNullOrEmpty(data.host))
                    {
                        host = data.host;
                        Debug.Log($"[ServerConfig] Successfully loaded Host: {host}");
                    }

                    if (data.port != 0)
                    {
                        port = data.port;
                        Debug.Log($"[ServerConfig] Successfully loaded Port: {port}");
                    }

                    if (data.reconnectDelay != 0)
                    {
                        retryDelay = data.reconnectDelay;
                        Debug.Log($"[ServerConfig] Successfully loaded Reconnect Delay: {retryDelay}");
                    }

                    if (!string.IsNullOrEmpty(data.username))
                    {
                        username = data.username;
                        Debug.Log($"[ServerConfig] Successfully loaded Username: {username}");
                    }

                    if (!string.IsNullOrEmpty(data.password))
                    {
                        password = data.password;
                        Debug.Log($"[ServerConfig] Successfully loaded Password: {password}");
                    }

                    serverUrl = $"https://{host}:{port}";
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[ServerConfig] Failed to parse config: {e.Message}");
                }
            }
            else
            {
                Debug.LogWarning("[ServerConfig] Config file not found, using default URL.");
            }
        }
    }
}
