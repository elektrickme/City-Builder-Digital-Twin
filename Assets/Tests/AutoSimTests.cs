#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using CityTwin.Config;
using CityTwin.Core;
using CityTwin.Simulation;
using CityTwin.Simulation.AutoSim;

namespace CityTwin.Tests
{
    /// <summary>End-to-end smoke tests for the auto-sim layer added for the expo build.
    /// Uses a tiny synthetic config so each test runs in well under a second.</summary>
    public class AutoSimTests
    {
        private static GameConfig MakeMiniConfig()
        {
            var c = new GameConfig
            {
                Meta = new GameConfig.MetaData(),
                Session = new GameConfig.SessionData { gameplaySeconds = 60, maxPlayers = 1 },
                Budget = new GameConfig.BudgetData { mode = "PerQuadrant", startingBudget = 1000 },
                Scoring = new GameConfig.ScoringData(),
                Accessibility = new GameConfig.AccessibilityData
                {
                    roadConnectRange = 500f, zoneRadius = 400f, defaultConnectionRadius = 600f
                },
                Stops = new GameConfig.StopsData { spacing = 60f, minDistanceFromNode = 30f, minDistanceBetweenStops = 30f, spacingJitter = 0.25f, removalRate = 0.30f, seed = 42 },
                Buildings = new[]
                {
                    new BuildingDefinition { Id = "garden",  ImpactSize = "Small",  Price = 100, ConnectionRadius = 200f, BaseValues = new BuildingDefinition.MetricValues { environment = 5 } },
                    new BuildingDefinition { Id = "office",  ImpactSize = "Small",  Price = 100, ConnectionRadius = 200f, BaseValues = new BuildingDefinition.MetricValues { economy = 5 } },
                    new BuildingDefinition { Id = "school",  ImpactSize = "Medium", Price = 200, ConnectionRadius = 250f, BaseValues = new BuildingDefinition.MetricValues { cultureEdu = 6 } },
                    new BuildingDefinition { Id = "hospital",ImpactSize = "Large",  Price = 300, ConnectionRadius = 300f, BaseValues = new BuildingDefinition.MetricValues { healthSafety = 8 } }
                },
                Map = new GameConfig.MapData
                {
                    nodes = new[]
                    {
                        new GameConfig.MapNodeData { x = -90, y = -90, population = 50000 },
                        new GameConfig.MapNodeData { x =  90, y = -90, population = 50000 },
                        new GameConfig.MapNodeData { x =  90, y =  90, population = 50000 },
                        new GameConfig.MapNodeData { x = -90, y =  90, population = 50000 }
                    },
                    edges = new[]
                    {
                        new GameConfig.MapEdgeData { from = 0, to = 1, length = 180 },
                        new GameConfig.MapEdgeData { from = 1, to = 2, length = 180 },
                        new GameConfig.MapEdgeData { from = 2, to = 3, length = 180 },
                        new GameConfig.MapEdgeData { from = 3, to = 0, length = 180 }
                    },
                    obstacles = new GameConfig.MapObstacleData[0]
                }
            };
            return c;
        }

        [Test]
        public void TransitGraph_StopRemovalRate_ShrinksOutputDeterministically()
        {
            var g0 = new TransitGraph();
            var g1 = new TransitGraph();
            foreach (var g in new[] { g0, g1 })
            {
                g.AddNode(new Vector2(-90f, -90f));
                g.AddNode(new Vector2( 90f, -90f));
                g.AddNode(new Vector2( 90f,  90f));
                g.AddNode(new Vector2(-90f,  90f));
                g.AddEdge(0, 1, 180); g.AddEdge(1, 0, 180);
                g.AddEdge(1, 2, 180); g.AddEdge(2, 1, 180);
                g.AddEdge(2, 3, 180); g.AddEdge(3, 2, 180);
                g.AddEdge(3, 0, 180); g.AddEdge(0, 3, 180);
            }
            g0.GenerateStops(spacing: 30f, minDistFromNode: 20f, minDistBetweenStops: 20f, spacingJitter: 0f, removalRate: 0f, seed: 99);
            g1.GenerateStops(spacing: 30f, minDistFromNode: 20f, minDistBetweenStops: 20f, spacingJitter: 0f, removalRate: 0.5f, seed: 99);
            Assert.That(g1.Stops.Count, Is.LessThan(g0.Stops.Count), "Removal rate must drop some stops.");

            // Re-run with same seed: result should be identical (deterministic)
            var g2 = new TransitGraph();
            g2.AddNode(new Vector2(-90f, -90f)); g2.AddNode(new Vector2(90f, -90f));
            g2.AddNode(new Vector2(90f, 90f));  g2.AddNode(new Vector2(-90f, 90f));
            g2.AddEdge(0, 1, 180); g2.AddEdge(1, 0, 180);
            g2.AddEdge(1, 2, 180); g2.AddEdge(2, 1, 180);
            g2.AddEdge(2, 3, 180); g2.AddEdge(3, 2, 180);
            g2.AddEdge(3, 0, 180); g2.AddEdge(0, 3, 180);
            g2.GenerateStops(30f, 20f, 20f, 0f, 0.5f, 99);
            Assert.That(g2.Stops.Count, Is.EqualTo(g1.Stops.Count), "Seed reuse must be deterministic.");
        }

        [Test]
        public void SimulationHarness_Build_LoadsCatalogGraphAndBudget()
        {
            var cfg = MakeMiniConfig();
            var h = new SimulationHarness();
            h.Build(cfg, seed: 1);
            try
            {
                Assert.That(h.Engine, Is.Not.Null);
                Assert.That(h.RemainingBudget, Is.EqualTo(1000));
                Assert.That(h.Engine.TransitGraph.Nodes.Count, Is.EqualTo(4));
                Assert.That(h.Engine.HubPositions.Count, Is.EqualTo(4));
                Assert.That(h.Roads.Count, Is.GreaterThan(0));
            }
            finally { h.Dispose(); }
        }

        [Test]
        public void SimulationHarness_TryPlace_DeductsBudgetAndRejectsStacks()
        {
            var cfg = MakeMiniConfig();
            var h = new SimulationHarness();
            h.Build(cfg, seed: 1);
            try
            {
                string a = h.TryPlace("garden", new Vector2(0f, -60f));
                Assert.That(a, Is.Not.Null);
                Assert.That(h.RemainingBudget, Is.EqualTo(900));

                // Same exact position should be rejected (overlap).
                string b = h.TryPlace("garden", new Vector2(0f, -60f));
                Assert.That(b, Is.Null);
                Assert.That(h.RemainingBudget, Is.EqualTo(900), "Rejected placement must not deduct budget.");

                // Far enough away → accepted
                string c = h.TryPlace("office", new Vector2(0f, 0f));
                Assert.That(c, Is.Not.Null);
                Assert.That(h.RemainingBudget, Is.EqualTo(800));
            }
            finally { h.Dispose(); }
        }

        [Test]
        public void BatchRunner_RunsTinyBatch_ProducesPopulatedResult()
        {
            var cfg = MakeMiniConfig();
            var settings = BatchRunner.Settings.Default;
            settings.Runs = 5;
            settings.SessionSeconds = 60f;
            settings.SimSecondsPerPlacement = 8f;
            settings.BaseSeed = 7;

            var result = BatchRunner.Run(cfg, _ => new RandomKidStrategy(), settings);

            Assert.That(result.Runs, Is.EqualTo(5));
            Assert.That(result.Games, Is.Not.Null);
            Assert.That(result.Games.Count, Is.EqualTo(5));
            foreach (var g in result.Games)
                Assert.That(g.FinalQol, Is.InRange(0f, 100f));
            Assert.That(result.AggregateBuildingUsage.Count, Is.GreaterThan(0));
        }

        [Test]
        public void GoalFunction_PenalizesOffCenterMean()
        {
            var centered = new BatchRunner.BatchResult
            {
                Strategy = "x", Runs = 100, MeanQol = 65f, StdQol = 10f,
                HitRateInBand = 0.8f, PillarBalanceIndex = 0.7f,
                AggregateBuildingUsage = new Dictionary<string, int> { { "a", 50 }, { "b", 50 } },
                Games = new List<BatchRunner.GameResult>()
            };
            var offCenter = centered;
            offCenter.MeanQol = 20f; offCenter.HitRateInBand = 0.1f;
            float sa = GoalFunction.Score(centered, GoalFunction.Weights.Default);
            float sb = GoalFunction.Score(offCenter, GoalFunction.Weights.Default);
            Assert.That(sa, Is.GreaterThan(sb), "Centered band-hitting config must outscore off-center.");
        }

        [Test]
        public void ConfigOptimizer_StepOnce_ProducesABestKnobsAndNeverNullsConfig()
        {
            var cfg = MakeMiniConfig();
            var opt = new ConfigOptimizer(cfg, maxIterations: 3, probeRuns: 3, baseSeed: 11);
            for (int i = 0; i < 3; i++) opt.StepOnce();
            Assert.That(opt.IsDone, Is.True);
            Assert.That(opt.BestConfig, Is.Not.Null);
            Assert.That(opt.BestConfig.Scoring, Is.Not.Null);
            Assert.That(opt.BestScore, Is.GreaterThanOrEqualTo(float.MinValue + 1f));
            Assert.That(opt.RecentScores.Count, Is.GreaterThan(0));
        }

        [Test]
        public void ConfigOptimizer_PatchJsonForBalance_RewritesScoringField()
        {
            string json = "{\n  \"scoring\": {\n    \"norm\": 150,\n    \"qolCap\": 80\n  },\n  \"budget\": { \"startingBudget\": 1000 }\n}";
            var k = new ConfigOptimizer.Knobs
            {
                Norm = 200f, QolCap = 70f, StartingBudget = 1200,
                InfluenceRefBase = 10f, DistanceExponent = 1f, QolBalancePenalty = 0.5f,
                SizeBoostSmall = 1.2f, SizeBoostMedium = 1.1f, SizeBoostLarge = 1f,
                NearCapSmall = 0.5f, NearCapMedium = 0.75f, NearCapLarge = 1f,
                QolWeightEnv = 1f, QolWeightEco = 1f, QolWeightSaf = 1f, QolWeightCul = 1f
            };
            string patched = ConfigOptimizer.PatchJsonForBalance(json, k);
            StringAssert.Contains("\"norm\": 200", patched);
            StringAssert.Contains("\"qolCap\": 70", patched);
            StringAssert.Contains("\"startingBudget\": 1200", patched);
        }
    }
}
#endif
