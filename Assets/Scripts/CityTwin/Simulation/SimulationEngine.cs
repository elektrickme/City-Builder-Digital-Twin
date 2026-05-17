using System;
using System.Collections.Generic;
using UnityEngine;
using CityTwin.Config;
using CityTwin.Core;

namespace CityTwin.Simulation
{
    /// <summary>Per-instance simulation engine. HTML-style curve-based road-network scoring.
    /// Raw = NORM × (base / refBase) × sizeBoost × curve × nearCap × (W / 100k).
    /// Per-hub QOL = weighted mean of 4 pillars. City QOL = mean(hub.qol) − penalty × (max − min), capped.</summary>
    public class SimulationEngine : MonoBehaviour
    {
        private readonly List<PlacedTile> _placedTiles = new List<PlacedTile>();
        private List<BuildingDefinition> _buildingCatalog = new List<BuildingDefinition>();
        private TransitGraph _transitGraph = new TransitGraph();
        private List<(Vector2 center, float radius)> _obstacles = new List<(Vector2, float)>();
        private List<(Vector2 position, float population)> _scoringHubs = new List<(Vector2, float)>();
        private int _nextTileId;

        // Accessibility / connectivity config
        private float _roadConnectRange = 500f;
        private float _defaultConnectionRadius = 600f;

        // Scoring config (HTML-style)
        private float _norm = 150f;
        private float _equalDistrictWeight = 100000f;
        private float _influenceRefBase = 10f;
        private float _influenceReferenceMeters = 15f;
        private float _distanceExponent = 1f;
        private float _distanceFloor = 50f;
        private float _distanceScale = 100f;
        private float _maxRoadDistance = 150f;
        private float _qolBalancePenalty = 0.5f;
        private float _qolCap = 80f;
        private float _sizeBoostSmall = 1.2f;
        private float _sizeBoostMedium = 1.1f;
        private float _sizeBoostLarge = 1f;
        private float _nearCapSmall = 0.5f;
        private float _nearCapMedium = 0.75f;
        private float _nearCapLarge = 1f;
        private float _impactRadiusSmall = 75f;
        private float _impactRadiusMedium = 112f;
        private float _impactRadiusLarge = 150f;
        private float _qolWeightEnv = 1f;
        private float _qolWeightEco = 1f;
        private float _qolWeightSaf = 1f;
        private float _qolWeightCul = 1f;

        private float _qol;
        private float _environment;
        private float _economy;
        private float _healthSafety;
        private float _cultureEdu;

        public float Qol => _qol;
        public float Environment => _environment;
        public float Economy => _economy;
        public float HealthSafety => _healthSafety;
        public float CultureEdu => _cultureEdu;

        // Debug-tunable properties (get/set with recalc on change)
        public float Norm { get => _norm; set => _norm = Mathf.Max(0.001f, value); }
        public float InfluenceRefBase { get => _influenceRefBase; set => _influenceRefBase = Mathf.Max(0.001f, value); }
        public float InfluenceReferenceMeters { get => _influenceReferenceMeters; set => _influenceReferenceMeters = Mathf.Max(0.01f, value); }
        public float DistanceExponent { get => _distanceExponent; set => _distanceExponent = Mathf.Max(0f, value); }
        public float DistanceFloor { get => _distanceFloor; set => _distanceFloor = Mathf.Max(0f, value); }
        public float DistanceScale { get => _distanceScale; set => _distanceScale = Mathf.Max(1f, value); }
        public float MaxRoadDistance { get => _maxRoadDistance; set => _maxRoadDistance = Mathf.Max(1f, value); }
        public float QolBalancePenalty { get => _qolBalancePenalty; set => _qolBalancePenalty = Mathf.Max(0f, value); }
        public float QolCap { get => _qolCap; set => _qolCap = Mathf.Max(1f, value); }
        public float RoadConnectRange { get => _roadConnectRange; set => _roadConnectRange = Mathf.Max(1f, value); }
        public float ImpactRadiusSmall { get => _impactRadiusSmall; set => _impactRadiusSmall = Mathf.Max(1f, value); }
        public float ImpactRadiusMedium { get => _impactRadiusMedium; set => _impactRadiusMedium = Mathf.Max(1f, value); }
        public float ImpactRadiusLarge { get => _impactRadiusLarge; set => _impactRadiusLarge = Mathf.Max(1f, value); }

        public struct HubMetricSnapshot
        {
            public int HubIndex;
            public float Environment;
            public float Economy;
            public float HealthSafety;
            public float CultureEdu;
            public float Qol;
        }

        private readonly List<HubMetricSnapshot> _hubMetrics = new List<HubMetricSnapshot>();
        public IReadOnlyList<HubMetricSnapshot> HubMetrics => _hubMetrics;

        public struct TileHubConnection
        {
            public string TileId;
            public int HubIndex;
        }

        private readonly List<TileHubConnection> _activeConnections = new List<TileHubConnection>();
        public IReadOnlyList<TileHubConnection> ActiveConnections => _activeConnections;

        public struct TileRoadConnection
        {
            public string TileId;
            public Vector2 SnapPoint;
            public float Distance;
        }

        private readonly List<TileRoadConnection> _activeRoadConnections = new List<TileRoadConnection>();
        public IReadOnlyList<TileRoadConnection> ActiveRoadConnections => _activeRoadConnections;

        public struct TileStopConnection
        {
            public string TileId;
            public Vector2 StopPosition;
            public float Distance;
            public int StopIndex;
        }

        private readonly List<TileStopConnection> _activeStopConnections = new List<TileStopConnection>();
        public IReadOnlyList<TileStopConnection> ActiveStopConnections => _activeStopConnections;

        public struct TileHubDirectConnection
        {
            public string TileId;
            public int HubIndex;
            public Vector2 HubPosition;
            public float Distance;
        }

        private readonly List<TileHubDirectConnection> _activeHubDirectConnections = new List<TileHubDirectConnection>();
        public IReadOnlyList<TileHubDirectConnection> ActiveHubDirectConnections => _activeHubDirectConnections;

        public struct TilePlacementState
        {
            public string TileId;
            public string BuildingId;
            public Vector2 Position;
            public bool Connected;
            public bool Inactive;
            public bool OverlapInvalid;
        }

        private readonly List<TilePlacementState> _tileStates = new List<TilePlacementState>();
        public IReadOnlyList<TilePlacementState> TileStates => _tileStates;

        public TransitGraph TransitGraph => _transitGraph;

        /// <summary>Resolved scoring hub positions in content-local space.
        /// Uses explicit scoring hubs when set, otherwise falls back to transit graph nodes.</summary>
        public IReadOnlyList<Vector2> HubPositions
        {
            get
            {
                if (_scoringHubs != null && _scoringHubs.Count > 0)
                {
                    var positions = new Vector2[_scoringHubs.Count];
                    for (int i = 0; i < _scoringHubs.Count; i++) positions[i] = _scoringHubs[i].position;
                    return positions;
                }
                var nodes = _transitGraph.Nodes;
                var arr = new Vector2[nodes.Count];
                for (int i = 0; i < nodes.Count; i++) arr[i] = nodes[i].Position;
                return arr;
            }
        }

        /// <summary>Optional delegate that returns the visual halo radius for a building id.
        /// When set, connection range uses halo radius instead of _roadConnectRange.</summary>
        public Func<string, float> HaloRadiusResolver { get; set; }

        public event Action OnMetricsChanged;

        [Tooltip("Log current metrics to console whenever they are recalculated (e.g. when no UI yet).")]
        [SerializeField] private bool logMetricsWhenChanged = true;

        public void SetBuildingCatalog(List<BuildingDefinition> catalog)
        {
            _buildingCatalog = catalog ?? new List<BuildingDefinition>();
        }

        /// <summary>Stop-search seed radius from catalog impact size and current scoring sliders.</summary>
        public bool TryGetImpactSearchRadius(string buildingId, out float radius)
        {
            radius = 0f;
            var b = GetBuilding(buildingId);
            if (b == null) return false;
            radius = GetImpactRadius(b);
            return true;
        }

        public void SetConfig(GameConfig.ScoringData scoring, GameConfig.AccessibilityData acc)
        {
            if (scoring != null)
            {
                _norm = Mathf.Max(0.001f, scoring.norm);
                _equalDistrictWeight = Mathf.Max(1f, scoring.equalDistrictWeight);
                _influenceRefBase = Mathf.Max(0.001f, scoring.influenceRefBase);
                _influenceReferenceMeters = Mathf.Max(0.01f, scoring.influenceReferenceMeters);
                _distanceExponent = Mathf.Max(0f, scoring.distanceExponent);
                _distanceFloor = Mathf.Max(0f, scoring.distanceFloor);
                _distanceScale = Mathf.Max(1f, scoring.distanceScale);
                _maxRoadDistance = Mathf.Max(1f, scoring.maxRoadDistance);
                _qolBalancePenalty = Mathf.Max(0f, scoring.qolBalancePenalty);
                _qolCap = Mathf.Max(1f, scoring.qolCap);
                _sizeBoostSmall = scoring.sizeBoostSmall;
                _sizeBoostMedium = scoring.sizeBoostMedium;
                _sizeBoostLarge = scoring.sizeBoostLarge;
                _nearCapSmall = scoring.nearCapSmall;
                _nearCapMedium = scoring.nearCapMedium;
                _nearCapLarge = scoring.nearCapLarge;
                _impactRadiusSmall = Mathf.Max(1f, scoring.impactRadiusSmall);
                _impactRadiusMedium = Mathf.Max(1f, scoring.impactRadiusMedium);
                _impactRadiusLarge = Mathf.Max(1f, scoring.impactRadiusLarge);
                _qolWeightEnv = Mathf.Max(0f, scoring.qolWeightEnv);
                _qolWeightEco = Mathf.Max(0f, scoring.qolWeightEco);
                _qolWeightSaf = Mathf.Max(0f, scoring.qolWeightSaf);
                _qolWeightCul = Mathf.Max(0f, scoring.qolWeightCul);
            }
            if (acc != null)
            {
                _roadConnectRange = Mathf.Max(1f, acc.roadConnectRange);
                _defaultConnectionRadius = Mathf.Max(1f, acc.defaultConnectionRadius);
            }
        }

        public void SetTransitGraph(TransitGraph graph)
        {
            _transitGraph = graph ?? new TransitGraph();
        }

        /// <summary>Set obstacles (e.g. water, mountains). Buildings placed on obstacles are inactive and do not affect QOL.</summary>
        public void SetObstacles(List<(Vector2 center, float radius)> obstacles)
        {
            _obstacles = obstacles ?? new List<(Vector2, float)>();
        }

        /// <summary>Set scoring hubs from HubRegistry (prefab-driven). When non-empty, metrics use (BaseValue × Population) / max(RadialDistance, epsilon). No dependency on config for hub positions or population.</summary>
        public void SetScoringHubs(List<(Vector2 position, float population)> hubs)
        {
            _scoringHubs = hubs ?? new List<(Vector2, float)>();
        }

        public string AddTile(TilePose pose)
        {
            var def = GetBuilding(pose.BuildingId);
            if (def == null)
            {
                Debug.LogWarning($"[SimEngine:AddTile] FAILED — no building in catalog for id '{pose.BuildingId}'. " +
                                 $"Catalog has {_buildingCatalog.Count} entries: [{string.Join(", ", _buildingCatalog.ConvertAll(b => b.Id))}]");
                return null;
            }
            string tileId = $"tile_{_nextTileId++}";
            bool inactive = IsOnObstacle(pose.Position);
            float connRange = GetConnectionRange(pose.BuildingId);
            var roadConns = inactive ? new List<TransitGraph.ConnectionPoint>()
                                     : _transitGraph.GetRoadConnections(pose.Position, connRange);
            var stopConns = inactive ? new List<TransitGraph.StopConnectionPoint>()
                                     : _transitGraph.GetStopConnections(pose.Position, connRange);

            bool nearHub = !inactive && IsNearAnyHub(pose.Position, connRange);
            bool connected = roadConns.Count > 0 || stopConns.Count > 0 || nearHub;

            Debug.Log($"[SimEngine:AddTile] {tileId} building={pose.BuildingId} pos=({pose.Position.x:F1},{pose.Position.y:F1}) " +
                      $"onObstacle={inactive} roadConns={roadConns.Count} stopConns={stopConns.Count} nearHub={nearHub} connected={connected} " +
                      $"connRange={connRange:F0} graphNodes={_transitGraph.Nodes.Count} graphEdges={_transitGraph.Edges.Count} stops={_transitGraph.Stops.Count}");

            _placedTiles.Add(new PlacedTile
            {
                TileId = tileId,
                BuildingId = pose.BuildingId,
                Position = pose.Position,
                Rotation = pose.Rotation,
                Inactive = inactive,
                OverlapInvalid = false,
                Connected = connected,
                RoadConnections = roadConns,
                StopConnections = stopConns
            });
            RecalculateMetrics();
            return tileId;
        }

        public bool UpdateTilePosition(string tileId, Vector2 position, float rotation, bool overlapInvalid = false)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            if (idx < 0) return false;
            var t = _placedTiles[idx];
            t.Position = position;
            t.Rotation = rotation;
            t.Inactive = IsOnObstacle(position);
            t.OverlapInvalid = overlapInvalid;
            float connRange = GetConnectionRange(t.BuildingId);
            t.RoadConnections = t.Inactive
                ? new List<TransitGraph.ConnectionPoint>()
                : _transitGraph.GetRoadConnections(position, connRange);
            t.StopConnections = t.Inactive
                ? new List<TransitGraph.StopConnectionPoint>()
                : _transitGraph.GetStopConnections(position, connRange);
            bool nearHub = !t.Inactive && IsNearAnyHub(position, connRange);
            t.Connected = t.RoadConnections.Count > 0 || t.StopConnections.Count > 0 || nearHub;
            _placedTiles[idx] = t;
            RecalculateMetrics();
            return true;
        }

        public bool RemoveTile(string tileId)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            if (idx < 0) return false;
            _placedTiles.RemoveAt(idx);
            RecalculateMetrics();
            return true;
        }

        /// <summary>Remove every placed tile. Used by per-instance restart flows.</summary>
        public void ClearAllTiles()
        {
            _placedTiles.Clear();
            _nextTileId = 0;
            RecalculateMetrics();
        }

        /// <summary>Number of currently placed tiles on this instance's play field.</summary>
        public int PlacedTileCount => _placedTiles.Count;

        /// <summary>True if the tile is placed on an obstacle (e.g. water) and does not affect QOL. Use for UI feedback.</summary>
        public bool IsTileInactive(string tileId)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            return idx >= 0 && _placedTiles[idx].Inactive;
        }

        /// <summary>True if the tile is connected to at least one road segment within roadConnectRange.</summary>
        public bool IsTileConnected(string tileId)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == tileId);
            return idx >= 0 && _placedTiles[idx].Connected;
        }

        /// <summary>Returns the building id for a placed tile, or null if not found. Use e.g. for refund on remove.</summary>
        public string GetBuildingIdForTile(string engineTileId)
        {
            int idx = _placedTiles.FindIndex(t => t.TileId == engineTileId);
            return idx >= 0 ? _placedTiles[idx].BuildingId : null;
        }

        public void RecalculateMetrics()
        {
            int hubCount;
            Vector2[] hubPositions;

            if (_scoringHubs != null && _scoringHubs.Count > 0)
            {
                hubCount = _scoringHubs.Count;
                hubPositions = new Vector2[hubCount];
                for (int i = 0; i < hubCount; i++)
                    hubPositions[i] = _scoringHubs[i].position;
            }
            else
            {
                var nodes = _transitGraph.Nodes;
                if (nodes.Count == 0)
                {
                    _environment = _economy = _healthSafety = _cultureEdu = 0f;
                    _qol = 0f;
                    _activeConnections.Clear();
                    _activeRoadConnections.Clear();
                    _activeStopConnections.Clear();
                    _activeHubDirectConnections.Clear();
                    _tileStates.Clear();
                    _hubMetrics.Clear();
                    if (logMetricsWhenChanged)
                        Debug.Log("[Metrics] QOL=0 (no hubs/graph) | tiles=" + _placedTiles.Count);
                    OnMetricsChanged?.Invoke();
                    return;
                }
                hubCount = nodes.Count;
                hubPositions = new Vector2[hubCount];
                for (int i = 0; i < hubCount; i++)
                    hubPositions[i] = nodes[i].Position;
            }

            // Raw pillar accumulators per hub (in NORM units)
            float[] hubEnv = new float[hubCount];
            float[] hubEco = new float[hubCount];
            float[] hubSaf = new float[hubCount];
            float[] hubCul = new float[hubCount];

            _activeConnections.Clear();
            _activeRoadConnections.Clear();
            _activeStopConnections.Clear();
            _activeHubDirectConnections.Clear();
            _tileStates.Clear();

            // Populate tile placement states, road/stop connection visuals
            foreach (var t in _placedTiles)
            {
                _tileStates.Add(new TilePlacementState
                {
                    TileId = t.TileId,
                    BuildingId = t.BuildingId,
                    Position = t.Position,
                    Connected = t.Connected,
                    Inactive = t.Inactive,
                    OverlapInvalid = t.OverlapInvalid
                });

                if (t.Inactive) continue;

                if (t.RoadConnections != null)
                    foreach (var rc in t.RoadConnections)
                        _activeRoadConnections.Add(new TileRoadConnection { TileId = t.TileId, SnapPoint = rc.Position, Distance = rc.Distance });

                if (t.StopConnections != null)
                    foreach (var sc in t.StopConnections)
                        _activeStopConnections.Add(new TileStopConnection { TileId = t.TileId, StopPosition = sc.Position, Distance = sc.Distance, StopIndex = sc.StopIndex });
            }

            // Pre-compute Dijkstra from every graph node (graph is small: 4-6 nodes)
            bool hasTransit = _transitGraph.Nodes.Count > 0 && _transitGraph.Edges.Count > 0;
            var dijkstraCache = new Dictionary<int, Dictionary<int, float>>();
            if (hasTransit)
            {
                for (int i = 0; i < _transitGraph.Nodes.Count; i++)
                {
                    int nodeId = _transitGraph.Nodes[i].Id;
                    dijkstraCache[nodeId] = _transitGraph.Dijkstra(nodeId);
                }
            }

            // Map each hub to its nearest graph node
            int[] hubNodeIds = new int[hubCount];
            if (hasTransit)
            {
                for (int i = 0; i < hubCount; i++)
                    hubNodeIds[i] = _transitGraph.NearestNodeId(hubPositions[i]);
            }

            var stops = _transitGraph.Stops;
            float wNorm = _equalDistrictWeight / 100000f;

            // Auto-detect coordinate scale: HTML reference uses ~180px edges.
            // Unity content-local space may be much larger, so scale distance params to match.
            float coordScale = 1f;
            if (hasTransit && _transitGraph.Edges.Count > 0)
            {
                float totalEdgeLen = 0f;
                foreach (var e in _transitGraph.Edges) totalEdgeLen += e.Length;
                float avgEdge = totalEdgeLen / _transitGraph.Edges.Count;
                coordScale = Mathf.Max(1f, avgEdge / 180f);
            }
            float adjustedDistScale = _distanceScale * coordScale;
            float floorM = Mathf.Max(0.01f, _distanceFloor / _distanceScale); // stays in metres regardless of scale

            // --- Curve-based road-network scoring (matches HTML recalc) ---
            foreach (var t in _placedTiles)
            {
                if (t.Inactive || t.OverlapInvalid) continue;
                var b = GetBuilding(t.BuildingId);
                if (b == null || b.BaseValues == null) continue;

                float sizeBoost = GetSizeBoost(b);
                float connDistMult = GetConnectionDistanceMult(b);
                float maxRoadDist = _maxRoadDistance * connDistMult * coordScale;
                float connRange = GetConnectionRange(t.BuildingId);

                // Use the tile's existing stop connections as seed stops for network scoring.
                // These were already filtered by the halo-based connection range.
                List<int> seedStopIndices = null;
                if (hasTransit && t.StopConnections != null && t.StopConnections.Count > 0)
                {
                    seedStopIndices = new List<int>(t.StopConnections.Count);
                    foreach (var sc in t.StopConnections)
                        seedStopIndices.Add(sc.StopIndex);
                }

                for (int hi = 0; hi < hubCount; hi++)
                {
                    float directDist = Vector2.Distance(t.Position, hubPositions[hi]);
                    float pathUnits = float.MaxValue;
                    int transportEdgeHops = 0;

                    // 1. Direct path: building halo overlaps hub
                    if (directDist <= connRange)
                    {
                        pathUnits = directDist;
                        transportEdgeHops = 0;
                    }

                    // 2. Network path via stops: try even if direct path found (take shorter)
                    if (hasTransit && seedStopIndices != null && seedStopIndices.Count > 0)
                    {
                        // 2. Network path: walk to seed stop → graph edge nodes → Dijkstra → hub
                        int hubNodeId = hubNodeIds[hi];
                        float bestNetPath = float.MaxValue;

                        foreach (int si in seedStopIndices)
                        {
                            var stop = stops[si];
                            float walkToStop = Vector2.Distance(t.Position, stop.Position);
                            int fromId = stop.EdgeFromId;
                            int toId = stop.EdgeToId;

                            // Try entering graph via FromId
                            if (dijkstraCache.TryGetValue(fromId, out var distFromNode))
                            {
                                float stopToFrom = Vector2.Distance(stop.Position, _transitGraph.GetNode(fromId).Position);
                                if (distFromNode.TryGetValue(hubNodeId, out float gd) && gd < float.MaxValue)
                                {
                                    float total = walkToStop + stopToFrom + gd;
                                    if (total < bestNetPath) bestNetPath = total;
                                }
                            }

                            // Try entering graph via ToId
                            if (dijkstraCache.TryGetValue(toId, out var distToNode))
                            {
                                float stopToTo = Vector2.Distance(stop.Position, _transitGraph.GetNode(toId).Position);
                                if (distToNode.TryGetValue(hubNodeId, out float gd) && gd < float.MaxValue)
                                {
                                    float total = walkToStop + stopToTo + gd;
                                    if (total < bestNetPath) bestNetPath = total;
                                }
                            }
                        }

                        if (bestNetPath <= maxRoadDist + 2f * coordScale && bestNetPath < pathUnits)
                        {
                            pathUnits = bestNetPath;
                            transportEdgeHops = 1; // network path = 1+ hops
                        }
                    }

                    if (pathUnits >= float.MaxValue) continue;

                    _activeConnections.Add(new TileHubConnection { TileId = t.TileId, HubIndex = hi });

                    if (transportEdgeHops == 0)
                    {
                        _activeHubDirectConnections.Add(new TileHubDirectConnection
                        {
                            TileId = t.TileId,
                            HubIndex = hi,
                            HubPosition = hubPositions[hi],
                            Distance = directDist
                        });
                    }

                    // Apply HTML curve: (ref / (ref + pathMeters))^exp
                    float pathMeters = pathUnits / adjustedDistScale;
                    float effM = Mathf.Max(floorM, pathMeters);
                    float curve = Mathf.Pow(_influenceReferenceMeters / (_influenceReferenceMeters + effM), _distanceExponent);
                    float nearCap = GetNearCap(b, transportEdgeHops);

                    float factor = _norm * (1f / _influenceRefBase) * sizeBoost * curve * nearCap * wNorm;

                    var v = b.BaseValues;
                    if (v.environment != 0f) hubEnv[hi] += v.environment * factor;
                    if (v.economy != 0f) hubEco[hi] += v.economy * factor;
                    if (v.healthSafety != 0f) hubSaf[hi] += v.healthSafety * factor;
                    if (v.cultureEdu != 0f) hubCul[hi] += v.cultureEdu * factor;
                }
            }

            // --- Per-hub: normalize raw → 0-100, compute per-hub QOL ---
            float wSum = Mathf.Max(0.001f, _qolWeightEnv + _qolWeightEco + _qolWeightSaf + _qolWeightCul);
            float[] hubQols = new float[hubCount];

            _hubMetrics.Clear();
            float sumEnv = 0, sumEco = 0, sumSaf = 0, sumCul = 0;

            for (int i = 0; i < hubCount; i++)
            {
                float pctEnv = Mathf.Clamp(hubEnv[i] / _norm * 100f, 0f, 100f);
                float pctEco = Mathf.Clamp(hubEco[i] / _norm * 100f, 0f, 100f);
                float pctSaf = Mathf.Clamp(hubSaf[i] / _norm * 100f, 0f, 100f);
                float pctCul = Mathf.Clamp(hubCul[i] / _norm * 100f, 0f, 100f);

                hubQols[i] = (pctEnv * _qolWeightEnv + pctEco * _qolWeightEco +
                              pctSaf * _qolWeightSaf + pctCul * _qolWeightCul) / wSum;

                sumEnv += pctEnv;
                sumEco += pctEco;
                sumSaf += pctSaf;
                sumCul += pctCul;

                _hubMetrics.Add(new HubMetricSnapshot
                {
                    HubIndex = i,
                    Environment = pctEnv,
                    Economy = pctEco,
                    HealthSafety = pctSaf,
                    CultureEdu = pctCul,
                    Qol = hubQols[i]
                });
            }

            // --- City-level: average pillars across hubs ---
            float n = hubCount;
            _environment = Mathf.Round(sumEnv / n);
            _economy = Mathf.Round(sumEco / n);
            _healthSafety = Mathf.Round(sumSaf / n);
            _cultureEdu = Mathf.Round(sumCul / n);

            // --- City QOL: mean(hub.qol) − balancePenalty × (max − min), capped ---
            float meanQol = 0f, maxQol = float.MinValue, minQol = float.MaxValue;
            for (int i = 0; i < hubCount; i++)
            {
                meanQol += hubQols[i];
                if (hubQols[i] > maxQol) maxQol = hubQols[i];
                if (hubQols[i] < minQol) minQol = hubQols[i];
            }
            meanQol /= n;
            _qol = Mathf.Clamp(Mathf.Round(meanQol - _qolBalancePenalty * (maxQol - minQol)), 0f, _qolCap);

            if (logMetricsWhenChanged)
            {
                Debug.Log($"[Metrics] QOL={_qol:F0} (cap={_qolCap}) | Env={_environment:F0} Eco={_economy:F0} Safe={_healthSafety:F0} Cul={_cultureEdu:F0} | " +
                          $"tiles={_placedTiles.Count} conns={_activeConnections.Count} stopConns={_activeStopConnections.Count} | " +
                          $"meanHubQol={meanQol:F1} maxHub={maxQol:F1} minHub={minQol:F1} penalty={_qolBalancePenalty * (maxQol - minQol):F1} | " +
                          $"coordScale={coordScale:F2} hubs={hubCount} edges={_transitGraph.Edges.Count} stops={stops.Count}");
            }

            OnMetricsChanged?.Invoke();
        }

        private float GetSizeBoost(BuildingDefinition b)
        {
            switch (b.ImpactSize)
            {
                case "Small":  return _sizeBoostSmall;
                case "Medium": return _sizeBoostMedium;
                case "Large":  return _sizeBoostLarge;
                default:       return 1f;
            }
        }

        private float GetConnectionDistanceMult(BuildingDefinition b)
        {
            switch (b.ImpactSize)
            {
                case "Small":  return 1f;
                case "Medium": return 1.5f;
                case "Large":  return 2.5f;
                default:       return 1f;
            }
        }

        private float GetImpactRadius(BuildingDefinition b)
        {
            switch (b.ImpactSize)
            {
                case "Small":  return _impactRadiusSmall;
                case "Medium": return _impactRadiusMedium;
                case "Large":  return _impactRadiusLarge;
                default:       return _impactRadiusSmall;
            }
        }

        private float GetNearCap(BuildingDefinition b, int transportEdgeHops)
        {
            if (transportEdgeHops >= 1) return 1f;
            switch (b.ImpactSize)
            {
                case "Small":  return _nearCapSmall;
                case "Medium": return _nearCapMedium;
                case "Large":  return _nearCapLarge;
                default:       return 1f;
            }
        }

        private BuildingDefinition GetBuilding(string buildingId)
        {
            foreach (var b in _buildingCatalog)
                if (b.Id == buildingId) return b;
            return null;
        }

        private bool IsOnObstacle(Vector2 position)
        {
            foreach (var (center, radius) in _obstacles)
                if (Vector2.Distance(position, center) <= radius) return true;
            return false;
        }

        [Tooltip("Extra range added to every building's halo radius for connection checks.")]
        [SerializeField] private float _connectionRangeOffset = 0f;

        public float ConnectionRangeOffset { get => _connectionRangeOffset; set => _connectionRangeOffset = value; }

        private float GetConnectionRange(string buildingId)
        {
            if (HaloRadiusResolver != null)
            {
                float r = HaloRadiusResolver(buildingId);
                if (r > 0f) return r + _connectionRangeOffset;
            }
            return _roadConnectRange + _connectionRangeOffset;
        }

        private bool IsNearAnyHub(Vector2 position, float range)
        {
            if (_scoringHubs != null)
            {
                for (int i = 0; i < _scoringHubs.Count; i++)
                    if (Vector2.Distance(position, _scoringHubs[i].position) <= range) return true;
            }
            else
            {
                var nodes = _transitGraph.Nodes;
                for (int i = 0; i < nodes.Count; i++)
                    if (Vector2.Distance(position, nodes[i].Position) <= range) return true;
            }
            return false;
        }

        private struct PlacedTile
        {
            public string TileId;
            public string BuildingId;
            public Vector2 Position;
            public float Rotation;
            public bool Inactive;
            public bool OverlapInvalid;
            public bool Connected;
            public List<TransitGraph.ConnectionPoint> RoadConnections;
            public List<TransitGraph.StopConnectionPoint> StopConnections;
        }
    }
}
