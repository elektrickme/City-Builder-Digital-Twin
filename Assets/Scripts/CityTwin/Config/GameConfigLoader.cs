using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using CityTwin.Core;

namespace CityTwin.Config
{
    /// <summary>Loads game_config.json from StreamingAssets. Caches in instance; no statics.</summary>
    /// <remarks>
    /// Uses a two-pass parse: JsonUtility for most of the config, and a small hand-written parser
    /// for the localization section only (JsonUtility cannot deserialize nested dictionaries).
    /// </remarks>
    public class GameConfigLoader : MonoBehaviour
    {
        [Tooltip("Path relative to StreamingAssets.")]
        [SerializeField] private string configPath = "game_config.json";

        private GameConfig _cachedConfig;
        private bool _isLoading;

        /// <summary>Raised when config is loaded/reloaded successfully.</summary>
        public event Action<GameConfig> OnConfigLoaded;

        private void Awake()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL cannot read StreamingAssets with System.IO; keep defaults until async web load completes.
            _cachedConfig = CreateDefaultConfig();
            BeginWebLoadIfNeeded();
#endif
        }

        /// <summary>Loaded config (from cache or load). Returns null if load fails.</summary>
        public GameConfig Config
        {
            get
            {
                if (_cachedConfig == null)
                    Load();
                return _cachedConfig;
            }
        }

        /// <summary>Load from StreamingAssets. Uses fallback defaults on missing/valid fields.</summary>
        public bool Load()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            BeginWebLoadIfNeeded();
            return _cachedConfig != null;
#else
            string path = Path.Combine(Application.streamingAssetsPath, configPath);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[GameConfigLoader] Config not found: {path}. Using defaults.");
                _cachedConfig = CreateDefaultConfig();
                return false;
            }
            string json = File.ReadAllText(path);
            bool ok = TryParse(json, out var parsed);
            _cachedConfig = ok ? parsed : CreateDefaultConfig();
            if (ok)
                OnConfigLoaded?.Invoke(_cachedConfig);
            return ok;
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private void BeginWebLoadIfNeeded()
        {
            if (_isLoading) return;
            StartCoroutine(LoadFromWebGlStreamingAssets());
        }

        private IEnumerator LoadFromWebGlStreamingAssets()
        {
            _isLoading = true;
            string url = CombineStreamingAssetsUrl(Application.streamingAssetsPath, configPath);
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[GameConfigLoader] Config fetch failed: {url} ({req.error}). Using defaults.");
                    _isLoading = false;
                    yield break;
                }

                if (TryParse(req.downloadHandler.text, out var parsed))
                {
                    _cachedConfig = parsed;
                    OnConfigLoaded?.Invoke(_cachedConfig);
                }
                else
                {
                    _cachedConfig = CreateDefaultConfig();
                }
            }

            _isLoading = false;
        }

        private static string CombineStreamingAssetsUrl(string root, string relativePath)
        {
            string normalizedRoot = (root ?? string.Empty).TrimEnd('/', '\\');
            string normalizedRel = (relativePath ?? string.Empty).TrimStart('/', '\\').Replace("\\", "/");
            return $"{normalizedRoot}/{normalizedRel}";
        }
#endif

        /// <summary>Parse JSON string into config. Returns false on parse error (config may be partial/default).</summary>
        /// <remarks>
        /// Two-pass approach: Unity JsonUtility cannot deserialize nested dictionaries (e.g. localization.en, .ru).
        /// So we (1) strip the localization object to "{}" and parse the rest with JsonUtility, then
        /// (2) parse the original JSON by hand only for the localization section.
        /// </remarks>
        public bool TryParse(string json, out GameConfig config)
        {
            config = new GameConfig();
            try
            {
                string jsonWithoutLoc = StripLocalization(json);
                GameConfigRoot root = JsonUtility.FromJson<GameConfigRoot>(jsonWithoutLoc);
                if (root == null) return false;

                config.Meta = root.meta ?? new GameConfig.MetaData();
                config.Session = root.session ?? new GameConfig.SessionData();
                config.Budget = root.budget ?? new GameConfig.BudgetData();
                config.Scoring = root.scoring ?? new GameConfig.ScoringData();
                config.Accessibility = root.accessibility ?? new GameConfig.AccessibilityData();
                if (config.Accessibility != null)
                {
                    if (config.Accessibility.roadConnectRange <= 0) config.Accessibility.roadConnectRange = 200f;
                    if (config.Accessibility.zoneRadius <= 0) config.Accessibility.zoneRadius = 200f;
                    if (config.Accessibility.defaultConnectionRadius <= 0) config.Accessibility.defaultConnectionRadius = 500f;
                }
                config.Osc = root.osc ?? new GameConfig.OscData();
                config.Tooltips = root.tooltips ?? new GameConfig.TooltipsData();
                config.Stops = root.stops ?? new GameConfig.StopsData();
                config.Tutorial = root.tutorial ?? new GameConfig.TutorialData();
                config.Inactivity = root.inactivity ?? new GameConfig.InactivityData();
                config.EndMessages = root.endMessages ?? Array.Empty<GameConfig.EndMessageData>();

                config.Buildings = MapBuildings(root.buildings);
                config.Map = root.map;
                config.Localization = ParseLocalization(json);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameConfigLoader] Parse error: {e}. Using defaults.");
                config = CreateDefaultConfig();
                return false;
            }
        }

        /// <summary>Replace the "localization": { ... } block with "localization":{} so JsonUtility can parse the rest.</summary>
        private static string StripLocalization(string json)
        {
            int start = json.IndexOf("\"localization\"", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return json;
            int colon = json.IndexOf(':', start);
            if (colon < 0) return json;
            int braceStart = json.IndexOf('{', colon);
            if (braceStart < 0) return json;
            int depth = 1;
            int i = braceStart + 1;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                i++;
            }
            return json.Substring(0, start) + "\"localization\":{}" + json.Substring(i);
        }

        /// <summary>Parse only the localization object from JSON (language -> key -> value). JsonUtility cannot do this for nested dictionaries.</summary>
        private static Dictionary<string, Dictionary<string, string>> ParseLocalization(string json)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            int locStart = json.IndexOf("\"localization\"", StringComparison.OrdinalIgnoreCase);
            if (locStart < 0) return result;
            int objStart = json.IndexOf('{', locStart);
            if (objStart < 0) return result;

            int depth = 1;
            int i = objStart + 1;
            string currentLang = null; // when inside a language object (e.g. "EN": { ... }), keys go into result[currentLang]

            while (i < json.Length && depth > 0)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;

                if (json[i] == '}')
                {
                    depth--;
                    if (depth == 1) currentLang = null;
                    i++;
                    continue;
                }
                if (json[i] == '{')
                {
                    depth++;
                    i++;
                    continue;
                }
                if (json[i] == '"')
                {
                    string key = ReadString(json, ref i);
                    SkipWhitespace(json, ref i);
                    if (i < json.Length && json[i] == ':')
                    {
                        i++;
                        SkipWhitespace(json, ref i);
                        if (i < json.Length && json[i] == '{')
                        {
                            currentLang = key;
                            result[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            i++;
                            depth++;
                        }
                        else if (i < json.Length && json[i] == '"')
                        {
                            string value = ReadString(json, ref i);
                            if (currentLang != null && result.TryGetValue(currentLang, out var dict))
                                dict[key] = value;
                        }
                    }
                    continue;
                }
                i++;
            }
            return result;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        /// <summary>Read a JSON string value (including after the opening quote); advances i past the closing quote.</summary>
        private static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return "";
            i++;
            int start = i;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\') i++;
                i++;
            }
            string result = s.Substring(start, i - start);
            if (i < s.Length) i++;
            return result;
        }

        /// <summary>Map JSON DTOs to BuildingDefinition (with null-safe baseValues).</summary>
        private static BuildingDefinition[] MapBuildings(BuildingDto[] dtos)
        {
            if (dtos == null || dtos.Length == 0) return Array.Empty<BuildingDefinition>();
            var list = new List<BuildingDefinition>();
            foreach (var d in dtos)
            {
                if (d == null) continue;
                list.Add(new BuildingDefinition
                {
                    Id = d.id ?? "",
                    Category = d.category ?? "",
                    ImpactSize = d.impactSize ?? "Small",
                    Importance = d.importance,
                    Price = d.price,
                    BaseValues = d.baseValues == null
                        ? new BuildingDefinition.MetricValues()
                        : new BuildingDefinition.MetricValues
                        {
                            environment = d.baseValues.environment,
                            economy = d.baseValues.economy,
                            healthSafety = d.baseValues.healthSafety,
                            cultureEdu = d.baseValues.cultureEdu
                        },
                    ConnectionRadius = d.connectionRadius > 0 ? d.connectionRadius : 0f,
                    LocalizationKey = d.localizationKey ?? ""
                });
            }
            return list.ToArray();
        }

        /// <summary>Serialize the current in-memory config back to the StreamingAssets JSON file.
        /// Preserves the original "localization" block verbatim (JsonUtility cannot round-trip nested dictionaries).
        /// Keeps a .bak of the previous file. Not supported on WebGL (read-only StreamingAssets). Returns true on success.</summary>
        public bool SaveToFile()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogWarning("[GameConfigLoader] SaveToFile is not supported on WebGL (read-only StreamingAssets).");
            return false;
#else
            if (_cachedConfig == null)
            {
                Debug.LogWarning("[GameConfigLoader] SaveToFile: no config loaded.");
                return false;
            }
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, configPath);
                string existingLoc = File.Exists(path) ? ExtractLocalizationVerbatim(File.ReadAllText(path)) : null;

                var root = BuildRoot(_cachedConfig);
                string json = JsonUtility.ToJson(root, true);
                if (!string.IsNullOrEmpty(existingLoc))
                    json = InsertLocalizationBeforeFinalBrace(json, existingLoc);

                if (File.Exists(path))
                    File.Copy(path, path + ".bak", true);
                File.WriteAllText(path, json);
                Debug.Log($"[GameConfigLoader] Saved config to {path}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameConfigLoader] SaveToFile failed: {e}");
                return false;
            }
#endif
        }

        /// <summary>Map the in-memory config back to the JsonUtility-serializable DTO (reverse of TryParse).</summary>
        private static GameConfigRoot BuildRoot(GameConfig c)
        {
            return new GameConfigRoot
            {
                meta = c.Meta,
                session = c.Session,
                budget = c.Budget,
                scoring = c.Scoring,
                accessibility = c.Accessibility,
                osc = c.Osc,
                buildings = MapBuildingsToDto(c.Buildings),
                map = c.Map,
                tooltips = c.Tooltips,
                stops = c.Stops,
                tutorial = c.Tutorial,
                inactivity = c.Inactivity,
                endMessages = c.EndMessages
            };
        }

        /// <summary>Inverse of <see cref="MapBuildings"/>: BuildingDefinition -> JSON DTO so building scores persist.</summary>
        private static BuildingDto[] MapBuildingsToDto(BuildingDefinition[] defs)
        {
            if (defs == null || defs.Length == 0) return Array.Empty<BuildingDto>();
            var arr = new BuildingDto[defs.Length];
            for (int i = 0; i < defs.Length; i++)
            {
                var d = defs[i];
                if (d == null) { arr[i] = new BuildingDto(); continue; }
                arr[i] = new BuildingDto
                {
                    id = d.Id,
                    category = d.Category,
                    impactSize = d.ImpactSize,
                    importance = d.Importance,
                    price = d.Price,
                    connectionRadius = d.ConnectionRadius,
                    localizationKey = d.LocalizationKey,
                    baseValues = d.BaseValues == null
                        ? new BuildingDto.BaseValuesDto()
                        : new BuildingDto.BaseValuesDto
                        {
                            environment = d.BaseValues.environment,
                            economy = d.BaseValues.economy,
                            healthSafety = d.BaseValues.healthSafety,
                            cultureEdu = d.BaseValues.cultureEdu
                        }
                };
            }
            return arr;
        }

        /// <summary>Extract the original "localization": { ... } text (including the key) so it can be re-inserted verbatim.</summary>
        private static string ExtractLocalizationVerbatim(string json)
        {
            int start = json.IndexOf("\"localization\"", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            int braceStart = json.IndexOf('{', start);
            if (braceStart < 0) return null;
            int depth = 1;
            int i = braceStart + 1;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                i++;
            }
            return json.Substring(start, i - start);
        }

        /// <summary>Insert a raw "localization": {...} block just before the closing brace of a serialized object.</summary>
        private static string InsertLocalizationBeforeFinalBrace(string json, string localizationText)
        {
            int lastBrace = json.LastIndexOf('}');
            if (lastBrace < 0) return json;
            string head = json.Substring(0, lastBrace).TrimEnd();
            string sep = head.EndsWith("{") ? "\n  " : ",\n  ";
            return head + sep + localizationText + "\n}";
        }

        /// <summary>Build a valid config when file is missing or parse fails.</summary>
        private static GameConfig CreateDefaultConfig()
        {
            return new GameConfig
            {
                Meta = new GameConfig.MetaData(),
                Session = new GameConfig.SessionData(),
                Budget = new GameConfig.BudgetData(),
                Scoring = new GameConfig.ScoringData(),
                Accessibility = new GameConfig.AccessibilityData(),
                Osc = new GameConfig.OscData { sources = Array.Empty<GameConfig.OscSourceData>() },
                Buildings = Array.Empty<BuildingDefinition>(),
                Tooltips = new GameConfig.TooltipsData { introKeys = Array.Empty<string>() },
                Stops = new GameConfig.StopsData(),
                Tutorial = new GameConfig.TutorialData(),
                Inactivity = new GameConfig.InactivityData(),
                EndMessages = Array.Empty<GameConfig.EndMessageData>(),
                Localization = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
