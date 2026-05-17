using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>
    /// IConnectionVisual that draws a sine-wave ribbon instead of a straight line.
    /// - On (re)activation it plays a one-shot "spawn" burst: the wave draws on from the
    ///   building toward its connection and the amplitude over-shoots then settles.
    /// - After settling it keeps a small constant travelling wave.
    /// - Wave humps + amplitude scale with the building→connection distance.
    ///
    /// Drop on a prefab (RectTransform + CanvasRenderer auto-added by Graphic) and assign that
    /// prefab as HubConnectionRenderer.connectionPrefab. HubConnectionRenderer styles it through
    /// Graphic.color and rect height (thickness) exactly like StretchedImageConnection — no
    /// renderer changes needed.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class SineWaveConnection : MaskableGraphic, IConnectionVisual
    {
        [Header("Wave shape")]
        [Tooltip("World px between two wave crests. Lower = more humps.")]
        [SerializeField] private float wavelength = 46f;
        [Tooltip("Base wave height (px) at the centreline.")]
        [SerializeField] private float baseAmplitude = 5f;
        [Tooltip("Extra amplitude added per px of connection length.")]
        [SerializeField] private float amplitudePerPx = 0.012f;
        [SerializeField] private float minAmplitude = 3f;
        [SerializeField] private float maxAmplitude = 16f;
        [Tooltip("Ribbon stroke width in px. Independent of the RectTransform — set this for line thinness.")]
        [SerializeField] private float strokeWidth = 2.5f;
        [Tooltip("Mesh segments per 100 px of length (clamped).")]
        [SerializeField] private float segmentsPer100Px = 28f;

        [Header("Constant travel")]
        [Tooltip("Phase speed of the settled wave (radians/sec). Negative = travels toward building.")]
        [SerializeField] private float travelSpeed = 3.2f;

        [Header("Spawn burst")]
        [Tooltip("Seconds for the wave to draw on from building to connection.")]
        [SerializeField] private float drawOnDuration = 0.40f;
        [Tooltip("Seconds for the amplitude over-shoot to settle.")]
        [SerializeField] private float settleDuration = 0.55f;
        [Tooltip("Peak amplitude multiplier at spawn (1 = no burst).")]
        [SerializeField] private float burstAmplitudeMul = 2.4f;

        private RectTransform _rt;
        private float _length;
        private float _phase;
        private float _spawnTime = -999f;

        protected override void Awake()
        {
            base.Awake();
            _rt = (RectTransform)transform;
            _rt.pivot = new Vector2(0f, 0.5f);
            _rt.anchorMin = new Vector2(0.5f, 0.5f);
            _rt.anchorMax = new Vector2(0.5f, 0.5f);
            raycastTarget = false;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            // (Re)activation = a fresh connection: replay the spawn burst.
            _spawnTime = Time.unscaledTime;
            SetVerticesDirty();
        }

        // ---- IConnectionVisual ----

        public void UpdateEndpoints(Vector2 from, Vector2 to)
        {
            if (_rt == null) _rt = (RectTransform)transform;

            Vector2 delta = to - from;
            _length = delta.magnitude;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            _rt.anchoredPosition = from;
            _rt.sizeDelta = new Vector2(_length, strokeWidth);
            _rt.localRotation = Quaternion.Euler(0f, 0f, angle);
            SetVerticesDirty();
        }

        public void SetActive(bool active)
        {
            if (gameObject.activeSelf != active)
                gameObject.SetActive(active);
        }

        // ---- animation ----

        private void Update()
        {
            // Constant gentle travel + keep redrawing during the spawn window.
            _phase += travelSpeed * Time.unscaledDeltaTime;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_length <= 1f) return;

            float half = Mathf.Max(0.25f, strokeWidth * 0.5f);

            // Spawn envelope
            float t = Time.unscaledTime - _spawnTime;
            float reveal = drawOnDuration <= 0f ? 1f : Mathf.Clamp01(t / drawOnDuration);
            reveal = 1f - Mathf.Pow(1f - reveal, 3f); // ease-out
            float settle01 = settleDuration <= 0f ? 1f : Mathf.Clamp01(t / settleDuration);
            float burst = Mathf.Lerp(burstAmplitudeMul, 1f, settle01 * settle01);

            float amp = Mathf.Clamp(baseAmplitude + _length * amplitudePerPx, minAmplitude, maxAmplitude) * burst;
            float drawnLen = _length * reveal;
            float k = 2f * Mathf.PI / Mathf.Max(4f, wavelength); // angular wavenumber

            int segs = Mathf.Clamp(Mathf.RoundToInt(drawnLen / 100f * segmentsPer100Px), 8, 96);
            Color32 c = color;

            for (int i = 0; i <= segs; i++)
            {
                float u = (float)i / segs;
                float x = u * drawnLen;
                // taper amplitude to 0 at both ends so it visually "plugs into" building + target
                float endTaper = Mathf.Sin(Mathf.Clamp01(u) * Mathf.PI);
                float y = Mathf.Sin(x * k - _phase) * amp * endTaper;

                vh.AddVert(new Vector3(x, y + half, 0f), c, Vector2.zero);
                vh.AddVert(new Vector3(x, y - half, 0f), c, Vector2.zero);

                if (i > 0)
                {
                    int b = i * 2;
                    vh.AddTriangle(b - 2, b - 1, b + 1);
                    vh.AddTriangle(b - 2, b + 1, b);
                }
            }
        }
    }
}
