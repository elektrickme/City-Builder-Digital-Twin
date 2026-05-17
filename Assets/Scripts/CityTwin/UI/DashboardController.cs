using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
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

    private void ResetMetricUI()
    {
        _displayQol = _displayEnv = _displayEco = _displaySaf = _displayCul = 0f;

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
    }

    private void Update()
    {
        if (sessionTimer != null && timerText != null)
            timerText.text = sessionTimer.FormatTime();
        if (coordinator != null && budgetText != null)
            budgetText.text = coordinator.Budget.ToString();
    }

    public enum Pillar { Environment, Economy, HealthSafety, CultureEdu, Qol }

    /// <summary>Brief scale punch on a pillar bar to draw the eye after a placement.</summary>
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
        StopCoroutine(nameof(PunchRoutineRunner));
        StartCoroutine(PunchRoutine(target, strength, duration));
    }

    private void PunchRoutineRunner() { /* marker for StopCoroutine */ }

    private static IEnumerator PunchRoutine(RectTransform rt, float strength, float duration)
    {
        if (rt == null) yield break;
        Vector3 baseScale = rt.localScale;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float u = Mathf.Clamp01(t / duration);
            // damped sine: peaks then settles
            float wobble = Mathf.Sin(u * Mathf.PI * 2f) * (1f - u) * strength;
            rt.localScale = baseScale * (1f + wobble);
            yield return null;
        }
        rt.localScale = baseScale;
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
        if (qolText != null) qolText.text = Mathf.RoundToInt(_displayQol).ToString();
        if (environmentText != null) environmentText.text = $"{Mathf.RoundToInt(_displayEnv)}%";
        if (economyText != null) economyText.text = $"{Mathf.RoundToInt(_displayEco)}%";
        if (healthSafetyText != null) healthSafetyText.text = $"{Mathf.RoundToInt(_displaySaf)}%";
        if (cultureEduText != null) cultureEduText.text = $"{Mathf.RoundToInt(_displayCul)}%";
        if (environmentFill != null) environmentFill.fill = Mathf.Clamp01(_displayEnv * metricFillScale);
        if (economyFill != null) economyFill.fill = Mathf.Clamp01(_displayEco * metricFillScale);
        if (healthSafetyFill != null) healthSafetyFill.fill = Mathf.Clamp01(_displaySaf * metricFillScale);
        if (cultureEduFill != null) cultureEduFill.fill = Mathf.Clamp01(_displayCul * metricFillScale);
        if (qolRadialFill != null) qolRadialFill.fill = Mathf.Clamp01(_displayQol / 100f);
    }
}
