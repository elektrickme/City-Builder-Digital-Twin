using UnityEngine;
using UnityEngine.UI;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>
    /// Draws small particles flowing along a connection line — citizens traveling the network.
    /// Place on a child GameObject of a connection prefab (StretchedImageConnection etc.).
    /// The child stretch-fills its parent, so particles travel the parent's local X axis —
    /// which HubConnectionRenderer already rotates/sizes to span from→to.
    ///
    /// Each particle gets deterministic per-index variation (speed, direction, size,
    /// perpendicular offset, start phase) from a hash, so no per-particle state is stored
    /// and pooled lines stay stable. Assign an additive UI material for a glow look.
    /// Inherits the parent line's color so connection-type tints match.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class ConnectionFlowParticles : MaskableGraphic
    {
        [Header("Flow")]
        [Tooltip("Slowest particle speed along the line, px/sec.")]
        [SerializeField] private float minSpeed = 10f;
        [Tooltip("Fastest particle speed along the line, px/sec.")]
        [SerializeField] private float maxSpeed = 45f;
        [Tooltip("Fraction of particles traveling the reverse direction (to → from).")]
        [Range(0f, 1f)]
        [SerializeField] private float reverseFraction = 0.5f;
        [Tooltip("Target distance between particles in px. Lower = denser traffic.")]
        [SerializeField] private float particleSpacing = 18f;
        [SerializeField] private int maxParticles = 48;

        [Header("Look")]
        [Tooltip("Smallest particle diameter in px (drawn as a diamond).")]
        [SerializeField] private float minSize = 2.5f;
        [Tooltip("Largest particle diameter in px.")]
        [SerializeField] private float maxSize = 6f;
        [Tooltip("Max perpendicular drift from the line's center, px. Particles pick a lane within ±this.")]
        [SerializeField] private float maxPerpOffset = 5f;
        [Tooltip("Use the parent line Graphic's color so particles match the connection tint.")]
        [SerializeField] private bool inheritLineColor = true;
        [Tooltip("Lerp the inherited color toward white so particles stand out against the line (0 = line color, 1 = pure white).")]
        [Range(0f, 1f)]
        [SerializeField] private float whiten = 0.75f;
        [Tooltip("Alpha multiplier applied on top of the inherited line color.")]
        [SerializeField] private float alphaBoost = 1.5f;
        [Tooltip("Fade particles in/out near the line ends instead of popping.")]
        [SerializeField] private bool taperEnds = true;

        [Header("City Activity (buildings placed)")]
        [Tooltip("Placed buildings needed for particles to reach full size/speed. City feels sleepy when empty, busy when built up.")]
        [SerializeField] private int buildingsForFullActivity = 12;
        [Tooltip("Particle size multiplier with zero buildings (grows to 1 at full activity).")]
        [Range(0.1f, 1f)]
        [SerializeField] private float emptyCitySizeScale = 0.35f;
        [Tooltip("Particle speed multiplier with zero buildings (grows to 1 at full activity).")]
        [Range(0.1f, 1f)]
        [SerializeField] private float emptyCitySpeedScale = 0.5f;

        private RectTransform _rt;
        private Graphic _parentLine;
        /// <summary>0..1 fade used by the round-intro reveal (particles are the last beat). 1 = fully visible.</summary>
        public float RevealFade { get; set; } = 1f;

        private SimulationEngine _engine;
        private float _activity; // 0 = empty city, 1 = fully built up
        private float _flowTime; // activity-scaled clock so speed changes never teleport particles
        private int _seed;

        protected override void Awake()
        {
            base.Awake();
            _rt = rectTransform;
            raycastTarget = false;
            _seed = GetInstanceID(); // desync parallel lines

            // Stretch-fill the parent so our rect width == line length.
            _rt.anchorMin = Vector2.zero;
            _rt.anchorMax = Vector2.one;
            _rt.offsetMin = Vector2.zero;
            _rt.offsetMax = Vector2.zero;
            _rt.localRotation = Quaternion.identity;
            _rt.localScale = Vector3.one;

            if (transform.parent != null)
                _parentLine = transform.parent.GetComponent<Graphic>();
            _engine = GetComponentInParent<SimulationEngine>(true);
        }

        private void Update()
        {
            if (_rt.rect.width < 1f) return;

            // Ease activity toward the current building count so growth feels organic.
            if (_engine == null) _engine = GetComponentInParent<SimulationEngine>(true);
            float target = _engine != null && buildingsForFullActivity > 0
                ? Mathf.Clamp01((float)_engine.TileStates.Count / buildingsForFullActivity)
                : 1f;
            _activity = Mathf.MoveTowards(_activity, target, Time.deltaTime * 0.5f);
            if (Application.isPlaying)
                _flowTime += Time.deltaTime * Mathf.Lerp(emptyCitySpeedScale, 1f, _activity);

            if (inheritLineColor && _parentLine != null)
            {
                Color c = Color.Lerp(_parentLine.color, Color.white, whiten);
                c.a = Mathf.Clamp01(_parentLine.color.a * alphaBoost);
                if (c != color)
                    color = c; // setter marks vertices dirty
            }

            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_rt == null) return;

            Rect rect = _rt.rect;
            float length = rect.width;
            if (length < particleSpacing * 0.5f) return;

            int count = Mathf.Clamp(Mathf.CeilToInt(length / particleSpacing), 1, maxParticles);
            float midY = rect.center.y;
            Color baseColor = color;
            baseColor.a *= Mathf.Clamp01(RevealFade);
            if (baseColor.a <= 0.001f) return;

            float sizeScale = Mathf.Lerp(emptyCitySizeScale, 1f, _activity);

            for (int i = 0; i < count; i++)
            {
                float dir = Hash(i, 0) < reverseFraction ? -1f : 1f;
                float speed = Mathf.Lerp(minSpeed, maxSpeed, Hash(i, 1));
                float u = Mathf.Repeat(Hash(i, 2) + dir * speed * _flowTime / length, 1f);

                float x = rect.xMin + u * length;
                float y = midY + (Hash(i, 3) * 2f - 1f) * maxPerpOffset;
                float half = Mathf.Lerp(minSize, maxSize, Hash(i, 4)) * 0.5f * sizeScale;

                Color c = baseColor;
                // subtle per-particle brightness variation so the stream doesn't look uniform
                c.a *= Mathf.Lerp(0.55f, 1f, Hash(i, 5));
                if (taperEnds)
                    c.a *= Mathf.Sin(u * Mathf.PI);

                AddDiamond(vh, x, y, half, c);
            }
        }

        /// <summary>Deterministic pseudo-random in [0,1] from particle index + salt + instance seed.</summary>
        private float Hash(int i, int salt)
        {
            uint h = (uint)(i * 374761393 + salt * 668265263 + _seed * 144665);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }

        private static void AddDiamond(VertexHelper vh, float x, float y, float half, Color32 c)
        {
            int start = vh.currentVertCount;
            vh.AddVert(new Vector3(x, y + half, 0f), c, Vector2.zero);
            vh.AddVert(new Vector3(x + half, y, 0f), c, Vector2.zero);
            vh.AddVert(new Vector3(x, y - half, 0f), c, Vector2.zero);
            vh.AddVert(new Vector3(x - half, y, 0f), c, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
