using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.Simulation.AutoSim
{
    /// <summary>Random-pick strategy with a road-bias toggle. Models an expo child: grab any tile, drop near a road most of the time.</summary>
    public sealed class RandomKidStrategy : IPlacementStrategy
    {
        public string Name => "RandomKid";

        /// <summary>Probability the chosen position snaps onto a road segment instead of random open ground.</summary>
        public float RoadBias = 0.6f;

        public bool ChooseNextPlacement(SimulationSnapshot snap, System.Random rng, out string buildingId, out Vector2 position)
        {
            buildingId = null;
            position = Vector2.zero;

            if (snap.Catalog == null || snap.Catalog.Count == 0) return false;

            // Pick a random building the player can still afford.
            var affordable = new List<BuildingDefinition>();
            foreach (var b in snap.Catalog) if (b.Price <= snap.RemainingBudget) affordable.Add(b);
            if (affordable.Count == 0) return false;
            buildingId = affordable[rng.Next(affordable.Count)].Id;

            // Pick a position.
            bool onRoad = rng.NextDouble() < RoadBias && snap.Roads != null && snap.Roads.Count > 0;
            if (onRoad)
            {
                var seg = snap.Roads[rng.Next(snap.Roads.Count)];
                float t = (float)rng.NextDouble();
                position = Vector2.Lerp(seg.a, seg.b, t) + RandomOffset(rng, 12f);
            }
            else
            {
                position = new Vector2(
                    RandomInRange(rng, -snap.FieldHalfExtent.x, snap.FieldHalfExtent.x),
                    RandomInRange(rng, -snap.FieldHalfExtent.y, snap.FieldHalfExtent.y));
            }
            return true;
        }

        private static float RandomInRange(System.Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);
        private static Vector2 RandomOffset(System.Random rng, float r) =>
            new Vector2(RandomInRange(rng, -r, r), RandomInRange(rng, -r, r));
    }

    /// <summary>Models a thinking player, not a raw-max bot. Scores each candidate by
    /// cost-efficiency (QOL gain per √price) with a bias toward whatever pillar the city is
    /// currently weakest in, so it spreads budget across many cheaper tiles and patches gaps
    /// instead of blowing the budget on two mega-tiles. Small score jitter keeps games varied.
    /// Uses clone-and-revert probing on the host engine.</summary>
    public sealed class GreedyStrategy : IPlacementStrategy
    {
        public string Name => "Greedy";

        public int CandidatesPerPick = 6;
        /// <summary>How hard to favour cheaper tiles. score = delta / (price/100)^thrift. 0 = ignore price (old behaviour), ~0.5 = balanced.</summary>
        public float ThriftExponent = 0.5f;
        /// <summary>Multiplier applied when a building's dominant pillar is the city's weakest pillar.</summary>
        public float WeakPillarBoost = 1.4f;

        private readonly SimulationHarness _host;

        public GreedyStrategy(SimulationHarness host) { _host = host; }

        public bool ChooseNextPlacement(SimulationSnapshot snap, System.Random rng, out string buildingId, out Vector2 position)
        {
            buildingId = null;
            position = Vector2.zero;
            if (snap.Catalog == null || snap.Catalog.Count == 0) return false;

            float baseline = snap.Qol;
            int weakest = WeakestPillar(snap); // 0=env 1=eco 2=saf 3=cul
            float bestScore = float.MinValue;
            string bestId = null;
            Vector2 bestPos = Vector2.zero;

            foreach (var b in snap.Catalog)
            {
                if (b.Price > snap.RemainingBudget) continue;

                float priceFactor = Mathf.Pow(Mathf.Max(0.01f, b.Price / 100f), ThriftExponent);
                float pillarBoost = (b.BaseValues != null && DominantPillar(b.BaseValues) == weakest) ? WeakPillarBoost : 1f;

                for (int k = 0; k < CandidatesPerPick; k++)
                {
                    Vector2 candidate;
                    if (snap.Roads != null && snap.Roads.Count > 0 && rng.NextDouble() < 0.75)
                    {
                        var seg = snap.Roads[rng.Next(snap.Roads.Count)];
                        float t = (float)rng.NextDouble();
                        candidate = Vector2.Lerp(seg.a, seg.b, t);
                    }
                    else
                    {
                        candidate = new Vector2(
                            (float)rng.NextDouble() * snap.FieldHalfExtent.x * 2f - snap.FieldHalfExtent.x,
                            (float)rng.NextDouble() * snap.FieldHalfExtent.y * 2f - snap.FieldHalfExtent.y);
                    }

                    float delta = _host.ProbeQolDelta(b.Id, candidate) - baseline;
                    if (delta <= 0f) continue; // skip placements that don't help

                    float jitter = 1f + ((float)rng.NextDouble() * 0.06f - 0.03f);
                    float score = (delta / priceFactor) * pillarBoost * jitter;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestId = b.Id;
                        bestPos = candidate;
                    }
                }
            }

            if (bestId == null) return false;
            buildingId = bestId;
            position = bestPos;
            return true;
        }

        private static int WeakestPillar(SimulationSnapshot s)
        {
            float env = s.Environment, eco = s.Economy, saf = s.HealthSafety, cul = s.CultureEdu;
            int idx = 0; float min = env;
            if (eco < min) { min = eco; idx = 1; }
            if (saf < min) { min = saf; idx = 2; }
            if (cul < min) { min = cul; idx = 3; }
            return idx;
        }

        private static int DominantPillar(BuildingDefinition.MetricValues v)
        {
            float env = v.environment, eco = v.economy, saf = v.healthSafety, cul = v.cultureEdu;
            int idx = 0; float max = env;
            if (eco > max) { max = eco; idx = 1; }
            if (saf > max) { max = saf; idx = 2; }
            if (cul > max) { max = cul; idx = 3; }
            return idx;
        }
    }

    /// <summary>Replay a recorded list of placements for deterministic regression tests.</summary>
    public sealed class ScriptedStrategy : IPlacementStrategy
    {
        public string Name => "Scripted";
        private readonly List<(string buildingId, Vector2 pos)> _script;
        private int _cursor;

        public ScriptedStrategy(IEnumerable<(string buildingId, Vector2 pos)> script)
        {
            _script = new List<(string, Vector2)>(script);
        }

        public bool ChooseNextPlacement(SimulationSnapshot snap, System.Random rng, out string buildingId, out Vector2 position)
        {
            if (_cursor >= _script.Count)
            {
                buildingId = null; position = Vector2.zero; return false;
            }
            var step = _script[_cursor++];
            buildingId = step.buildingId; position = step.pos;
            return true;
        }
    }
}
