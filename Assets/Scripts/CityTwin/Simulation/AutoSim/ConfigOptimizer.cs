using System.Collections.Generic;
using System.Text;
using UnityEngine;
using CityTwin.Config;

namespace CityTwin.Simulation.AutoSim
{
    /// <summary>
    /// Simulated annealing over a knob vector. Stepwise (`StepOnce()` per editor tick) so the UI stays
    /// responsive. After each step `BestConfig`/`BestKnobs` reflect the best feasible setting found.
    /// </summary>
    public class ConfigOptimizer
    {
        public struct Knobs
        {
            public float Norm;
            public float InfluenceRefBase;
            public float DistanceExponent;
            public float QolBalancePenalty;
            public float QolCap;
            public float SizeBoostSmall, SizeBoostMedium, SizeBoostLarge;
            public float NearCapSmall, NearCapMedium, NearCapLarge;
            public float QolWeightEnv, QolWeightEco, QolWeightSaf, QolWeightCul;
            public int StartingBudget;

            public static Knobs FromConfig(GameConfig c) => new Knobs
            {
                Norm = c.Scoring.norm,
                InfluenceRefBase = c.Scoring.influenceRefBase,
                DistanceExponent = c.Scoring.distanceExponent,
                QolBalancePenalty = c.Scoring.qolBalancePenalty,
                QolCap = c.Scoring.qolCap,
                SizeBoostSmall = c.Scoring.sizeBoostSmall,
                SizeBoostMedium = c.Scoring.sizeBoostMedium,
                SizeBoostLarge = c.Scoring.sizeBoostLarge,
                NearCapSmall = c.Scoring.nearCapSmall,
                NearCapMedium = c.Scoring.nearCapMedium,
                NearCapLarge = c.Scoring.nearCapLarge,
                QolWeightEnv = c.Scoring.qolWeightEnv,
                QolWeightEco = c.Scoring.qolWeightEco,
                QolWeightSaf = c.Scoring.qolWeightSaf,
                QolWeightCul = c.Scoring.qolWeightCul,
                StartingBudget = c.Budget != null ? c.Budget.startingBudget : 1000
            };

            public void ApplyTo(GameConfig c)
            {
                c.Scoring.norm = Mathf.Max(1f, Norm);
                c.Scoring.influenceRefBase = Mathf.Max(0.5f, InfluenceRefBase);
                c.Scoring.distanceExponent = Mathf.Max(0.2f, DistanceExponent);
                c.Scoring.qolBalancePenalty = Mathf.Clamp(QolBalancePenalty, 0f, 2f);
                c.Scoring.qolCap = Mathf.Clamp(QolCap, 30f, 100f);
                c.Scoring.sizeBoostSmall = Mathf.Clamp(SizeBoostSmall, 0.5f, 2.5f);
                c.Scoring.sizeBoostMedium = Mathf.Clamp(SizeBoostMedium, 0.5f, 2.5f);
                c.Scoring.sizeBoostLarge = Mathf.Clamp(SizeBoostLarge, 0.5f, 2.5f);
                c.Scoring.nearCapSmall = Mathf.Clamp(NearCapSmall, 0.1f, 1f);
                c.Scoring.nearCapMedium = Mathf.Clamp(NearCapMedium, 0.1f, 1f);
                c.Scoring.nearCapLarge = Mathf.Clamp(NearCapLarge, 0.1f, 1f);
                c.Scoring.qolWeightEnv = Mathf.Clamp(QolWeightEnv, 0.2f, 3f);
                c.Scoring.qolWeightEco = Mathf.Clamp(QolWeightEco, 0.2f, 3f);
                c.Scoring.qolWeightSaf = Mathf.Clamp(QolWeightSaf, 0.2f, 3f);
                c.Scoring.qolWeightCul = Mathf.Clamp(QolWeightCul, 0.2f, 3f);
                if (c.Budget != null) c.Budget.startingBudget = Mathf.Clamp(StartingBudget, 300, 5000);
            }
        }

        public int MaxIterations { get; }
        public int CurrentIteration { get; private set; }
        public bool IsDone => CurrentIteration >= MaxIterations || _stopped;
        public float BestScore { get; private set; } = float.MinValue;
        public float CurrentScore { get; private set; }
        public float BestHitRate { get; private set; }
        public float BestPillarBalance { get; private set; }
        public float BestMeanQol { get; private set; }
        public GameConfig BestConfig { get; private set; }
        public Knobs BestKnobs { get; private set; }
        public List<float> RecentScores { get; } = new List<float>();

        private readonly GameConfig _seedConfig;
        private readonly int _probeRuns;
        private readonly int _baseSeed;
        private Knobs _currentKnobs;
        private GameConfig _currentConfig;
        private readonly System.Random _rng;
        private bool _stopped;

        public ConfigOptimizer(GameConfig seedConfig, int maxIterations, int probeRuns, int baseSeed)
        {
            _seedConfig = seedConfig;
            MaxIterations = maxIterations;
            _probeRuns = probeRuns;
            _baseSeed = baseSeed;
            _rng = new System.Random(baseSeed);
            _currentConfig = CloneConfig(seedConfig);
            _currentKnobs = Knobs.FromConfig(_currentConfig);
            BestConfig = CloneConfig(_currentConfig);
            BestKnobs = _currentKnobs;
            // seed score
            var seedResult = ScoreConfig(_currentConfig);
            CurrentScore = GoalFunction.Score(seedResult, GoalFunction.Weights.Default);
            BestScore = CurrentScore;
            BestHitRate = seedResult.HitRateInBand;
            BestPillarBalance = seedResult.PillarBalanceIndex;
            BestMeanQol = seedResult.MeanQol;
            RecentScores.Add(CurrentScore);
        }

        public void Stop() => _stopped = true;

        public void StepOnce()
        {
            if (IsDone) return;
            CurrentIteration++;
            float T = Mathf.Lerp(0.8f, 0.05f, CurrentIteration / (float)MaxIterations); // cooling

            var trial = _currentKnobs;
            Perturb(ref trial, _rng);
            var trialConfig = CloneConfig(_currentConfig);
            trial.ApplyTo(trialConfig);
            var trialResult = ScoreConfig(trialConfig);
            float trialScore = GoalFunction.Score(trialResult, GoalFunction.Weights.Default);

            float dE = trialScore - CurrentScore;
            bool accept = dE > 0f || _rng.NextDouble() < Mathf.Exp(dE / Mathf.Max(0.01f, T));
            if (accept)
            {
                _currentKnobs = trial;
                _currentConfig = trialConfig;
                CurrentScore = trialScore;
                if (trialScore > BestScore)
                {
                    BestScore = trialScore;
                    BestKnobs = trial;
                    BestConfig = CloneConfig(trialConfig);
                    BestHitRate = trialResult.HitRateInBand;
                    BestPillarBalance = trialResult.PillarBalanceIndex;
                    BestMeanQol = trialResult.MeanQol;
                }
            }
            RecentScores.Add(CurrentScore);
            if (RecentScores.Count > 200) RecentScores.RemoveAt(0);
        }

        private BatchRunner.BatchResult ScoreConfig(GameConfig cfg)
        {
            var settings = BatchRunner.Settings.Default;
            settings.Runs = _probeRuns;
            settings.BaseSeed = _baseSeed;
            settings.SessionSeconds = 270f;
            settings.SimSecondsPerPlacement = 8f;
            return BatchRunner.Run(cfg, _ => new RandomKidStrategy(), settings);
        }

        private static void Perturb(ref Knobs k, System.Random rng)
        {
            // pick a random subset of fields to nudge ±~10%
            int hits = 3;
            for (int i = 0; i < hits; i++)
            {
                int field = rng.Next(0, 17);
                float pct = 1f + ((float)rng.NextDouble() * 0.2f - 0.1f); // ±10%
                switch (field)
                {
                    case 0:  k.Norm *= pct; break;
                    case 1:  k.InfluenceRefBase *= pct; break;
                    case 2:  k.DistanceExponent *= pct; break;
                    case 3:  k.QolBalancePenalty = Mathf.Clamp(k.QolBalancePenalty + ((float)rng.NextDouble() * 0.2f - 0.1f), 0f, 1.5f); break;
                    case 4:  k.QolCap = Mathf.Clamp(k.QolCap + ((float)rng.NextDouble() * 6f - 3f), 40f, 100f); break;
                    case 5:  k.SizeBoostSmall *= pct; break;
                    case 6:  k.SizeBoostMedium *= pct; break;
                    case 7:  k.SizeBoostLarge *= pct; break;
                    case 8:  k.NearCapSmall = Mathf.Clamp(k.NearCapSmall + ((float)rng.NextDouble() * 0.1f - 0.05f), 0.1f, 1f); break;
                    case 9:  k.NearCapMedium = Mathf.Clamp(k.NearCapMedium + ((float)rng.NextDouble() * 0.1f - 0.05f), 0.1f, 1f); break;
                    case 10: k.NearCapLarge = Mathf.Clamp(k.NearCapLarge + ((float)rng.NextDouble() * 0.1f - 0.05f), 0.1f, 1f); break;
                    case 11: k.QolWeightEnv *= pct; break;
                    case 12: k.QolWeightEco *= pct; break;
                    case 13: k.QolWeightSaf *= pct; break;
                    case 14: k.QolWeightCul *= pct; break;
                    case 15: k.StartingBudget = Mathf.Clamp(k.StartingBudget + rng.Next(-100, 101), 300, 5000); break;
                    case 16: k.QolBalancePenalty = Mathf.Clamp(k.QolBalancePenalty * pct, 0f, 1.5f); break;
                }
            }
        }

        private static GameConfig CloneConfig(GameConfig src)
        {
            var copy = new GameConfig
            {
                Meta = src.Meta,
                Session = src.Session,
                Budget = src.Budget != null ? new GameConfig.BudgetData { mode = src.Budget.mode, startingBudget = src.Budget.startingBudget } : new GameConfig.BudgetData(),
                Scoring = CloneScoring(src.Scoring),
                Accessibility = src.Accessibility,
                Osc = src.Osc,
                Buildings = src.Buildings,
                Map = src.Map,
                Tooltips = src.Tooltips,
                Stops = src.Stops,
                Tutorial = src.Tutorial,
                Inactivity = src.Inactivity,
                EndMessages = src.EndMessages,
                Localization = src.Localization
            };
            return copy;
        }

        private static GameConfig.ScoringData CloneScoring(GameConfig.ScoringData s)
        {
            if (s == null) return new GameConfig.ScoringData();
            return new GameConfig.ScoringData
            {
                epsilonDistance = s.epsilonDistance,
                norm = s.norm,
                equalDistrictWeight = s.equalDistrictWeight,
                influenceRefBase = s.influenceRefBase,
                influenceReferenceMeters = s.influenceReferenceMeters,
                distanceExponent = s.distanceExponent,
                distanceFloor = s.distanceFloor,
                distanceScale = s.distanceScale,
                maxRoadDistance = s.maxRoadDistance,
                qolBalancePenalty = s.qolBalancePenalty,
                qolCap = s.qolCap,
                sizeBoostSmall = s.sizeBoostSmall,
                sizeBoostMedium = s.sizeBoostMedium,
                sizeBoostLarge = s.sizeBoostLarge,
                nearCapSmall = s.nearCapSmall,
                nearCapMedium = s.nearCapMedium,
                nearCapLarge = s.nearCapLarge,
                impactRadiusSmall = s.impactRadiusSmall,
                impactRadiusMedium = s.impactRadiusMedium,
                impactRadiusLarge = s.impactRadiusLarge,
                qolWeightEnv = s.qolWeightEnv,
                qolWeightEco = s.qolWeightEco,
                qolWeightSaf = s.qolWeightSaf,
                qolWeightCul = s.qolWeightCul
            };
        }

        /// <summary>Patch the scoring + budget fields of an existing game_config.json text to match Knobs.
        /// Hand-written so we preserve comments, ordering, and untouched fields rather than re-emitting JSON.</summary>
        public static string PatchJsonForBalance(string json, Knobs k)
        {
            json = ReplaceNumberField(json, "norm", k.Norm.ToString("0.###"));
            json = ReplaceNumberField(json, "influenceRefBase", k.InfluenceRefBase.ToString("0.###"));
            json = ReplaceNumberField(json, "distanceExponent", k.DistanceExponent.ToString("0.###"));
            json = ReplaceNumberField(json, "qolBalancePenalty", k.QolBalancePenalty.ToString("0.###"));
            json = ReplaceNumberField(json, "qolCap", k.QolCap.ToString("0.#"));
            json = ReplaceNumberField(json, "sizeBoostSmall", k.SizeBoostSmall.ToString("0.###"));
            json = ReplaceNumberField(json, "sizeBoostMedium", k.SizeBoostMedium.ToString("0.###"));
            json = ReplaceNumberField(json, "sizeBoostLarge", k.SizeBoostLarge.ToString("0.###"));
            json = ReplaceNumberField(json, "nearCapSmall", k.NearCapSmall.ToString("0.###"));
            json = ReplaceNumberField(json, "nearCapMedium", k.NearCapMedium.ToString("0.###"));
            json = ReplaceNumberField(json, "nearCapLarge", k.NearCapLarge.ToString("0.###"));
            json = ReplaceNumberField(json, "qolWeightEnv", k.QolWeightEnv.ToString("0.###"));
            json = ReplaceNumberField(json, "qolWeightEco", k.QolWeightEco.ToString("0.###"));
            json = ReplaceNumberField(json, "qolWeightSaf", k.QolWeightSaf.ToString("0.###"));
            json = ReplaceNumberField(json, "qolWeightCul", k.QolWeightCul.ToString("0.###"));
            json = ReplaceNumberField(json, "startingBudget", k.StartingBudget.ToString());
            return json;
        }

        private static string ReplaceNumberField(string json, string fieldName, string newValue)
        {
            string needle = "\"" + fieldName + "\"";
            int idx = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (idx < 0) return json;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return json;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && "0123456789.-+eE".IndexOf(json[end]) >= 0) end++;
            if (end <= start) return json;
            var sb = new StringBuilder(json.Length + newValue.Length);
            sb.Append(json, 0, start);
            sb.Append(newValue);
            sb.Append(json, end, json.Length - end);
            return sb.ToString();
        }
    }
}
