using System.IO;
using UnityEditor;
using UnityEngine;

namespace Desqueeze.EditorTools
{
    /// <summary>
    /// Editor conveniences: applies desktop-app player settings once per project (windowed, resizable,
    /// keeps running in the background), and adds a Desqueeze menu.
    /// </summary>
    [InitializeOnLoad]
    internal static class ProjectSetup
    {
        private const string AppliedKey = "Desqueeze.PlayerSettingsApplied.v1";

        static ProjectSetup()
        {
            EditorApplication.delayCall += ApplyOnce;
        }

        private static void ApplyOnce()
        {
            if (EditorUserSettings.GetConfigValue(AppliedKey) == "1") return;
            ApplyPlayerSettings();
            EditorUserSettings.SetConfigValue(AppliedKey, "1");
        }

        [MenuItem("Desqueeze/Apply Recommended Player Settings")]
        private static void ApplyPlayerSettings()
        {
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 1000;
            PlayerSettings.runInBackground = true;
            PlayerSettings.macRetinaSupport = true;
            AssetDatabase.SaveAssets();
            Debug.Log("[Desqueeze] Player settings applied: windowed, resizable, 1600x1000, runs in background, Retina on. " +
                      "(Menu: Desqueeze > Apply Recommended Player Settings)");
        }

        private static string ConfigPath => Path.Combine(Application.streamingAssetsPath, "desqueeze-config.json");

        [MenuItem("Desqueeze/Open Config File")]
        private static void OpenConfig()
        {
            if (File.Exists(ConfigPath)) EditorUtility.OpenWithDefaultApp(ConfigPath);
            else Debug.LogWarning("[Desqueeze] Config file not found: " + ConfigPath);
        }

        [MenuItem("Desqueeze/Reveal Config File")]
        private static void RevealConfig()
        {
            EditorUtility.RevealInFinder(ConfigPath);
        }

        [MenuItem("Desqueeze/Forget Remembered Settings")]
        private static void ClearPrefs()
        {
            string[] keys =
            {
                "Factor", "WidthShare", "Algorithm", "ReduceLight", "EnlargeLight", "AntiRinging", "SaveFormat", "OpenDir", "SaveDir",
                "Mode", "BatchIn", "BatchOut", "BatchSubfolders", "BatchOverwrite", "BatchSuffix",
                "Direction", "Widen", "DistortionAmount", "DistortionEdges", "DistortionOrder", "CalTool", "LineSnap", "ProfileDir", "LensProfilePath",
            };
            foreach (var k in keys) PlayerPrefs.DeleteKey("Desqueeze." + k);
            PlayerPrefs.Save();
            Debug.Log("[Desqueeze] Remembered settings cleared; the config file defaults apply on next launch.");
        }
    }
}
