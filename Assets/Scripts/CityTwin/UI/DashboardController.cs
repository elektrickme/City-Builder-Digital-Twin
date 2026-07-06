using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using CityTwin.Core;
using CityTwin.Simulation;

/// <summary>Per-instance dashboard: metric bars, QOL, timer, budget. Bind to SimulationEngine, SessionTimer, and budget source. No statics.</summary>
public class DashboardController : MonoBehaviour
{
    [Header("Data sources")]
    [SerializeField] private SimulationEngine simulationEngine;
    [SerializeField] private SessionTimer sessionTimer;
    [SerializeField] private GameInstanceCoordinator coordinator;

    [Header("Top bar")]
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private TextMeshProUGUI budgetText;
    [SerializeField] private TextMeshProUGUI qolText;
    [SerializeField] private RoundedRadialFill qolRadialFill;

    [Header("Metric bars (fill 0-1)")]
    [SerializeField] private CurvedBarFill environmentFill;
    [SerializeField] private CurvedBarFill economyFill;
    [SerializeField] private CurvedBarFill healthSafetyFill;
    [SerializeField] private CurvedBarFill cultureEduFill;

    [Header("Metric percentage texts (optional)")]
    [SerializeField] private TextMeshProUGUI environmentText;
    [SerializeField] private TextMeshProUGUI economyText;
    [SerializeField] private TextMeshProUGUI healthSafetyText;
    [SerializeField] private TextMeshProUGUI cultureEduText;

    [Tooltip("Scale metric percentage (0-100) to fill (0-1). Use 0.01 for 0-100% range.")]
    [SerializeField] private float metricFillScale = 0.01f;
    [Tooltip("Smooth metric bar changes (0 = instant).")]
    [SerializeField] private float metricSmoothTime = 0.3f;

    [Header("HDR glow (bloom)")]
    [Tooltip("Give every top-bar text and icon a UIGlow handle so tutorial highlights can bloom them (values > 1 glow).")]
    [SerializeField] private bool glowTopBar = true;
    [Tooltip("Resting HDR multiplier for top-bar texts and icons. 1 = no glow at rest; the tutorial highlight pulses still sweep well above 1.")]
    [Range(1f, 6f)] [SerializeField] private float topBarGlowBoost = 1f;

    [Header("Tutorial highlight")]
    [Tooltip("HDR multiplier at the top of a highlight flash. Bloom threshold is ~2.24, so higher = stronger halo.")]
    [Range(2.3f, 8f)] [SerializeField] private float highlightGlowPeak = 2.5f;
    [Tooltip("Scale the highlighted element reaches at the top of a tutorial flash (1.2 = +20%). 1 = no scaling.")]
    [Range(1f, 1.5f)] [SerializeField] private float highlightScalePeak = 1.2f;
    [Tooltip("Rise time as a fraction of the step's highlight duration.")]
    [Range(0.1f, 1f)] [SerializeField] private float highlightRiseFraction = 0.4f;
    [Tooltip("Hold at full brightness/size, as a fraction of the duration.")]
    [Range(0f, 1f)] [SerializeField] private float highlightHoldFraction = 0.3f;
    [Tooltip("Settle time as a fraction of the duration.")]
    [Range(0.1f, 1.5f)] [SerializeField] private float highlightFallFraction = 0.6f;

    private float _displayQol;
    public float DisplayQol => _displayQol;

    private float _displayEnv, _displayEco, _displaySaf, _displayCul;

    // Cached last-shown integers so a text field (and its string allocation + TMP rebuild) is only
    // touched when the displayed value actually changes, instead of every frame.
    private int _lastQol = int.MinValue, _lastEnv = int.MinValue, _lastEco = int.MinValue, _lastSaf = int.MinValue, _lastCul = int.MinValue;
    private int _lastBudget = int.MinValue, _lastTimerSeconds = int.MinValue;

    private void ResetMetricUI()
    {
        _displayQol = _displayEnv = _displayEco = _displaySaf = _displayCul = 0f;
        _lastQol = _lastEnv = _lastEco = _lastSaf = _lastCul = 0;
        _lastBudget = _lastTimerSeconds = int.MinValue;

        if (qolText != null) qolText.text = "0";
        if (environmentText != null) environmentText.text = "0%";
        if (economyText != null) economyText.text = "0%";
        if (healthSafetyText != null) healthSafetyText.text = "0%";
        if (cultureEduText != null) cultureEduText.text = "0%";

        if (environmentFill != null) environmentFill.fill = 0f;
        if (economyFill != null) economyFill.fill = 0f;
        if (healthSafetyFill != null) healthSafetyFill.fill = 0f;
        if (cultureEduFill != null) cultureEduFill.fill = 0f;
        if (qolRadialFill != null) qolRadialFill.fill = 0f;
    }

    private void Awake()
    {
        if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
        if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true);
        if (coordinator == null) coordinator = GetComponentInChildren<GameInstanceCoordinator>(true);
    }

    private void Start()
    {
        if (glowTopBar) ApplyTopBarGlow();
    }

    /// <summary>Give every top-bar text and icon a UIGlow so the whole readout strip blooms.
    /// The top bar is located from the referenced readouts (readout → group → Top Bar UI), so no
    /// extra scene wiring is needed. Fill bars and SVGs are skipped: bars glow through their own
    /// shader, and SVG images have generated materials a swap would break.</summary>
    private void ApplyTopBarGlow()
    {
        Transform readout = budgetText != null ? budgetText.transform
                          : timerText != null ? timerText.transform
                          : qolText != null ? qolText.transform : null;
        if (readout == null || readout.parent == null || readout.parent.parent == null) return;
        Transform topBar = readout.parent.parent;

        foreach (var g in topBar.GetComponentsInChildren<Graphic>(true))
        {
            if (g is RawImage) continue;                    // fill bars already glow via their own shader
            if (g.GetType().Name.Contains("SVG")) continue; // vector graphics own their material setup
            if (g.GetComponent<UIGlow>() != null) continue;
            var glow = g.gameObject.AddComponent<UIGlow>();
            glow.glowBoost = topBarGlowBoost;
        }
    }

    private void OnEnable()
    {
        // Ensure dashboard starts from empty values before any metrics arrive.
        // Update() drives RefreshMetrics every frame, so no OnMetricsChanged subscription:
        // a second call on edit frames would double-step the smoothing and make bars stutter.
        ResetMetricUI();
    }

    private void OnDisable()
    {
        // Stop any in-flight pulses and restore resting scale so a disabled/destroyed bar
        // doesn't keep a tween alive or freeze at an inflated scale.
        foreach (var kv in _pillarBaseScales)
        {
            if (kv.Key == null) continue;
            kv.Key.DOKill(false);
            kv.Key.localScale = kv.Value;
        }
    }

    private void Update()
    {
        if (sessionTimer != null && timerText != null)
        {
            int secs = Mathf.Max(0, Mathf.FloorToInt(sessionTimer.RemainingSeconds));
            if (secs != _lastTimerSeconds)
            {
                _lastTimerSeconds = secs;
                timerText.text = sessionTimer.FormatTime();
            }
        }
        if (coordinator != null && budgetText != null && coordinator.Budget != _lastBudget)
        {
            _lastBudget = coordinator.Budget;
            budgetText.text = _lastBudget.ToString();
        }

        // Drive the metric smoothing every frame so the bars/texts converge to the live values and
        // settle to 0 when the last building is removed. OnMetricsChanged fires only on discrete edits,
        // which is too sparse for the lerp to reach its target on its own (a single removal would
        // otherwise nudge the bars ~5% toward 0 and then freeze).
        RefreshMetrics();
    }

    public enum Pillar { Environment, Economy, HealthSafety, CultureEdu, Qol }

    // Resting scale per pillar transform, captured before the first pulse so repeated pulses always
    // start from the true base and never compound into a runaway scale.
    private readonly Dictionary<Transform, Vector3> _pillarBaseScales = new Dictionary<Transform, Vector3>();

    /// <summary>Brief scale punch on a pillar bar to draw the eye after a placement.
    /// Re-pulsing kills the in-flight tween and restores the base scale first, so rapid
    /// placements can't stack pulses and blow up the scale.</summary>
    public void PunchPillar(Pillar pillar, float strength = 0.18f, float duration = 0.35f)
    {
        RectTransform target = pillar switch
        {
            Pillar.Environment   => environmentFill   != null ? environmentFill.transform   as RectTransform : null,
            Pillar.Economy       => economyFill       != null ? economyFill.transform       as RectTransform : null,
            Pillar.HealthSafety  => healthSafetyFill  != null ? healthSafetyFill.transform  as RectTransform : null,
            Pillar.CultureEdu    => cultureEduFill    != null ? cultureEduFill.transform    as RectTransform : null,
            Pillar.Qol           => qolRadialFill     != null ? qolRadialFill.transform     as RectTransform : null,
            _                    => null
        };
        if (target == null || !isActiveAndEnabled) return;

        // Capture the true resting scale on first use (no pulse is active yet, so it is not inflated).
        if (!_pillarBaseScales.TryGetValue(target, out Vector3 baseScale))
        {
            baseScale = target.localScale;
            _pillarBaseScales[target] = baseScale;
        }

        // Kill any in-flight pulse on this target and snap back to base before re-punching, so
        // overlapping calls can't compound. DOPunchScale returns to the scale at tween start.
        target.DOKill(false);
        target.localScale = baseScale;
        target.DOPunchScale(baseScale * strength, duration, vibrato: 1, elasticity: 0.5f)
            .SetUpdate(true)
            .SetTarget(target);
    }

    // ---- tutorial highlights ----

    /// <summary>Draw the eye to a pillar: an HDR glow pulse on its bar/badge (bloom post pass picks
    /// it up). No scale animation — geometry stays inside its layout, only brightness moves.</summary>
    public void HighlightPillar(Pillar pillar, float seconds = 1f)
    {
        Transform target = PillarTransform(pillar);
        if (target == null) return;
        FlashGraphics(target, seconds);
        ScalePulse(target, seconds);
        var label = pillar switch
        {
            Pillar.Environment => environmentText,
            Pillar.Economy => economyText,
            Pillar.HealthSafety => healthSafetyText,
            Pillar.CultureEdu => cultureEduText,
            Pillar.Qol => qolText,
            _ => null
        };
        if (label != null)
        {
            FlashGraphics(label.transform, seconds);
            ScalePulse(label.transform, seconds);
        }
    }

    /// <summary>Brief white flash on the session timer readout.</summary>
    public void HighlightTimer(float seconds = 1f)
    {
        if (timerText == null) return;
        FlashGraphics(timerText.transform, seconds);
        ScalePulse(timerText.transform, seconds);
    }

    /// <summary>Brief white flash on the budget readout.</summary>
    public void HighlightBudget(float seconds = 1f)
    {
        if (budgetText == null) return;
        FlashGraphics(budgetText.transform, seconds);
        ScalePulse(budgetText.transform, seconds);
    }

    /// <summary>Size punch riding a highlight flash: up to highlightScalePeak and back over the
    /// flash duration. Base scales are cached (same map as PunchPillar) so repeats never compound.</summary>
    private void ScalePulse(Transform target, float seconds)
    {
        if (target == null || highlightScalePeak <= 1.001f || !isActiveAndEnabled) return;

        if (!_pillarBaseScales.TryGetValue(target, out Vector3 baseScale))
        {
            baseScale = target.localScale;
            _pillarBaseScales[target] = baseScale;
        }

        target.DOKill(false);
        target.localScale = baseScale;
        float rise = Mathf.Max(0.35f, seconds * highlightRiseFraction);
        float hold = Mathf.Max(0.2f, seconds * highlightHoldFraction);
        float fall = Mathf.Max(0.5f, seconds * highlightFallFraction);
        DOTween.Sequence().SetTarget(target).SetUpdate(true)
            .Append(target.DOScale(baseScale * highlightScalePeak, rise).SetEase(Ease.OutQuad))
            .AppendInterval(hold)
            .Append(target.DOScale(baseScale, fall).SetEase(Ease.InOutSine))
            .OnKill(() => { if (target != null) target.localScale = baseScale; });
    }

    /// <summary>Tutorial demo: sweep the QOL badge to 99 and back, then hand display back to the live
    /// value. 99 not 100 — the badge layout breaks with three digits at full fill.</summary>
    public void PlayQolMaxDemo(float seconds = 3f)
    {
        if (_qolDemoRoutine != null) StopCoroutine(_qolDemoRoutine);
        _qolDemoRoutine = StartCoroutine(QolDemoRoutine(Mathf.Max(1f, seconds)));
    }

    private Coroutine _qolDemoRoutine;
    private bool _qolDemoActive;
    private float _qolDemoValue;

    // Per-pillar demo sweeps (tutorial): bar animates to full and back, then live values resume.
    private readonly bool[] _pillarDemo = new bool[4];
    private readonly float[] _pillarDemoValue = new float[4];
    private readonly Coroutine[] _pillarDemoRoutines = new Coroutine[4];

    /// <summary>Tutorial demo: sweep one category bar to full and back (Qol routes to the badge demo).</summary>
    public void PlayPillarMaxDemo(Pillar pillar, float seconds = 1.2f)
    {
        if (pillar == Pillar.Qol) { PlayQolMaxDemo(seconds); return; }
        int idx = (int)pillar;
        if (idx < 0 || idx > 3) return;
        if (_pillarDemoRoutines[idx] != null) StopCoroutine(_pillarDemoRoutines[idx]);
        _pillarDemoRoutines[idx] = StartCoroutine(PillarDemoRoutine(idx, Mathf.Max(0.4f, seconds)));
    }

    private System.Collections.IEnumerator PillarDemoRoutine(int idx, float seconds)
    {
        _pillarDemo[idx] = true;
        float baseVal = idx switch { 0 => _displayEnv, 1 => _displayEco, 2 => _displaySaf, _ => _displayCul };
        float up = seconds * 0.4f, hold = seconds * 0.2f, down = seconds * 0.4f;
        for (float t = 0f; t < up; t += Time.deltaTime)
        {
            _pillarDemoValue[idx] = Mathf.Lerp(baseVal, 99f, Mathf.SmoothStep(0f, 1f, t / up));
            yield return null;
        }
        _pillarDemoValue[idx] = 99f;
        yield return new WaitForSeconds(hold);
        for (float t = 0f; t < down; t += Time.deltaTime)
        {
            _pillarDemoValue[idx] = Mathf.Lerp(99f, baseVal, Mathf.SmoothStep(0f, 1f, t / down));
            yield return null;
        }
        _pillarDemo[idx] = false;
        _pillarDemoRoutines[idx] = null;
    }

    private System.Collections.IEnumerator QolDemoRoutine(float seconds)
    {
        _qolDemoActive = true;
        float baseVal = _displayQol;
        float up = seconds * 0.4f, hold = seconds * 0.2f, down = seconds * 0.4f;
        for (float t = 0f; t < up; t += Time.deltaTime)
        {
            _qolDemoValue = Mathf.Lerp(baseVal, 99f, Mathf.SmoothStep(0f, 1f, t / up));
            yield return null;
        }
        _qolDemoValue = 99f;
        yield return new WaitForSeconds(hold);
        for (float t = 0f; t < down; t += Time.deltaTime)
        {
            _qolDemoValue = Mathf.Lerp(99f, baseVal, Mathf.SmoothStep(0f, 1f, t / down));
            yield return null;
        }
        _qolDemoActive = false;
        _qolDemoRoutine = null;
    }

    private RectTransform PillarTransform(Pillar pillar) => pillar switch
    {
        Pillar.Environment  => environmentFill  != null ? environmentFill.transform  as RectTransform : null,
        Pillar.Economy      => economyFill      != null ? economyFill.transform      as RectTransform : null,
        Pillar.HealthSafety => healthSafetyFill != null ? healthSafetyFill.transform as RectTransform : null,
        Pillar.CultureEdu   => cultureEduFill   != null ? cultureEduFill.transform   as RectTransform : null,
        Pillar.Qol          => qolRadialFill    != null ? qolRadialFill.transform    as RectTransform : null,
        _                   => null
    };

    private static Shader _glowShader;

    /// <summary>Tutorial highlight: a proper glow pass. Images/bars get an HDR intensity sweep
    /// above 1 so the bloom post pass halos them; text glows to white (TMP vertex colors clamp
    /// at 1, so material boost doesn't apply there). Timing/strength come from the
    /// "Tutorial highlight" inspector sliders.</summary>
    private void FlashGraphics(Transform target, float seconds)
    {
        if (_glowShader == null) _glowShader = Shader.Find("CityTwin/UI/GlowBoost");
        // Slow, readable sweep: rise, HOLD at full brightness, then a long settle.
        float rise = Mathf.Max(0.35f, seconds * highlightRiseFraction);
        float hold = Mathf.Max(0.2f, seconds * highlightHoldFraction);
        float fall = Mathf.Max(0.5f, seconds * highlightFallFraction);

        // Graphics that already have an HDR glow knob (fill bars, ring, UIGlow texts/icons) pulse
        // that in place so their shader keeps working during the highlight. Swapping materials
        // (the old approach) stripped the fill mask and flashed the raw texture.
        var glowOwned = new HashSet<Graphic>();
        foreach (var mb in target.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!(mb is IGlowBoost boost)) continue;
            if (mb is UIGlow ug) glowOwned.Add(ug.GetComponent<Graphic>());
            else foreach (var img in mb.GetComponentsInChildren<RawImage>(true)) glowOwned.Add(img);
            PulseGlowBoost(mb, boost, rise, hold, fall);
        }

        foreach (var g in target.GetComponentsInChildren<Graphic>(true))
        {
            if (g == null || glowOwned.Contains(g)) continue;
            g.DOKill(false);

            if (g is TMPro.TMP_Text)
            {
                Color orig = g.color;
                bool nearWhite = orig.r > 0.85f && orig.g > 0.85f && orig.b > 0.85f;
                if (!nearWhite)
                {
                    DOTween.Sequence().SetTarget(g).SetUpdate(true)
                        .Append(g.DOColor(Color.white, rise).SetEase(Ease.OutQuad))
                        .AppendInterval(seconds * 0.2f)
                        .Append(g.DOColor(orig, fall).SetEase(Ease.InOutSine))
                        .OnKill(() => { if (g != null) g.color = orig; });
                }
                else
                {
                    DOTween.Sequence().SetTarget(g).SetUpdate(true)
                        .Append(g.DOFade(Mathf.Min(0.15f, orig.a), rise).SetEase(Ease.InOutSine))
                        .Append(g.DOFade(orig.a, fall).SetEase(Ease.InOutSine))
                        .OnKill(() => { if (g != null) { var c = g.color; c.a = orig.a; g.color = c; } });
                }
            }
            else if (_glowShader != null)
            {
                GlowPulse(g, rise, hold, fall);
            }
        }
    }

    /// <summary>Sweep an IGlowBoost graphic's HDR multiplier base → peak → base on its own
    /// material. The component keeps writing the value every frame, so the tween drives the
    /// component property, not the material directly.</summary>
    private void PulseGlowBoost(MonoBehaviour owner, IGlowBoost boost, float rise, float hold, float fall)
    {
        owner.DOKill(false);
        float baseVal = boost.BaseGlowBoost;
        float peak = Mathf.Max(highlightGlowPeak, baseVal * 1.5f);
        boost.GlowBoost = baseVal;
        DOTween.Sequence().SetTarget(owner).SetUpdate(true)
            .Append(DOTween.To(() => boost.GlowBoost, v => boost.GlowBoost = v, peak, rise).SetEase(Ease.OutQuad))
            .AppendInterval(hold)
            .Append(DOTween.To(() => boost.GlowBoost, v => boost.GlowBoost = v, baseVal, fall).SetEase(Ease.InOutSine))
            .OnComplete(() => boost.GlowBoost = baseVal)
            .OnKill(() => boost.GlowBoost = baseVal);
    }

    /// <summary>Swap in the HDR-boost material, sweep intensity 1 → peak → 1, then restore.</summary>
    private void GlowPulse(Graphic g, float rise, float hold, float fall)
    {
        Material original = g.material;
        var glow = new Material(_glowShader);
        glow.SetFloat("_GlowBoost", 1f);
        g.material = glow;

        DOTween.Sequence().SetTarget(glow).SetUpdate(true)
            .Append(DOTween.To(() => glow.GetFloat("_GlowBoost"), v => glow.SetFloat("_GlowBoost", v), highlightGlowPeak, rise).SetEase(Ease.OutQuad))
            .AppendInterval(hold)
            .Append(DOTween.To(() => glow.GetFloat("_GlowBoost"), v => glow.SetFloat("_GlowBoost", v), 1f, fall).SetEase(Ease.InOutSine))
            .OnComplete(() =>
            {
                if (g != null) g.material = original;
                Object.Destroy(glow);
            })
            .OnKill(() =>
            {
                if (g != null && g.material == glow) g.material = original;
            });
    }

    /// <summary>Framerate-independent exponential approach: same convergence per second at any
    /// fps (raw Lerp(a, b, dt/smoothTime) moved faster at higher framerates). Snaps the last
    /// sub-visible sliver so values actually arrive instead of crawling forever.</summary>
    private static float Approach(float current, float target, float k)
    {
        float next = Mathf.Lerp(current, target, k);
        return Mathf.Abs(target - next) < 0.05f ? target : next;
    }

    private void RefreshMetrics()
    {
        if (simulationEngine == null) return;
        float k = metricSmoothTime > 0 ? 1f - Mathf.Exp(-Time.deltaTime / metricSmoothTime) : 1f;
        _displayQol = Approach(_displayQol, simulationEngine.Qol, k);
        _displayEnv = Approach(_displayEnv, simulationEngine.Environment, k);
        _displayEco = Approach(_displayEco, simulationEngine.Economy, k);
        _displaySaf = Approach(_displaySaf, simulationEngine.HealthSafety, k);
        _displayCul = Approach(_displayCul, simulationEngine.CultureEdu, k);
        if (_qolDemoActive)
        {
            // Tutorial sweep owns the badge; bars keep tracking live values.
            SetMetricText(qolText, Mathf.RoundToInt(_qolDemoValue), ref _lastQol, false);
            if (qolRadialFill != null) qolRadialFill.fill = Mathf.Clamp01(_qolDemoValue / 100f);
        }
        else
        SetMetricText(qolText, Mathf.RoundToInt(_displayQol), ref _lastQol, false);
        // Tutorial demo sweeps take over individual bars; live values resume when they end.
        float env = _pillarDemo[0] ? _pillarDemoValue[0] : _displayEnv;
        float eco = _pillarDemo[1] ? _pillarDemoValue[1] : _displayEco;
        float saf = _pillarDemo[2] ? _pillarDemoValue[2] : _displaySaf;
        float cul = _pillarDemo[3] ? _pillarDemoValue[3] : _displayCul;
        SetMetricText(environmentText, Mathf.RoundToInt(env), ref _lastEnv, true);
        SetMetricText(economyText, Mathf.RoundToInt(eco), ref _lastEco, true);
        SetMetricText(healthSafetyText, Mathf.RoundToInt(saf), ref _lastSaf, true);
        SetMetricText(cultureEduText, Mathf.RoundToInt(cul), ref _lastCul, true);
        if (environmentFill != null) environmentFill.fill = Mathf.Clamp01(env * metricFillScale);
        if (economyFill != null) economyFill.fill = Mathf.Clamp01(eco * metricFillScale);
        if (healthSafetyFill != null) healthSafetyFill.fill = Mathf.Clamp01(saf * metricFillScale);
        if (cultureEduFill != null) cultureEduFill.fill = Mathf.Clamp01(cul * metricFillScale);
        if (qolRadialFill != null && !_qolDemoActive) qolRadialFill.fill = Mathf.Clamp01(_displayQol / 100f);
    }

    private static void SetMetricText(TextMeshProUGUI label, int value, ref int last, bool percent)
    {
        if (label == null || value == last) return;
        last = value;
        label.text = percent ? $"{value}%" : value.ToString();
    }
}
