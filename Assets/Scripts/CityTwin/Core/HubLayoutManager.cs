using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.Core
{
    /// <summary>
    /// Holds all hub layout presets and activates one at random on Awake.
    /// Each preset is a child GameObject containing ResidentialHubMono instances
    /// and its own connection configuration.
    /// All non-selected presets are deactivated so HubRegistry only finds the active hubs.
    /// </summary>
    public class HubLayoutManager : MonoBehaviour
    {
        [Tooltip("All available hub layout presets. One will be chosen at random on startup.")]
        [SerializeField] private List<HubLayoutPreset> presets = new List<HubLayoutPreset>();

        [Header("Random background")]
        [Tooltip("Sprites to pick from at random on start/restart.")]
        [SerializeField] private Sprite[] backgrounds;
        [Tooltip("The Image component that displays the game background.")]
        [SerializeField] private Image backgroundImage;

        /// <summary>The preset that was selected this session. Set in Awake.</summary>
        public HubLayoutPreset ActivePreset { get; private set; }

        private void Awake()
        {
            PickRandomPreset();
        }

        /// <summary>Select a random preset, activate it, deactivate all others. Safe to call at runtime for restart flows.</summary>
        public void PickRandomPreset()
        {
            if (presets == null || presets.Count == 0)
            {
                Debug.LogWarning("[HubLayoutManager] No presets assigned.");
                return;
            }

            int index = Random.Range(0, presets.Count);

            for (int i = 0; i < presets.Count; i++)
            {
                if (presets[i] == null) continue;
                presets[i].gameObject.SetActive(i == index);
            }

            ActivePreset = presets[index];
            Debug.Log($"[HubLayoutManager] Selected preset '{ActivePreset.gameObject.name}' ({index + 1}/{presets.Count})");

            ApplyRandomBackground();
        }

        private void ApplyRandomBackground()
        {
            if (backgroundImage == null || backgrounds == null || backgrounds.Length == 0) return;
            backgroundImage.sprite = backgrounds[Random.Range(0, backgrounds.Length)];
        }
    }
}
