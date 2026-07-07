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

        [Header("Round-intro reveal (hubs pop → roads draw → particles flow)")]
        [Tooltip("Delay after the start screen closes before the first hub pops.")]
        [SerializeField] private float revealStartDelay = 0.5f;
        [Tooltip("Seconds for the road network to fade in after the hubs.")]
        [SerializeField] private float revealLinesSeconds = 1.0f;
        [Tooltip("Seconds for the flow particles to fade in after the roads.")]
        [SerializeField] private float revealParticlesSeconds = 1.0f;
        [Tooltip("Road renderer whose holder is faded during the reveal. Auto-resolved.")]
        [SerializeField] private HubConnectionRenderer connectionRenderer;

        [Header("Step choreography")]
        [Tooltip("Dashboard used for the metric glow steps. Auto-resolved.")]
        [SerializeField] private DashboardController dashboard;
        [Tooltip("Where the placement-hint ripple circles, as a fraction of the play area's half-height below center.")]
        [SerializeField] private float rippleHintYFraction = 0.75f;
        [Tooltip("Horizontal offset of the hint spot as a fraction of the play area's half-width (negative = left).")]
        [SerializeField] private float rippleHintXFraction = -0.05f;
        [Tooltip("Extra downward shift of the hint spot in play-field pixels.")]
        [SerializeField] private float rippleHintYExtraPx = 30f;
        [SerializeField] private float rippleHintRadius = 30f;
        [Tooltip("Radians/second the ripple hint orbits at (movement is what makes the wake visible).")]
        [SerializeField] private float rippleHintSpeed = 2.5f;

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
        [Tooltip("Bubble background sprite for hub tips. When set, wins over copying the robot popup's bubble style.")]
        [SerializeField] private Sprite bubbleSpriteOverride;

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

        [Tooltip("Optional SKIP button shown while the tutorial runs; hidden the rest of the round.")]
        [SerializeField] private GameObject skipButton;
        [Tooltip("UI hidden on the start screen (building palette pins etc.) and slowly faded in when the round begins.")]
        [SerializeField] private CanvasGroup[] revealOnGameStart;
        [Tooltip("Keep the palette pins visible (but inert) on the start screen instead of hiding them until the round begins.")]
        [SerializeField] private bool showPinsOnStartScreen = true;
        [SerializeField] private float revealFadeSeconds = 1.4f;
        [SerializeField] private float revealFadeDelay = 0.8f;

        public event Action OnTutorialComplete;
        public bool IsRunning => _isRunning;

        /// <summary>Over-budget placement feedback, spoken by the bottom-right robot popup like every
        /// other tip. Important enough to bypass the minimum tip gap.</summary>
        public void ShowBudgetDepletedTip()
        {
            if (_isRunning) return; // never cut into the tutorial choreography
            string text = GetString("ui.budgetDepleted");
            if (string.IsNullOrEmpty(text) || text == "ui.budgetDepleted") return;
            DestroyCurrentBubble();
            if (_bandRoutine != null) StopCoroutine(_bandRoutine);
            _bandRoutine = StartCoroutine(TipChunksRoutine(ResolveBandPopup(), new List<string> { text }));
        }

        /// <summary>Player pressed SKIP: abandon the choreography and hand over the game immediately.</summary>
        public void SkipTutorial()
        {
            if (!_isRunning) return;
            if (_sequenceRoutine != null) { StopCoroutine(_sequenceRoutine); _sequenceRoutine = null; }
            if (_interruptRoutine != null) { StopCoroutine(_interruptRoutine); _interruptRoutine = null; }
            HideAll();
            RestoreRevealVisuals();
            FinishTutorial(); // unpauses the clock, unlocks the table, fires OnTutorialComplete
        }

        /// <summary>While true the coordinator ignores tile placements/moves. Blocked by default from
        /// round start (the language-select tile must not spawn a building), unblocked when the
        /// tutorial's placement-gate step opens, re-blocked after the first tile lands until the
        /// tutorial finishes, then free for the whole round.</summary>
        public bool TileEditsBlocked { get; private set; } = true;

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

            if (connectionRenderer == null) connectionRenderer = GetComponentInChildren<HubConnectionRenderer>(true) ?? GetComponentInParent<HubConnectionRenderer>();
            if (dashboard == null) dashboard = GetComponentInChildren<DashboardController>(true) ?? GetComponentInParent<DashboardController>();

            if (popups == null || popups.Length == 0)
                popups = GetComponentsInChildren<TutorialPopup>(true);

            HideAll();
            HideGameStartUi(); // start screen first: pins/skip appear when the round begins
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

            // Round begins: the palette pins and SKIP ease in instead of popping into existence.
            if (skipButton != null)
            {
                skipButton.SetActive(true);
                var cg = skipButton.GetComponent<CanvasGroup>();
                if (cg == null) cg = skipButton.AddComponent<CanvasGroup>();
                DOTween.Kill(cg);
                cg.alpha = 0f;
                cg.DOFade(1f, revealFadeSeconds).SetDelay(revealFadeDelay + 0.6f).SetUpdate(true).SetTarget(cg);
            }
            if (revealOnGameStart != null)
            {
                foreach (var cg in revealOnGameStart)
                {
                    if (cg == null) continue;
                    DOTween.Kill(cg);
                    cg.blocksRaycasts = true;
                    if (showPinsOnStartScreen)
                    {
                        cg.alpha = 1f; // pins were already visible on the welcome screen
                    }
                    else
                    {
                        cg.alpha = 0f;
                        cg.DOFade(1f, revealFadeSeconds).SetDelay(revealFadeDelay).SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(cg);
                    }
                }
            }

            SetTutorialEyeCandy(true); // roads dim, particles play big while the table is locked
            _sequenceRoutine = StartCoroutine(RunSequence(0));
        }

        /// <summary>Hide the game-start UI (palette pins, skip) — called while the start screen owns
        /// the stage. With showPinsOnStartScreen the pins stay visible but inert instead.</summary>
        private void HideGameStartUi()
        {
            if (revealOnGameStart != null)
            {
                foreach (var cg in revealOnGameStart)
                {
                    if (cg == null) continue;
                    DOTween.Kill(cg);
                    cg.alpha = showPinsOnStartScreen ? 1f : 0f;
                    cg.blocksRaycasts = false;
                }
            }
        }

        public void StopTutorial()
        {
            if (_sequenceRoutine != null) { StopCoroutine(_sequenceRoutine); _sequenceRoutine = null; }
            if (_interruptRoutine != null) { StopCoroutine(_interruptRoutine); _interruptRoutine = null; }
            _isRunning = false;
            StopRippleHint();
            RestoreRevealVisuals();
            SetTutorialEyeCandy(false);
            ClearStaging();
            if (skipButton != null) skipButton.SetActive(false);
            HideGameStartUi(); // back to the start screen: pins hide until the next round begins
            TileEditsBlocked = true; // back to the start screen: the table locks for the next round
            sessionTimer?.SetPaused(false);
            HideAll();
            bandPopup?.HideImmediate();
        }

        private IEnumerator RunSequence(int startIndex)
        {
            _isRunning = true;
            HideAll();

            // Hold the round clock through the whole guided intro; play begins at "The game is on!".
            sessionTimer?.SetPaused(true);

            // Beat 0: the city reveals itself — hubs pop, roads draw, traffic starts flowing.
            if (startIndex == 0)
                yield return CityRevealRoutine();

            var steps = configLoader?.Config?.Tutorial?.steps;
            if (steps == null || steps.Length == 0 || popups == null || popups.Length == 0)
            {
                Debug.LogWarning("[TutorialSequence] No tutorial steps or popups configured - skipping tutorial.");
                FinishTutorial();
                yield break;
            }

            // One popup for the whole tutorial: the robot enters once and stays; only the
            // speech bubble animates between steps.
            var popup = popups[0];
            bool avatarShown = false;

            for (int i = Mathf.Clamp(startIndex, 0, steps.Length); i < steps.Length; i++)
            {
                _currentStepIndex = i;
                var step = steps[i];
                string action = step.action ?? string.Empty;

                string text = localization != null ? localization.GetString(step.textKey) : step.textKey;
                popup.SetText(text);

                // Each step change ripples through the city — the scene stays alive while the
                // android talks, so onlookers see the map "listening" rather than a frozen screen.
                PulseCenters();

                bool gate = action == "placementGate";
                if (gate)
                {
                    _gateTilePlaced = false;
                    TileEditsBlocked = false; // the table opens exactly when we ask for the first tile
                    StartRippleHint();
                }

                if (!avatarShown && popup.HasSplitParts)
                {
                    // The assistant walks in first, beat, then speaks.
                    var avatarTween = popup.PlayAvatarShowTween();
                    if (avatarTween != null) yield return avatarTween.WaitForCompletion(true);
                    yield return new WaitForSeconds(0.5f);
                    avatarShown = true;
                }

                var showTween = popup.HasSplitParts ? popup.PlayBubbleShowTween() : popup.PlayShowTween();
                if (showTween != null) yield return showTween.WaitForCompletion(true);

                // Step visual: fire alongside the text, once the popup has landed.
                // Staging: only the element(s) the robot talks about stay lit on the bar.
                // Glow always rides a value animation: the badge sweeps while it blooms.
                if (action == "scoreGlow" && dashboard != null)
                {
                    StageFocus("Quality Of Life");
                    dashboard.HighlightPillar(DashboardController.Pillar.Qol, 1.8f);
                    dashboard.PlayQolMaxDemo(1.8f);
                }
                else if (action == "hubSpawn")
                {
                    // The city presents itself: centers spawn in while the robot talks about them.
                    StartCoroutine(SpawnHubsShowRoutine());
                }

                float duration = step.durationSeconds > 0 ? step.durationSeconds : 5f;

                if (action == "categoryGlow" && dashboard != null)
                {
                    // Walk the four categories, half a second of glow each; the stage light
                    // follows the category currently being demonstrated, and every city
                    // center's matching ring arc sweeps in sync — bar and ring are the
                    // same stat, shown as one gesture.
                    var pillars = new[]
                    {
                        (DashboardController.Pillar.Environment, "Environment", 0),
                        (DashboardController.Pillar.Economy, "Economy", 1),
                        (DashboardController.Pillar.HealthSafety, "Safety", 2),
                        (DashboardController.Pillar.CultureEdu, "Culture", 3)
                    };
                    foreach (var (p, groupName, ringQuadrant) in pillars)
                    {
                        StageFocus(groupName);
                        dashboard.HighlightPillar(p, 1.2f);
                        dashboard.PlayPillarMaxDemo(p, 1.2f); // bar sweeps to full and back
                        if (hubRegistry != null && hubRegistry.Hubs != null)
                            foreach (var h in hubRegistry.Hubs)
                                if (h != null) h.PlayRingDemo(ringQuadrant, 1.2f);
                        yield return new WaitForSeconds(0.85f);
                    }
                    // Rest of the step: all four categories hold the stage together.
                    StageFocus("Environment", "Economy", "Safety", "Culture");
                    float rest = duration - pillars.Length * 0.85f;
                    if (rest > 0f) yield return new WaitForSeconds(rest);
                }
                else if (action == "qolDemo" && dashboard != null)
                {
                    // QOL sweeps to max and back while Budget, then Time, blink in turn —
                    // sequential so each readout gets its own beat of attention.
                    StageFocus("Quality Of Life", "Budget", "Timer");
                    float sweep = Mathf.Min(3f, duration - 0.5f);
                    dashboard.PlayQolMaxDemo(sweep);
                    dashboard.HighlightPillar(DashboardController.Pillar.Qol, sweep); // glow rides the sweep
                    StartCoroutine(BudgetThenTimerBlink());
                    yield return new WaitForSeconds(duration);
                }
                else
                {
                    // Talk/map/spawn steps: the whole bar stays lit — the base stage is
                    // "centers off, network animated, top menu on"; only the stat steps
                    // narrow the light to their subject.
                    if (action != "scoreGlow") ClearStaging();
                    yield return new WaitForSeconds(duration);
                }

                if (gate)
                {
                    // Hold this step (text + ripple hint) until the first tile lands on the table.
                    while (!_gateTilePlaced) yield return null;
                    StopRippleHint();
                    TileEditsBlocked = true; // freeze the table for the remaining guided steps
                    yield return new WaitForSeconds(0.8f); // let the placement feedback breathe
                }

                // Between steps only the bubble swaps; the robot leaves with the final step.
                bool lastStep = i == steps.Length - 1;
                var hideTween = !lastStep && popup.HasSplitParts ? popup.PlayBubbleHideTween() : popup.PlayHideTween();
                if (hideTween != null) yield return hideTween.WaitForCompletion(true);
                yield return new WaitForSeconds(0.35f); // beat between bubbles
            }

            FinishTutorial();
        }

        private void FinishTutorial()
        {
            _isRunning = false;
            _sequenceRoutine = null;
            StopRippleHint();
            SetTutorialEyeCandy(false); // roads back to full, particles back to city-driven
            RestoreHiddenHubs();        // safety net if no step ever spawned the centers
            ClearStaging();             // full top bar for live play
            if (skipButton != null) skipButton.SetActive(false);
            TileEditsBlocked = false;
            sessionTimer?.SetPaused(false); // the game is on — clock starts now
            OnTutorialComplete?.Invoke();
        }

        private void HideAll()
        {
            if (popups == null) return;
            foreach (var p in popups)
                if (p != null) p.HideImmediate();
        }

        // ===================== Round-intro reveal =====================

        private bool _gateTilePlaced;
        private GameObject _rippleHint;
        private GameObject _rippleMarker;
        private Coroutine _rippleRoutine;
        private static Sprite _ringSprite;

        [Tooltip("Color of the small glowing ring marking the placement-hint spot.")]
        [SerializeField] private Color rippleMarkerColor = new Color(0.35f, 0.9f, 1f, 0.9f);
        [Tooltip("Diameter of the placement-hint ring in play-field pixels.")]
        [SerializeField] private float rippleMarkerSize = 52f;
        private readonly List<(Transform t, Vector3 scale)> _revealHubScales = new List<(Transform, Vector3)>();

        /// <summary>Round-intro reveal: the road network draws in and traffic starts flowing — but
        /// the city centers stay hidden. They are the payoff of the tutorial's "hubSpawn" step:
        /// spawning them while the robot introduces the city guides the eye far better than
        /// lighting up something that was already there.</summary>
        private IEnumerator CityRevealRoutine()
        {
            var holder = connectionRenderer != null ? connectionRenderer.RoadHolder : null;
            CanvasGroup roadsGroup = null;
            if (holder != null)
            {
                roadsGroup = holder.GetComponent<CanvasGroup>();
                if (roadsGroup == null) roadsGroup = holder.gameObject.AddComponent<CanvasGroup>();
                roadsGroup.alpha = 0f;
            }

            var particles = GetComponentsInChildren<ConnectionFlowParticles>(true);
            foreach (var p in particles) if (p != null) p.RevealFade = 0f;

            // Hubs are created async from config; wait briefly for them.
            float deadline = Time.time + 5f;
            while ((hubRegistry == null || hubRegistry.Hubs == null || hubRegistry.Hubs.Count == 0) && Time.time < deadline)
                yield return null;

            _revealHubScales.Clear();
            var hubs = hubRegistry != null ? hubRegistry.Hubs : null;
            if (hubs != null)
            {
                foreach (var h in hubs)
                {
                    if (h == null) continue;
                    Vector3 s = h.transform.localScale;
                    if (s.sqrMagnitude < 0.0001f) s = Vector3.one; // never capture a hidden scale as base
                    _revealHubScales.Add((h.transform, s));
                    h.transform.localScale = Vector3.zero; // held until the hubSpawn step (or restore)
                }
            }

            yield return new WaitForSeconds(revealStartDelay);

            // Beat 1: the road network draws in.
            if (roadsGroup != null)
            {
                var fade = roadsGroup.DOFade(1f, revealLinesSeconds).SetEase(Ease.InOutSine).SetTarget(roadsGroup);
                yield return fade.WaitForCompletion(true);
            }

            // Beat 2: traffic starts flowing (re-grab: the pool may have grown while we waited).
            particles = GetComponentsInChildren<ConnectionFlowParticles>(true);
            for (float t = 0f; t < revealParticlesSeconds; t += Time.deltaTime)
            {
                float f = Mathf.Clamp01(t / revealParticlesSeconds);
                foreach (var p in particles) if (p != null) p.RevealFade = f;
                yield return null;
            }
            foreach (var p in particles) if (p != null) p.RevealFade = 1f;
            // _revealHubScales intentionally stays populated: the hubSpawn step consumes it.
        }

        [Tooltip("Seconds between consecutive hub pops when the hubSpawn tutorial step brings the centers in.")]
        [SerializeField] private float hubSpawnStagger = 0.25f;

        /// <summary>qolDemo beat: Budget blinks first, Time follows once it settles.</summary>
        private IEnumerator BudgetThenTimerBlink()
        {
            dashboard.HighlightBudget(1.2f);
            yield return new WaitForSeconds(1.6f);
            dashboard.HighlightTimer(1.2f);
        }

        /// <summary>"hubSpawn" step action: the city centers pop in one by one, each with a glow
        /// pulse and a liquid splash, while the robot introduces the city.</summary>
        private IEnumerator SpawnHubsShowRoutine()
        {
            var pending = new List<(Transform t, Vector3 s)>(_revealHubScales);
            _revealHubScales.Clear();
            var liquid = GetComponent<LiquidSurface>() ?? GetComponentInParent<LiquidSurface>();
            foreach (var (t, s) in pending)
            {
                if (t == null) continue;
                t.DOKill(false);
                t.DOScale(s, 0.55f).SetEase(Ease.OutBack).SetUpdate(true).SetTarget(t);
                var hub = t.GetComponent<ResidentialHubMono>();
                if (hub != null) PulseCenterGlow(hub, 0.15f);
                // No ring demo here: the arcs animate only once, during the category
                // examples, so the spawn beat stays clean.
                liquid?.Splash(t.position, 2.2f, 1.4f, 1f);
                yield return new WaitForSeconds(hubSpawnStagger);
            }
        }

        /// <summary>Any hub still waiting for its spawn step pops in now — covers skip, stop, and
        /// configs without a hubSpawn action, so the round never starts with invisible centers.</summary>
        private void RestoreHiddenHubs()
        {
            foreach (var (t, s) in _revealHubScales)
            {
                if (t == null) continue;
                t.DOKill(false);
                t.DOScale(s, 0.4f).SetEase(Ease.OutBack).SetUpdate(true).SetTarget(t);
            }
            _revealHubScales.Clear();
        }

        [Tooltip("HDR glow peak the city centers reach at the top of a life-sign pulse (must clear the bloom threshold, ~2.24).")]
        [SerializeField] private float centerPulseGlowPeak = 3f;

        [Header("Tutorial eye candy")]
        [Tooltip("Road network alpha while the tutorial talks. 1 = network stays fully lit (it IS the show while the centers are off); lower to dim it.")]
        [Range(0.05f, 1f)] [SerializeField] private float tutorialRoadDim = 1f;
        [Tooltip("Flow particle size multiplier during the tutorial.")]
        [SerializeField] private float tutorialParticleSize = 1.6f;
        [Tooltip("Particles behave as if the city were this built-up during the tutorial. 1 = fully built city; above 1 over-drives size and speed beyond the normal maximum.")]
        [Range(0f, 10f)] [SerializeField] private float tutorialParticleActivity = 1f;
        [Tooltip("Seconds before the road dim kicks in, so it never fights the round-intro reveal fade.")]
        [SerializeField] private float roadDimDelay = 4f;
        [Tooltip("Map background brightness while the table is locked during the tutorial (1 = no dim). Brightens the moment placements are allowed, so the dim doubles as a 'may I place?' signal.")]
        [Range(0.1f, 1f)] [SerializeField] private float tutorialMapDim = 0.45f;
        [Tooltip("Alpha of top-bar groups the robot is NOT talking about during a tutorial step. The subject of each step stays at full brightness; 1 disables staging.")]
        [Range(0f, 1f)] [SerializeField] private float unfocusedGroupAlpha = 0.15f;
        [Tooltip("Seconds for staging focus changes to fade.")]
        [SerializeField] private float stageFadeSeconds = 0.5f;
        [Tooltip("The map background image that gets dimmed. Auto-resolved (the Sector_0 RawImage) when empty.")]
        [SerializeField] private Graphic tutorialMapGraphic;

        private bool _mapDimmed;

        /// <summary>Dim tracks the table lock: dark while TileEditsBlocked during the tutorial,
        /// full brightness whenever the player may place. Polled cheaply from LateUpdate.</summary>
        private void UpdateMapDim()
        {
            bool want = _isRunning && TileEditsBlocked;
            if (want == _mapDimmed) return;
            _mapDimmed = want;

            var g = ResolveMapGraphic();
            if (g == null) return;
            DOTween.Kill(g);
            float v = want ? tutorialMapDim : 1f;
            g.DOColor(new Color(v, v, v, g.color.a), 0.8f).SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(g);
        }

        private Graphic ResolveMapGraphic()
        {
            if (tutorialMapGraphic != null) return tutorialMapGraphic;
            // "Main BG" carries the map art (HubLayoutManager assigns the background sprite to it);
            // Sector_0 is just a black backdrop, so dimming that would be invisible.
            foreach (var g in GetComponentsInChildren<Image>(true))
                if (g.name == "Main BG") { tutorialMapGraphic = g; break; }
            return tutorialMapGraphic;
        }

        // ---- staging: only the elements the robot talks about stay lit ----

        private readonly Dictionary<string, CanvasGroup> _topBarGroups = new Dictionary<string, CanvasGroup>();

        private void CacheTopBarGroups()
        {
            if (_topBarGroups.Count > 0) return;
            Transform topBar = null;
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.name == "Top Bar UI") { topBar = t; break; }
            if (topBar == null) return;
            foreach (Transform group in topBar)
            {
                var cg = group.GetComponent<CanvasGroup>();
                if (cg == null) cg = group.gameObject.AddComponent<CanvasGroup>();
                _topBarGroups[group.name] = cg;
            }
        }

        /// <summary>Fade every top-bar group down except the named ones — the robot's current
        /// subjects. Call with no names to dim the whole bar (map/tile talk); ClearStaging restores.</summary>
        private void StageFocus(params string[] focusNames)
        {
            if (unfocusedGroupAlpha >= 0.999f) return; // staging disabled
            CacheTopBarGroups();
            foreach (var kv in _topBarGroups)
            {
                bool focused = false;
                if (focusNames != null)
                    for (int i = 0; i < focusNames.Length; i++)
                        if (kv.Key == focusNames[i]) { focused = true; break; }
                var cg = kv.Value;
                if (cg == null) continue;
                DOTween.Kill(cg);
                cg.DOFade(focused ? 1f : unfocusedGroupAlpha, Mathf.Max(0.05f, stageFadeSeconds))
                  .SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(cg);
            }
        }

        private void ClearStaging()
        {
            foreach (var kv in _topBarGroups)
            {
                var cg = kv.Value;
                if (cg == null) continue;
                DOTween.Kill(cg);
                cg.DOFade(1f, Mathf.Max(0.05f, stageFadeSeconds)).SetEase(Ease.InOutSine).SetUpdate(true).SetTarget(cg);
            }
        }

        private void LateUpdate()
        {
            UpdateMapDim();
        }

        /// <summary>Dim the roads and let the flow particles play big while the table is locked.
        /// Everything restores to normal the moment the round opens (finish, skip, or stop).</summary>
        private void SetTutorialEyeCandy(bool on)
        {
            var holder = connectionRenderer != null ? connectionRenderer.RoadHolder : null;
            if (holder != null)
            {
                var cg = holder.GetComponent<CanvasGroup>();
                if (cg == null) cg = holder.gameObject.AddComponent<CanvasGroup>();
                DOTween.Kill(cg);
                cg.DOFade(on ? tutorialRoadDim : 1f, 0.8f)
                  .SetDelay(on ? roadDimDelay : 0f)
                  .SetEase(Ease.InOutSine)
                  .SetUpdate(true)
                  .SetTarget(cg);
            }

            foreach (var p in GetComponentsInChildren<ConnectionFlowParticles>(true))
            {
                if (p == null) continue;
                p.EyeCandySize = on ? tutorialParticleSize : 1f;
                p.ActivityFloor = on ? tutorialParticleActivity : 0f;
            }
        }

        /// <summary>Quick life-sign across the map: every hub does a small staggered scale pop and
        /// kicks a gentle ripple into the liquid surface. Used on tutorial step changes.</summary>
        private void PulseCenters()
        {
            var hubs = hubRegistry != null ? hubRegistry.Hubs : null;
            if (hubs == null || hubs.Count == 0) return;
            var liquid = GetComponent<LiquidSurface>() ?? GetComponentInParent<LiquidSurface>();

            float delay = 0f;
            foreach (var h in hubs)
            {
                if (h == null) continue;
                var t = h.transform;
                if (t.localScale.sqrMagnitude < 0.0001f) continue; // mid-reveal, leave it alone

                t.DOKill(true); // complete any running pop so scales never compound
                Vector3 baseScale = t.localScale;
                t.DOPunchScale(baseScale * 0.12f, 0.5f, 1, 0.6f)
                    .SetDelay(delay)
                    .SetUpdate(true)
                    .SetTarget(t);
                PulseCenterGlow(h, delay);
                liquid?.Splash(t.position, 1.6f, 0.9f, 0.7f);
                delay += 0.08f;
            }
        }

        /// <summary>HDR flash riding the life-sign pop: each hub graphic sweeps its glow boost above 1
        /// so the bloom post pass halos the center while it blinks. UIGlow rests at 1 (visually
        /// identical to no glow) and is added lazily the first time a hub pulses.</summary>
        private void PulseCenterGlow(ResidentialHubMono hub, float delay)
        {
            foreach (var g in hub.GetComponentsInChildren<Graphic>(true))
            {
                if (g.GetType().Name.Contains("SVG")) continue; // vector graphics own their material setup

                // Graphics that already own an HDR glow knob (e.g. the procedural stat ring)
                // are pulsed through it; adding a UIGlow on top would fight their material.
                IGlowBoost boost = g.GetComponent<IGlowBoost>();
                MonoBehaviour owner = boost as MonoBehaviour;
                if (boost == null)
                {
                    var glow = g.gameObject.AddComponent<UIGlow>();
                    glow.glowBoost = 1f; // neutral at rest: centers only glow while blinking
                    boost = glow;
                    owner = glow;
                }

                owner.DOKill(false);
                float baseVal = boost.BaseGlowBoost;
                float peak = Mathf.Max(centerPulseGlowPeak, baseVal);
                DOTween.Sequence().SetTarget(owner).SetUpdate(true).SetDelay(delay)
                    .Append(DOTween.To(() => boost.GlowBoost, v => boost.GlowBoost = v, peak, 0.18f).SetEase(Ease.OutQuad))
                    .Append(DOTween.To(() => boost.GlowBoost, v => boost.GlowBoost = v, baseVal, 0.32f).SetEase(Ease.InOutSine))
                    .OnComplete(() => boost.GlowBoost = baseVal)
                    .OnKill(() => boost.GlowBoost = baseVal);
            }
        }

        /// <summary>Undo any half-finished reveal (StopTutorial mid-flight): everything fully visible.</summary>
        private void RestoreRevealVisuals()
        {
            foreach (var (t, s) in _revealHubScales)
            {
                if (t == null) continue;
                t.DOKill(false);
                t.localScale = s;
            }
            _revealHubScales.Clear();

            var holder = connectionRenderer != null ? connectionRenderer.RoadHolder : null;
            var cg = holder != null ? holder.GetComponent<CanvasGroup>() : null;
            if (cg != null) { DOTween.Kill(cg); cg.alpha = 1f; }

            foreach (var p in GetComponentsInChildren<ConnectionFlowParticles>(true))
                if (p != null) p.RevealFade = 1f;
        }

        // ===================== Placement-hint ripple =====================

        /// <summary>Invisible interactor circling near the bottom of the play area; the liquid surface
        /// turns its motion into a beckoning ripple ("put a tile here").</summary>
        private void StartRippleHint()
        {
            if (_rippleHint != null || contentRoot == null) return;
            var liquid = GetComponent<LiquidSurface>() ?? GetComponentInParent<LiquidSurface>();
            if (liquid == null) return;

            _rippleHint = new GameObject("TutorialRippleHint");
            var t = _rippleHint.transform;
            t.SetParent(contentRoot, false);
            Vector2 center = new Vector2(
                contentRoot.rect.xMax * Mathf.Clamp(rippleHintXFraction, -1f, 1f),
                contentRoot.rect.yMin * Mathf.Clamp01(rippleHintYFraction) - rippleHintYExtraPx);
            t.localPosition = center;
            liquid.AddInteractor(_rippleHint); // interactor list drops it automatically once destroyed

            SpawnRippleMarker(center);
            _rippleRoutine = StartCoroutine(RippleHintOrbit(t, center));
        }

        /// <summary>Small breathing ring outline (no icon) that marks the hint spot at the ripple's center.</summary>
        private void SpawnRippleMarker(Vector2 center)
        {
            _rippleMarker = new GameObject("TutorialRippleMarker", typeof(RectTransform));
            var rt = (RectTransform)_rippleMarker.transform;
            rt.SetParent(contentRoot, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localPosition = center;
            rt.sizeDelta = new Vector2(rippleMarkerSize, rippleMarkerSize);

            var img = _rippleMarker.AddComponent<Image>();
            img.sprite = GetOrBuildRingSprite();
            img.color = rippleMarkerColor;
            img.raycastTarget = false;

            // gentle breath: scale + alpha, looping until the first tile lands
            rt.localScale = Vector3.one * 0.85f;
            rt.DOScale(1.15f, 0.9f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetTarget(rt);
            img.DOFade(rippleMarkerColor.a * 0.45f, 0.9f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetTarget(img);
        }

        /// <summary>Procedural soft ring outline sprite (cached).</summary>
        private static Sprite GetOrBuildRingSprite()
        {
            if (_ringSprite != null) return _ringSprite;
            const int size = 64;
            const float radius = 26f, thickness = 3.5f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Abs(Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) - radius);
                    float a = Mathf.Clamp01(1f - (d - thickness * 0.5f)); // 1px soft edge
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            _ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _ringSprite.name = "TutorialHintRing";
            return _ringSprite;
        }

        private IEnumerator RippleHintOrbit(Transform t, Vector2 center)
        {
            float a = 0f;
            while (t != null)
            {
                a += Time.deltaTime * rippleHintSpeed;
                t.localPosition = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rippleHintRadius;
                yield return null;
            }
        }

        private void StopRippleHint()
        {
            if (_rippleRoutine != null) { StopCoroutine(_rippleRoutine); _rippleRoutine = null; }
            if (_rippleHint != null) { Destroy(_rippleHint); _rippleHint = null; }
            if (_rippleMarker != null)
            {
                _rippleMarker.transform.DOKill(false);
                var img = _rippleMarker.GetComponent<Image>();
                if (img != null) img.DOKill(false);
                Destroy(_rippleMarker);
                _rippleMarker = null;
            }
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
            _firstSplashDone = false;
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

        private bool _firstSplashDone;

        private void HandleTileSpawned(string engineTileId, string buildingId, GameObject marker)
        {
            // The round's very first building lands with a big, slow ripple — a moment, not a blip.
            if (!_firstSplashDone && marker != null)
            {
                _firstSplashDone = true;
                var liquid = GetComponent<LiquidSurface>() ?? GetComponentInParent<LiquidSurface>();
                liquid?.Splash(marker.transform.position, 3.5f, 3f, 1.8f);
            }

            _gateTilePlaced = true; // releases the tutorial's wait-for-first-tile step
            _lastPlacementTime = Time.time;
            _reminderStreak = 0;    // player is active again: idle nudging starts fresh
            if (!InGameplay) return;
            _anyPlacement = true;   // arms the starting (0-20) band and the timed reactions
            EvaluateBands();
        }

        [Tooltip("Seconds of total tip silence after which the robot gently re-shows the current band's pro-tip (or the place-a-tile nudge before the first placement).")]
        [SerializeField] private float tipReminderSeconds = 30f;
        [Tooltip("Each consecutive reminder without a placement in between waits this much longer, so idle nudging never turns into nagging. Resets on placement.")]
        [SerializeField] private float reminderBackoffSeconds = 20f;
        [Tooltip("Never show two tips (band, reaction or reminder) closer together than this — stops the early game from firing a tip on every single placement.")]
        [SerializeField] private float minTipGapSeconds = 25f;
        [Tooltip("Hub indices that never host reaction bubbles (their bubbles would cover menus/dashboard). Layout-specific.")]
        [SerializeField] private int[] bubbleExcludedHubIndices = new int[0];

        private float _lastTipTime;
        private float _lastPlacementTime;
        private int _reminderStreak;

        private bool IsBubbleExcludedHub(int hubIndex)
        {
            if (bubbleExcludedHubIndices == null) return false;
            for (int i = 0; i < bubbleExcludedHubIndices.Length; i++)
                if (bubbleExcludedHubIndices[i] == hubIndex) return true;
            return false;
        }

        /// <summary>A tip visual (robot popup or hub bubble) is currently on screen.</summary>
        private bool TipVisualActive => _bandRoutine != null || _reactionRoutine != null || _currentBubble != null;

        private void Update()
        {
            // Timed citizen reactions. Hold the clock while the tutorial (or a non-gameplay phase) is up,
            // so the first bubble lands one full interval after play actually starts.
            if (!IsPlaying)
            {
                _nextReactionTime = Time.time + reactionIntervalSeconds;
                _lastTipTime = Time.time;
                return;
            }

            if (Time.time >= _nextReactionTime)
            {
                _nextReactionTime = Time.time + reactionIntervalSeconds;
                // Never talk over an active tip, and keep a minimum breath between tips.
                if (!TipVisualActive && Time.time - _lastTipTime >= minTipGapSeconds)
                    EvaluateTimedReaction();
            }

            // Graceful idle fallback: nudge sooner at first, then back off the longer the idle lasts.
            float reminderWait = tipReminderSeconds + Mathf.Min(_reminderStreak, 3) * reminderBackoffSeconds;
            if (!TipVisualActive
                && Time.time - _lastTipTime > reminderWait
                && Time.time - _lastPlacementTime > tipReminderSeconds)
            {
                _reminderStreak++;
                ShowTipReminder();
            }
        }

        /// <summary>Quiet-period reminder. Before the first placement: the approved place-a-tile nudge.
        /// After: the current score band's pro-tip paragraph (approved text, always contextual).</summary>
        private void ShowTipReminder()
        {
            _lastTipTime = Time.time;
            var popup = ResolveBandPopup();
            if (popup == null) return;

            string text;
            if (!_anyPlacement)
            {
                text = GetString("ui.inactivity"); // "Place a physical tile on the interactive table."
            }
            else
            {
                if (_liveBands == null || _liveBands.Count == 0) BuildLiveBands();
                int idx = simulationEngine != null ? CurrentBandIndex(simulationEngine.Qol) : -1;
                if (idx < 0 || _liveBands.Count == 0) return;
                var chunks = ChunkTipText(GetString(_liveBands[idx].bodyKey), tipChunkMaxChars);
                if (chunks.Count == 0) return;
                // The pro-tip is the last paragraph-chunk of every band text.
                text = chunks[chunks.Count - 1];
            }
            if (string.IsNullOrEmpty(text)) return;

            if (_bandRoutine != null) StopCoroutine(_bandRoutine);
            _bandRoutine = StartCoroutine(TipChunksRoutine(popup, new List<string> { text }));
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

        [Tooltip("Longest text chunk (chars) shown in one popup bubble; longer tip texts are split at sentence boundaries and shown one after another.")]
        [SerializeField] private int tipChunkMaxChars = 170;
        [Tooltip("Extra reading seconds per character of a tip chunk (added to a 2.5s base).")]
        [SerializeField] private float tipSecondsPerChar = 0.05f;
        [Tooltip("Quiet pause between consecutive chunks of one tip, so multi-part tips breathe instead of machine-gunning.")]
        [SerializeField] private float tipChunkGapSeconds = 1.6f;

        private void ShowBand(GameConfig.EndMessageData band)
        {
            string title = GetString(band.titleKey);
            string body = GetString(band.bodyKey);

            // Never cut into the choreographed tutorial; the band re-fires on a later placement if earned.
            if (_isRunning) { _highestBandShown--; return; }

            // Early game crosses a band on nearly every placement — keep a minimum gap so the robot
            // doesn't comment on each tile. A deferred band re-fires on a later placement.
            if (Time.time - _lastTipTime < minTipGapSeconds) { _highestBandShown--; return; }

            DestroyCurrentBubble(); // a band milestone outranks a lingering hub bubble

            // Client tip texts run to several paragraphs — far too much for one bubble. Split into
            // popup-sized chunks and page through them in the same robot popup the tutorial uses.
            var chunks = ChunkTipText(body, tipChunkMaxChars);
            if (chunks.Count == 0) chunks.Add(string.Empty);
            chunks[0] = string.IsNullOrEmpty(title) ? chunks[0] : (title + "\n" + chunks[0]).Trim();

            if (_bandRoutine != null) StopCoroutine(_bandRoutine);
            _bandRoutine = StartCoroutine(TipChunksRoutine(ResolveBandPopup(), chunks));
        }

        /// <summary>Split a long tip text into bubble-sized chunks: paragraphs first, then sentences
        /// packed greedily up to maxChars. Handles both real and literal "\n" escapes.</summary>
        private static List<string> ChunkTipText(string text, int maxChars)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text)) return chunks;
            text = text.Replace("\\n", "\n");

            foreach (var paraRaw in text.Split('\n'))
            {
                string para = paraRaw.Trim();
                if (para.Length == 0) continue;
                if (para.Length <= maxChars) { chunks.Add(para); continue; }

                var current = new System.Text.StringBuilder();
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(para, @"[^.!?…]+[.!?…]+(\s+|$)|[^.!?…]+$"))
                {
                    string sentence = m.Value.Trim();
                    if (sentence.Length == 0) continue;
                    if (current.Length > 0 && current.Length + sentence.Length + 1 > maxChars)
                    {
                        chunks.Add(current.ToString());
                        current.Clear();
                    }
                    if (current.Length > 0) current.Append(' ');
                    current.Append(sentence);
                }
                if (current.Length > 0) chunks.Add(current.ToString());
            }
            return chunks;
        }

        /// <summary>Page through tip chunks in the robot popup: robot enters with the first chunk and
        /// stays while only the bubble swaps, exactly like the tutorial steps.</summary>
        private IEnumerator TipChunksRoutine(TutorialPopup popup, List<string> chunks)
        {
            if (popup == null) yield break;
            for (int i = 0; i < chunks.Count; i++)
            {
                popup.SetText(chunks[i]);

                Tween show;
                if (i == 0 && popup.HasSplitParts)
                {
                    // Signature entrance every time: the android walks in first, beat, then speaks.
                    var avatarTween = popup.PlayAvatarShowTween();
                    if (avatarTween != null) yield return avatarTween.WaitForCompletion(true);
                    yield return new WaitForSeconds(0.45f);
                    show = popup.PlayBubbleShowTween();
                }
                else
                {
                    show = popup.HasSplitParts ? popup.PlayBubbleShowTween() : popup.PlayShowTween();
                }
                if (show != null) yield return show.WaitForCompletion(true);

                float readSeconds = Mathf.Max(bandPopupSeconds * 0.5f, 2.5f + chunks[i].Length * tipSecondsPerChar);
                yield return new WaitForSeconds(readSeconds);

                bool last = i == chunks.Count - 1;
                Tween hide = !last && popup.HasSplitParts ? popup.PlayBubbleHideTween() : popup.PlayHideTween();
                if (hide != null) yield return hide.WaitForCompletion(true);
                if (!last) yield return new WaitForSeconds(tipChunkGapSeconds);
            }
            _lastTipTime = Time.time; // quiet-period clock restarts when the tip finishes
            _bandRoutine = null;
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
            // Robot popup path (hub bubbles off): same bottom-right android with the
            // icon → pause → bubble entrance used by every other tip.
            var chunks = ChunkTipText(text, tipChunkMaxChars);
            if (chunks.Count == 0) return;
            if (_bandRoutine != null) StopCoroutine(_bandRoutine);
            _bandRoutine = StartCoroutine(TipChunksRoutine(ResolveBandPopup(), chunks));
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
                if (HubOnCooldown(i) || IsBubbleExcludedHub(i)) continue;
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
                if (IsBubbleExcludedHub(i)) continue; // bubble would sit over menus/dashboard
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
            // The end report owns the screen: any tip robot, band popup, or citizen bubble that is
            // visible (or animating in) disappears the moment the final screen pops up.
            if (_bandRoutine != null) { StopCoroutine(_bandRoutine); _bandRoutine = null; }
            if (_reactionRoutine != null) { StopCoroutine(_reactionRoutine); _reactionRoutine = null; }
            if (_interruptRoutine != null) { StopCoroutine(_interruptRoutine); _interruptRoutine = null; }
            DestroyCurrentBubble();
            bandPopup?.HideImmediate();
            reactionPopup?.HideImmediate();
            if (popups != null)
                foreach (var p in popups) p?.HideImmediate();

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

            // Budget card: approved "Budget Remaining" label + the live remaining amount.
            string budget = coordinator != null
                ? $"{GetString("report.budgetRemaining")}: {GetString("ui.currency")} {coordinator.Budget}"
                : string.Empty;

            endScreen.SetReport(balance, strategic, budget);
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
            // Bottom-right pivot: the bubble sprite's tail corner sits on the hub and the body
            // grows up-left, matching how the robot popup's bubble points at its speaker.
            rt.pivot = new Vector2(1f, 0f);

            var cg = go.AddComponent<CanvasGroup>();
            cg.blocksRaycasts = false;
            cg.interactable = false;

            var bg = go.AddComponent<Image>();
            // Explicit sprite override wins; otherwise match the robot popup's speech-bubble style.
            var template = FirstPopup() != null ? FirstPopup().BubbleBackgroundImage : null;
            if (bubbleSpriteOverride != null)
            {
                bg.sprite = bubbleSpriteOverride;
                bg.type = Image.Type.Sliced;
                bg.color = Color.white;
            }
            else if (template != null)
            {
                bg.sprite = template.sprite;
                bg.type = template.type;
                bg.color = template.color;
                bg.material = template.material;
                bg.pixelsPerUnitMultiplier = template.pixelsPerUnitMultiplier;
            }
            else
            {
                bg.sprite = GetOrBuildBubbleSprite();
                bg.type = Image.Type.Sliced;
                bg.color = bubbleBackColor;
            }
            bg.raycastTarget = false;

            // Fixed width, dynamic height: same layout recipe as the robot popup's bubble, so any
            // text length fits with even padding instead of the old measure-and-hope sizing.
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            int pad = Mathf.RoundToInt(bubblePadding);
            vlg.padding = new RectOffset(pad, pad, Mathf.RoundToInt(pad * 0.75f), pad);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rt.sizeDelta = new Vector2(bubbleMaxWidth, 0f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(rt, false);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = bubbleFontSize;
            tmp.color = bubbleTextColor;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;   // VLG constrains width; TMP wraps and reports height

            LayoutRebuilder.ForceRebuildLayoutImmediate(rt); // valid height before the first frame

            if (viaRegistry)
                rt.localPosition = hubLocal + new Vector3(0f, bubbleHubGap, 0f);
            else
                rt.anchoredPosition = (Vector2)hubLocal + new Vector2(0f, bubbleHubGap);

            _hubLastTime[hubIndex] = Time.unscaledTime;
            _lastTipTime = Time.time;
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
