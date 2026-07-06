using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using CityTwin.UI;

namespace CityTwin.EditorTools
{
    /// <summary>
    /// Persists liquid tuning edited during Play mode. Unity discards Play-mode changes
    /// to serialized fields; on returning to Edit mode this reloads the JSON that
    /// LiquidSurfaceControl wrote on Play-mode exit and marks the scene dirty, so the
    /// values you dialed in while playing survive (save the scene to keep them).
    /// </summary>
    [InitializeOnLoad]
    internal static class LiquidTuningPersistence
    {
        static LiquidTuningPersistence()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;

#if UNITY_2023_1_OR_NEWER
            var controls = Object.FindObjectsByType<LiquidSurfaceControl>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var controls = Object.FindObjectsOfType<LiquidSurfaceControl>(true);
#endif
            bool any = false;
            foreach (var c in controls)
            {
                if (c != null && c.LoadTuning())
                {
                    EditorUtility.SetDirty(c);
                    any = true;
                }
            }

            if (any)
            {
                var scene = SceneManager.GetActiveScene();
                if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            }
        }
    }
}
