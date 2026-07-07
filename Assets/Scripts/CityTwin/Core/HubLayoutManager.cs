using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.Core
{
    /// <summary>
    /// Holds all hub layout presets and activates one on Awake and on round restart.
    /// Each preset is a child GameObject containing ResidentialHubMono instances
    /// and its own connection configuration.
    /// All non-selected presets are deactivated so HubRegistry only finds the active hubs.
    ///
    /// A saved custom road layout (StreamingAssets/road_layout.json) pins its preset: when the
    /// file names a preset, that preset ALWAYS loads — launch and every restart — so hand-tuned
    /// maps are deterministic. Without a saved layout the preset is random, as before.
    /// </summary>
    public class HubLayoutManager : MonoBehaviour
    {
        [Tooltip("All available hub layout presets. Startup picks the preset pinned by the saved road layout, or a random one when no layout is saved.")]
        [SerializeField] private List<HubLayoutPreset> presets = new List<HubLayoutPreset>();

        [Header("Random background")]
        [Tooltip("Sprites to pick from at random on start/restart.")]
        [SerializeField] private Sprite[] backgrounds;
        [Tooltip("The Image component that displays the game background.")]
        [SerializeField] private Image backgroundImage;

        /// <summary>The preset that was selected this session. Set in Awake.</summary>
        public HubLayoutPreset ActivePreset { get; private set; }

        /// <summary>Fired after a preset is activated (startup and every restart). The road
        /// layout editor re-applies the saved layout on this signal.</summary>
        public event System.Action OnPresetActivated;

        /// <summary>Just the preset name out of road_layout.json — the rest is the editor's.</summary>
        [System.Serializable] private class LayoutPeek { public string preset; }

        private void Awake()
        {
            PickPresetForRound();
        }

        /// <summary>Select the preset for a round: the one pinned by the saved road layout when
        /// present, otherwise random. Safe to call at runtime for restart flows.</summary>
        public void PickPresetForRound()
        {
            if (presets == null || presets.Count == 0)
            {
                Debug.LogWarning("[HubLayoutManager] No presets assigned.");
                return;
            }

            int index = FindSavedPresetIndex();
            if (index < 0) index = Random.Range(0, presets.Count);
            ActivateIndex(index);
        }

        /// <summary>Legacy entry point; routes through the saved-first rule.</summary>
        public void PickRandomPreset() => PickPresetForRound();

        private void ActivateIndex(int index)
        {
            for (int i = 0; i < presets.Count; i++)
            {
                if (presets[i] == null) continue;
                presets[i].gameObject.SetActive(i == index);
            }

            ActivePreset = presets[index];

            ApplyRandomBackground();
            OnPresetActivated?.Invoke();
        }

        /// <summary>Index of the preset pinned by the saved road layout, or -1 when there is no
        /// saved layout / no pin / the pinned preset no longer exists (all logged).</summary>
        private int FindSavedPresetIndex()
        {
            try
            {
                string path = RoadNetworkEditor.SharedLayoutPath;
                if (!File.Exists(path)) return -1;
                var peek = JsonUtility.FromJson<LayoutPeek>(File.ReadAllText(path));
                if (peek == null || string.IsNullOrEmpty(peek.preset)) return -1;

                for (int i = 0; i < presets.Count; i++)
                    if (presets[i] != null && presets[i].gameObject.name == peek.preset)
                    {
                        Debug.Log($"[HubLayoutManager] Saved road layout pins preset '{peek.preset}'.");
                        return i;
                    }

                Debug.LogWarning($"[HubLayoutManager] Saved road layout names preset '{peek.preset}' but no such preset exists - picking random.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[HubLayoutManager] Could not read saved road layout: " + e.Message);
            }
            return -1;
        }

        private void ApplyRandomBackground()
        {
            if (backgroundImage == null || backgrounds == null || backgrounds.Length == 0) return;
            backgroundImage.sprite = backgrounds[Random.Range(0, backgrounds.Length)];
        }
    }
}
