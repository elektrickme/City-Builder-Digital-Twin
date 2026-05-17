using System.Collections.Generic;
using System.IO;
using UnityEngine;
using CityTwin.Config;
using CityTwin.Core;

namespace CityTwin.Simulation.AutoSim
{
    /// <summary>
    /// Headless wrapper around a single SimulationEngine instance. Owns its own engine + transit graph
    /// and reproduces the GameInstanceCoordinator boot path: load config, build catalog, build graph,
    /// generate stops, then loop strategy → AddTile → Recalc.
    /// One harness per concurrent session; cheap to reuse across runs via Reset().
    /// </summary>
    public class SimulationHarness
    {
        public SimulationEngine Engine { get; private set; }
        public GameConfig Config { get; private set; }
        public int RemainingBudget { get; private set; }
        public int PlacedCount { get; private set; }
        public List<string> PlacedBuildingIds { get; private set; } = new List<string>();

        private GameObject _hostGo;
        private readonly List<(Vector2 a, Vector2 b)> _roadSegments = new List<(Vector2, Vector2)>();
        private Vector2 _fieldHalfExtent;
        private int _seed;
        private System.Random _rng;
        private readonly List<Vector2> _placedPositions = new List<Vector2>();

        public SimulationHarness Build(GameConfig config, int seed)
        {
            Config = config ?? throw new System.ArgumentNullException(nameof(config));
            _seed = seed;
            _rng = new System.Random(seed);

            _hostGo = new GameObject("AutoSim_Harness");
            _hostGo.hideFlags = HideFlags.HideAndDontSave;
            Engine = _hostGo.AddComponent<SimulationEngine>();
            Engine.SetBuildingCatalog(new List<BuildingDefinition>(config.Buildings ?? System.Array.Empty<BuildingDefinition>()));
            Engine.SetConfig(config.Scoring, config.Accessibility);

            var graph = new TransitGraph();
            _roadSegments.Clear();
            float maxAbs = 0f;
            if (config.Map?.nodes != null)
            {
                for (int i = 0; i < config.Map.nodes.Length; i++)
                {
                    var n = config.Map.nodes[i];
                    graph.AddNode(new Vector2(n.x, n.y), n.population);
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(n.x), Mathf.Abs(n.y));
                }
            }
            if (config.Map?.edges != null && config.Map?.nodes != null)
            {
                for (int i = 0; i < config.Map.edges.Length; i++)
                {
                    var e = config.Map.edges[i];
                    graph.AddEdge(e.from, e.to, e.length);
                    graph.AddEdge(e.to, e.from, e.length);
                    if (e.from >= 0 && e.from < config.Map.nodes.Length && e.to >= 0 && e.to < config.Map.nodes.Length)
                    {
                        var a = config.Map.nodes[e.from];
                        var b = config.Map.nodes[e.to];
                        _roadSegments.Add((new Vector2(a.x, a.y), new Vector2(b.x, b.y)));
                    }
                }
            }
            // Generous play area: 1.4x the convex bound of hubs.
            float ext = Mathf.Max(120f, maxAbs * 1.4f);
            _fieldHalfExtent = new Vector2(ext, ext);

            var stops = config.Stops ?? new GameConfig.StopsData();
            graph.GenerateStops(stops.spacing, stops.minDistanceFromNode, stops.minDistanceBetweenStops,
                                stops.spacingJitter, stops.removalRate, stops.seed);
            Engine.SetTransitGraph(graph);

            // Obstacles, if any
            if (config.Map?.obstacles != null && config.Map.obstacles.Length > 0)
            {
                var list = new List<(Vector2, float)>(config.Map.obstacles.Length);
                for (int i = 0; i < config.Map.obstacles.Length; i++)
                {
                    var o = config.Map.obstacles[i];
                    list.Add((new Vector2(o.x, o.y), o.radius));
                }
                Engine.SetObstacles(list);
            }

            RemainingBudget = config.Budget?.startingBudget ?? 1000;
            PlacedCount = 0;
            return this;
        }

        /// <summary>Wipe all placed tiles and reset budget for the next session, reusing the same engine + graph.</summary>
        public void Reset(int newSeed)
        {
            _seed = newSeed;
            _rng = new System.Random(newSeed);
            Engine.ClearAllTiles();
            RemainingBudget = Config.Budget?.startingBudget ?? 1000;
            PlacedCount = 0;
            PlacedBuildingIds.Clear();
            _placedPositions.Clear();
        }

        public void Dispose()
        {
            if (_hostGo != null)
            {
                if (Application.isPlaying) Object.Destroy(_hostGo);
                else Object.DestroyImmediate(_hostGo);
                _hostGo = null;
            }
        }

        public SimulationSnapshot BuildSnapshot(float elapsedSeconds)
        {
            return new SimulationSnapshot
            {
                RemainingBudget = RemainingBudget,
                ElapsedSeconds = elapsedSeconds,
                Qol = Engine.Qol,
                Environment = Engine.Environment,
                Economy = Engine.Economy,
                HealthSafety = Engine.HealthSafety,
                CultureEdu = Engine.CultureEdu,
                Catalog = Config.Buildings ?? System.Array.Empty<BuildingDefinition>(),
                HubPositions = Engine.HubPositions,
                PlacedPositions = _placedPositions,
                FieldHalfExtent = _fieldHalfExtent,
                Roads = _roadSegments,
                ImpactRadius = bid => Engine.TryGetImpactSearchRadius(bid, out var r) ? r : 75f
            };
        }

        /// <summary>Minimum distance between two placed building centers. Mirrors PlacementOverlapValidator's
        /// fallback. Sim doesn't see visual halos, so this is a flat approximation.</summary>
        public float MinPlacementSeparation = 30f;

        /// <summary>Attempt placement; deducts budget if accepted. Returns engine tile id or null if rejected
        /// (unaffordable, overlaps another placement, or too close to a hub).</summary>
        public string TryPlace(string buildingId, Vector2 pos)
        {
            var def = FindBuilding(buildingId);
            if (def == null) return null;
            if (def.Price > RemainingBudget) return null;

            // Reject stacks
            for (int i = 0; i < _placedPositions.Count; i++)
                if (Vector2.Distance(_placedPositions[i], pos) < MinPlacementSeparation) return null;
            // Reject placements that overlap a hub footprint
            var hubs = Engine.HubPositions;
            for (int i = 0; i < hubs.Count; i++)
                if (Vector2.Distance(hubs[i], pos) < MinPlacementSeparation) return null;

            var pose = new TilePose(pos, 0f, buildingId, sourceId: -1, tileId: $"auto_{PlacedCount}");
            string id = Engine.AddTile(pose);
            if (id != null)
            {
                RemainingBudget -= def.Price;
                PlacedCount++;
                PlacedBuildingIds.Add(buildingId);
                _placedPositions.Add(pos);
            }
            return id;
        }

        /// <summary>Place, read QOL, remove. Used by GreedyStrategy to score candidates without mutating the run.</summary>
        public float ProbeQolDelta(string buildingId, Vector2 pos)
        {
            var def = FindBuilding(buildingId);
            if (def == null || def.Price > RemainingBudget) return Engine.Qol;
            var pose = new TilePose(pos, 0f, buildingId, sourceId: -2, tileId: $"probe_{PlacedCount}_{_rng.Next()}");
            string id = Engine.AddTile(pose);
            float q = Engine.Qol;
            if (id != null) Engine.RemoveTile(id);
            return q;
        }

        public BuildingDefinition FindBuilding(string id)
        {
            if (Config.Buildings == null) return null;
            foreach (var b in Config.Buildings) if (b.Id == id) return b;
            return null;
        }

        public System.Random Rng => _rng;
        public IReadOnlyList<(Vector2 a, Vector2 b)> Roads => _roadSegments;
        public Vector2 FieldHalfExtent => _fieldHalfExtent;

        /// <summary>Load game_config.json from StreamingAssets without booting the runtime loader MonoBehaviour.</summary>
        public static GameConfig LoadConfigFromStreamingAssets(string relativePath = "game_config.json")
        {
            string path = Path.Combine(Application.streamingAssetsPath, relativePath);
            if (!File.Exists(path)) return null;
            var loader = new GameObject("AutoSim_ConfigLoader_TMP").AddComponent<GameConfigLoader>();
            try
            {
                if (loader.TryParse(File.ReadAllText(path), out var parsed)) return parsed;
                return null;
            }
            finally
            {
                if (Application.isPlaying) Object.Destroy(loader.gameObject);
                else Object.DestroyImmediate(loader.gameObject);
            }
        }
    }
}
