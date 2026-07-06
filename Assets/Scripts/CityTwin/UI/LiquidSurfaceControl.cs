using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.UI
{
    /// <summary>
    /// Central control for every liquid map surface in the scene. Put this on a
    /// "Simulation Control" GameObject and edit the parameters here — they are pushed
    /// to all <see cref="LiquidSurface"/> instances (all four game instances) live,
    /// including while playing, so you can tune the whole table from one place.
    /// </summary>
    [DisallowMultipleComponent]
    public class LiquidSurfaceControl : MonoBehaviour
    {
        [Tooltip("Master liquid parameters. Edit these live to tune every surface at once.")]
        [SerializeField] private LiquidTuning tuning = new LiquidTuning();

        [Tooltip("If empty and Auto Collect is on, every LiquidSurface in the scene is driven.")]
        [SerializeField] private List<LiquidSurface> surfaces = new List<LiquidSurface>();
        [Tooltip("Auto-find all LiquidSurface components in the scene at startup.")]
        [SerializeField] private bool autoCollect = true;

        [Header("Persistence")]
        [Tooltip("Save tuning to a JSON file so values changed in Play mode are not lost. On returning to Edit mode an editor hook writes them back into the scene.")]
        [SerializeField] private bool persistTuning = true;
        [Tooltip("Also load the saved file at startup (Awake), so a build resumes with the last-used tuning. Leave off to keep the inspector values authoritative.")]
        [SerializeField] private bool loadOnStartup = false;
        [Tooltip("File name under Application.persistentDataPath.")]
        [SerializeField] private string tuningFileName = "liquid_tuning.json";

        /// <summary>Master parameters shared by every driven surface.</summary>
        public LiquidTuning Tuning => tuning;

        /// <summary>Absolute path of the persisted tuning file.</summary>
        public string TuningFilePath => System.IO.Path.Combine(Application.persistentDataPath, tuningFileName);

        private void Awake()
        {
            Collect();
            if (persistTuning && loadOnStartup) LoadTuning();
        }

        private void Start()
        {
            Apply();
        }

        private void OnDisable()
        {
            // Fires on Play-mode exit — capture whatever was tuned while playing.
            if (persistTuning && Application.isPlaying) SaveTuning();
        }

        private void OnApplicationQuit()
        {
            if (persistTuning) SaveTuning();
        }

        private void Update()
        {
            // Cheap (a few material.SetFloat per surface) and lets inspector edits take
            // effect immediately while playing.
            if (Application.isPlaying) Apply();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying) Apply();
        }
#endif

        private void Collect()
        {
            if (!autoCollect || surfaces.Count > 0) return;
#if UNITY_2023_1_OR_NEWER
            surfaces.AddRange(FindObjectsByType<LiquidSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None));
#else
            surfaces.AddRange(FindObjectsOfType<LiquidSurface>(true));
#endif
        }

        /// <summary>Push the master tuning to every driven surface.</summary>
        public void Apply()
        {
            for (int i = 0; i < surfaces.Count; i++)
                if (surfaces[i] != null) surfaces[i].ApplyTuning(tuning);
        }

        /// <summary>Write the current tuning to the JSON file.</summary>
        [ContextMenu("Save Tuning To File")]
        public void SaveTuning()
        {
            try { System.IO.File.WriteAllText(TuningFilePath, JsonUtility.ToJson(tuning, true)); }
            catch (System.Exception e) { Debug.LogWarning($"[LiquidSurfaceControl] tuning save failed: {e.Message}", this); }
        }

        /// <summary>Overwrite the tuning from the JSON file (if present) and re-apply. Returns true if loaded.</summary>
        [ContextMenu("Load Tuning From File")]
        public bool LoadTuning()
        {
            try
            {
                var p = TuningFilePath;
                if (!System.IO.File.Exists(p)) return false;
                JsonUtility.FromJsonOverwrite(System.IO.File.ReadAllText(p), tuning);
                Apply();
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[LiquidSurfaceControl] tuning load failed: {e.Message}", this);
                return false;
            }
        }
    }
}
