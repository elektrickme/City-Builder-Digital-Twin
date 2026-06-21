using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CityTwin.Config
{
    /// <summary>Platform-abstracted export/import of the config JSON, used by the debug menu.
    /// WebGL: browser file download / upload (via HwConfigIO.jslib).
    /// Editor: native save / open file panels.
    /// Standalone: a file under persistentDataPath.
    /// Import is async on WebGL (file picker), so the flow is: BeginImport() then poll TryTakeImport()
    /// each frame until it returns true. Editor/standalone resolve synchronously and surface on the next poll.</summary>
    public static class ConfigFileIO
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void HwDownloadTextFile(string filename, string content);
        [DllImport("__Internal")] private static extern void HwOpenTextFilePicker(string accept);
        [DllImport("__Internal")] private static extern string HwTakePendingUpload();
#endif

        private const string DefaultFileName = "game_config.json";

        private static string _pendingImport;
        private static bool _importInFlight;

        /// <summary>Save/download the JSON as a file the user keeps.</summary>
        public static void Export(string filename, string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            if (string.IsNullOrEmpty(filename)) filename = DefaultFileName;
#if UNITY_WEBGL && !UNITY_EDITOR
            HwDownloadTextFile(filename, json);
#elif UNITY_EDITOR
            string path = UnityEditor.EditorUtility.SaveFilePanel("Export config", "", filename, "json");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, json);
#else
            string p = Path.Combine(Application.persistentDataPath, filename);
            File.WriteAllText(p, json);
            Debug.Log($"[ConfigFileIO] Exported to {p}");
#endif
        }

        /// <summary>Start an import. On WebGL this opens a browser file picker (async - poll TryTakeImport).
        /// On Editor/standalone the file is read immediately and returned on the next TryTakeImport.</summary>
        public static void BeginImport()
        {
            _pendingImport = null;
            _importInFlight = true;
#if UNITY_WEBGL && !UNITY_EDITOR
            HwOpenTextFilePicker(".json,application/json");
#elif UNITY_EDITOR
            string path = UnityEditor.EditorUtility.OpenFilePanel("Import config", "", "json");
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) _pendingImport = File.ReadAllText(path);
            else _importInFlight = false; // cancelled
#else
            string p = Path.Combine(Application.persistentDataPath, DefaultFileName);
            if (File.Exists(p)) _pendingImport = File.ReadAllText(p);
            else { _importInFlight = false; Debug.LogWarning($"[ConfigFileIO] No file at {p} to import."); }
#endif
        }

        /// <summary>Returns true exactly once when imported JSON becomes available. Call every frame after BeginImport.</summary>
        public static bool TryTakeImport(out string json)
        {
            json = null;
            if (!_importInFlight) return false;
#if UNITY_WEBGL && !UNITY_EDITOR
            string s = HwTakePendingUpload();
            if (string.IsNullOrEmpty(s)) return false; // still waiting on the user / file read
            _importInFlight = false;
            json = s;
            return true;
#else
            if (_pendingImport == null) { _importInFlight = false; return false; }
            json = _pendingImport;
            _pendingImport = null;
            _importInFlight = false;
            return true;
#endif
        }
    }
}
