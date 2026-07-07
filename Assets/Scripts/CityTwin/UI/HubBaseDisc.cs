using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>Drives the CityTwin/UI/HubBaseDisc shader: the hub's base circle drawn
    /// procedurally (radial-gradient fill + rim stroke) instead of the City Hub PNG, so it
    /// scales crisply and every color/thickness stays editable from the inspector or script.
    /// The population icon and stat texts remain separate children layered on top.
    /// Implements IGlowBoost so the city-center blink pulse can sweep the rim's HDR glow.</summary>
    // NOT ExecuteInEditMode: material is instanced at runtime only (an edit-mode instance
    // would replace the shared material reference with an unsaveable scene object).
    [RequireComponent(typeof(RawImage))]
    public class HubBaseDisc : MonoBehaviour, IGlowBoost
    {
        [Header("Fill")]
        [Tooltip("Disc color at the center of the gradient.")]
        public Color centerColor = new Color(0.045f, 0.055f, 0.13f, 1f);
        [Tooltip("Disc color at the outer edge, just inside the rim.")]
        public Color edgeColor = new Color(0.24f, 0.26f, 0.34f, 1f);
        [Tooltip("Gradient falloff exponent. 1 = linear; higher keeps the center dark longer.")]
        [Range(0.25f, 4f)] public float gradientPower = 1.6f;

        [Header("Rim")]
        public Color rimColor = new Color(0.016f, 0.757f, 0.996f, 1f);
        [Tooltip("Stroke width of the rim in pixels.")]
        [Range(0.5f, 20f)] public float rimThickness = 3f;

        [Header("Geometry (pixels)")]
        [Tooltip("Outer radius of the disc including the rim.")]
        public float discRadius = 74f;
        [Tooltip("Resting HDR multiplier on the rim. 1 = no glow; blink pulses sweep above the bloom threshold.")]
        [Range(1f, 6f)] public float glowBoost = 1f;

        private static readonly int CenterColorId = Shader.PropertyToID("_CenterColor");
        private static readonly int EdgeColorId = Shader.PropertyToID("_EdgeColor");
        private static readonly int GradientPowerId = Shader.PropertyToID("_GradientPower");
        private static readonly int RimColorId = Shader.PropertyToID("_RimColor");
        private static readonly int DiscRadiusId = Shader.PropertyToID("_DiscRadius");
        private static readonly int RimThicknessId = Shader.PropertyToID("_RimThickness");
        private static readonly int GlowId = Shader.PropertyToID("_GlowBoost");

        private RawImage _image;
        private Material _matInstance;

        // Runtime glow actually written to the material: pulses tween this and it snaps back
        // to the serialized base when they finish.
        private float _runtimeGlow = -1f;

        public float BaseGlowBoost => glowBoost;
        public float GlowBoost
        {
            get => _runtimeGlow > 0f ? _runtimeGlow : glowBoost;
            set => _runtimeGlow = value;
        }

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            _image = GetComponent<RawImage>();
            if (_matInstance == null && _image.material != null)
            {
                _matInstance = new Material(_image.material);
                _image.material = _matInstance;
            }
            ApplyProperties();
        }

        private void OnValidate()
        {
            ApplyProperties();
        }

        private void Update()
        {
            ApplyProperties();
        }

        private void ApplyProperties()
        {
            if (_matInstance == null) return;

            // Pixel-parametric geometry: the rect hugs the disc, and the shader's UV values
            // are derived so radius/thickness read directly in pixels.
            float rectSize = Mathf.Max(2f, discRadius * 2f + 4f); // small AA margin
            var rt = (RectTransform)transform;
            if (!Mathf.Approximately(rt.sizeDelta.x, rectSize))
                rt.sizeDelta = new Vector2(rectSize, rectSize);

            _matInstance.SetColor(CenterColorId, centerColor);
            _matInstance.SetColor(EdgeColorId, edgeColor);
            _matInstance.SetFloat(GradientPowerId, gradientPower);
            _matInstance.SetColor(RimColorId, rimColor);
            _matInstance.SetFloat(DiscRadiusId, discRadius / rectSize);
            _matInstance.SetFloat(RimThicknessId, rimThickness / rectSize);
            _matInstance.SetFloat(GlowId, GlowBoost);
        }

        /// <summary>Scene-view preview while tuning: disc edge and rim inner bound.</summary>
        private void OnDrawGizmosSelected()
        {
            var t = transform;
            float scale = t.lossyScale.x;
            Gizmos.color = new Color(0.02f, 0.76f, 1f, 0.9f);
            Gizmos.DrawWireSphere(t.position, discRadius * scale);
            Gizmos.color = new Color(0.02f, 0.76f, 1f, 0.35f);
            Gizmos.DrawWireSphere(t.position, (discRadius - rimThickness) * scale);
        }

        private void OnDestroy()
        {
            if (_matInstance != null)
            {
                if (Application.isPlaying)
                    Destroy(_matInstance);
                else
                    DestroyImmediate(_matInstance);
            }
        }
    }
}
