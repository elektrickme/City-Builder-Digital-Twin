using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using CityTwin.Core;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Manages visual connection lines between placed building tiles and road snap-points,
    /// and optional hub-to-hub links.
    /// Buildings connect to nearest points on road segments (transit graph edges).
    /// Each building-road connection is drawn as two layers:
    ///   Layer 1 (bg): wide, low-opacity stroke matching road style.
    ///   Layer 2 (fg): thinner, higher-opacity stroke.
    /// After drawing, updates building marker visuals to reflect connection state.
    /// </summary>
    public class HubConnectionRenderer : MonoBehaviour
    {
        [Tooltip("Optional override. Connection lines are always parented and positioned in BuildingSpawner's content root (the table/map) so they align with buildings. Only set this if you have no BuildingSpawner.")]
        [SerializeField] private RectTransform contentRootOverride;

        [Tooltip("Prefab with a MonoBehaviour implementing IConnectionVisual (e.g. StretchedImageConnection).")]
        [SerializeField] private GameObject connectionPrefab;

        [Tooltip("Optional holder for building-road connection lines. Must be a RectTransform child of the content root. Automatically configured to stretch-fill so coordinates match. If null, lines parent directly to the content root.")]
        [SerializeField] private RectTransform buildingRoadLineHolder;

        [Tooltip("Optional holder for hub-hub connection lines. Must be a RectTransform child of the content root. Automatically configured to stretch-fill so coordinates match. If null, lines parent directly to the content root.")]
        [SerializeField] private RectTransform hubHubLineHolder;

        [SerializeField] private HubRegistry hubRegistry;
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private HubLayoutManager hubLayoutManager;

        [Header("Hub -> Hub")]
        [SerializeField] private bool drawHubToHubConnections = true;
        [SerializeField] private bool useHubToHubColorOverride = true;
        [SerializeField] private Color hubToHubColor = new Color(0.5f, 0.85f, 1f, 0.7f);

        [Header("Building -> Stop")]
        [SerializeField] private Color buildingStopCloseColor = new Color(0f, 0.8f, 1f, 0.85f);
        [SerializeField] private Color buildingStopFarColor = new Color(0f, 0.8f, 1f, 0.12f);
        [SerializeField] private float buildingStopThickness = 3f;
        [Tooltip("Distance threshold: closer than this = solid/close color, further = semi-transparent/far color.")]
        [SerializeField] private float stopCloseDistanceThreshold = 150f;

        [Header("Building -> Hub (direct)")]
        [SerializeField] private Color buildingHubColor = new Color(0.2f, 1f, 0.4f, 0.7f);
        [SerializeField] private float buildingHubThickness = 3f;

        [Header("Transit Stops")]
        [Tooltip("Prefab for stop markers. Should be a small UI element (e.g. Image). Will be rotated 45 degrees to form a diamond.")]
        [SerializeField] private GameObject stopMarkerPrefab;
        [SerializeField] private float stopMarkerSize = 12f;
        [SerializeField] private bool drawStops = true;
        [SerializeField] private bool stopPulse = true;
        [Tooltip("Uniform scale peak for the breath (1 = no growth). Typical 1.04–1.12.")]
        [SerializeField] private float stopPulseScale = 1.08f;
        [Tooltip("Seconds for one leg of the pulse (scale up); Yoyo doubles for a full in-out cycle.")]
        [SerializeField] private float stopPulseDuration = 0.75f;

        private readonly Dictionary<(string tileId, int stopIdx), IConnectionVisual> _buildingStopLines =
            new Dictionary<(string, int), IConnectionVisual>();
        private readonly HashSet<(string, int)> _currentBuildingStopKeys = new HashSet<(string, int)>();
        private readonly Dictionary<(string tileId, int hubIdx), IConnectionVisual> _buildingHubLines =
            new Dictionary<(string, int), IConnectionVisual>();
        private readonly HashSet<(string, int)> _currentBuildingHubKeys = new HashSet<(string, int)>();
        private readonly Dictionary<(int hubA, int hubB), IConnectionVisual> _activeHubHub =
            new Dictionary<(int, int), IConnectionVisual>();
        private readonly List<IConnectionVisual> _pool = new List<IConnectionVisual>();
        private readonly HashSet<(int, int)> _currentHubHubKeys = new HashSet<(int, int)>();
        private readonly List<RectTransform> _activeStopMarkers = new List<RectTransform>();
        private readonly List<RectTransform> _stopMarkerPool = new List<RectTransform>();

        private void Awake()
        {
            if (hubRegistry == null) hubRegistry = GetComponentInChildren<HubRegistry>(true);
            if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
            if (hubLayoutManager == null) hubLayoutManager = transform.root.GetComponentInChildren<HubLayoutManager>(true);
        }

        private void OnEnable()
        {
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged += Refresh;
            Refresh();
        }

        private IEnumerator Start()
        {
            yield return null;
            Refresh();
        }

        private void OnDisable()
        {
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged -= Refresh;
            KillStopMarkerTweens();
        }

        private void Refresh()
        {
            if (buildingSpawner == null || connectionPrefab == null) return;

            RectTransform root = buildingSpawner.ContentRoot != null ? buildingSpawner.ContentRoot : contentRootOverride;
            if (root == null) return;

            _currentHubHubKeys.Clear();
            bool useTableSpace = (root == buildingSpawner.ContentRoot);

            RectTransform brParent = buildingRoadLineHolder != null ? buildingRoadLineHolder : root;
            RectTransform hhParent = hubHubLineHolder != null ? hubHubLineHolder : root;
            EnsureHolderSetup(buildingRoadLineHolder);
            EnsureHolderSetup(hubHubLineHolder);



            // --- Building -> Stop lines (distance-based opacity) ---
            _currentBuildingStopKeys.Clear();
            if (simulationEngine != null)
            {
                var stopConnections = simulationEngine.ActiveStopConnections;
                var perTileStopIndex = new Dictionary<string, int>();

                for (int i = 0; i < stopConnections.Count; i++)
                {
                    var sc = stopConnections[i];

                    Vector2 buildingPos;
                    bool gotBuilding = useTableSpace
                        ? buildingSpawner.TryGetMarkerPosition(sc.TileId, out buildingPos)
                        : buildingSpawner.TryGetMarkerPositionIn(sc.TileId, root, out buildingPos);
                    if (!gotBuilding) continue;

                    if (!perTileStopIndex.TryGetValue(sc.TileId, out int sIdx))
                        sIdx = 0;
                    perTileStopIndex[sc.TileId] = sIdx + 1;

                    var key = (sc.TileId, sIdx);
                    _currentBuildingStopKeys.Add(key);

                    Vector2 from = RootToHolderSpace(buildingPos, root, brParent);
                    Vector2 to = RootToHolderSpace(sc.StopPosition, root, brParent);

                    Color lineColor = sc.Distance <= stopCloseDistanceThreshold
                        ? buildingStopCloseColor
                        : buildingStopFarColor;

                    if (!_buildingStopLines.TryGetValue(key, out IConnectionVisual visual))
                    {
                        visual = Acquire(brParent);
                        if (visual != null)
                            _buildingStopLines[key] = visual;
                    }
                    if (visual != null)
                    {
                        visual.UpdateEndpoints(from, to);
                        ApplyStyle(visual, lineColor, buildingStopThickness);
                        visual.SetActive(true);
                    }
                }
            }

            // --- Building -> Hub direct lines ---
            _currentBuildingHubKeys.Clear();
            if (simulationEngine != null)
            {
                var hubDirectConns = simulationEngine.ActiveHubDirectConnections;
                for (int i = 0; i < hubDirectConns.Count; i++)
                {
                    var hc = hubDirectConns[i];

                    Vector2 buildingPos;
                    bool gotBuilding = useTableSpace
                        ? buildingSpawner.TryGetMarkerPosition(hc.TileId, out buildingPos)
                        : buildingSpawner.TryGetMarkerPositionIn(hc.TileId, root, out buildingPos);
                    if (!gotBuilding) continue;

                    var key = (hc.TileId, hc.HubIndex);
                    _currentBuildingHubKeys.Add(key);

                    Vector2 from = RootToHolderSpace(buildingPos, root, brParent);
                    Vector2 to = RootToHolderSpace(hc.HubPosition, root, brParent);

                    if (!_buildingHubLines.TryGetValue(key, out IConnectionVisual visual))
                    {
                        visual = Acquire(brParent);
                        if (visual != null)
                            _buildingHubLines[key] = visual;
                    }
                    if (visual != null)
                    {
                        visual.UpdateEndpoints(from, to);
                        ApplyStyle(visual, buildingHubColor, buildingHubThickness);
                        visual.SetActive(true);
                    }
                }
            }

            // --- Hub -> Hub lines (from transit graph edges) ---
            if (drawHubToHubConnections && simulationEngine != null && hubRegistry != null)
            {
                hubRegistry.FetchHubs();
                var hubs = hubRegistry.Hubs;
                var graph = simulationEngine.TransitGraph;

                if (graph != null)
                {
                    var edges = graph.Edges;
                    for (int e = 0; e < edges.Count; e++)
                    {
                        var edge = edges[e];
                        int idxA = edge.FromId;
                        int idxB = edge.ToId;
                        if (idxA < 0 || idxA >= hubs.Count || idxB < 0 || idxB >= hubs.Count) continue;

                        var key = idxA < idxB ? (idxA, idxB) : (idxB, idxA);
                        if (!_currentHubHubKeys.Add(key)) continue; // deduplicate A→B / B→A

                        Vector2 a = RootToHolderSpace(GetHubLocalPosition(hubs[idxA], root), root, hhParent);
                        Vector2 b = RootToHolderSpace(GetHubLocalPosition(hubs[idxB], root), root, hhParent);

                        if (!_activeHubHub.TryGetValue(key, out IConnectionVisual visual))
                        {
                            visual = Acquire(hhParent);
                            if (visual == null) continue;
                            _activeHubHub[key] = visual;
                        }

                        visual.UpdateEndpoints(a, b);
                        if (useHubToHubColorOverride)
                            ApplyColor(visual, hubToHubColor);
                        visual.SetActive(true);
                    }
                }
            }

            // --- Transit Stop markers ---
            RefreshStopMarkers(root);

            // --- Deactivate unused building-stop visuals ---
            RecycleStale(_buildingStopLines, _currentBuildingStopKeys);

            // --- Deactivate unused building-hub visuals ---
            RecycleStale(_buildingHubLines, _currentBuildingHubKeys);

            // --- Deactivate unused hub-hub visuals ---
            var toRemoveHubHub = new List<(int, int)>();
            foreach (var kv in _activeHubHub)
            {
                if (!_currentHubHubKeys.Contains(kv.Key))
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                    toRemoveHubHub.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRemoveHubHub.Count; i++)
                _activeHubHub.Remove(toRemoveHubHub[i]);

            // --- Update building marker connection states ---
            UpdateMarkerConnectionStates();
        }

        private void RecycleStale(Dictionary<(string, int), IConnectionVisual> dict, HashSet<(string, int)> currentKeys)
        {
            var toRemove = new List<(string, int)>();
            foreach (var kv in dict)
            {
                if (!currentKeys.Contains(kv.Key))
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                    toRemove.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRemove.Count; i++)
                dict.Remove(toRemove[i]);
        }

        private void UpdateMarkerConnectionStates()
        {
            if (simulationEngine == null || buildingSpawner == null) return;

            var graph = simulationEngine.TransitGraph;
            bool hasRoads = graph != null && graph.Edges.Count > 0;

            var tileStates = simulationEngine.TileStates;
            for (int i = 0; i < tileStates.Count; i++)
            {
                var ts = tileStates[i];
                MarkerConnectionState state;

                if (ts.Inactive || ts.OverlapInvalid)
                    state = MarkerConnectionState.Inactive;
                else if (hasRoads && !ts.Connected)
                    state = MarkerConnectionState.Disconnected;
                else
                    state = MarkerConnectionState.Connected;

                buildingSpawner.SetMarkerConnectionState(ts.TileId, state);
            }
        }

        /// <summary>Force a holder RectTransform to stretch-fill its parent with center pivot.</summary>
        private static void EnsureHolderSetup(RectTransform holder)
        {
            if (holder == null) return;
            holder.anchorMin = Vector2.zero;
            holder.anchorMax = Vector2.one;
            holder.offsetMin = Vector2.zero;
            holder.offsetMax = Vector2.zero;
            holder.pivot = new Vector2(0.5f, 0.5f);
            holder.localScale = Vector3.one;
            holder.localRotation = Quaternion.identity;
        }

        /// <summary>Convert a position from content root center-origin space to a holder's
        /// center-origin space via world-space, so the holder can live anywhere in the hierarchy.</summary>
        private static Vector2 RootToHolderSpace(Vector2 pos, RectTransform root, RectTransform holder)
        {
            if (holder == null || holder == root) return pos;
            Vector2 rootLocal = pos + (Vector2)root.rect.center;
            Vector3 world = root.TransformPoint(new Vector3(rootLocal.x, rootLocal.y, 0f));
            Vector3 hl = holder.InverseTransformPoint(world);
            return new Vector2(hl.x, hl.y) - (Vector2)holder.rect.center;
        }

        private IConnectionVisual Acquire(RectTransform root)
        {
            for (int i = _pool.Count - 1; i >= 0; i--)
            {
                var v = _pool[i];
                if (v != null)
                {
                    _pool.RemoveAt(i);
                    if (v is MonoBehaviour mb && mb.transform.parent != root)
                        mb.transform.SetParent(root, false);
                    return v;
                }
                _pool.RemoveAt(i);
            }

            var go = Instantiate(connectionPrefab, root);

            var visual = go.GetComponent<IConnectionVisual>();
            if (visual == null)
            {
                Debug.LogError("[HubConnectionRenderer] connectionPrefab is missing an IConnectionVisual component.");
                Destroy(go);
                return null;
            }
            return visual;
        }

        private static void ApplyColor(IConnectionVisual visual, Color color)
        {
            if (!(visual is MonoBehaviour mb) || mb == null) return;
            var graphic = mb.GetComponent<Graphic>();
            if (graphic != null) graphic.color = color;
        }

        private static void ApplyStyle(IConnectionVisual visual, Color color, float thickness)
        {
            if (!(visual is MonoBehaviour mb) || mb == null) return;
            var graphic = mb.GetComponent<Graphic>();
            if (graphic != null) graphic.color = color;
            if (mb.transform is RectTransform rt)
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, thickness);
        }

        /// <summary>Hub position in the center-anchored space of root (same space as building markers).
        /// Corrects for root pivot so (0,0) = center of root rect, matching TuioToLocal and marker anchoredPositions.</summary>
        private Vector2 GetHubLocalPosition(ResidentialHubMono hub, RectTransform root)
        {
            if (root == null) return Vector2.zero;
            Vector3 local3d = root.InverseTransformPoint(hub.transform.position);
            Vector2 pivotCorrection = (new Vector2(0.5f, 0.5f) - root.pivot) * root.rect.size;
            return new Vector2(local3d.x, local3d.y) - pivotCorrection;
        }

        private void RefreshStopMarkers(RectTransform root)
        {
            // Return all active markers to pool
            foreach (var rt in _activeStopMarkers)
            {
                if (rt != null)
                {
                    ResetStopMarkerTween(rt);
                    rt.gameObject.SetActive(false);
                    _stopMarkerPool.Add(rt);
                }
            }
            _activeStopMarkers.Clear();

            if (!drawStops) return;
            if (stopMarkerPrefab == null || simulationEngine == null) return;

            var graph = simulationEngine.TransitGraph;
            if (graph == null) return;

            var stops = graph.Stops;
            if (stops.Count == 0) return;

            RectTransform parent = hubHubLineHolder != null ? hubHubLineHolder : root;

            for (int i = 0; i < stops.Count; i++)
            {
                Vector2 pos = RootToHolderSpace(stops[i].Position, root, parent);
                RectTransform marker = AcquireStopMarker(parent);
                marker.anchoredPosition = pos;
                marker.sizeDelta = new Vector2(stopMarkerSize, stopMarkerSize);
                marker.localRotation = Quaternion.Euler(0, 0, 45f);
                marker.gameObject.SetActive(true);
                _activeStopMarkers.Add(marker);
                StartStopMarkerPulse(marker);
            }

        }

        private void StartStopMarkerPulse(RectTransform marker)
        {
            if (marker == null || !stopPulse) return;
            marker.DOKill(false);
            marker.localScale = Vector3.one;
            float peak = Mathf.Clamp(stopPulseScale, 1.002f, 2f);
            float dur = Mathf.Max(0.05f, stopPulseDuration);
            marker.DOScale(peak, dur)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .SetTarget(marker);
        }

        private void KillStopMarkerTweens()
        {
            for (int i = 0; i < _activeStopMarkers.Count; i++)
                ResetStopMarkerTween(_activeStopMarkers[i]);
            for (int i = 0; i < _stopMarkerPool.Count; i++)
                ResetStopMarkerTween(_stopMarkerPool[i]);
        }

        private static void ResetStopMarkerTween(RectTransform rt)
        {
            if (rt == null) return;
            rt.DOKill(false);
            rt.localScale = Vector3.one;
        }

        private RectTransform AcquireStopMarker(RectTransform parent)
        {
            for (int i = _stopMarkerPool.Count - 1; i >= 0; i--)
            {
                var rt = _stopMarkerPool[i];
                if (rt != null)
                {
                    _stopMarkerPool.RemoveAt(i);
                    ResetStopMarkerTween(rt);
                    if (rt.parent != parent) rt.SetParent(parent, false);
                    return rt;
                }
                _stopMarkerPool.RemoveAt(i);
            }

            var go = Instantiate(stopMarkerPrefab, parent);
            var rect = go.GetComponent<RectTransform>();
            if (rect == null) rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            return rect;
        }

        /// <summary>Remove all visuals and return them to pool. Call on reset.</summary>
        public void ClearAll()
        {
            ClearDict(_buildingStopLines);
            ClearDict(_buildingHubLines);

            foreach (var kv in _activeHubHub)
            {
                if (kv.Value != null)
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                }
            }
            _activeHubHub.Clear();

            foreach (var rt in _activeStopMarkers)
            {
                if (rt != null)
                {
                    ResetStopMarkerTween(rt);
                    rt.gameObject.SetActive(false);
                    _stopMarkerPool.Add(rt);
                }
            }
            _activeStopMarkers.Clear();
        }

        private void ClearDict(Dictionary<(string, int), IConnectionVisual> dict)
        {
            foreach (var kv in dict)
            {
                if (kv.Value != null)
                {
                    kv.Value.SetActive(false);
                    _pool.Add(kv.Value);
                }
            }
            dict.Clear();
        }

        private static int IndexOfHub(IReadOnlyList<ResidentialHubMono> hubs, ResidentialHubMono hub)
        {
            for (int i = 0; i < hubs.Count; i++)
                if (hubs[i] == hub) return i;
            return -1;
        }

    }
}
