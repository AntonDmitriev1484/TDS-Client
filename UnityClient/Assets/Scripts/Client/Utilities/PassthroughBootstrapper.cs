using UnityEngine;
using UnityEngine.SceneManagement;


namespace Client.Utilities
{
    /// <summary>
    /// Turns this project from fully-immersive VR into AR (passthrough) without having to edit
    /// every scene asset. On each scene load it:
    ///   * switches OVRManager into Insight Passthrough mode,
    ///   * adds an underlay OVRPassthroughLayer if the scene doesn't already have one,
    ///   * clears the eye cameras to transparent black, so passthrough shows through wherever
    ///     nothing is rendered. This also replaces the skybox, which would otherwise hide it.
    ///
    /// Passthrough is only composited on-device; the Editor Game view stays black/transparent
    /// unless you run through Meta Quest Link or the Meta XR Simulator.
    ///
    /// Delete this file to go back to fully-immersive VR.
    /// </summary>
    public static class PassthroughBootstrapper
    {
        static readonly Color TransparentBlack = new Color(0f, 0f, 0f, 0f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Apply();
        }

        static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode) => Apply();

        static void Apply()
        {
            var manager = Object.FindFirstObjectByType<OVRManager>(FindObjectsInactive.Include);
            if (manager == null)
                return;

            manager.isInsightPassthroughEnabled = true;

            if (Object.FindFirstObjectByType<OVRPassthroughLayer>(FindObjectsInactive.Include) == null)
            {
                // Defaults are already what we want: a full-screen Reconstructed underlay at full
                // opacity. Setting projectionSurfaceType/overlayType explicitly is deprecated in v205.
                var host = new GameObject("[AR] Passthrough");
                host.transform.SetParent(manager.transform, false);
                host.AddComponent<OVRPassthroughLayer>();
            }

            foreach (var rig in Object.FindObjectsByType<OVRCameraRig>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
            {
                ClearToPassthrough(rig.leftEyeCamera);
                ClearToPassthrough(rig.rightEyeCamera);
                ClearToPassthrough(rig.centerEyeAnchor != null ? rig.centerEyeAnchor.GetComponent<Camera>() : null);
            }
        }

        static void ClearToPassthrough(Camera cam)
        {
            // Cameras rendering into a RenderTexture (OVROverlayCanvas, the video samples) keep their own setup.
            if (cam == null || cam.targetTexture != null)
                return;

            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = TransparentBlack;
        }
    }
}
