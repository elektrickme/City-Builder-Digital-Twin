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
        [Tooltip("Extend hub-to-hub lines past the hubs until they reach the fan-shaped table edge, so the network appears to run across the whole map. Requires a TableBounds on the game instance.")]
        [SerializeField] private bool extendHubLinesToTableEdge = true;
        [Tooltip("Safety cap (px) on how far past a hub a line may extend while searching for the table edge.")]
        [SerializeField] private float maxHubLineExtension = 1600f;
        [Tooltip("Only extend past a hub when the extension points away from the hub network's center (dot vs outward direction). Prevents extensions re-crossing the middle of the map.")]
        [Range(-1f, 1f)]
        [SerializeField] private float extendOutwardDotThreshold = 0.15f;
        [Tooltip("Cancel an extension that would pass within this distance (px) of another hub — otherwise it visually recreates hub connections that were removed from the graph.")]
        [SerializeField] private float extensionHubClearance = 110f;
        [Tooltip("Extensions stop before entering these rects (dashboard etc.). 'Top Bar UI' is auto-added when empty.")]
        [SerializeField] private RectTransform[] extensionBlockers;
        [SerializeField] private TableBounds tableBounds;

        private readonly List<Vector2> _hubHolderPositions = new List<Vector2>();

        /// <summary>Manual extension endpoint overrides, in content-root local space.
        /// Key: (lower hub index, higher hub index, end) where end 0 = lower-index hub's side.</summary>
        private readonly Dictionary<(int, int, int), Vector2> _extensionOverrides =
            new Dictionary<(int, int, int), Vector2>();

        // A road is split into three independently-editable sub-segments so a bend stays on the
        // piece it was placed on (an extension bend never jumps onto the hub-to-hub span).
        public const int SegExtMin = 0; // extension beyond the lower-index hub
        public const int SegCore = 1;   // hubMin → hubMax
        public const int SegExtMax = 2; // extension beyond the higher-index hub

        /// <summary>Ordered bend waypoints (content-root local) per (hubMin, hubMax, seg).
        /// Order runs along the path direction (endMin → endMax).</summary>
        private readonly Dictionary<(int, int, int), List<Vector2>> _waypoints =
            new Dictionary<(int, int, int), List<Vector2>>();

        /// <summary>Last drawn hub-hub road, split into its three sub-segments, for editors.</summary>
        public struct HubLineSnapshot
        {
            public int HubMin, HubMax;
            public Vector2 EndMin, EndMax;       // actual drawn line ends (after extension/override)
            public Vector2 HubMinPos, HubMaxPos; // hub centers
            public List<Vector2> WpExtMin;       // bends on the endMin→hubMin extension
            public List<Vector2> WpCore;         // bends on the hubMin→hubMax span
            public List<Vector2> WpExtMax;       // bends on the hubMax→endMax extension
        }

        [System.Serializable]
        public struct WaypointEntry
        {
            public int hubMin, hubMax, seg, index;
            public Vector2 pos;
        }
        private readonly Dictionary<(int, int), HubLineSnapshot> _lastLineEndpoints =
            new Dictionary<(int, int), HubLineSnapshot>();

        /// <summary>Holder that all hub-hub road visuals (and their flow particles) live under.
        /// The round-intro reveal fades it in as the "lines draw" beat.</summary>
        public RectTransform RoadHolder => hubHubLineHolder;

        /// <summary>Raised after a draw pass whenever the drawn road geometry (endpoints,
        /// extensions, bends) differs from the previous pass — lets the coordinator re-cover
        /// the network with transit stops.</summary>
        public event System.Action OnRoadGeometryChanged;
        private int _lastGeometryHash;

        [System.Serializable]
        public struct ExtensionOverrideEntry
        {
            public int hubMin, hubMax, end;
            public Vector2 pos;
        }

        [Header("Building -> Stop")]
        [SerializeField] private Color buildingStopCloseColor = new Color(0f, 0.8f, 1f, 0.85f);
        [SerializeField] private Color buildingStopFarColor = new Color(0f, 0.8f, 1f, 0.12f);
        [SerializeField] private float buildingStopThickness = 3f;
        [Tooltip("Distance threshold: closer than this = solid/close color, further = semi-transparent/far color.")]
        [SerializeField] private float stopCloseDistanceThreshold = 150f;

        [Header("Building -> Hub (direct)")]
        [SerializeField] private Color buildingHubColor = new Color(0.2f, 1f, 0.4f, 0.7f);
        [SerializeField] private float buildingHubThickness = 3f;

        [Header("Connection thinning")]
        [Tooltip("0..1 fraction of per-building connection lines (building→stop and building→hub) randomly hidden to de-clutter a dense network. Purely visual — scoring is unaffected. Normally driven by config accessibility.connectionRemovalRate.")]
        [SerializeField, Range(0f, 1f)] private float connectionRemovalRate = 0f;
        [Tooltip("Seed picking which connections are hidden. Stable per seed, so the same rate always hides the same lines.")]
        [SerializeField] private int connectionRemovalSeed = 4321;

        [Header("Transit Stops")]
        [Tooltip("Prefab for stop markers. Should be a small UI element (e.g. Image).")]
        [SerializeField] private GameObject stopMarkerPrefab;
        [SerializeField] private float stopMarkerSize = 12f;
        [Tooltip("Z rotation of the marker: 0 = square, 45 = diamond.")]
        [SerializeField] private float stopMarkerAngle = 0f;
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
        // Each hub-hub road is now a polyline of one-or-more pooled segment visuals.
        private readonly Dictionary<(int hubA, int hubB), List<IConnectionVisual>> _activeHubHub =
            new Dictionary<(int, int), List<IConnectionVisual>>();
        private readonly List<IConnectionVisual> _pool = new List<IConnectionVisual>();
        private readonly List<Vector2> _pathScratch = new List<Vector2>();
        private readonly HashSet<(int, int)> _currentHubHubKeys = new HashSet<(int, int)>();
        private readonly List<RectTransform> _activeStopMarkers = new List<RectTransform>();
        private readonly List<RectTransform> _stopMarkerPool = new List<RectTransform>();

        private void Awake()
        {
            if (hubRegistry == null) hubRegistry = GetComponentInChildren<HubRegistry>(true);
            if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
            if (hubLayoutManager == null) hubLayoutManager = transform.root.GetComponentInChildren<HubLayoutManager>(true);
            if (tableBounds == null) tableBounds = GetComponentInParent<TableBounds>(true);
            // Rect blockers are only a fallback default when no paintable mask exists.
            if ((extensionBlockers == null || extensionBlockers.Length == 0) &&
                (tableBounds == null || !tableBounds.HasRoadBlockMask))
            {
                Transform blockerRoot = tableBounds != null ? tableBounds.transform : transform;
                var rects = blockerRoot.GetComponentsInChildren<RectTransform>(true);
                for (int i = 0; i < rects.Length; i++)
                {
                    if (rects[i].name == "Top Bar UI")
                    {
                        extensionBlockers = new[] { rects[i] };
                        break;
                    }
                }
            }
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
                    // Thin dense networks: deterministically hide a fraction of connections (visual only).
                    if (IsConnectionCulled(sc.TileId, sIdx, 0)) continue;
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
                    if (IsConnectionCulled(hc.TileId, hc.HubIndex, 1)) continue;
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
                    // Hub positions in holder space + their centroid, for outward-only
                    // extension checks and hub-clearance tests.
                    _hubHolderPositions.Clear();
                    Vector2 centroid = Vector2.zero;
                    for (int h = 0; h < hubs.Count; h++)
                    {
                        Vector2 hp = RootToHolderSpace(GetHubLocalPosition(hubs[h], root), root, hhParent);
                        _hubHolderPositions.Add(hp);
                        centroid += hp;
                    }
                    if (hubs.Count > 0) centroid /= hubs.Count;
                    _lastLineEndpoints.Clear();

                    var edges = graph.Edges;
                    for (int e = 0; e < edges.Count; e++)
                    {
                        var edge = edges[e];
                        int idxA = edge.FromId;
                        int idxB = edge.ToId;
                        if (idxA < 0 || idxA >= hubs.Count || idxB < 0 || idxB >= hubs.Count) continue;

                        var key = idxA < idxB ? (idxA, idxB) : (idxB, idxA);
                        if (!_currentHubHubKeys.Add(key)) continue; // deduplicate A→B / B→A

                        // Normalize so 'a' is always the lower-index hub's end (stable override keys).
                        Vector2 rootA = GetHubLocalPosition(hubs[key.Item1], root);
                        Vector2 rootB = GetHubLocalPosition(hubs[key.Item2], root);
                        Vector2 a = RootToHolderSpace(rootA, root, hhParent);
                        Vector2 b = RootToHolderSpace(rootB, root, hhParent);

                        if (extendHubLinesToTableEdge && tableBounds != null)
                        {
                            Vector2 d = b - a;
                            if (d.sqrMagnitude > 1f)
                            {
                                Vector2 dir = d.normalized;
                                if (ShouldExtend(a, -dir, centroid, key.Item1, key.Item2))
                                    a = ExtendToTableEdge(a, -dir, hhParent);
                                if (ShouldExtend(b, dir, centroid, key.Item1, key.Item2))
                                    b = ExtendToTableEdge(b, dir, hhParent);
                            }
                        }

                        // Hand-edited endpoints always win over the automatic extension rules.
                        if (_extensionOverrides.TryGetValue((key.Item1, key.Item2, 0), out Vector2 ovA))
                            a = RootToHolderSpace(ovA, root, hhParent);
                        if (_extensionOverrides.TryGetValue((key.Item1, key.Item2, 1), out Vector2 ovB))
                            b = RootToHolderSpace(ovB, root, hhParent);

                        _waypoints.TryGetValue((key.Item1, key.Item2, SegExtMin), out List<Vector2> wpExtMin);
                        _waypoints.TryGetValue((key.Item1, key.Item2, SegCore), out List<Vector2> wpCore);
                        _waypoints.TryGetValue((key.Item1, key.Item2, SegExtMax), out List<Vector2> wpExtMax);

                        _lastLineEndpoints[key] = new HubLineSnapshot
                        {
                            HubMin = key.Item1,
                            HubMax = key.Item2,
                            EndMin = HolderToRootSpace(a, root, hhParent),
                            EndMax = HolderToRootSpace(b, root, hhParent),
                            HubMinPos = rootA,
                            HubMaxPos = rootB,
                            WpExtMin = wpExtMin,
                            WpCore = wpCore,
                            WpExtMax = wpExtMax
                        };

                        // Build the full road path in holder space, per sub-segment:
                        //   endMin(a) -> [extMin bends] -> hubMin -> [core bends] -> hubMax -> [extMax bends] -> endMax(b)
                        // then collapse collinear/coincident points so straight runs stay 1 segment.
                        _pathScratch.Clear();
                        _pathScratch.Add(a);
                        AppendWps(wpExtMin, root, hhParent);
                        _pathScratch.Add(RootToHolderSpace(rootA, root, hhParent));
                        AppendWps(wpCore, root, hhParent);
                        _pathScratch.Add(RootToHolderSpace(rootB, root, hhParent));
                        AppendWps(wpExtMax, root, hhParent);
                        _pathScratch.Add(b);
                        CollapseCollinear(_pathScratch);

                        DrawRoadPolyline(key, _pathScratch, hhParent);
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
                    var segs = kv.Value;
                    for (int i = 0; i < segs.Count; i++)
                    {
                        segs[i].SetActive(false);
                        _pool.Add(segs[i]);
                    }
                    segs.Clear();
                    toRemoveHubHub.Add(kv.Key);
                }
            }
            for (int i = 0; i < toRemoveHubHub.Count; i++)
                _activeHubHub.Remove(toRemoveHubHub[i]);

            // --- Update building marker connection states ---
            UpdateMarkerConnectionStates();

            // Notify when the drawn road layout changed so stops can re-cover extensions.
            int geomHash = ComputeGeometryHash();
            if (geomHash != _lastGeometryHash)
            {
                _lastGeometryHash = geomHash;
                OnRoadGeometryChanged?.Invoke();
            }
        }

        private int ComputeGeometryHash()
        {
            unchecked
            {
                int h = 17;
                foreach (var kv in _lastLineEndpoints)
                {
                    var s = kv.Value;
                    h = h * 31 + s.HubMin;
                    h = h * 31 + s.HubMax;
                    h = h * 31 + s.EndMin.GetHashCode();
                    h = h * 31 + s.EndMax.GetHashCode();
                    h = h * 31 + s.HubMinPos.GetHashCode();
                    h = h * 31 + s.HubMaxPos.GetHashCode();
                    if (s.WpExtMin != null)
                        for (int i = 0; i < s.WpExtMin.Count; i++) h = h * 31 + s.WpExtMin[i].GetHashCode();
                    if (s.WpExtMax != null)
                        for (int i = 0; i < s.WpExtMax.Count; i++) h = h * 31 + s.WpExtMax[i].GetHashCode();
                }
                return h;
            }
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

        // ── Road editing API (used by RoadNetworkEditor) ──

        /// <summary>Copy the last drawn hub-hub lines (content-root local space) into the buffer.</summary>
        public void GetHubLineSnapshots(List<HubLineSnapshot> buffer)
        {
            buffer.Clear();
            foreach (var kv in _lastLineEndpoints)
                buffer.Add(kv.Value);
        }

        /// <summary>Pin a line end to a hand-edited position (content-root local). end: 0 = lower-index hub side.</summary>
        public void SetExtensionOverride(int hubMin, int hubMax, int end, Vector2 contentLocalPos)
        {
            _extensionOverrides[(hubMin, hubMax, end)] = contentLocalPos;
        }

        public bool RemoveExtensionOverride(int hubMin, int hubMax, int end)
        {
            return _extensionOverrides.Remove((hubMin, hubMax, end));
        }

        public void ClearExtensionOverrides()
        {
            _extensionOverrides.Clear();
        }

        public void GetExtensionOverrides(List<ExtensionOverrideEntry> buffer)
        {
            buffer.Clear();
            foreach (var kv in _extensionOverrides)
                buffer.Add(new ExtensionOverrideEntry
                {
                    hubMin = kv.Key.Item1,
                    hubMax = kv.Key.Item2,
                    end = kv.Key.Item3,
                    pos = kv.Value
                });
        }

        /// <summary>Redraw all connections now (e.g. after changing overrides from an editor tool).</summary>
        public void RefreshNow() => Refresh();

        /// <summary>Set the fraction of per-building connection lines randomly hidden (0..1) and redraw.
        /// Purely visual de-cluttering for dense networks; scoring/connectivity is unaffected.</summary>
        public void SetConnectionRemoval(float rate, int seed)
        {
            connectionRemovalRate = Mathf.Clamp01(rate);
            connectionRemovalSeed = seed;
            RefreshNow();
        }

        /// <summary>Deterministic keep/drop for one connection, stable across frames for the same
        /// (tileId, index, typeSalt, seed). typeSalt separates stop-lines from hub-lines so they
        /// aren't dropped in lockstep.</summary>
        private bool IsConnectionCulled(string tileId, int index, int typeSalt)
        {
            if (connectionRemovalRate <= 0f) return false;
            if (connectionRemovalRate >= 1f) return true;
            unchecked
            {
                uint h = 2166136261u; // FNV-1a
                if (tileId != null)
                    for (int i = 0; i < tileId.Length; i++) h = (h ^ tileId[i]) * 16777619u;
                h = (h ^ (uint)index) * 16777619u;
                h = (h ^ (uint)typeSalt) * 16777619u;
                h = (h ^ (uint)connectionRemovalSeed) * 16777619u;
                float r = (h & 0xFFFFFFu) / (float)0x1000000u; // 0..1
                return r < connectionRemovalRate;
            }
        }

        // ── road waypoints (bend points), per sub-segment ──

        private void AppendWps(List<Vector2> wps, RectTransform root, RectTransform holder)
        {
            if (wps == null) return;
            for (int w = 0; w < wps.Count; w++)
                _pathScratch.Add(RootToHolderSpace(wps[w], root, holder));
        }

        private List<Vector2> WaypointList(int hubMin, int hubMax, int seg, bool create)
        {
            var key = (hubMin, hubMax, seg);
            if (!_waypoints.TryGetValue(key, out var list) && create)
            {
                list = new List<Vector2>();
                _waypoints[key] = list;
            }
            return list;
        }

        /// <summary>Insert a bend point (content-root local) into a road sub-segment at list position index.</summary>
        public void InsertWaypoint(int hubMin, int hubMax, int seg, int index, Vector2 contentLocalPos)
        {
            var list = WaypointList(hubMin, hubMax, seg, true);
            index = Mathf.Clamp(index, 0, list.Count);
            list.Insert(index, contentLocalPos);
        }

        public void SetWaypoint(int hubMin, int hubMax, int seg, int index, Vector2 contentLocalPos)
        {
            var list = WaypointList(hubMin, hubMax, seg, false);
            if (list != null && index >= 0 && index < list.Count)
                list[index] = contentLocalPos;
        }

        public void RemoveWaypoint(int hubMin, int hubMax, int seg, int index)
        {
            var list = WaypointList(hubMin, hubMax, seg, false);
            if (list != null && index >= 0 && index < list.Count)
            {
                list.RemoveAt(index);
                if (list.Count == 0) _waypoints.Remove((hubMin, hubMax, seg));
            }
        }

        public void ClearWaypoints()
        {
            _waypoints.Clear();
        }

        public void GetWaypointEntries(List<WaypointEntry> buffer)
        {
            buffer.Clear();
            foreach (var kv in _waypoints)
                for (int i = 0; i < kv.Value.Count; i++)
                    buffer.Add(new WaypointEntry { hubMin = kv.Key.Item1, hubMax = kv.Key.Item2, seg = kv.Key.Item3, index = i, pos = kv.Value[i] });
        }

        // ── polyline drawing ──

        /// <summary>Draw a road as a chain of pooled segment visuals through the given holder-space points.</summary>
        private void DrawRoadPolyline((int, int) key, List<Vector2> points, RectTransform parent)
        {
            if (!_activeHubHub.TryGetValue(key, out var segs))
            {
                segs = new List<IConnectionVisual>();
                _activeHubHub[key] = segs;
            }

            int needed = Mathf.Max(0, points.Count - 1);

            // Return surplus segments to the pool.
            while (segs.Count > needed)
            {
                int last = segs.Count - 1;
                segs[last].SetActive(false);
                _pool.Add(segs[last]);
                segs.RemoveAt(last);
            }
            // Acquire missing segments.
            while (segs.Count < needed)
            {
                var v = Acquire(parent);
                if (v == null) break;
                segs.Add(v);
            }

            for (int i = 0; i < segs.Count; i++)
            {
                segs[i].UpdateEndpoints(points[i], points[i + 1]);
                if (useHubToHubColorOverride)
                    ApplyColor(segs[i], hubToHubColor);
                segs[i].SetActive(true);
            }
        }

        /// <summary>Drop interior points that are coincident with, or collinear between, their neighbors.</summary>
        private static void CollapseCollinear(List<Vector2> pts)
        {
            const float coincidentSqr = 1f;
            const float collinearEps = 0.5f; // px of perpendicular deviation tolerated

            for (int i = pts.Count - 2; i >= 1; i--)
            {
                Vector2 prev = pts[i - 1];
                Vector2 cur = pts[i];
                Vector2 next = pts[i + 1];

                if ((cur - prev).sqrMagnitude < coincidentSqr)
                {
                    pts.RemoveAt(i);
                    continue;
                }
                Vector2 seg = next - prev;
                float segLen = seg.magnitude;
                if (segLen < 0.001f) { pts.RemoveAt(i); continue; }
                float cross = Mathf.Abs((cur.x - prev.x) * seg.y - (cur.y - prev.y) * seg.x) / segLen;
                if (cross < collinearEps)
                    pts.RemoveAt(i);
            }
        }

        /// <summary>Inverse of RootToHolderSpace: holder center-origin → root center-origin.</summary>
        private static Vector2 HolderToRootSpace(Vector2 pos, RectTransform root, RectTransform holder)
        {
            if (holder == null || holder == root) return pos;
            Vector2 holderLocal = pos + (Vector2)holder.rect.center;
            Vector3 world = holder.TransformPoint(new Vector3(holderLocal.x, holderLocal.y, 0f));
            Vector3 rl = root.InverseTransformPoint(world);
            return new Vector2(rl.x, rl.y) - (Vector2)root.rect.center;
        }

        /// <summary>
        /// Whether a hub-line endpoint should extend outward: the direction must point
        /// away from the hub network's centroid, and the ray must not pass close to any
        /// other hub (which would look like a connection the graph doesn't have).
        /// </summary>
        private bool ShouldExtend(Vector2 origin, Vector2 dir, Vector2 centroid, int skipHubA, int skipHubB)
        {
            Vector2 fromCenter = origin - centroid;
            if (fromCenter.sqrMagnitude > 1f &&
                Vector2.Dot(dir, fromCenter.normalized) < extendOutwardDotThreshold)
                return false;

            for (int h = 0; h < _hubHolderPositions.Count; h++)
            {
                if (h == skipHubA || h == skipHubB) continue;
                Vector2 v = _hubHolderPositions[h] - origin;
                float along = Vector2.Dot(v, dir);
                if (along < 20f || along > maxHubLineExtension) continue;
                float perp = (v - dir * along).magnitude;
                if (perp < extensionHubClearance)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// March from a point along a direction (holder center-origin space) until the
        /// fan-shaped table edge, returning the furthest point still on the table.
        /// Coarse steps + binary refinement against the TableBounds alpha mask.
        /// </summary>
        private Vector2 ExtendToTableEdge(Vector2 start, Vector2 dir, RectTransform holder)
        {
            const float coarseStep = 24f;
            float traveled = 0f;
            Vector2 lastInside = start;

            while (traveled < maxHubLineExtension)
            {
                traveled += coarseStep;
                Vector2 p = start + dir * traveled;
                if (IsInsideTable(p, holder))
                {
                    lastInside = p;
                    continue;
                }

                float lo = traveled - coarseStep;
                float hi = traveled;
                for (int i = 0; i < 5; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (IsInsideTable(start + dir * mid, holder)) lo = mid;
                    else hi = mid;
                }
                return start + dir * lo;
            }
            return lastInside;
        }

        private bool IsInsideTable(Vector2 holderLocal, RectTransform holder)
        {
            Vector3 world = holder.TransformPoint(holderLocal + (Vector2)holder.rect.center);
            if (!tableBounds.ContainsWorld(world))
                return false;

            // Paintable no-road zones (dashboard arc, assistant, badges...).
            if (tableBounds.IsRoadBlocked(world))
                return false;

            // Treat UI regions (dashboard etc.) as off-table so extensions stop before them.
            if (extensionBlockers != null)
            {
                for (int i = 0; i < extensionBlockers.Length; i++)
                {
                    var blocker = extensionBlockers[i];
                    if (blocker == null || !blocker.gameObject.activeInHierarchy) continue;
                    Vector2 bl = blocker.InverseTransformPoint(world);
                    if (blocker.rect.Contains(bl))
                        return false;
                }
            }
            return true;
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
                marker.localRotation = Quaternion.Euler(0, 0, stopMarkerAngle);
                marker.gameObject.SetActive(true);
                _activeStopMarkers.Add(marker);
                StartStopMarkerPulse(marker);
            }

        }

        private void StartStopMarkerPulse(RectTransform marker)
        {
            if (marker == null || !stopPulse) return;
            marker.DOKill(false);
            Vector3 baseScale = stopMarkerPrefab != null ? stopMarkerPrefab.transform.localScale : Vector3.one;
            marker.localScale = baseScale;
            float peak = Mathf.Clamp(stopPulseScale, 1.002f, 2f);
            // Per-marker jitter: random start offset and ±20% period so the stops never breathe
            // in lockstep — a field of pins blinking in sync reads as one big strobe.
            float dur = Mathf.Max(0.05f, stopPulseDuration) * Random.Range(0.8f, 1.2f);
            marker.DOScale(baseScale * peak, dur)
                .SetDelay(Random.Range(0f, dur))
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

        private void ResetStopMarkerTween(RectTransform rt)
        {
            if (rt == null) return;
            rt.DOKill(false);
            rt.localScale = stopMarkerPrefab != null ? stopMarkerPrefab.transform.localScale : Vector3.one;
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
                var segs = kv.Value;
                for (int i = 0; i < segs.Count; i++)
                {
                    segs[i].SetActive(false);
                    _pool.Add(segs[i]);
                }
                segs.Clear();
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
