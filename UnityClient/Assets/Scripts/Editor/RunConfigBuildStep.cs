using Client.Communication.Config;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Device builds can't read files outside the build, so this copies runconfig.json from the Unity
/// project root into StreamingAssets for the duration of the build, where <see cref="ServerConfig"/>
/// reads it on the device. The Editor reads the project-root file directly.
/// </summary>
public class RunConfigBuildStep : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    public int callbackOrder => 0;

    static string SourcePath => Path.Combine(Path.GetDirectoryName(Application.dataPath), ServerConfig.RunConfigFileName);
    static string BuildCopyPath => Path.Combine(Application.streamingAssetsPath, ServerConfig.RunConfigFileName);

    public void OnPreprocessBuild(BuildReport report)
    {
        if (!File.Exists(SourcePath))
        {
            Debug.LogWarning($"{nameof(RunConfigBuildStep)}: {SourcePath} not found; the build will use the default run mode.");
            return;
        }

        Directory.CreateDirectory(Application.streamingAssetsPath);
        File.Copy(SourcePath, BuildCopyPath, overwrite: true);
        AssetDatabase.Refresh();
        Debug.Log($"{nameof(RunConfigBuildStep)}: copied {SourcePath} into StreamingAssets for the build.");
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        // Remove the copy so the project-root file stays the only one to edit.
        if (File.Exists(BuildCopyPath))
            AssetDatabase.DeleteAsset("Assets/StreamingAssets/" + ServerConfig.RunConfigFileName);
    }
}
