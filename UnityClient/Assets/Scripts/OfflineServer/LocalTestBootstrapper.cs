using Client.Communication.Config;
using System.Collections;
using UnityEngine;

/// <summary>
/// In local test mode (run_mode "local_test" in runconfig.json), starts the in-app game server: an
/// <see cref="OfflineGameStateManager"/>, whose <see cref="MessageHandler"/> the client reaches over
/// loopback TCP. Does nothing in remote mode, or if the scene already has an offline server.
/// </summary>
public class LocalTestBootstrapper : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreateBootstrapper()
    {
        new GameObject(nameof(LocalTestBootstrapper)).AddComponent<LocalTestBootstrapper>();
    }

    IEnumerator Start()
    {
        while (ServerConfig.Instance == null || !ServerConfig.Instance.Initialized)
            yield return null;

        if (ServerConfig.Instance.IsLocalTest && FindFirstObjectByType<OfflineGameStateManager>() == null)
        {
            Debug.Log($"{nameof(LocalTestBootstrapper)}: local test mode; starting the offline game server.");
            new GameObject("OfflineGameServer").AddComponent<OfflineGameStateManager>();
        }

        Destroy(gameObject);
    }
}
