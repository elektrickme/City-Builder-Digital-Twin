using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;
using CityTwin.Input;
using CityTwin.Simulation;
using CityTwin.Config;
using CityTwin.UI;

namespace CityTwin.Core
{
    /// <summary>Wires TileTrackingManager and SimulationEngine for this instance. Subscribes to OSC and updates simulation. No statics.</summary>
    public class GameInstanceCoordinator : MonoBehaviour
    {
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private TileTrackingManager tileTracking;
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private GameInstanceRoot gameInstanceRoot;
        [Tooltip("Optional. If set and valid, simulation uses prefab-driven hub positions and population instead of config.")]
        [SerializeField] private HubRegistry hubRegistry;
        [Tooltip("Optional. Draws connection lines between buildings and hubs. Assign or auto-found in children.")]
        [SerializeField] private HubConnectionRenderer hubConnectionRenderer;
        [Tooltip("Optional. Validates building overlap against buildings and hubs using visual radii.")]
        [SerializeField] private PlacementOverlapValidator placementOverlapValidator;
        [Tooltip("Optional. Manages randomized hub layout presets. Restart will pick a new random preset.")]
        [SerializeField] private HubLayoutManager hubLayoutManager;
        [Tooltip("Optional. Start/language overlay. Restart will re-show it so the next session is a fresh start.")]
        [SerializeField] private StartScreenController startScreen;
        [Tooltip("Optional. End-of-session overlay. Restart will hide it.")]
        [SerializeField] private EndScreenController endScreen;

        private readonly Dictionary<string, string> _oscToEngineTileId = new Dictionary<string, string>();
        private readonly HashSet<string> _overBudgetTiles = new HashSet<string>();
        private int _overBudgetCounter;

        /// <summary>Current budget for this instance. Decremented when placing tiles.</summary>
        public int Budget { get; private set; }

        /// <summary>Fires whenever a tile is placed, moved, or removed (from any input source).</summary>
        public event System.Action OnTileActivity;

        private void Awake()
        {
            if (gameInstanceRoot == null) gameInstanceRoot = GetComponentInParent<GameInstanceRoot>(true) ?? GetComponent<GameInstanceRoot>();
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true);

            if (gameInstanceRoot != null && configLoader?.Config != null)
                gameInstanceRoot.ApplyOscConfig(configLoader.Config);

            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
            if (tileTracking == null) tileTracking = GetComponent<TileTrackingManager>();
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true);
            if (hubLayoutManager == null) hubLayoutManager = GetComponentInChildren<HubLayoutManager>(true);
        }

        private void OnEnable()
        {
            if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
            if (placementOverlapValidator == null) placementOverlapValidator = GetComponentInChildren<PlacementOverlapValidator>(true);
            if (startScreen == null) startScreen = GetComponentInChildren<CityTwin.UI.StartScreenController>(true);
            if (endScreen == null) endScreen = GetComponentInChildren<CityTwin.UI.EndScreenController>(true);
            if (configLoader != null)
            {
                configLoader.OnConfigLoaded += HandleConfigLoaded;
                if (configLoader.Config != null)
                    ApplyConfig(configLoader.Config);
            }
            if (tileTracking != null)
            {
                tileTracking.OnTileUpdated += OnTileUpdated;
                tileTracking.OnTileRemoved += OnTileRemoved;
            }
            if (hubConnectionRenderer == null) hubConnectionRenderer = GetComponentInChildren<HubConnectionRenderer>(true);
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged += PushHubIndicators;
        }

        private void Start()
        {
            ApplyRegistryHubsToSimulation();
            // Only generate stops here if config is already loaded; otherwise ApplyConfig will do it.
            if (configLoader != null && configLoader.Config != null)
                GenerateTransitStops();
            placementOverlapValidator?.RefreshHubFootprints();
            simulationEngine?.RecalculateMetrics();

            LogStartupDiagnostics();
        }

        private void LogStartupDiagnostics()
        {
            Debug.Log($"[Coordinator:Startup] simulationEngine={simulationEngine != null} buildingSpawner={buildingSpawner != null} " +
                      $"tileTracking={tileTracking != null} configLoader={configLoader != null} hubRegistry={hubRegistry != null} " +
                      $"overlapValidator={placementOverlapValidator != null} connectionRenderer={hubConnectionRenderer != null}");
            Debug.Log($"[Coordinator:Startup] Budget={Budget}");

            if (buildingSpawner != null && buildingSpawner.ContentRoot != null)
            {
                var cr = buildingSpawner.ContentRoot;
                Debug.Log($"[Coordinator:Startup] ContentRoot rect=({cr.rect.width:F0}x{cr.rect.height:F0}) pivot=({cr.pivot.x:F2},{cr.pivot.y:F2})");
            }
            else
                Debug.LogWarning("[Coordinator:Startup] ContentRoot is NULL — TUIO mapping will fail!");

            if (simulationEngine != null)
            {
                var graph = simulationEngine.TransitGraph;
                Debug.Log($"[Coordinator:Startup] TransitGraph nodes={graph.Nodes.Count} edges={graph.Edges.Count}");
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    var n = graph.Nodes[i];
                    Debug.Log($"[Coordinator:Startup]   Node[{i}] pos=({n.Position.x:F1},{n.Position.y:F1}) pop={n.Population:F0}");
                }
                for (int i = 0; i < graph.Edges.Count; i++)
                {
                    var e = graph.Edges[i];
                    Debug.Log($"[Coordinator:Startup]   Edge[{i}] {e.FromId}->{e.ToId} len={e.Length:F1}");
                }
            }

            if (hubRegistry != null && hubRegistry.IsValid)
            {
                var hubs = hubRegistry.Hubs;
                for (int i = 0; i < hubs.Count; i++)
                {
                    var h = hubs[i];
                    Vector2 contentPos = buildingSpawner != null
                        ? buildingSpawner.WorldToContentLocal(h.transform.position)
                        : h.Position2D;
                    Debug.Log($"[Coordinator:Startup]   Hub[{i}] worldPos={h.transform.position} contentLocal=({contentPos.x:F1},{contentPos.y:F1}) pop={h.Population:F0}");
                }
            }
            else
                Debug.LogWarning("[Coordinator:Startup] HubRegistry is null or invalid — no scoring hubs!");

            if (configLoader?.Config?.Buildings != null)
            {
                Debug.Log($"[Coordinator:Startup] BuildingCatalog has {configLoader.Config.Buildings.Length} entries: " +
                          string.Join(", ", System.Array.ConvertAll(configLoader.Config.Buildings, b => b.Id)));
            }
            else
                Debug.LogWarning("[Coordinator:Startup] No building catalog loaded!");
        }

        private void OnDisable()
        {
            if (configLoader != null)
                configLoader.OnConfigLoaded -= HandleConfigLoaded;
            if (tileTracking != null)
            {
                tileTracking.OnTileUpdated -= OnTileUpdated;
                tileTracking.OnTileRemoved -= OnTileRemoved;
            }
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged -= PushHubIndicators;
        }

        private void HandleConfigLoaded(GameConfig cfg)
        {
            if (cfg == null) return;
            ApplyConfig(cfg);
            gameInstanceRoot?.ApplyOscConfig(cfg);
        }

        private void ApplyConfig(GameConfig cfg)
        {
            if (cfg == null) return;

            Budget = cfg.Budget?.startingBudget ?? 1000;
            simulationEngine?.SetBuildingCatalog(new List<BuildingDefinition>(cfg.Buildings ?? System.Array.Empty<BuildingDefinition>()));

            simulationEngine?.SetConfig(cfg.Scoring, cfg.Accessibility);

            if (simulationEngine != null && buildingSpawner != null)
            {
                simulationEngine.HaloRadiusResolver = (buildingId) =>
                {
                    if (buildingSpawner.TryGetEstimatedBuildingRadius(buildingId, out float r))
                        return r;
                    return 0f;
                };
            }

            if (cfg.Map != null && cfg.Map.nodes != null && cfg.Map.nodes.Length > 0)
                BuildTransitGraphFromConfig(cfg.Map);
            else
                BuildDefaultTransitGraphIfNeeded();

            ApplyRegistryHubsToSimulation();
            GenerateTransitStops();
            placementOverlapValidator?.RefreshHubFootprints();

            if (sessionTimer != null)
            {
                sessionTimer.SetFromConfig(cfg);
                sessionTimer.Stop();
            }
        }

        /// <summary>
        /// Process a tile update through the same path used by OSC/TUIO.
        /// Returns true only when the tile is tracked by the coordinator after processing
        /// (e.g. placement succeeded and budget allowed, or an existing tile was updated).
        /// </summary>
        public bool TryProcessTileUpdate(TilePose pose, out string engineTileId)
        {
            engineTileId = null;
            if (string.IsNullOrEmpty(pose.TileId))
                return false;

            OnTileUpdated(pose);
            return _oscToEngineTileId.TryGetValue(pose.TileId, out engineTileId);
        }

        /// <summary>
        /// Remove a tracked tile through the same path used by OSC/TUIO alive messages.
        /// Returns true when a tracked tile id existed and was removed.
        /// </summary>
        public bool TryProcessTileRemoval(string tileId)
        {
            if (string.IsNullOrEmpty(tileId))
                return false;
            bool existed = _oscToEngineTileId.ContainsKey(tileId);
            if (!existed)
                return false;

            OnTileRemoved(tileId);
            return true;
        }

        /// <summary>
        /// Local per-instance restart. Clears all placed tiles, resets budget, picks a new
        /// random hub layout preset, rebuilds the transit graph, and restarts the session timer.
        /// Does NOT reload the scene — other game instances are unaffected.
        /// </summary>
        public void RestartGame()
        {
            Debug.Log($"[Coordinator] RestartGame requested for instance {gameInstanceRoot?.InstanceId ?? -1}");

            buildingSpawner?.ClearAll();
            simulationEngine?.ClearAllTiles();
            placementOverlapValidator?.ClearAllTiles();
            tileTracking?.ClearSessions();
            _oscToEngineTileId.Clear();
            _overBudgetTiles.Clear();

            if (configLoader?.Config != null)
                Budget = configLoader.Config.Budget?.startingBudget ?? 1000;

            hubLayoutManager?.PickRandomPreset();

            ApplyRegistryHubsToSimulation();
            GenerateTransitStops();
            placementOverlapValidator?.RefreshHubFootprints();
            simulationEngine?.RecalculateMetrics();

            sessionTimer?.Stop();
            endScreen?.Hide();
            startScreen?.ShowStartScreen();

            Debug.Log($"[Coordinator] Restart complete — budget={Budget}, preset={hubLayoutManager?.ActivePreset?.name}");
        }

        private void ApplyRegistryHubsToSimulation()
        {
            if (simulationEngine == null) return;
            if (hubRegistry == null) hubRegistry = GetComponentInChildren<HubRegistry>(true);
            if (hubRegistry == null) return;

            hubRegistry.FetchHubs();

            var hubs = new List<(Vector2 position, float population)>();
            if (hubRegistry.IsValid && hubRegistry.Hubs.Count > 0)
            {
                foreach (var h in hubRegistry.Hubs)
                {
                    Vector2 hubPos = buildingSpawner != null
                        ? buildingSpawner.WorldToContentLocal(h.transform.position)
                        : h.Position2D;
                    hubs.Add((hubPos, h.Population));
                }
            }

            simulationEngine.SetScoringHubs(hubs);

            if (hubs.Count >= 2)
                RebuildTransitGraphFromHubs(hubs);
        }

        /// <summary>
        /// Build a transit graph using the actual hub positions in content-local space.
        /// Each hub connects to its k nearest neighbors (k=3), matching the HTML editor logic.
        /// Edges are undirected and deduplicated.
        /// </summary>
        private void RebuildTransitGraphFromHubs(List<(Vector2 position, float population)> hubs)
        {
            if (simulationEngine == null || hubs.Count < 2) return;

            var graph = new TransitGraph();
            for (int i = 0; i < hubs.Count; i++)
                graph.AddNode(hubs[i].position, hubs[i].population);

            const int k = 3;
            var edgeSet = new HashSet<long>();
            int edgeCount = 0;

            for (int i = 0; i < hubs.Count; i++)
            {
                // Sort all other hubs by distance
                var nearest = new List<(int index, float dist)>();
                for (int j = 0; j < hubs.Count; j++)
                {
                    if (j == i) continue;
                    nearest.Add((j, Vector2.Distance(hubs[i].position, hubs[j].position)));
                }
                nearest.Sort((a, b) => a.dist.CompareTo(b.dist));

                int keep = Mathf.Min(k, nearest.Count);
                for (int n = 0; n < keep; n++)
                {
                    int a = i;
                    int b = nearest[n].index;
                    int lo = Mathf.Min(a, b);
                    int hi = Mathf.Max(a, b);
                    long key = ((long)lo << 32) | (uint)hi;
                    if (!edgeSet.Add(key)) continue;

                    float dist = nearest[n].dist;
                    graph.AddEdge(a, b, dist);
                    graph.AddEdge(b, a, dist);
                    edgeCount += 2;
                }
            }

            simulationEngine.SetTransitGraph(graph);
            Debug.Log($"[Coordinator] Rebuilt transit graph (k-nearest k={k}): {hubs.Count} nodes, {edgeCount} directed edges, {edgeSet.Count} unique links");
        }

        /// <summary>Generate transit stops along road edges using config values.</summary>
        private void GenerateTransitStops()
        {
            if (simulationEngine == null)
            {
                Debug.LogWarning("[Coordinator:Stops] simulationEngine is NULL — cannot generate stops.");
                return;
            }
            var graph = simulationEngine.TransitGraph;
            if (graph == null)
            {
                Debug.LogWarning("[Coordinator:Stops] TransitGraph is NULL — cannot generate stops.");
                return;
            }

            var stopsConfig = configLoader?.Config?.Stops;
            float spacing = stopsConfig?.spacing ?? 60f;
            float minNodeDist = stopsConfig?.minDistanceFromNode ?? 30f;
            float minStopDist = stopsConfig?.minDistanceBetweenStops ?? 30f;
            float jitter = stopsConfig?.spacingJitter ?? 0.25f;
            float removal = stopsConfig?.removalRate ?? 0.30f;
            int seed = stopsConfig?.seed ?? 1234;

            Debug.Log($"[Coordinator:Stops] Generating stops — graph has {graph.Nodes.Count} nodes, {graph.Edges.Count} edges. Config: spacing={spacing}, minNodeDist={minNodeDist}, minStopDist={minStopDist}, jitter={jitter}, removal={removal}, seed={seed}");
            graph.GenerateStops(spacing, minNodeDist, minStopDist, jitter, removal, seed);
            Debug.Log($"[Coordinator:Stops] Result: {graph.Stops.Count} stops generated.");
        }

        private static int IndexOfHub(IReadOnlyList<ResidentialHubMono> list, ResidentialHubMono hub)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == hub) return i;
            return -1;
        }

        /// <summary>Build transit graph from config map (nodes + edges). Node positions are treated as content root local space.</summary>
        private void BuildTransitGraphFromConfig(GameConfig.MapData map)
        {
            if (simulationEngine == null || map.nodes == null || map.nodes.Length == 0) return;
            var graph = new TransitGraph();
            for (int i = 0; i < map.nodes.Length; i++)
            {
                var n = map.nodes[i];
                Vector2 nodePos = new Vector2(n.x, n.y);
                graph.AddNode(nodePos, n.population > 0 ? n.population : 50000f);
            }
            if (map.edges != null)
            {
                for (int i = 0; i < map.edges.Length; i++)
                {
                    var e = map.edges[i];
                    if (e.from >= 0 && e.from < map.nodes.Length && e.to >= 0 && e.to < map.nodes.Length)
                    {
                        float len = e.length > 0 ? e.length : Vector2.Distance(graph.GetNode(e.from).Position, graph.GetNode(e.to).Position);
                        graph.AddEdge(e.from, e.to, len);
                    }
                }
            }
            simulationEngine.SetTransitGraph(graph);
            if (map.obstacles != null && map.obstacles.Length > 0)
            {
                var obstacles = new List<(Vector2 center, float radius)>();
                foreach (var o in map.obstacles)
                    obstacles.Add((new Vector2(o.x, o.y), Mathf.Max(0.1f, o.radius)));
                simulationEngine.SetObstacles(obstacles);
            }
        }

        /// <summary>Build a simple default hub+road layout when no map config is provided. Uses content root local space (center-origin).</summary>
        private void BuildDefaultTransitGraphIfNeeded()
        {
            if (simulationEngine == null) return;
            var graph = new TransitGraph();
            float half = 90f;
            graph.AddNode(new Vector2(-half, -half), 50000f);
            graph.AddNode(new Vector2( half, -half), 50000f);
            graph.AddNode(new Vector2( half,  half), 50000f);
            graph.AddNode(new Vector2(-half,  half), 50000f);
            float len = Vector2.Distance(graph.GetNode(0).Position, graph.GetNode(1).Position);
            graph.AddEdge(0, 1, len);
            graph.AddEdge(1, 2, len);
            graph.AddEdge(2, 3, len);
            graph.AddEdge(3, 0, len);
            graph.AddEdge(0, 2, len * 1.4f);
            graph.AddEdge(1, 3, len * 1.4f);
            simulationEngine.SetTransitGraph(graph);
        }

        [Tooltip("Scale TUIO 0-1 coordinates to simulation space (default graph uses 0-300).")]
        [SerializeField] private float tableScale = 300f;

        private void OnTileUpdated(TilePose pose)
        {
            OnTileActivity?.Invoke();
            if (simulationEngine == null) { Debug.LogWarning("[Coordinator] simulationEngine is null, skipping."); return; }
            placementOverlapValidator?.RefreshHubFootprints();

            Vector2 simPos = buildingSpawner != null
                ? buildingSpawner.TuioToLocalPosition(pose.Position)
                : pose.Position * tableScale;
            var simPose = new TilePose(simPos, pose.Rotation, pose.BuildingId, pose.SourceId, pose.TileId);

            if (_oscToEngineTileId.TryGetValue(pose.TileId, out string engineId))
            {
                if (_overBudgetTiles.Contains(engineId))
                {
                    buildingSpawner?.MoveBuilding(pose, engineId);
                    return;
                }

                buildingSpawner?.MoveBuilding(pose, engineId);
                float radius = placementOverlapValidator != null
                    ? placementOverlapValidator.ResolveRadiusForTile(engineId, simPose.BuildingId)
                    : 24f;
                bool overlaps = placementOverlapValidator != null &&
                                placementOverlapValidator.IsOverlapping(engineId, simPose.Position, radius);

                simulationEngine.UpdateTilePosition(engineId, simPose.Position, simPose.Rotation, overlaps);
                bool connected = simulationEngine.IsTileConnected(engineId);

                placementOverlapValidator?.UpsertTile(engineId, simPose.Position, radius, overlaps);
                placementOverlapValidator?.SetTileVisualInvalid(engineId, overlaps);
                UpdateMarkerConnectionState(engineId, overlaps);
                return;
            }

            int price = 0;
            if (configLoader?.Config?.Buildings != null)
            {
                foreach (var b in configLoader.Config.Buildings)
                {
                    if (b.Id == pose.BuildingId) { price = b.Price; break; }
                }
            }
            float candidateRadius = placementOverlapValidator != null
                ? placementOverlapValidator.ResolveRadiusForBuilding(simPose.BuildingId)
                : 24f;
            bool initialOverlap = placementOverlapValidator != null &&
                placementOverlapValidator.IsOverlapping(null, simPose.Position, candidateRadius);

            Debug.Log($"[Coordinator:NewTile] building={pose.BuildingId} price={price} budget={Budget} overlap={initialOverlap} candidateRadius={candidateRadius:F1}");

            if (initialOverlap)
            {
                Debug.Log($"[Coordinator:NewTile] BLOCKED — overlapping another building or hub at ({simPos.x:F1},{simPos.y:F1}).");
                return;
            }
            if (price > 0 && Budget < price)
            {
                Debug.LogWarning($"[Coordinator:NewTile] OVER BUDGET — need {price}, have {Budget}. Spawning visual-only marker.");
                string overBudgetId = $"ob:{pose.TileId}:{++_overBudgetCounter}";
                _oscToEngineTileId[pose.TileId] = overBudgetId;
                _overBudgetTiles.Add(overBudgetId);
                buildingSpawner?.SpawnBuilding(pose, overBudgetId);
                buildingSpawner?.SetMarkerOverBudget(overBudgetId, true);
                buildingSpawner?.SetMarkerConnectionState(overBudgetId, MarkerConnectionState.Inactive);
                return;
            }
            if (price > 0) Budget -= price;
            engineId = simulationEngine.AddTile(simPose);

            if (string.IsNullOrEmpty(engineId))
            {
                Debug.LogWarning($"[Coordinator:NewTile] BLOCKED — SimulationEngine.AddTile returned null for building '{pose.BuildingId}'. " +
                                 "Building not in catalog? Check classId→buildingId mapping.");
                return;
            }

            if (!string.IsNullOrEmpty(pose.TileId))
                _oscToEngineTileId[pose.TileId] = engineId;
            if (buildingSpawner == null) Debug.LogWarning("[Coordinator:NewTile] buildingSpawner is null, no visual will be spawned.");

            buildingSpawner?.SpawnBuilding(pose, engineId);
            float placedRadius = placementOverlapValidator != null
                ? placementOverlapValidator.ResolveRadiusForTile(engineId, simPose.BuildingId)
                : candidateRadius;
            placementOverlapValidator?.UpsertTile(engineId, simPose.Position, placedRadius, false);
            placementOverlapValidator?.SetTileVisualInvalid(engineId, false);
            UpdateMarkerConnectionState(engineId, false);

            bool isConnected = simulationEngine.IsTileConnected(engineId);
            Debug.Log($"[Coordinator:NewTile] PLACED engineId={engineId} connected={isConnected} budgetLeft={Budget}");

            simulationEngine.RecalculateMetrics();
        }

        private void UpdateMarkerConnectionState(string engineId, bool overlapInvalid)
        {
            if (buildingSpawner == null || simulationEngine == null) return;

            bool inactive = simulationEngine.IsTileInactive(engineId);
            bool connected = simulationEngine.IsTileConnected(engineId);

            MarkerConnectionState state;
            if (inactive)
                state = MarkerConnectionState.Inactive;
            else if (!connected)
                state = MarkerConnectionState.Disconnected;
            else
                state = MarkerConnectionState.Connected;

            //Debug.Log($"[Coordinator:MarkerState] engineId={engineId} inactive={inactive} connected={connected} overlap={overlapInvalid} → {state}");
            buildingSpawner.SetMarkerConnectionState(engineId, state);
        }

        private void OnTileRemoved(string oscTileId)
        {
            OnTileActivity?.Invoke();
            if (simulationEngine == null) return;
            if (_oscToEngineTileId.TryGetValue(oscTileId, out string engineId))
            {
                if (_overBudgetTiles.Remove(engineId))
                {
                    _oscToEngineTileId.Remove(oscTileId);
                    buildingSpawner?.RemoveBuilding(engineId);
                    return;
                }

                string buildingId = simulationEngine.GetBuildingIdForTile(engineId);
                _oscToEngineTileId.Remove(oscTileId);
                buildingSpawner?.RemoveBuilding(engineId);
                simulationEngine.RemoveTile(engineId);
                placementOverlapValidator?.RemoveTile(engineId);
                RefundBudgetForBuilding(buildingId);
                //Debug.Log($"[Coordinator] Tile removed oscTileId={oscTileId} engineId={engineId} → refunded; budget now {Budget}");
            }
        }

        private void PushHubIndicators()
        {
            if (hubRegistry == null || simulationEngine == null) return;
            var hubs = hubRegistry.Hubs;
            var metrics = simulationEngine.HubMetrics;
            int count = Mathf.Min(hubs.Count, metrics.Count);
            for (int i = 0; i < count; i++)
            {
                var m = metrics[i];
                hubs[i].SetMetricState(m.Environment, m.Economy, m.HealthSafety, m.CultureEdu);
            }
        }

        private void RefundBudgetForBuilding(string buildingId)
        {
            if (configLoader?.Config?.Buildings == null || string.IsNullOrEmpty(buildingId)) return;
            foreach (var b in configLoader.Config.Buildings)
            {
                if (b.Id == buildingId)
                {
                    Budget += b.Price;
                    return;
                }
            }
        }
    }
}
