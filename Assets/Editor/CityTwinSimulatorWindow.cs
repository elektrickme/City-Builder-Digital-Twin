#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using CityTwin.Config;
using CityTwin.Simulation;
using CityTwin.Simulation.AutoSim;

namespace CityTwin.EditorTools
{
    /// <summary>
    /// In-Editor visual auto-player + balance dashboard. Runs the headless harness against the live
    /// game_config.json, draws a placement preview, a QOL histogram, and per-building usage bars.
    /// Block D adds the auto-balance tab on top of this same window.
    /// </summary>
    public class CityTwinSimulatorWindow : EditorWindow
    {
        private enum Tab { RunSim, AutoBalance }
        private Tab _tab = Tab.RunSim;

        // run-sim state
        private int _runs = 200;
        private int _baseSeed = 1234;
        private float _sessionSeconds = 270f;
        private float _simSecondsPerPlacement = 8f;
        private string _strategyChoice = "RandomKid";
        private string[] _strategies = { "RandomKid", "Greedy" };

        private BatchRunner.BatchResult? _lastResult;
        private string _lastCsvPath;
        private string _lastError;

        // visual play state
        private bool _visualPlaying;
        private double _lastVisualStep;
        private SimulationHarness _visualHarness;
        private IPlacementStrategy _visualStrategy;
        private float _visualElapsed;
        private List<(Vector2 pos, string buildingId)> _visualHistory = new List<(Vector2, string)>();

        // auto-balance state
        private int _optimizerIterations = 50;
        private int _optimizerProbeRuns = 200;
        private bool _optimizerRunning;
        private ConfigOptimizer _optimizer;

        [MenuItem("CityTwin/Run Auto-Sim", priority = 200)]
        public static void Open()
        {
            var win = GetWindow<CityTwinSimulatorWindow>();
            win.titleContent = new GUIContent("CityTwin Sim");
            win.minSize = new Vector2(440f, 540f);
            win.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            StopVisual();
            _optimizer?.Stop();
        }

        private void OnEditorUpdate()
        {
            if (_visualPlaying) StepVisual();
            if (_optimizerRunning && _optimizer != null)
            {
                _optimizer.StepOnce();
                Repaint();
                if (_optimizer.IsDone)
                {
                    _optimizerRunning = false;
                }
            }
        }

        private void OnGUI()
        {
            _tab = (Tab)GUILayout.Toolbar((int)_tab, new[] { "Run Sim", "Auto-Balance" });
            EditorGUILayout.Space(6);

            if (_tab == Tab.RunSim) DrawRunSimTab();
            else DrawAutoBalanceTab();
        }

        private void DrawRunSimTab()
        {
            EditorGUILayout.LabelField("Headless playthroughs", EditorStyles.boldLabel);
            _runs = EditorGUILayout.IntSlider("Runs", _runs, 10, 5000);
            _baseSeed = EditorGUILayout.IntField("Base seed", _baseSeed);
            _sessionSeconds = EditorGUILayout.Slider("Session seconds", _sessionSeconds, 30f, 600f);
            _simSecondsPerPlacement = EditorGUILayout.Slider("Sim s / placement", _simSecondsPerPlacement, 1f, 30f);
            int strategyIdx = Array.IndexOf(_strategies, _strategyChoice);
            if (strategyIdx < 0) strategyIdx = 0;
            strategyIdx = EditorGUILayout.Popup("Strategy", strategyIdx, _strategies);
            _strategyChoice = _strategies[strategyIdx];

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run Batch", GUILayout.Height(28)))
                {
                    RunBatchSync();
                }
                if (GUILayout.Button(_visualPlaying ? "Stop Visual" : "Start Visual", GUILayout.Height(28)))
                {
                    if (_visualPlaying) StopVisual(); else StartVisual();
                }
                if (GUILayout.Button("Write CSV", GUILayout.Height(28)))
                {
                    WriteCsv();
                }
            }

            if (!string.IsNullOrEmpty(_lastError))
                EditorGUILayout.HelpBox(_lastError, MessageType.Error);
            if (!string.IsNullOrEmpty(_lastCsvPath))
                EditorGUILayout.HelpBox("CSV written: " + _lastCsvPath, MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Last batch result", EditorStyles.boldLabel);
            if (_lastResult.HasValue)
            {
                var r = _lastResult.Value;
                EditorGUILayout.LabelField($"Strategy: {r.Strategy} | Runs: {r.Runs}");
                EditorGUILayout.LabelField($"Mean QOL: {r.MeanQol:F1}  σ={r.StdQol:F1}  Hit-rate(50–80): {r.HitRateInBand * 100f:F0}%  PillarBalance: {r.PillarBalanceIndex:F2}");
                DrawQolHistogram(r);
                DrawBuildingUsage(r);
            }
            else EditorGUILayout.LabelField("(no run yet)");

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Visual auto-player", EditorStyles.boldLabel);
            DrawVisualPreview();
        }

        private void DrawAutoBalanceTab()
        {
            EditorGUILayout.LabelField("Auto-balancer (simulated annealing)", EditorStyles.boldLabel);
            _optimizerIterations = EditorGUILayout.IntSlider("Iterations", _optimizerIterations, 5, 200);
            _optimizerProbeRuns = EditorGUILayout.IntSlider("Games per probe", _optimizerProbeRuns, 20, 1000);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !_optimizerRunning;
                if (GUILayout.Button("Start", GUILayout.Height(28))) StartOptimizer();
                GUI.enabled = _optimizerRunning;
                if (GUILayout.Button("Stop", GUILayout.Height(28))) { _optimizer?.Stop(); _optimizerRunning = false; }
                GUI.enabled = _optimizer != null && !_optimizerRunning && _optimizer.BestConfig != null;
                if (GUILayout.Button("Write to game_config.json", GUILayout.Height(28))) WriteOptimizedConfig();
                GUI.enabled = true;
            }

            if (_optimizer != null)
            {
                EditorGUILayout.HelpBox(
                    $"Iter {_optimizer.CurrentIteration}/{_optimizer.MaxIterations}  best={_optimizer.BestScore:F3}  current={_optimizer.CurrentScore:F3}\n" +
                    $"band hit-rate: {_optimizer.BestHitRate * 100f:F0}%  pillar-balance: {_optimizer.BestPillarBalance:F2}  meanQol: {_optimizer.BestMeanQol:F1}",
                    MessageType.None);
                var bars = _optimizer.RecentScores;
                Rect r = GUILayoutUtility.GetRect(400f, 80f);
                EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.14f, 1f));
                if (bars != null && bars.Count > 1)
                {
                    float min = float.MaxValue, max = float.MinValue;
                    foreach (var b in bars) { if (b < min) min = b; if (b > max) max = b; }
                    if (max - min < 0.0001f) max = min + 0.0001f;
                    float w = r.width / Mathf.Max(1, bars.Count - 1);
                    var prev = new Vector2(r.x, r.yMax - (bars[0] - min) / (max - min) * r.height);
                    for (int i = 1; i < bars.Count; i++)
                    {
                        var next = new Vector2(r.x + i * w, r.yMax - (bars[i] - min) / (max - min) * r.height);
                        Handles.color = new Color(0.4f, 0.85f, 0.6f, 1f);
                        Handles.DrawLine(prev, next);
                        prev = next;
                    }
                }
            }
            else EditorGUILayout.LabelField("(optimizer idle)");
        }

        // ---------- batch run ----------

        private void RunBatchSync()
        {
            _lastError = null;
            _lastCsvPath = null;
            try
            {
                var config = SimulationHarness.LoadConfigFromStreamingAssets();
                if (config == null) { _lastError = "Failed to load game_config.json from StreamingAssets."; return; }

                BatchRunner.StrategyFactory factory = _strategyChoice == "Greedy"
                    ? (BatchRunner.StrategyFactory)(h => new GreedyStrategy(h))
                    : (BatchRunner.StrategyFactory)(_ => new RandomKidStrategy());

                var settings = BatchRunner.Settings.Default;
                settings.Runs = _runs;
                settings.BaseSeed = _baseSeed;
                settings.SessionSeconds = _sessionSeconds;
                settings.SimSecondsPerPlacement = _simSecondsPerPlacement;

                _lastResult = BatchRunner.Run(config, factory, settings);
            }
            catch (Exception e)
            {
                _lastError = e.ToString();
            }
        }

        private void WriteCsv()
        {
            if (!_lastResult.HasValue) { _lastError = "No batch result to export. Run a batch first."; return; }
            string folder = Path.Combine(Application.dataPath, "../SimulationOutput");
            _lastCsvPath = BatchRunner.WriteCsv(_lastResult.Value, folder);
        }

        // ---------- visual sim ----------

        private void StartVisual()
        {
            try
            {
                var config = SimulationHarness.LoadConfigFromStreamingAssets();
                if (config == null) { _lastError = "Failed to load game_config.json."; return; }
                _visualHarness = new SimulationHarness();
                _visualHarness.Build(config, _baseSeed);
                _visualStrategy = _strategyChoice == "Greedy" ? (IPlacementStrategy)new GreedyStrategy(_visualHarness) : new RandomKidStrategy();
                _visualElapsed = 0f;
                _visualHistory.Clear();
                _lastVisualStep = EditorApplication.timeSinceStartup;
                _visualPlaying = true;
            }
            catch (Exception e) { _lastError = e.ToString(); }
        }

        private void StopVisual()
        {
            _visualPlaying = false;
            _visualHarness?.Dispose();
            _visualHarness = null;
            _visualStrategy = null;
        }

        private void StepVisual()
        {
            if (_visualHarness == null || _visualStrategy == null) { StopVisual(); return; }
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastVisualStep < 0.4) return;
            _lastVisualStep = now;

            if (_visualElapsed >= _sessionSeconds) { _visualPlaying = false; Repaint(); return; }
            var snap = _visualHarness.BuildSnapshot(_visualElapsed);
            if (!_visualStrategy.ChooseNextPlacement(snap, _visualHarness.Rng, out var bid, out var pos))
            { _visualPlaying = false; Repaint(); return; }

            _visualHarness.TryPlace(bid, pos);
            _visualHistory.Add((pos, bid));
            _visualElapsed += _simSecondsPerPlacement;
            Repaint();
        }

        private void DrawVisualPreview()
        {
            Rect r = GUILayoutUtility.GetRect(420f, 280f);
            EditorGUI.DrawRect(r, new Color(0.08f, 0.10f, 0.12f, 1f));
            if (_visualHarness == null) { GUI.Label(r, "  (Start visual to begin)"); return; }

            Vector2 half = _visualHarness.FieldHalfExtent;
            float padding = 12f;
            float drawW = r.width - padding * 2f;
            float drawH = r.height - padding * 2f;
            Vector2 ToScreen(Vector2 contentLocal)
            {
                float u = (contentLocal.x + half.x) / (half.x * 2f);
                float v = 1f - (contentLocal.y + half.y) / (half.y * 2f);
                return new Vector2(r.x + padding + u * drawW, r.y + padding + v * drawH);
            }

            // roads
            Handles.color = new Color(0.35f, 0.35f, 0.4f, 1f);
            foreach (var seg in _visualHarness.Roads)
                Handles.DrawLine(ToScreen(seg.a), ToScreen(seg.b));

            // hubs
            foreach (var hp in _visualHarness.Engine.HubPositions)
            {
                var p = ToScreen(hp);
                EditorGUI.DrawRect(new Rect(p.x - 6f, p.y - 6f, 12f, 12f), new Color(1f, 0.85f, 0.25f, 1f));
            }

            // placed buildings
            foreach (var (pos, bid) in _visualHistory)
            {
                var p = ToScreen(pos);
                Color c = ColorFor(bid);
                EditorGUI.DrawRect(new Rect(p.x - 3f, p.y - 3f, 6f, 6f), c);
            }

            GUI.Label(new Rect(r.x + 8f, r.y + 4f, 400f, 18f),
                $"placed={_visualHarness.PlacedCount}  budget={_visualHarness.RemainingBudget}  qol={_visualHarness.Engine.Qol:F0}");
        }

        private static Color ColorFor(string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId)) return Color.gray;
            string id = buildingId.ToLowerInvariant();
            if (id.Contains("garden") || id.Contains("park") || id.Contains("recycl")) return new Color(0.30f, 0.85f, 0.45f, 1f);
            if (id.Contains("office") || id.Contains("mall") || id.Contains("factory")) return new Color(1f, 0.78f, 0.20f, 1f);
            if (id.Contains("police") || id.Contains("fire") || id.Contains("hospital")) return new Color(1f, 0.32f, 0.32f, 1f);
            return new Color(0.62f, 0.45f, 1f, 1f);
        }

        private void DrawQolHistogram(BatchRunner.BatchResult r)
        {
            const int buckets = 20;
            int[] counts = new int[buckets];
            foreach (var g in r.Games)
            {
                int b = Mathf.Clamp(Mathf.FloorToInt(g.FinalQol / 100f * buckets), 0, buckets - 1);
                counts[b]++;
            }
            int max = 1;
            for (int i = 0; i < buckets; i++) if (counts[i] > max) max = counts[i];

            Rect area = GUILayoutUtility.GetRect(420f, 100f);
            EditorGUI.DrawRect(area, new Color(0.12f, 0.12f, 0.14f, 1f));
            float bw = area.width / buckets;
            for (int i = 0; i < buckets; i++)
            {
                float h = (counts[i] / (float)max) * area.height;
                Color c = (i >= 10 && i <= 16) ? new Color(0.4f, 0.85f, 0.6f, 1f) : new Color(0.7f, 0.7f, 0.7f, 1f);
                EditorGUI.DrawRect(new Rect(area.x + i * bw, area.yMax - h, bw - 1f, h), c);
            }
            GUI.Label(new Rect(area.x + 4f, area.y + 2f, 200f, 16f), "QOL histogram (green = 50–80 band)");
        }

        private void DrawBuildingUsage(BatchRunner.BatchResult r)
        {
            if (r.AggregateBuildingUsage == null || r.AggregateBuildingUsage.Count == 0) return;
            EditorGUILayout.LabelField("Building usage (placements across all runs)", EditorStyles.miniBoldLabel);
            int max = 1;
            foreach (var kv in r.AggregateBuildingUsage) if (kv.Value > max) max = kv.Value;
            foreach (var kv in r.AggregateBuildingUsage)
            {
                Rect row = GUILayoutUtility.GetRect(420f, 16f);
                EditorGUI.DrawRect(new Rect(row.x, row.y + 2f, 140f, 12f), new Color(0.1f, 0.1f, 0.1f, 0.5f));
                GUI.Label(new Rect(row.x + 4f, row.y, 140f, 16f), kv.Key);
                float w = (kv.Value / (float)max) * (row.width - 160f);
                EditorGUI.DrawRect(new Rect(row.x + 150f, row.y + 3f, w, 10f), ColorFor(kv.Key));
                GUI.Label(new Rect(row.xMax - 60f, row.y, 60f, 16f), kv.Value.ToString());
            }
        }

        // ---------- optimizer ----------

        private void StartOptimizer()
        {
            try
            {
                var config = SimulationHarness.LoadConfigFromStreamingAssets();
                if (config == null) { _lastError = "Failed to load game_config.json."; return; }
                _optimizer = new ConfigOptimizer(config, _optimizerIterations, _optimizerProbeRuns, _baseSeed);
                _optimizerRunning = true;
            }
            catch (Exception e) { _lastError = e.ToString(); }
        }

        private void WriteOptimizedConfig()
        {
            if (_optimizer?.BestConfig == null) return;
            string src = Path.Combine(Application.streamingAssetsPath, "game_config.json");
            string backup = src + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (File.Exists(src)) File.Copy(src, backup, overwrite: false);
            File.WriteAllText(src, ConfigOptimizer.PatchJsonForBalance(File.ReadAllText(src), _optimizer.BestKnobs));
            AssetDatabase.Refresh();
            _lastCsvPath = "Updated game_config.json (backup at " + backup + ")";
        }
    }
}
#endif
