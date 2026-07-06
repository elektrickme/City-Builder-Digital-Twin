using System;
using System.Collections.Generic;
using UnityEngine;
using CityTwin.Config;

namespace CityTwin.Localization
{
    /// <summary>Per-instance localization. Holds reference to config localization data; no statics.</summary>
    public class LocalizationService : MonoBehaviour
    {
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private string currentLanguage = "EN";

        private Dictionary<string, string> _currentTable;

        /// <summary>Fired when CurrentLanguage is set. Use for LocalizedLabel etc. to refresh.</summary>
        public event Action OnLanguageChanged;

        public string CurrentLanguage
        {
            get => currentLanguage;
            set
            {
                currentLanguage = value ?? "EN";
                RefreshTable();
                OnLanguageChanged?.Invoke();
            }
        }

        private void Awake()
        {
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true);
        }

        private void OnEnable()
        {
            if (configLoader != null)
                configLoader.OnConfigLoaded += HandleConfigLoaded;
            RefreshTable();
        }

        private void OnDisable()
        {
            if (configLoader != null)
                configLoader.OnConfigLoaded -= HandleConfigLoaded;
        }

        private void HandleConfigLoaded(GameConfig _)
        {
            ReloadFromConfig();
            OnLanguageChanged?.Invoke();
        }

        private void RefreshTable()
        {
            _currentTable = null;
            if (configLoader?.Config?.Localization == null) return;
            if (configLoader.Config.Localization.TryGetValue(currentLanguage, out var table))
                _currentTable = table;
            else if (configLoader.Config.Meta != null &&
                     configLoader.Config.Localization.TryGetValue(configLoader.Config.Meta.defaultLanguage ?? "EN", out var def))
                _currentTable = def;
        }

        /// <summary>Get localized string for key. Returns key if missing.</summary>
        public string GetString(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (_currentTable != null && _currentTable.TryGetValue(key, out string value))
                // Config strings may carry literal "\n" escapes — sometimes double-escaped ("\\n")
                // depending on how the JSON was authored. Collapse any run of backslashes before an
                // 'n' into a real line break at the single display choke point.
                return System.Text.RegularExpressions.Regex.Replace(value, @"\\+n", "\n");
            return key;
        }

        /// <summary>Call after config is loaded to refresh from config.</summary>
        public void ReloadFromConfig()
        {
            if (configLoader?.Config?.Meta != null)
                currentLanguage = configLoader.Config.Meta.defaultLanguage ?? "EN";
            RefreshTable();
        }
    }
}
