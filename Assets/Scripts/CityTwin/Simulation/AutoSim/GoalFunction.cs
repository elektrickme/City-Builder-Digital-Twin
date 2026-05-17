using UnityEngine;

namespace CityTwin.Simulation.AutoSim
{
    /// <summary>
    /// Scores a BatchRunner.BatchResult against the expo goals:
    ///   1. Random child runs land in 50–80 QOL band (hit rate)
    ///   2. Distribution centered near 65 (penalty on |mean-65|)
    ///   3. Pillar variety rewarded (balance index)
    ///   4. (soft) No dead-pick buildings (usage spread)
    ///
    /// Returns a higher-is-better scalar in roughly [-1, +2].
    /// </summary>
    public static class GoalFunction
    {
        public struct Weights
        {
            public float HitRate;       // +
            public float MeanError;     // - |mean - 65|/35
            public float PillarBalance; // +
            public float UsageSpread;   // - (small)
            public static Weights Default => new Weights
            {
                HitRate = 1.0f,
                MeanError = 0.5f,
                PillarBalance = 0.6f,
                UsageSpread = 0.1f
            };
        }

        public static float Score(BatchRunner.BatchResult r, Weights w)
        {
            float hit = r.HitRateInBand;
            float meanErr = Mathf.Abs(r.MeanQol - 65f) / 35f; // 0 at 65, 1 at 30 or 100
            float balance = r.PillarBalanceIndex;
            float usagePenalty = UsageImbalance(r);
            return w.HitRate * hit
                 - w.MeanError * meanErr
                 + w.PillarBalance * balance
                 - w.UsageSpread * usagePenalty;
        }

        /// <summary>0 = even usage, 1 = one building dominates. Floors low-count buildings at 1 to avoid div-by-zero.</summary>
        private static float UsageImbalance(BatchRunner.BatchResult r)
        {
            if (r.AggregateBuildingUsage == null || r.AggregateBuildingUsage.Count < 2) return 0f;
            int max = 0, min = int.MaxValue;
            foreach (var kv in r.AggregateBuildingUsage)
            {
                if (kv.Value > max) max = kv.Value;
                if (kv.Value < min) min = kv.Value;
            }
            min = Mathf.Max(1, min);
            float ratio = (float)max / min;
            // log scale ratio so 5x penalty isn't crushed by 50x outlier
            return Mathf.Clamp01(Mathf.Log(ratio) / Mathf.Log(20f));
        }
    }
}
