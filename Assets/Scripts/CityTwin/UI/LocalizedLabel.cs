using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CityTwin.Core;
using CityTwin.Localization;

namespace CityTwin.UI
{
    /// <summary>
    /// Attach to a button or any GameObject with text. Shows the localized string for
    /// <see cref="localizationKey"/> using the currently chosen language and refreshes
    /// when language changes. Resolves LocalizationService from GameInstanceRoot (one parent walk).
    /// </summary>
    [DisallowMultipleComponent]
    public class LocalizedLabel : MonoBehaviour
    {
        [SerializeField] private LocalizationService localization;
        [Tooltip("Key in your localization table (e.g. start.english, ui.timer).")]
        [SerializeField] private string localizationKey;

        [Header("Target (optional)")]
        [Tooltip("If set, this text is updated. Otherwise uses TextMeshProUGUI or Text on this GameObject.")]
        [SerializeField] private TextMeshProUGUI targetTmp;

        private void Awake()
        {
            if (localization == null)
            {
                var root = GetComponentInParent<GameInstanceRoot>(true);
                if (root != null)
                    localization = root.LocalizationService;
            }
            // The tooltip has always promised this fallback; without it a label with no explicit
            // target silently does nothing (several end-screen labels were dormant because of it).
            if (targetTmp == null)
                targetTmp = GetComponent<TextMeshProUGUI>();
        }

        private void OnEnable()
        {
            if (localization != null)
                localization.OnLanguageChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (localization != null)
                localization.OnLanguageChanged -= Refresh;
        }

        /// <summary>Set the key and refresh (e.g. from code).</summary>
        public void SetKey(string key)
        {
            localizationKey = key ?? "";
            Refresh();
        }

        /// <summary>Apply the current language string to the target text.</summary>
        public void Refresh()
        {
            string text = localization != null && !string.IsNullOrEmpty(localizationKey)
                ? localization.GetString(localizationKey)
                : localizationKey;

            if (targetTmp != null)
                targetTmp.text = text;
        }
    }
}
