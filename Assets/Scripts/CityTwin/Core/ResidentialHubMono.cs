using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.Core
{
    /// <summary>Residential hub placed in the scene. Population comes from prefab (Inspector), not JSON. Used by HubRegistry and SimulationEngine.</summary>
    public class ResidentialHubMono : MonoBehaviour
    {
        [Tooltip("Unique id for this hub (e.g. H1, H2, H3).")]
        public string HubId = "H1";

        [Tooltip("Population for scoring formulas. Set on prefab or variant (e.g. 60000, 90000).")]
        public int Population = 50000;

        [SerializeField] private TextMeshProUGUI populationText;
        [Tooltip("Procedural stat ring (preferred): one shader draws all four quadrant arcs, perfectly aligned. When set, the legacy per-quadrant images below are ignored.")]
        [SerializeField] private CityTwin.UI.HubStatRing statRing;
        [SerializeField] private Image safetyFillImage;
        [SerializeField] private Image economyFillImage;
        [SerializeField] private Image cultureFillImage;
        [SerializeField] private Image environmentFillImage;

        [Tooltip("Seconds for the metric arcs to tween to their new value (like the dashboard bars). 0 = instant.")]
        [SerializeField] private float metricTweenDuration = 0.45f;

        [Tooltip("Draw gizmo sphere at hub position in editor / debug.")]
        public bool ShowDebugGizmos = true;

        [Tooltip("Optional: scale this transform by population for visual feedback.")]
        public Transform VisualRoot;

        /// <summary>World position as Vector2 (X,Z or X,Y depending on your map plane). Override if your hub uses a different plane.</summary>
        public virtual Vector2 Position2D
        {
            get
            {
                var p = transform.position;
                return new Vector2(p.x, p.z);
            }
        }

        private void OnValidate()
        {
            if (Population < 0) Population = 0;
            RefreshPopulationText();
        }

        private void OnDrawGizmos()
        {
            if (!ShowDebugGizmos) return;
            Gizmos.color = Color.cyan;
            var p = transform.position;
            Gizmos.DrawWireSphere(p, 0.5f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(p + Vector3.up * 15f, $"{HubId} {Population:N0}");
#endif
        }

        /// <summary>Update indicator arcs based on per-hub metric scores (percent, 0-100).</summary>
        public void SetMetricState(float env, float eco, float safety, float culture)
        {
            const float metricCap = 100f; // values are percentages (0-100)

            if (statRing != null)
            {
                // Procedural ring: each quadrant fills 0-1 within its own (gap-separated) sector.
                statRing.DOKill();
                TweenRing(() => statRing.environmentFill, v => statRing.environmentFill = v, Mathf.Clamp01(env / metricCap));
                TweenRing(() => statRing.economyFill, v => statRing.economyFill = v, Mathf.Clamp01(eco / metricCap));
                TweenRing(() => statRing.safetyFill, v => statRing.safetyFill = v, Mathf.Clamp01(safety / metricCap));
                TweenRing(() => statRing.cultureFill, v => statRing.cultureFill = v, Mathf.Clamp01(culture / metricCap));
                return;
            }

            const float maxFill = 0.25f; // legacy quarter-pie images
            TweenFill(environmentFillImage, Mathf.Clamp01(env / metricCap) * maxFill);
            TweenFill(economyFillImage, Mathf.Clamp01(eco / metricCap) * maxFill);
            TweenFill(safetyFillImage, Mathf.Clamp01(safety / metricCap) * maxFill);
            TweenFill(cultureFillImage, Mathf.Clamp01(culture / metricCap) * maxFill);
        }

        /// <summary>Ring counterpart of <see cref="TweenFill"/>: same easing, drives a component field.</summary>
        private void TweenRing(System.Func<float> getter, System.Action<float> setter, float target)
        {
            if (metricTweenDuration <= 0f)
            {
                setter(target);
                return;
            }
            DOTween.To(() => getter(), v => setter(v), target, metricTweenDuration)
                   .SetEase(Ease.OutCubic)
                   .SetUpdate(true)
                   .SetTarget(statRing);
        }

        /// <summary>Animate an arc's fillAmount to target, mirroring the dashboard bar easing. Kills any in-flight tween on that image first.</summary>
        private void TweenFill(Image img, float target)
        {
            if (img == null) return;
            img.DOKill();
            if (metricTweenDuration <= 0f)
            {
                img.fillAmount = target;
                return;
            }
            img.DOFillAmount(target, metricTweenDuration)
               .SetEase(Ease.OutCubic)
               .SetUpdate(true)         // animate even if timeScale is 0 (intro/pause safe)
               .SetTarget(img);
        }

        private void OnDisable()
        {
            // Stop tweens so pooled / disabled hubs don't leak DOTween references.
            if (statRing != null) statRing.DOKill();
            if (environmentFillImage != null) environmentFillImage.DOKill();
            if (economyFillImage != null) economyFillImage.DOKill();
            if (safetyFillImage != null) safetyFillImage.DOKill();
            if (cultureFillImage != null) cultureFillImage.DOKill();
        }

        private void Start()
        {
            // Start with empty indicator arcs; they will be filled when metrics are pushed.
            const float empty = 0f;
            if (statRing != null)
            {
                statRing.environmentFill = empty;
                statRing.economyFill = empty;
                statRing.safetyFill = empty;
                statRing.cultureFill = empty;
            }
            if (environmentFillImage != null) environmentFillImage.fillAmount = empty;
            if (economyFillImage != null) economyFillImage.fillAmount = empty;
            if (safetyFillImage != null) safetyFillImage.fillAmount = empty;
            if (cultureFillImage != null) cultureFillImage.fillAmount = empty;

            RefreshPopulationText();
        }

        private void RefreshPopulationText()
        {
            if (populationText != null)
                populationText.text = $"{Population / 1000}K";
        }
    }
}
