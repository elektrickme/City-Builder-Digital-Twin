using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>Drives the CityTwin/UI/HubStatRing shader: one square RawImage draws all four
    /// hub stat arcs procedurally, so alignment and the gaps between sections are exact.
    /// Quadrants (clockwise from 12 o'clock): top-right, bottom-right, bottom-left, top-left.
    /// Implements IGlowBoost so the city-center blink pulse can sweep the ring's HDR glow.</summary>
    // NOT ExecuteInEditMode: the material is instanced at runtime only, so the serialized
    // RawImage keeps pointing at the shared material asset (an edit-mode instance would
    // replace that reference with an unsaveable scene object and break the prefab).
    [RequireComponent(typeof(RawImage))]
    public class HubStatRing : MonoBehaviour, IGlowBoost
    {
        [Header("Fills (0-1, one per quadrant)")]
        [Range(0f, 1f)] public float safetyFill;      // top-right, cyan
        [Range(0f, 1f)] public float economyFill;     // bottom-right, orange
        [Range(0f, 1f)] public float environmentFill; // bottom-left, green
        [Range(0f, 1f)] public float cultureFill;     // top-left, magenta

        [Header("Look")]
        public Color safetyColor = new Color(0.20f, 0.78f, 0.96f, 1f);
        public Color economyColor = new Color(1.00f, 0.63f, 0.15f, 1f);
        public Color environmentColor = new Color(0.65f, 0.91f, 0.15f, 1f);
        public Color cultureColor = new Color(1.00f, 0.18f, 0.70f, 1f);
        [Tooltip("Degrees of empty space between neighbouring sections. The gaps are part of the design - keep them readable.")]
        [Range(0f, 45f)] public float gapDegrees = 14f;
        [Range(0.1f, 0.5f)] public float ringRadius = 0.42f;
        [Tooltip("Half-thickness of the arc stroke in UV units.")]
        [Range(0.005f, 0.1f)] public float thickness = 0.03f;
        [Tooltip("Alpha of the faint full-length track behind each arc (0 = no track).")]
        [Range(0f, 1f)] public float trackAlpha = 0.16f;
        [Tooltip("Resting HDR multiplier. 1 = no glow at rest; the center blink pulse sweeps above the bloom threshold.")]
        [Range(1f, 6f)] public float glowBoost = 1f;

        private static readonly int FillAId = Shader.PropertyToID("_FillA");
        private static readonly int FillBId = Shader.PropertyToID("_FillB");
        private static readonly int FillCId = Shader.PropertyToID("_FillC");
        private static readonly int FillDId = Shader.PropertyToID("_FillD");
        private static readonly int ColorAId = Shader.PropertyToID("_ColorA");
        private static readonly int ColorBId = Shader.PropertyToID("_ColorB");
        private static readonly int ColorCId = Shader.PropertyToID("_ColorC");
        private static readonly int ColorDId = Shader.PropertyToID("_ColorD");
        private static readonly int GapId = Shader.PropertyToID("_GapDegrees");
        private static readonly int RadiusId = Shader.PropertyToID("_RingRadius");
        private static readonly int ThicknessId = Shader.PropertyToID("_Thickness");
        private static readonly int TrackId = Shader.PropertyToID("_TrackAlpha");
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
            _matInstance.SetFloat(FillAId, safetyFill);
            _matInstance.SetFloat(FillBId, economyFill);
            _matInstance.SetFloat(FillCId, environmentFill);
            _matInstance.SetFloat(FillDId, cultureFill);
            _matInstance.SetColor(ColorAId, safetyColor);
            _matInstance.SetColor(ColorBId, economyColor);
            _matInstance.SetColor(ColorCId, environmentColor);
            _matInstance.SetColor(ColorDId, cultureColor);
            _matInstance.SetFloat(GapId, gapDegrees);
            _matInstance.SetFloat(RadiusId, ringRadius);
            _matInstance.SetFloat(ThicknessId, thickness);
            _matInstance.SetFloat(TrackId, trackAlpha);
            _matInstance.SetFloat(GlowId, GlowBoost);
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
