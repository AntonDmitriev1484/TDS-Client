using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Client.Scene.Manager
{
    public class CustomSceneManager : MonoBehaviour
    {
        #region Properties

        public static CustomSceneManager Instance { get; set; } // Singleton Instance
        public bool IsInitialized { get; set; }

        #region Events

        public event Action<UnityEngine.SceneManagement.Scene> OnSceneUnloading = delegate { };
        public event Action<UnityEngine.SceneManagement.Scene> OnSceneUnloaded = delegate { };
        public event Action<UnityEngine.SceneManagement.Scene> OnSceneLoaded = delegate { };

        #endregion Events

        #endregion Properties

        #region Initialization


        /// <summary>
        /// Singleton for Custom Scene Manager.
        /// </summary>
        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);

                // Set initial variables on state and settings
                IsInitialized = false;

                // Initialize the custom scene manager.
                Initialize();
            }
            else
            {
                Destroy(gameObject);
            }
        }

        public void Initialize()
        {
            IsInitialized = true;
        }

        #endregion Initialization

        #region Scene Transitions

        /// <summary>
        /// Perform the custom scene loading.
        /// </summary>
        /// <param name="sceneName">The name of the scene.</param>
        /// <returns></returns>
        public async Task LoadSceneAsync(SceneName sceneName)
        {
            UnityEngine.SceneManagement.Scene currentScene = SceneManager.GetActiveScene();
            OnSceneUnloading.Invoke(currentScene);

            AsyncOperation asyncLoadingScene = SceneManager.LoadSceneAsync(sceneName.ToString());

            // Wait until the scene is fully loaded
            while (!asyncLoadingScene.isDone)
            {
                await Task.Yield(); // Keep waiting until the scene finishes loading
            }

            UnityEngine.SceneManagement.Scene newScene = SceneManager.GetActiveScene();

            OnSceneUnloaded.Invoke(newScene);
            OnSceneLoaded.Invoke(newScene);
        }

        #endregion Scene Transitions
    }

    public enum SceneName
    {
        InitialEntrypoint,
        ServerError,
        Loading,

        MainMenu,

        GameState,
    }
}
