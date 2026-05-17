using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using CityTwin.Config;

namespace CityTwin.Simulation.AutoSim
{
    /// <summary>
    /// Plays N synthetic sessions against a single (config, strategy) pair and emits a result-set the
    /// editor window and ConfigOptimizer both consume. Pure-data — no Unity UI deps.
    /// </summary>
    public static class BatchRunner
    {
        public struct GameResult
        {
            public int Seed;
            public string Strategy;
            public float FinalQol;
            public float Environment;
            public float Economy;
            public float HealthSafety;
            public float CultureEdu;
            public float HubSpread;
            public int PlacedCount;
            public int BudgetUsed;
            public Dictionary<string, int> BuildingUsage; // building id -> placements
        }

        public struct BatchResult
        {
            public string Strategy;
            public int Runs;
            public float MeanQol;
            public float StdQol;
            public float HitRateInBand;       // P(50 <= qol <= 80)
            public float PillarBalanceIndex;  // 1 - stddev(pillars)/mean
            public Dictionary<string, int> AggregateBuildingUsage;
            public List<GameResult> Games;
        }

        public struct Settings
        {
            public int Runs;                  // sessions per strategy
            public float SessionSeconds;      // 270 = match config; less = quick optimizer probe
            public float SimSecondsPerPlacement; // virtual seconds consumed by one placement (ex. 8)
            public int BaseSeed;
            public float BandMin;
            public float BandMax;
            public bool Verbose;
            public static Settings Default => new Settings
            {
                Runs = 1000,
                SessionSeconds = 270f,
                SimSecondsPerPlacement = 8f,
                BaseSeed = 1234,
                BandMin = 50f,
                BandMax = 80f,
                Verbose = false
            };
        }

        public delegate IPlacementStrategy StrategyFactory(SimulationHarness harness);

        public static BatchResult Run(GameConfig config, StrategyFactory factory, Settings settings)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var harness = new SimulationHarness();
            harness.Build(config, settings.BaseSeed);
            var strategy = factory(harness);

            var games = new List<GameResult>(settings.Runs);
            var aggUsage = new Dictionary<string, int>();
            int placementsPerSession = Mathf.Max(1, Mathf.FloorToInt(settings.SessionSeconds / Mathf.Max(0.1f, settings.SimSecondsPerPlacement)));

            for (int i = 0; i < settings.Runs; i++)
            {
                int seed = settings.BaseSeed + i;
                harness.Reset(seed);
                var rng = harness.Rng;
                float elapsed = 0f;
                int hardCap = placementsPerSession + 50;
                for (int step = 0; step < hardCap; step++)
                {
                    if (elapsed >= settings.SessionSeconds) break;
                    var snap = harness.BuildSnapshot(elapsed);
                    if (!strategy.ChooseNextPlacement(snap, rng, out var bid, out var pos)) break;
                    if (harness.TryPlace(bid, pos) == null && harness.RemainingBudget < MinPrice(config)) break;
                    elapsed += settings.SimSecondsPerPlacement;
                }
                games.Add(BuildResult(seed, strategy.Name, harness, config));
                AccumulateUsage(aggUsage, harness.PlacedBuildingIds);
            }

            harness.Dispose();

            float meanQ = 0f, hits = 0f, sumE = 0f, sumEco = 0f, sumS = 0f, sumC = 0f;
            for (int i = 0; i < games.Count; i++)
            {
                meanQ += games[i].FinalQol;
                sumE += games[i].Environment;
                sumEco += games[i].Economy;
                sumS += games[i].HealthSafety;
                sumC += games[i].CultureEdu;
                if (games[i].FinalQol >= settings.BandMin && games[i].FinalQol <= settings.BandMax) hits++;
            }
            meanQ /= Mathf.Max(1, games.Count);
            float varQ = 0f;
            for (int i = 0; i < games.Count; i++) varQ += (games[i].FinalQol - meanQ) * (games[i].FinalQol - meanQ);
            float stdQ = Mathf.Sqrt(varQ / Mathf.Max(1, games.Count));

            int n = Mathf.Max(1, games.Count);
            float aE = sumE / n, aEco = sumEco / n, aS = sumS / n, aC = sumC / n;
            float pillarMean = (aE + aEco + aS + aC) * 0.25f;
            float pillarVar = ((aE - pillarMean) * (aE - pillarMean) + (aEco - pillarMean) * (aEco - pillarMean) +
                               (aS - pillarMean) * (aS - pillarMean) + (aC - pillarMean) * (aC - pillarMean)) * 0.25f;
            float balance = pillarMean > 0.01f ? Mathf.Clamp01(1f - Mathf.Sqrt(pillarVar) / pillarMean) : 0f;

            return new BatchResult
            {
                Strategy = strategy.Name,
                Runs = games.Count,
                MeanQol = meanQ,
                StdQol = stdQ,
                HitRateInBand = hits / Mathf.Max(1, games.Count),
                PillarBalanceIndex = balance,
                AggregateBuildingUsage = aggUsage,
                Games = games
            };
        }

        private static GameResult BuildResult(int seed, string strategy, SimulationHarness harness, GameConfig cfg)
        {
            var hubMetrics = harness.Engine.HubMetrics;
            float maxQ = float.MinValue, minQ = float.MaxValue;
            if (hubMetrics != null && hubMetrics.Count > 0)
            {
                for (int i = 0; i < hubMetrics.Count; i++)
                {
                    if (hubMetrics[i].Qol > maxQ) maxQ = hubMetrics[i].Qol;
                    if (hubMetrics[i].Qol < minQ) minQ = hubMetrics[i].Qol;
                }
            }
            else { maxQ = minQ = harness.Engine.Qol; }

            var usage = new Dictionary<string, int>();
            for (int i = 0; i < harness.PlacedBuildingIds.Count; i++)
            {
                var b = harness.PlacedBuildingIds[i];
                usage[b] = usage.TryGetValue(b, out var c) ? c + 1 : 1;
            }

            return new GameResult
            {
                Seed = seed,
                Strategy = strategy,
                FinalQol = harness.Engine.Qol,
                Environment = harness.Engine.Environment,
                Economy = harness.Engine.Economy,
                HealthSafety = harness.Engine.HealthSafety,
                CultureEdu = harness.Engine.CultureEdu,
                HubSpread = maxQ - minQ,
                PlacedCount = harness.PlacedCount,
                BudgetUsed = (cfg.Budget?.startingBudget ?? 1000) - harness.RemainingBudget,
                BuildingUsage = usage
            };
        }

        private static void AccumulateUsage(Dictionary<string, int> target, List<string> placed)
        {
            for (int i = 0; i < placed.Count; i++)
            {
                target[placed[i]] = target.TryGetValue(placed[i], out var c) ? c + 1 : 1;
            }
        }

        private static int MinPrice(GameConfig cfg)
        {
            if (cfg.Buildings == null || cfg.Buildings.Length == 0) return 1;
            int min = int.MaxValue;
            foreach (var b in cfg.Buildings) if (b.Price < min) min = b.Price;
            return min;
        }

        /// <summary>Write per-game CSV next to the project so the user can inspect outside Unity.</summary>
        public static string WriteCsv(BatchResult result, string folder)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"autosim_{result.Strategy}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new StringBuilder();
            // Collect every building id encountered across runs to make a stable column order
            var allBuildings = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var g in result.Games)
                foreach (var kv in g.BuildingUsage) allBuildings.Add(kv.Key);

            sb.Append("seed,strategy,final_qol,env,eco,saf,cul,hub_spread,placed_count,budget_used");
            foreach (var b in allBuildings) sb.Append(",use_").Append(b);
            sb.AppendLine();

            foreach (var g in result.Games)
            {
                sb.Append(g.Seed).Append(',').Append(g.Strategy).Append(',')
                  .Append(g.FinalQol.ToString("F1")).Append(',')
                  .Append(g.Environment.ToString("F1")).Append(',')
                  .Append(g.Economy.ToString("F1")).Append(',')
                  .Append(g.HealthSafety.ToString("F1")).Append(',')
                  .Append(g.CultureEdu.ToString("F1")).Append(',')
                  .Append(g.HubSpread.ToString("F1")).Append(',')
                  .Append(g.PlacedCount).Append(',')
                  .Append(g.BudgetUsed);
                foreach (var b in allBuildings)
                {
                    sb.Append(',').Append(g.BuildingUsage.TryGetValue(b, out var v) ? v : 0);
                }
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }
    }
}
