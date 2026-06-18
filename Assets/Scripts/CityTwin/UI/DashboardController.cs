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

    private void OnEnable()
    {
        // Ensure dashboard starts from empty values before any metrics arrive.
        ResetMetricUI();

        if (simulationEngine != null)
            simulationEngine.OnMetricsChanged += RefreshMetrics;
    }

    private void OnDisable()
    {
        if (simulationEngine != null)
            simulationEngine.OnMetricsChanged -= RefreshMetrics;

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

    private void RefreshMetrics()
    {
        if (simulationEngine == null) return;
        float dt = metricSmoothTime > 0 ? Time.deltaTime / metricSmoothTime : 1f;
        _displayQol = Mathf.Lerp(_displayQol, simulationEngine.Qol, dt);
        _displayEnv = Mathf.Lerp(_displayEnv, simulationEngine.Environment, dt);
        _displayEco = Mathf.Lerp(_displayEco, simulationEngine.Economy, dt);
        _displaySaf = Mathf.Lerp(_displaySaf, simulationEngine.HealthSafety, dt);
        _displayCul = Mathf.Lerp(_displayCul, simulationEngine.CultureEdu, dt);
        SetMetricText(qolText, Mathf.RoundToInt(_displayQol), ref _lastQol, false);
        SetMetricText(environmentText, Mathf.RoundToInt(_displayEnv), ref _lastEnv, true);
        SetMetricText(economyText, Mathf.RoundToInt(_displayEco), ref _lastEco, true);
        SetMetricText(healthSafetyText, Mathf.RoundToInt(_displaySaf), ref _lastSaf, true);
        SetMetricText(cultureEduText, Mathf.RoundToInt(_displayCul), ref _lastCul, true);
        if (environmentFill != null) environmentFill.fill = Mathf.Clamp01(_displayEnv * metricFillScale);
        if (economyFill != null) economyFill.fill = Mathf.Clamp01(_displayEco * metricFillScale);
        if (healthSafetyFill != null) healthSafetyFill.fill = Mathf.Clamp01(_displaySaf * metricFillScale);
        if (cultureEduFill != null) cultureEduFill.fill = Mathf.Clamp01(_displayCul * metricFillScale);
        if (qolRadialFill != null) qolRadialFill.fill = Mathf.Clamp01(_displayQol / 100f);
    }

    private static void SetMetricText(TextMeshProUGUI label, int value, ref int last, bool percent)
    {
        if (label == null || value == last) return;
        last = value;
        label.text = percent ? $"{value}%" : value.ToString();
    }
}
