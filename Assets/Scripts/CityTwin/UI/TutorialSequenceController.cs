using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using CityTwin.Config;
using CityTwin.Core;
using CityTwin.Localization;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Drives the full per-instance game flow: the intro tutorial, then live gameplay feedback, then the end report.
    ///
    ///   - Tutorial: cycles child TutorialPopups through the config tutorial steps (localized, timed),
    ///     firing OnTutorialComplete when done.
    ///   - Score bands: the end.* band message fires once, the first time city QOL crosses UP into a new band
    ///     (multi-band jumps show only the landed band; the 80-100 band is ignored because QOL caps at 80).
    ///   - Citizen reactions (hybrid, timer-driven): every reactionIntervalSeconds after the tutorial, the pillar
    ///     that moved most since the last tick (or, failing that, the most extreme pillar by level) is commented
    ///     on; its level decides polarity (high = praise, low = complain, mid = silent) and the bubble appears
    ///     next to the hub most affected over that window. Uses reaction.*.v2 wording.
    ///   - End report: fills the end screen's Balance card (feedback.* by pillar spread) and Strategic card
    ///     (reaction.*.access.v2 by building connectivity).
    ///
    /// Band/reaction popups reuse the tutorial popups when no dedicated popup is assigned. Live feedback only
    /// runs after the tutorial finishes. Per-instance, no statics.
    /// Note: the simulation tracks 4 pillars (Environment/Economy/Health and Safety/Culture and Edu); there is no
    /// accessibility signal, so reaction.*.access stays as data and is not triggered live.
    /// </summary>
    public class TutorialSequenceController : MonoBehaviour
    {
        [Header("Tutorial")]
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private LocalizationService localization;
        [Tooltip("Child TutorialPopup components in display order. If empty, auto-collected from children at Awake.")]
        [SerializeField] private TutorialPopup[] popups;

        [Header("Feedback references (auto-resolved if null)")]
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private GameInstanceCoordinator coordinator;
        [SerializeField] private EndScreenController endScreen;
        [Tooltip("Play-field root that hub positions are expressed in. Auto-resolved from BuildingSpawner.ContentRoot.")]
        [SerializeField] private RectTransform contentRoot;
        [Tooltip("Residential hub registry; bubbles anchor to the hub visuals through it. Auto-resolved.")]
        [SerializeField] private HubRegistry hubRegistry;

        [Header("Score-band popup")]
        [Tooltip("Popup for score-band messages. If null, reuses the first tutorial popup.")]
        [SerializeField] private TutorialPopup bandPopup;
        [SerializeField] private float bandPopupSeconds = 6f;

        [Header("Citizen reactions")]
        [Tooltip("Fallback popup when hub bubbles are off or no play-field root. If null, reuses the first tutorial popup.")]
        [SerializeField] private TutorialPopup reactionPopup;
        [SerializeField] private float reactionPopupSeconds = 4f;
        [Tooltip("When on, a reaction spawns a bubble over the most-relevant hub instead of the fallback popup.")]
        [SerializeField] private bool useHubBubbles = true;
        [SerializeField] private float bubbleSeconds = 4f;
        [SerializeField] private float bubbleMaxWidth = 280f;
        [SerializeField] private float bubbleFontSize = 26f;
        [SerializeField] private float bubblePadding = 18f;
        [Tooltip("Vertical gap between the hub point and the bottom of the bubble.")]
        [SerializeField] private float bubbleHubGap = 28f;
        [Tooltip("How far the bubble drifts up over its lifetime.")]
        [SerializeField] private float bubbleRise = 24f;
        [SerializeField] private Color bubbleBackColor = new Color(0.10f, 0.12f, 0.16f, 0.92f);
        [SerializeField] private Color bubbleTextColor = Color.white;

        [Header("Reaction thresholds (pillar %, 0-100)")]
        [Tooltip("At or below this level a commented pillar produces a negative reaction.")]
        [SerializeField, Range(0f, 100f)] private float lowLevel = 30f;
        [Tooltip("At or above this level a commented pillar produces a positive reaction.")]
        [SerializeField, Range(0f, 100f)] private float highLevel = 70f;
        [Tooltip("Minimum pillar move (absolute %) since the last tick for the biggest-mover pick to win.")]
        [SerializeField] private float minDeltaToReact = 1f;
        [Tooltip("Seconds between timed citizen reactions once the tutorial is over. Ticks with nothing to say stay silent.")]
        [SerializeField] private float reactionIntervalSeconds = 25f;
        [Tooltip("Per-pillar minimum seconds before the same pillar is commented on again.")]
        [SerializeField] private float perPillarCooldown = 60f;
        [Tooltip("Minimum seconds before a bubble may anchor to the same hub again. Keeps bubbles moving around the map; ignored when no other hub qualifies.")]
        [SerializeField] private float hubRepeatCooldown = 50f;

        [Header("End-screen report")]
        [Tooltip("Pillar spread (max - min, %) at or below which the Balance card praises even development.")]
        [SerializeField] private float balancedSpreadMax = 15f;
        [Tooltip("Spread above balancedSpreadMax up to this reads 'some areas need care'; beyond it, 'find a better balance'.")]
        [SerializeField] private float unevenSpreadMax = 35f;
        [Tooltip("Average pillar level (%) below which the Balance card always encourages instead of praising. Stops an empty city (all pillars 0, spread 0) from reading as 'perfectly balanced'.")]
        [SerializeField] private float minBalancePraiseLevel = 20f;

        public event Action OnTutorialComplete;
        public bool IsRunning => _isRunning;

        // tutorial state
        private Coroutine _sequenceRoutine;
        private bool _isRunning;
        private int _currentStepIndex;
        private Coroutine _interruptRoutine;   // band message that cut the tutorial short; resumes it after

        // band state
        private const int TopBandFloor = 80;
        private List<GameConfig.EndMessageData> _liveBands;
        private int _highestBandShown = -1;
        private bool _anyPlacement;

        // reaction state: city + per-hub baseline captured at each reaction tick, so the next tick sees
        // what moved in between (hub arrays index-aligned with HubMetrics)
        private enum Pillar { Environment, Economy, HealthSafety, CultureEdu }
        private struct HubFrame { public float[] Env, Eco, Saf, Cul; }
        private float _tickEnv, _tickEco, _tickSaf, _tickCul;
        private HubFrame _tickHubs;
        private float _nextReactionTime;
        private readonly Dictionary<Pillar, float> _pillarLastTime = new Dictionary<Pillar, float>();
        private readonly Dictionary<int, float> _hubLastTime = new Dictionary<int, float>();

        private Coroutine _bandRoutine, _reactionRoutine, _bubbleRoutine;
        private GameObject _currentBubble;
        private static Sprite _bubbleSprite;

        private void Awake()
        {
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true) ?? GetComponentInParent<GameConfigLoader>();
            if (localization == null) localization = GetComponentInChildren<LocalizationService>(true) ?? GetComponentInParent<LocalizationService>();
            if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true) ?? GetComponentInParent<BuildingSpawner>();
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true) ?? GetComponentInParent<SimulationEngine>();
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true) ?? GetComponentInParent<SessionTimer>();
            if (coordinator == null) coordinator = GetComponentInChildren<GameInstanceCoordinator>(true) ?? GetComponentInParent<GameInstanceCoordinator>();
            if (endScreen == null) endScreen = GetComponentInChildren<EndScreenController>(true) ?? GetComponentInParent<EndScreenController>();
            if (contentRoot == null && buildingSpawner != null) contentRoot = buildingSpawner.ContentRoot;
            if (hubRegistry == null) hubRegistry = GetComponentInChildren<HubRegistry>(true) ?? GetComponentInParent<HubRegistry>();

            if (popups == null || popups.Length == 0)
                popups = GetComponentsInChildren<TutorialPopup>(true);

            HideAll();
        }

        private void OnEnable()
        {
            if (buildingSpawner != null) buildingSpawner.OnTileSpawned += HandleTileSpawned;
            if (simulationEngine != null) simulationEngine.OnMetricsChanged += HandleMetricsChanged;
            if (sessionTimer != null)
            {
                sessionTimer.OnPhaseChanged += HandlePhaseChanged;
                sessionTimer.OnTimerEnded += HandleTimerEnded;
            }
            if (configLoader != null) configLoader.OnConfigLoaded += HandleConfigLoaded;

            BuildLiveBands();
            ResetFeedbackState();
        }

        private void OnDisable()
        {
            if (buildingSpawner != null) buildingSpawner.OnTileSpawned -= HandleTileSpawned;
            if (simulationEngine != null) simulationEngine.OnMetricsChanged -= HandleMetricsChanged;
            if (sessionTimer != null)
            {
                sessionTimer.OnPhaseChanged -= HandlePhaseChanged;
                sessionTimer.OnTimerEnded -= HandleTimerEnded;
            }
            if (configLoader != null) configLoader.OnConfigLoaded -= HandleConfigLoaded;

            if (_bandRoutine != null) { StopCoroutine(_bandRoutine); _bandRoutine = null; }
            if (_reactionRoutine != null) { StopCoroutine(_reactionRoutine); _reactionRoutine = null; }
            DestroyCurrentBubble();
        }

        // ===================== Tutorial =====================

        public void StartTutorial()
        {
            if (_sequenceRoutine != null) StopCoroutine(_sequenceRoutine);
            if (_interruptRoutine != null) { StopCoroutine(_interruptRoutine); _interruptRoutine = null; }
            _sequenceRoutine = StartCoroutine(RunSequence(0));
        }

        public void StopTutorial()
        {
            if (_sequenceRoutine != null) { StopCoroutine(_sequenceRoutine); _sequenceRoutine = null; }
            if (_interruptRoutine != null) { StopCoroutine(_interruptRoutine); _interruptRoutine = null; }
            _isRunning = false;
            HideAll();
            bandPopup?.HideImmediate();
        }

        private IEnumerator RunSequence(int startIndex)
        {
            _isRunning = true;
            HideAll();

            var steps = configLoader?.Config?.Tutorial?.steps;
            if (steps == null || steps.Length == 0 || popups == null || popups.Length == 0)
            {
                Debug.LogWarning("[TutorialSequence] No tutorial steps or popups configured - skipping tutorial.");
                _isRunning = false;
                _sequenceRoutine = null;
                OnTutorialComplete?.Invoke();
                yield break;
            }

            int count = Mathf.Min(steps.Length, popups.Length);
            for (int i = Mathf.Clamp(startIndex, 0, count); i < count; i++)
            {
                _currentStepIndex = i;
                var step = steps[i];
                var popup = popups[i];

                string text = localization != null ? localization.GetString(step.textKey) : step.textKey;
                popup.SetText(text);

                var showTween = popup.PlayShowTween();
                if (showTween != null) yield return showTween.WaitForCompletion(true);

                float duration = step.durationSeconds > 0 ? step.durationSeconds : 5f;
                yield return new WaitForSeconds(duration);

                var hideTween = popup.PlayHideTween();
                if (hideTween != null) yield return hideTween.WaitForCompletion(true);
            }

            _isRunning = false;
            _sequenceRoutine = null;
            OnTutorialComplete?.Invoke();
        }

        private void HideAll()
        {
            if (popups == null) return;
            foreach (var p in popups)
                if (p != null) p.HideImmediate();
        }

        // ===================== Live feedback =====================

        /// <summary>Session timer is counting down gameplay, tutorial or not. Bands may fire in this state.</summary>
        private bool InGameplay =>
            sessionTimer != null && sessionTimer.CurrentPhase == SessionTimer.Phase.Gameplay && sessionTimer.IsRunning;

        /// <summary>Citizen reactions additionally wait for the tutorial to finish.</summary>
        private bool IsPlaying => !_isRunning && InGameplay;

        private void HandleConfigLoaded(GameConfig _) => BuildLiveBands();

        /// <summary>Cache the score bands eligible to fire live: end messages with min below the top-band floor,
        /// sorted ascending by min. The 80-100 band is excluded because QOL is capped at 80.</summary>
        private void BuildLiveBands()
        {
            var bands = new List<GameConfig.EndMessageData>();
            var src = configLoader != null && configLoader.Config != null ? configLoader.Config.EndMessages : null;
            if (src != null)
                foreach (var b in src)
                    if (b != null && b.min < TopBandFloor) bands.Add(b);
            bands.Sort((a, b) => a.min.CompareTo(b.min));
            _liveBands = bands;
        }

        /// <summary>Reset live-feedback state for a fresh session (start or restart).</summary>
        private void ResetFeedbackState()
        {
            _highestBandShown = -1;
            _anyPlacement = false;
            _pillarLastTime.Clear();
            _hubLastTime.Clear();
            _nextReactionTime = Time.time + reactionIntervalSeconds;
            CaptureTickBaseline();

            if (_bandRoutine != null) { StopCoroutine(_bandRoutine); _bandRoutine = null; }
            if (_reactionRoutine != null) { StopCoroutine(_reactionRoutine); _reactionRoutine = null; }
            if (_interruptRoutine != null) { StopCoroutine(_interruptRoutine); _interruptRoutine = null; }
            DestroyCurrentBubble();
            // Only hide dedicated popups; never the shared tutorial popups (the tutorial owns those).
            bandPopup?.HideImmediate();
            reactionPopup?.HideImmediate();
        }

        /// <summary>Snapshot city + per-hub pillars as the reference point for the next reaction tick.</summary>
        private void CaptureTickBaseline()
        {
            _tickEnv = simulationEngine != null ? simulationEngine.Environment : 0f;
            _tickEco = simulationEngine != null ? simulationEngine.Economy : 0f;
            _tickSaf = simulationEngine != null ? simulationEngine.HealthSafety : 0f;
            _tickCul = simulationEngine != null ? simulationEngine.CultureEdu : 0f;
            CaptureHubFrame(ref _tickHubs);
        }

        private void HandlePhaseChanged(SessionTimer.Phase phase)
        {
            if (phase == SessionTimer.Phase.Gameplay)
                ResetFeedbackState();
        }

        private void HandleMetricsChanged()
        {
            // Bands may fire during the tutorial (they interrupt it); reactions are timer-driven in Update.
            if (_anyPlacement && InGameplay)
                EvaluateBands();
        }

        private void HandleTileSpawned(string engineTileId, string buildingId, GameObject marker)
        {
            if (!InGameplay) return;
            _anyPlacement = true;   // arms the starting (0-20) band and the timed reactions
            EvaluateBands();        // may interrupt a running tutorial
        }

        private void Update()
        {
            // Timed citizen reactions. Hold the clock while the tutorial (or a non-gameplay phase) is up,
            // so the first bubble lands one full interval after play actually starts.
            if (!IsPlaying)
            {
                _nextReactionTime = Time.time + reactionIntervalSeconds;
                return;
            }
            if (Time.time < _nextReactionTime) return;
            _nextReactionTime = Time.time + reactionIntervalSeconds;
            EvaluateTimedReaction();
        }

        // ---- score bands ----

        private void EvaluateBands()
        {
            if (_liveBands == null || _liveBands.Count == 0) BuildLiveBands();
            if (_liveBands.Count == 0 || simulationEngine == null) return;

            int cur = CurrentBandIndex(simulationEngine.Qol);
            if (cur > _highestBandShown)
            {
                _highestBandShown = cur;   // also marks any skipped lower bands as seen
                ShowBand(_liveBands[cur]);
            }
        }

        private int CurrentBandIndex(float qol)
        {
            int idx = -1;
            for (int i = 0; i < _liveBands.Count; i++)
                if (qol >= _liveBands[i].min) idx = i;
            return idx;
        }

        private void ShowBand(GameConfig.EndMessageData band)
        {
            string title = GetString(band.titleKey);
            string body = GetString(band.bodyKey);
            string text = string.IsNullOrEmpty(body) ? title : $"{title}\n\n{body}";

            if (_isRunning) { InterruptTutorialWithBand(text); return; }
            ShowPopup(ResolveBandPopup(), text, bandPopupSeconds, ref _bandRoutine);
        }

        /// <summary>Cut the running tutorial short for a band message, then resume the tutorial from the
        /// interrupted step (replayed from its start, so no instruction is half-lost). A second band arriving
        /// during the interruption just replaces the band text; the resume point is unchanged.</summary>
        private void InterruptTutorialWithBand(string text)
        {
            if (_sequenceRoutine != null) { StopCoroutine(_sequenceRoutine); _sequenceRoutine = null; }
            HideAll();   // snap the current step away; the band popup brings its own show tween

            if (_interruptRoutine != null) StopCoroutine(_interruptRoutine);
            _interruptRoutine = StartCoroutine(BandInterruptRoutine(text));
        }

        private IEnumerator BandInterruptRoutine(string text)
        {
            var popup = ResolveBandPopup();
            if (popup != null)
            {
                popup.SetText(text);
                var show = popup.PlayShowTween();
                if (show != null) yield return show.WaitForCompletion(true);
                yield return new WaitForSeconds(bandPopupSeconds);
                var hide = popup.PlayHideTween();
                if (hide != null) yield return hide.WaitForCompletion(true);
            }
            _interruptRoutine = null;
            _sequenceRoutine = StartCoroutine(RunSequence(_currentStepIndex));
        }

        // ---- citizen reactions (hybrid) ----

        /// <summary>One timed reaction tick. Preferred subject: the pillar that moved most since the last tick
        /// (that is where the players just acted); fallback: the most extreme pillar by level. Either way the
        /// polarity comes from the resulting level (high = praise, low = complain, mid = silent) and the pillar's
        /// cooldown must have elapsed. The baseline always refreshes so the next tick judges only what changed
        /// after this one.</summary>
        private void EvaluateTimedReaction()
        {
            if (simulationEngine == null) return;
            if (!_anyPlacement) { CaptureTickBaseline(); return; }   // empty table: citizens have nothing to react to

            float curEnv = simulationEngine.Environment;
            float curEco = simulationEngine.Economy;
            float curSaf = simulationEngine.HealthSafety;
            float curCul = simulationEngine.CultureEdu;

            // Preferred: biggest mover since the last tick.
            Pillar pillar = Pillar.Environment;
            float bestAbs = Mathf.Abs(curEnv - _tickEnv), level = curEnv;
            if (Mathf.Abs(curEco - _tickEco) > bestAbs) { bestAbs = Mathf.Abs(curEco - _tickEco); pillar = Pillar.Economy; level = curEco; }
            if (Mathf.Abs(curSaf - _tickSaf) > bestAbs) { bestAbs = Mathf.Abs(curSaf - _tickSaf); pillar = Pillar.HealthSafety; level = curSaf; }
            if (Mathf.Abs(curCul - _tickCul) > bestAbs) { bestAbs = Mathf.Abs(curCul - _tickCul); pillar = Pillar.CultureEdu; level = curCul; }

            bool deltaPickOk = bestAbs >= minDeltaToReact && Polarity(level) != null && !OnCooldown(pillar);
            if (!deltaPickOk && !TryMostExtremePillar(curEnv, curEco, curSaf, curCul, out pillar, out level))
            {
                CaptureTickBaseline();
                return;   // silent tick: nothing moved and nothing is notably good or bad (or all on cooldown)
            }

            string polarity = Polarity(level);
            if (polarity != null)
            {
                string text = ResolveReaction(polarity, PillarKey(pillar));
                if (!string.IsNullOrEmpty(text))
                {
                    ShowReaction(pillar, polarity == "negative", text);
                    _pillarLastTime[pillar] = Time.unscaledTime;
                }
            }
            CaptureTickBaseline();
        }

        private string Polarity(float level) => level >= highLevel ? "positive" : level <= lowLevel ? "negative" : null;

        private bool OnCooldown(Pillar p) =>
            _pillarLastTime.TryGetValue(p, out float t) && Time.unscaledTime - t < perPillarCooldown;

        /// <summary>Distance beyond the low/high threshold; negative when the level sits in the neutral mid-range.</summary>
        private float Extremeness(float level) =>
            level <= lowLevel ? lowLevel - level : level >= highLevel ? level - highLevel : -1f;

        /// <summary>Most extreme pillar by level that is off cooldown. False when every pillar is mid-range or cooling.</summary>
        private bool TryMostExtremePillar(float env, float eco, float saf, float cul, out Pillar pillar, out float level)
        {
            pillar = Pillar.Environment; level = 0f;
            float best = -1f, e;
            e = Extremeness(env); if (e >= 0f && !OnCooldown(Pillar.Environment) && e > best) { best = e; pillar = Pillar.Environment; level = env; }
            e = Extremeness(eco); if (e >= 0f && !OnCooldown(Pillar.Economy) && e > best) { best = e; pillar = Pillar.Economy; level = eco; }
            e = Extremeness(saf); if (e >= 0f && !OnCooldown(Pillar.HealthSafety) && e > best) { best = e; pillar = Pillar.HealthSafety; level = saf; }
            e = Extremeness(cul); if (e >= 0f && !OnCooldown(Pillar.CultureEdu) && e > best) { best = e; pillar = Pillar.CultureEdu; level = cul; }
            return best >= 0f;
        }

        /// <summary>Show a reaction next to the single hub this placement affected the most (biggest move on the
        /// commented pillar). Falls back to the worst/best-level hub when no per-hub delta is measurable, then to
        /// the fixed/shared popup when no play-field root or hub data exists.</summary>
        private void ShowReaction(Pillar pillar, bool negative, string text)
        {
            if (useHubBubbles && contentRoot != null)
            {
                int hub = MostAffectedHub(pillar);
                if (hub < 0) hub = ChooseReactionHub(pillar, negative);
                if (hub >= 0) { SpawnReactionBubble(hub, text); return; }
            }
            ShowPopup(ResolveReactionPopup(), text, reactionPopupSeconds, ref _reactionRoutine);
        }

        /// <summary>Hub whose commented pillar moved the most since the last reaction tick (by magnitude, so a
        /// complaint still anchors to where the action happened). -1 when nothing measurably moved.</summary>
        private int MostAffectedHub(Pillar pillar)
        {
            var hubs = simulationEngine != null ? simulationEngine.HubMetrics : null;
            if (hubs == null || hubs.Count == 0) return -1;

            float[] prev = pillar switch
            {
                Pillar.Environment => _tickHubs.Env,
                Pillar.Economy => _tickHubs.Eco,
                Pillar.HealthSafety => _tickHubs.Saf,
                _ => _tickHubs.Cul
            };

            // Prefer hubs that have not hosted a bubble recently; only they compete here. If every mover is on
            // hub cooldown this returns -1 and the level-based fallback (which may reuse a hub) takes over.
            int best = -1;
            float bestAbs = 0.01f;   // ignore float noise; genuinely unmoved hubs never win
            for (int i = 0; i < hubs.Count; i++)
            {
                if (HubOnCooldown(i)) continue;
                float d = PillarValue(hubs[i], pillar) - SafeGet(prev, i);
                if (Mathf.Abs(d) > bestAbs) { bestAbs = Mathf.Abs(d); best = i; }
            }
            return best;
        }

        private bool HubOnCooldown(int hubIndex) =>
            _hubLastTime.TryGetValue(hubIndex, out float t) && Time.unscaledTime - t < hubRepeatCooldown;

        private void CaptureHubFrame(ref HubFrame frame)
        {
            var hubs = simulationEngine != null ? simulationEngine.HubMetrics : null;
            int n = hubs?.Count ?? 0;
            if (frame.Env == null || frame.Env.Length != n)
            {
                frame.Env = new float[n]; frame.Eco = new float[n];
                frame.Saf = new float[n]; frame.Cul = new float[n];
            }
            for (int i = 0; i < n; i++)
            {
                frame.Env[i] = hubs[i].Environment;
                frame.Eco[i] = hubs[i].Economy;
                frame.Saf[i] = hubs[i].HealthSafety;
                frame.Cul[i] = hubs[i].CultureEdu;
            }
        }

        private static float SafeGet(float[] arr, int i) => arr != null && i < arr.Length ? arr[i] : 0f;

        private int ChooseReactionHub(Pillar pillar, bool negative)
        {
            var hubs = simulationEngine != null ? simulationEngine.HubMetrics : null;
            if (hubs == null || hubs.Count == 0) return -1;
            // Two-track scan: best hub off cooldown wins; if every hub is cooling, take the best overall
            // rather than dropping the bubble entirely.
            int bestFree = -1, bestAny = -1;
            float bestFreeVal = negative ? float.MaxValue : float.MinValue;
            float bestAnyVal = negative ? float.MaxValue : float.MinValue;
            for (int i = 0; i < hubs.Count; i++)
            {
                float v = PillarValue(hubs[i], pillar);
                if (negative ? v < bestAnyVal : v > bestAnyVal) { bestAnyVal = v; bestAny = i; }
                if (!HubOnCooldown(i) && (negative ? v < bestFreeVal : v > bestFreeVal)) { bestFreeVal = v; bestFree = i; }
            }
            return bestFree >= 0 ? bestFree : bestAny;
        }

        private static float PillarValue(SimulationEngine.HubMetricSnapshot h, Pillar p) => p switch
        {
            Pillar.Environment => h.Environment,
            Pillar.Economy => h.Economy,
            Pillar.HealthSafety => h.HealthSafety,
            Pillar.CultureEdu => h.CultureEdu,
            _ => h.Qol
        };

        private static string PillarKey(Pillar p) => p switch
        {
            Pillar.Environment => "environment",
            Pillar.Economy => "economy",
            Pillar.HealthSafety => "safety",
            Pillar.CultureEdu => "culture",
            _ => "environment"
        };

        /// <summary>Prefer the expanded .v2 line; fall back to the base reaction key if .v2 is absent.</summary>
        private string ResolveReaction(string polarity, string pillarKey)
        {
            string v2Key = $"reaction.{polarity}.{pillarKey}.v2";
            string v2 = GetString(v2Key);
            if (!string.IsNullOrEmpty(v2) && v2 != v2Key) return v2;
            return GetString($"reaction.{polarity}.{pillarKey}");
        }

        // ---- end-screen report ----

        private void HandleTimerEnded()
        {
            if (endScreen == null || simulationEngine == null) return;

            // Balance card: the spread between the strongest and weakest pillar decides the verdict.
            float env = simulationEngine.Environment, eco = simulationEngine.Economy;
            float saf = simulationEngine.HealthSafety, cul = simulationEngine.CultureEdu;
            float spread = Mathf.Max(Mathf.Max(env, eco), Mathf.Max(saf, cul))
                         - Mathf.Min(Mathf.Min(env, eco), Mathf.Min(saf, cul));
            float mean = (env + eco + saf + cul) / 4f;
            // An undeveloped city has zero spread but deserves encouragement, not praise for "balance".
            string balance = mean < minBalancePraiseLevel ? GetString("feedback.low")
                           : spread <= balancedSpreadMax ? GetString("feedback.high")
                           : spread <= unevenSpreadMax ? GetString("feedback.mid")
                           : GetString("feedback.low");

            // Strategic card: connectivity. A building that never linked to the network contributed nothing,
            // so any stranded (disconnected, on-water or overlapping) tile flips the card to the complaint.
            var states = simulationEngine.TileStates;
            int stranded = 0;
            for (int i = 0; i < states.Count; i++)
                if (states[i].Inactive || states[i].OverlapInvalid || !states[i].Connected) stranded++;
            string strategic = states.Count > 0 && stranded == 0
                ? GetString("reaction.positive.access.v2")
                : GetString("reaction.negative.access.v2");

            endScreen.SetReport(balance, strategic);
        }

        // ===================== shared helpers =====================

        private TutorialPopup ResolveBandPopup() => bandPopup != null ? bandPopup : FirstPopup();
        private TutorialPopup ResolveReactionPopup() => reactionPopup != null ? reactionPopup : FirstPopup();
        private TutorialPopup FirstPopup() => popups != null && popups.Length > 0 ? popups[0] : null;

        private string GetString(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            return localization != null ? localization.GetString(key) : key;
        }

        private void ShowPopup(TutorialPopup popup, string text, float seconds, ref Coroutine routine)
        {
            if (popup == null || string.IsNullOrEmpty(text)) return;
            if (routine != null) StopCoroutine(routine);
            routine = StartCoroutine(PopupRoutine(popup, text, seconds));
        }

        private IEnumerator PopupRoutine(TutorialPopup popup, string text, float seconds)
        {
            popup.SetText(text);
            Tween show = popup.PlayShowTween();
            if (show != null) yield return show.WaitForCompletion(true);
            yield return new WaitForSeconds(seconds);
            popup.PlayHideTween();
        }

        // ---- hub-positioned reaction bubble (runtime-built, no prefab) ----

        private void SpawnReactionBubble(int hubIndex, string text)
        {
            // Prefer the hub's real visual transform (space-mismatch proof); engine coords only as fallback.
            bool viaRegistry = TryGetHubLocal(hubIndex, out Vector3 hubLocal);
            if (!viaRegistry)
            {
                var positions = simulationEngine != null ? simulationEngine.HubPositions : null;
                Vector2 enginePos = positions != null && hubIndex >= 0 && hubIndex < positions.Count
                    ? positions[hubIndex] : Vector2.zero;
                hubLocal = enginePos;
            }

            DestroyCurrentBubble();

            var go = new GameObject("ReactionBubble", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(contentRoot, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0f);   // grow upward from the hub point

            var cg = go.AddComponent<CanvasGroup>();
            cg.blocksRaycasts = false;
            cg.interactable = false;

            var bg = go.AddComponent<Image>();
            bg.sprite = GetOrBuildBubbleSprite();
            bg.type = Image.Type.Sliced;
            bg.color = bubbleBackColor;
            bg.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform));
            var trt = (RectTransform)textGo.transform;
            trt.SetParent(rt, false);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = bubbleFontSize;
            tmp.color = bubbleTextColor;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;   // TMP word-wraps by default; rect width below constrains it

            float maxTextWidth = Mathf.Max(40f, bubbleMaxWidth - 2f * bubblePadding);
            Vector2 pref = tmp.GetPreferredValues(text, maxTextWidth, 0f);
            float textW = Mathf.Min(pref.x, maxTextWidth);
            rt.sizeDelta = new Vector2(textW + 2f * bubblePadding, pref.y + 2f * bubblePadding);

            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(bubblePadding, bubblePadding);
            trt.offsetMax = new Vector2(-bubblePadding, -bubblePadding);

            if (viaRegistry)
                rt.localPosition = hubLocal + new Vector3(0f, bubbleHubGap, 0f);
            else
                rt.anchoredPosition = (Vector2)hubLocal + new Vector2(0f, bubbleHubGap);

            _hubLastTime[hubIndex] = Time.unscaledTime;
            _currentBubble = go;
            if (_bubbleRoutine != null) StopCoroutine(_bubbleRoutine);
            _bubbleRoutine = StartCoroutine(BubbleRoutine(rt, cg, go));
        }

        private IEnumerator BubbleRoutine(RectTransform rt, CanvasGroup cg, GameObject go)
        {
            Vector2 startPos = rt.anchoredPosition;
            cg.alpha = 0f;
            rt.localScale = Vector3.one * 0.88f;

            var inSeq = DOTween.Sequence().SetTarget(rt).SetUpdate(true);
            inSeq.Join(cg.DOFade(1f, 0.28f).SetEase(Ease.OutQuad));
            inSeq.Join(rt.DOScale(1f, 0.28f).SetEase(Ease.OutBack));
            rt.DOAnchorPos(startPos + new Vector2(0f, bubbleRise), bubbleSeconds).SetEase(Ease.OutCubic).SetUpdate(true).SetTarget(rt);

            yield return new WaitForSecondsRealtime(bubbleSeconds);

            if (rt != null)
            {
                var outSeq = DOTween.Sequence().SetTarget(rt).SetUpdate(true);
                outSeq.Join(cg.DOFade(0f, 0.3f).SetEase(Ease.InQuad));
                outSeq.Join(rt.DOScale(0.9f, 0.3f).SetEase(Ease.InQuad));
                yield return outSeq.WaitForCompletion(true);
            }

            if (go != null) Destroy(go);
            if (_currentBubble == go) _currentBubble = null;
            _bubbleRoutine = null;
        }

        /// <summary>Local position (in contentRoot space) of the hub's actual visual transform. Sidesteps any
        /// mismatch between engine hub coordinates and UI anchors: wherever the hub renders, the bubble follows.
        /// Valid because the coordinator feeds SetScoringHubs by iterating HubRegistry.Hubs in order, so engine
        /// hub index i == registry hub i.</summary>
        private bool TryGetHubLocal(int hubIndex, out Vector3 local)
        {
            local = Vector3.zero;
            if (hubRegistry == null || contentRoot == null) return false;
            var hubs = hubRegistry.Hubs;
            if (hubs == null || hubIndex < 0 || hubIndex >= hubs.Count || hubs[hubIndex] == null) return false;
            local = contentRoot.InverseTransformPoint(hubs[hubIndex].transform.position);
            local.z = 0f;
            return true;
        }

        private void DestroyCurrentBubble()
        {
            if (_bubbleRoutine != null) { StopCoroutine(_bubbleRoutine); _bubbleRoutine = null; }
            if (_currentBubble != null)
            {
                _currentBubble.transform.DOKill(false);
                Destroy(_currentBubble);
                _currentBubble = null;
            }
        }

        /// <summary>Procedural rounded-rect sprite (cached), 9-sliced so the bubble corners stay crisp at any size.</summary>
        private static Sprite GetOrBuildBubbleSprite()
        {
            if (_bubbleSprite != null) return _bubbleSprite;
            const int size = 48;
            const float radius = 16f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(radius - x, x - (size - 1 - radius), 0f);
                    float dy = Mathf.Max(radius - y, y - (size - 1 - radius), 0f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(radius - d + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            var border = new Vector4(radius, radius, radius, radius);
            _bubbleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            _bubbleSprite.name = "ReactionBubble_Rounded";
            return _bubbleSprite;
        }
    }
}
