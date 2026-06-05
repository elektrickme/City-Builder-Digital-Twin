using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CityTwin.Core;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Kid-friendly placement feedback: expanding radius disc on every spawn,
    /// per-hub colored pillar delta floaters when hub metrics move, dashboard pillar bar pop.
    /// Subscribes to BuildingSpawner.OnTileSpawned and SimulationEngine.OnMetricsChanged.
    /// All visuals built at runtime — no prefab assets required.
    /// </summary>
    public class PlacementFxController : MonoBehaviour
    {
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private DashboardController dashboard;
        [SerializeField] private RectTransform contentRoot;

        [Header("Ripple")]
        [Tooltip("Fallback disc radius when neither the building marker halo nor the engine impact " +
                 "radius can be resolved. In content-root space.")]
        [SerializeField] private float rippleFallbackRadius = 112f;
        [SerializeField] private float rippleDuration = 0.55f;
        [SerializeField, Range(0f, 1f)] private float rippleStartAlpha = 0.55f;
        [SerializeField] private Color rippleEnvColor = new Color(0.30f, 0.85f, 0.45f, 1f);
        [SerializeField] private Color rippleEcoColor = new Color(1f, 0.78f, 0.20f, 1f);
        [SerializeField] private Color rippleSafColor = new Color(0.20f, 0.55f, 1f, 1f);
        [SerializeField] private Color rippleCulColor = new Color(0.62f, 0.45f, 1f, 1f);
        [SerializeField] private Color rippleDefaultColor = new Color(0.35f, 0.80f, 1f, 1f);

        [Header("Floaters")]
        [Tooltip("Minimum delta (pillar %) needed to spawn a floater above a hub.")]
        [SerializeField] private float floaterThreshold = 1f;
        [SerializeField] private float floaterDuration = 1.2f;
        [SerializeField] private float floaterRiseDistance = 36f;
        [SerializeField] private float floaterFontSize = 28f;
        [SerializeField] private int maxFloatersPerHub = 6;

        private SnapshotFrame _previousFrame;   // metrics before the most recent OnMetricsChanged
        private SnapshotFrame _currentFrame;    // most recent snapshot
        private readonly Dictionary<int, int> _activeFloaterCount = new Dictionary<int, int>();
        private static Sprite _ringSprite;

        private struct SnapshotFrame
        {
            public float[] Env, Eco, Saf, Cul;
        }

        private struct PillarColorPair
        {
            public DashboardController.Pillar Pillar;
            public Color Color;
            public string Tag;
        }

        private void Awake()
        {
            if (buildingSpawner == null) buildingSpawner = GetComponentInParent<BuildingSpawner>()
                                                          ?? GetComponentInChildren<BuildingSpawner>(true);
            if (simulationEngine == null) simulationEngine = GetComponentInParent<SimulationEngine>()
                                                             ?? GetComponentInChildren<SimulationEngine>(true);
            if (dashboard == null) dashboard = GetComponentInParent<DashboardController>()
                                               ?? FindFirstObjectByType<DashboardController>();
            if (contentRoot == null && buildingSpawner != null) contentRoot = buildingSpawner.ContentRoot;

            Debug.Log($"[PlacementFxController] Awake on '{name}'. " +
                      $"spawner={(buildingSpawner != null ? buildingSpawner.name : "NULL")} " +
                      $"engine={(simulationEngine != null ? simulationEngine.name : "NULL")} " +
                      $"dashboard={(dashboard != null ? dashboard.name : "NULL")} " +
                      $"contentRoot={(contentRoot != null ? contentRoot.name : "NULL")}");
            if (buildingSpawner == null || simulationEngine == null || contentRoot == null)
                Debug.LogError("[PlacementFxController] Missing required refs — FX will not run. " +
                               "Put this component on the Game Instance root (same object as SimulationEngine) " +
                               "and ensure BuildingSpawner.ContentRoot is assigned.");
        }

        private void OnEnable()
        {
            if (buildingSpawner != null) buildingSpawner.OnTileSpawned += HandleTileSpawned;
            if (simulationEngine != null) simulationEngine.OnMetricsChanged += HandleMetricsChanged;
            // Seed both buffers with the current state so the first placement diffs against a real baseline.
            CaptureCurrentInto(ref _previousFrame);
            CaptureCurrentInto(ref _currentFrame);
            Debug.Log($"[PlacementFxController] OnEnable — subscribed: " +
                      $"OnTileSpawned={(buildingSpawner != null)} OnMetricsChanged={(simulationEngine != null)}");
        }

        private void OnDisable()
        {
            if (buildingSpawner != null) buildingSpawner.OnTileSpawned -= HandleTileSpawned;
            if (simulationEngine != null) simulationEngine.OnMetricsChanged -= HandleMetricsChanged;
        }

        private void HandleTileSpawned(string engineTileId, string buildingId, GameObject marker)
        {
            if (marker == null || contentRoot == null)
            {
                Debug.LogWarning($"[PlacementFxController] HandleTileSpawned skipped — " +
                                 $"marker={(marker != null)} contentRoot={(contentRoot != null)}");
                return;
            }

            Vector2 anchored = ResolveMarkerAnchored(marker);
            float radius = ResolveRippleRadius(buildingId);
            Color color = ResolveCategoryColor(buildingId);
            Debug.Log($"[PlacementFxController] Tile spawned id={buildingId} → ripple at ({anchored.x:F0},{anchored.y:F0}) r={radius:F0}");
            SpawnRipple(anchored, radius, color);

            // Coordinator order is AddTile → OnMetricsChanged → SpawnBuilding → OnTileSpawned.
            // So by the time we get here, _currentFrame already holds POST-placement metrics and
            // _previousFrame holds PRE-placement metrics. Diff against _previousFrame.
            EmitHubFloatersAndPunches();
        }

        private void HandleMetricsChanged()
        {
            // Shift current → previous, then capture fresh snapshot as current.
            CopyFrame(_currentFrame, ref _previousFrame);
            CaptureCurrentInto(ref _currentFrame);
        }

        private void EmitHubFloatersAndPunches()
        {
            var hubs = simulationEngine != null ? simulationEngine.HubMetrics : null;
            var positions = simulationEngine != null ? simulationEngine.HubPositions : null;
            if (hubs == null || positions == null || hubs.Count == 0) return;

            float cityEnvDelta = 0, cityEcoDelta = 0, citySafDelta = 0, cityCulDelta = 0;

            for (int i = 0; i < hubs.Count; i++)
            {
                var h = hubs[i];
                float dEnv = h.Environment   - SafeGet(_previousFrame.Env, i);
                float dEco = h.Economy       - SafeGet(_previousFrame.Eco, i);
                float dSaf = h.HealthSafety  - SafeGet(_previousFrame.Saf, i);
                float dCul = h.CultureEdu    - SafeGet(_previousFrame.Cul, i);

                cityEnvDelta += dEnv;
                cityEcoDelta += dEco;
                citySafDelta += dSaf;
                cityCulDelta += dCul;

                Vector2 hubAnchored = i < positions.Count ? positions[i] : Vector2.zero;
                TrySpawnFloater(hubAnchored, dEnv, new PillarColorPair { Pillar = DashboardController.Pillar.Environment,  Color = rippleEnvColor, Tag = "Env" }, i);
                TrySpawnFloater(hubAnchored, dEco, new PillarColorPair { Pillar = DashboardController.Pillar.Economy,      Color = rippleEcoColor, Tag = "Eco" }, i);
                TrySpawnFloater(hubAnchored, dSaf, new PillarColorPair { Pillar = DashboardController.Pillar.HealthSafety, Color = rippleSafColor, Tag = "Saf" }, i);
                TrySpawnFloater(hubAnchored, dCul, new PillarColorPair { Pillar = DashboardController.Pillar.CultureEdu,   Color = rippleCulColor, Tag = "Cul" }, i);
            }

            if (dashboard != null)
            {
                if (Mathf.Abs(cityEnvDelta) >= floaterThreshold) dashboard.PunchPillar(DashboardController.Pillar.Environment);
                if (Mathf.Abs(cityEcoDelta) >= floaterThreshold) dashboard.PunchPillar(DashboardController.Pillar.Economy);
                if (Mathf.Abs(citySafDelta) >= floaterThreshold) dashboard.PunchPillar(DashboardController.Pillar.HealthSafety);
                if (Mathf.Abs(cityCulDelta) >= floaterThreshold) dashboard.PunchPillar(DashboardController.Pillar.CultureEdu);
                dashboard.PunchPillar(DashboardController.Pillar.Qol, 0.12f, 0.45f);
            }
        }

        private void TrySpawnFloater(Vector2 anchoredHubPos, float delta, PillarColorPair pillar, int hubIndex)
        {
            if (Mathf.Abs(delta) < floaterThreshold) return;
            int current = _activeFloaterCount.TryGetValue(hubIndex, out var c) ? c : 0;
            if (current >= maxFloatersPerHub) return;
            _activeFloaterCount[hubIndex] = current + 1;

            var go = new GameObject($"Floater_Hub{hubIndex}_{pillar.Tag}", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(contentRoot, false);
            rt.sizeDelta = new Vector2(120f, 40f);
            // small horizontal jitter so simultaneous floaters don't stack
            float jitterX = (((hubIndex * 73) + pillar.Tag.GetHashCode()) % 31) - 15f;
            rt.anchoredPosition = anchoredHubPos + new Vector2(jitterX, 22f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = (delta > 0 ? "+" : "") + Mathf.RoundToInt(delta).ToString();
            tmp.fontSize = floaterFontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = pillar.Color;
            tmp.outlineWidth = 0.2f;
            tmp.outlineColor = new Color(0f, 0f, 0f, 0.6f);
            tmp.raycastTarget = false;

            StartCoroutine(FloaterRoutine(rt, tmp, hubIndex));
        }

        private IEnumerator FloaterRoutine(RectTransform rt, TextMeshProUGUI tmp, int hubIndex)
        {
            Vector2 startPos = rt.anchoredPosition;
            Color baseColor = tmp.color;
            float t = 0f;
            while (t < floaterDuration && rt != null)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.Clamp01(t / floaterDuration);
                rt.anchoredPosition = startPos + new Vector2(0f, floaterRiseDistance * u);
                float ease = 1f - Mathf.Pow(u, 2f);
                rt.localScale = Vector3.one * Mathf.Lerp(1.4f, 1f, Mathf.Clamp01(u * 2.5f));
                tmp.color = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * ease);
                yield return null;
            }
            if (_activeFloaterCount.TryGetValue(hubIndex, out var c))
                _activeFloaterCount[hubIndex] = Mathf.Max(0, c - 1);
            if (rt != null) Destroy(rt.gameObject);
        }

        private void SpawnRipple(Vector2 anchoredCenter, float radius, Color color)
        {
            if (contentRoot == null) return;
            var go = new GameObject("PlacementRipple", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(contentRoot, false);
            rt.anchoredPosition = anchoredCenter;
            rt.sizeDelta = Vector2.one * 8f;

            var img = go.AddComponent<Image>();
            img.sprite = GetOrBuildRingSprite();
            img.type = Image.Type.Simple;
            img.raycastTarget = false;
            img.color = new Color(color.r, color.g, color.b, rippleStartAlpha);

            StartCoroutine(RippleRoutine(rt, img, radius));
        }

        private IEnumerator RippleRoutine(RectTransform rt, Image img, float radius)
        {
            float diameter = radius * 2f;
            float t = 0f;
            Color start = img.color;
            while (t < rippleDuration && rt != null)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.Clamp01(t / rippleDuration);
                // ease-out cubic for size, linear for fade
                float eOut = 1f - Mathf.Pow(1f - u, 3f);
                float size = Mathf.Lerp(8f, diameter, eOut);
                rt.sizeDelta = new Vector2(size, size);
                img.color = new Color(start.r, start.g, start.b, start.a * (1f - u));
                yield return null;
            }
            if (rt != null) Destroy(rt.gameObject);
        }

        private void CaptureCurrentInto(ref SnapshotFrame frame)
        {
            var hubs = simulationEngine != null ? simulationEngine.HubMetrics : null;
            int n = hubs?.Count ?? 0;
            if (frame.Env == null || frame.Env.Length != n)
            {
                frame.Env = new float[n];
                frame.Eco = new float[n];
                frame.Saf = new float[n];
                frame.Cul = new float[n];
            }
            for (int i = 0; i < n; i++)
            {
                frame.Env[i] = hubs[i].Environment;
                frame.Eco[i] = hubs[i].Economy;
                frame.Saf[i] = hubs[i].HealthSafety;
                frame.Cul[i] = hubs[i].CultureEdu;
            }
        }

        private static void CopyFrame(SnapshotFrame src, ref SnapshotFrame dst)
        {
            int n = src.Env?.Length ?? 0;
            if (dst.Env == null || dst.Env.Length != n)
            {
                dst.Env = new float[n];
                dst.Eco = new float[n];
                dst.Saf = new float[n];
                dst.Cul = new float[n];
            }
            if (n == 0) return;
            System.Array.Copy(src.Env, dst.Env, n);
            System.Array.Copy(src.Eco, dst.Eco, n);
            System.Array.Copy(src.Saf, dst.Saf, n);
            System.Array.Copy(src.Cul, dst.Cul, n);
        }

        private Vector2 ResolveMarkerAnchored(GameObject marker)
        {
            if (marker.transform is RectTransform mrt && mrt.parent == contentRoot) return mrt.anchoredPosition;
            if (contentRoot == null) return Vector2.zero;
            Vector3 local = contentRoot.InverseTransformPoint(marker.transform.position);
            Vector2 pivotCorrection = (new Vector2(0.5f, 0.5f) - contentRoot.pivot) * contentRoot.rect.size;
            return new Vector2(local.x, local.y) - pivotCorrection;
        }

        private float ResolveRippleRadius(string buildingId)
        {
            if (buildingSpawner != null && buildingSpawner.TryGetEstimatedBuildingRadius(buildingId, out var haloR) && haloR > 1f)
                return haloR * 1.05f;
            if (simulationEngine != null && simulationEngine.TryGetImpactSearchRadius(buildingId, out var impR))
                return impR;
            return rippleFallbackRadius;
        }

        private Color ResolveCategoryColor(string buildingId)
        {
            // Look up the building's base values via the engine catalog by id; pick dominant pillar's color.
            if (simulationEngine == null) return rippleDefaultColor;
            // Engine catalog isn't exposed, so reuse SimulationEngine TryGetImpactSearchRadius pattern:
            // category color is best-effort; if the marker display knows the category we use it later.
            // For now, derive from the buildingId prefix.
            string id = buildingId?.ToLowerInvariant() ?? "";
            if (id.Contains("garden") || id.Contains("park") || id.Contains("recycl")) return rippleEnvColor;
            if (id.Contains("office") || id.Contains("mall") || id.Contains("factory")) return rippleEcoColor;
            if (id.Contains("police") || id.Contains("fire") || id.Contains("hospital")) return rippleSafColor;
            if (id.Contains("school") || id.Contains("library") || id.Contains("museum") || id.Contains("circus")) return rippleCulColor;
            return rippleDefaultColor;
        }

        private static float SafeGet(float[] arr, int i) => arr != null && i < arr.Length ? arr[i] : 0f;

        /// <summary>Procedural hollow-ring sprite (cached). 128x128, anti-aliased edges, outer ~10% thick.</summary>
        private static Sprite GetOrBuildRingSprite()
        {
            if (_ringSprite != null) return _ringSprite;
            const int size = 128;
            const float outerR = 60f;
            const float innerR = 48f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float aOuter = Mathf.Clamp01(outerR - d);          // smooth outside falloff
                    float aInner = Mathf.Clamp01(d - innerR);          // smooth inside falloff
                    float a = Mathf.Clamp01(Mathf.Min(aOuter, aInner));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            _ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            _ringSprite.name = "PlacementFx_Ring";
            return _ringSprite;
        }
    }
}
