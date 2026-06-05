using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>
    /// IConnectionVisual that draws a static sine-wave ribbon instead of a straight line.
    /// Wave humps + amplitude scale with the building→connection distance.
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

        private RectTransform _rt;
        private float _length;

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

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_length <= 1f) return;

            float half = Mathf.Max(0.25f, strokeWidth * 0.5f);

            float amp = Mathf.Clamp(baseAmplitude + _length * amplitudePerPx, minAmplitude, maxAmplitude);
            float drawnLen = _length;
            float k = 2f * Mathf.PI / Mathf.Max(4f, wavelength); // angular wavenumber

            int segs = Mathf.Clamp(Mathf.RoundToInt(drawnLen / 100f * segmentsPer100Px), 8, 96);
            Color32 c = color;

            for (int i = 0; i <= segs; i++)
            {
                float u = (float)i / segs;
                float x = u * drawnLen;
                // taper amplitude to 0 at both ends so it visually "plugs into" building + target
                float endTaper = Mathf.Sin(Mathf.Clamp01(u) * Mathf.PI);
                float y = Mathf.Sin(x * k) * amp * endTaper;

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
