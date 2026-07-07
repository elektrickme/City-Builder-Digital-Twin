using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace CityTwin.UI
{
    /// <summary>Rectangular drop target above the play field: releasing a dragged building inside
    /// it removes the building. A reliable alternative to dropping outside the fan-shaped table
    /// bounds. Visuals: subtle at rest, brighter while a drag is live, full-on while hovering it.</summary>
    public class ClearDropZone : MonoBehaviour
    {
        [SerializeField] private Image frame;
        [SerializeField] private TextMeshProUGUI label;
        [Tooltip("Frame alpha when nothing is being dragged.")]
        [Range(0f, 1f)] [SerializeField] private float idleAlpha = 0.35f;
        [Tooltip("Frame alpha while a building is being dragged anywhere.")]
        [Range(0f, 1f)] [SerializeField] private float armedAlpha = 0.65f;
        [Tooltip("Frame alpha while the dragged building hovers over the zone.")]
        [Range(0f, 1f)] [SerializeField] private float hoverAlpha = 1f;
        [SerializeField] private float fadeSeconds = 0.15f;

        private RectTransform _rect;
        private float _target;
        private float _current = -1f;

        private void Awake()
        {
            _rect = (RectTransform)transform;
            if (frame == null) frame = GetComponent<Image>();
            if (label == null) label = GetComponentInChildren<TextMeshProUGUI>(true);
            _target = idleAlpha;
        }

        /// <summary>Screen-space containment test used by the drag handlers on release.</summary>
        public bool ContainsScreenPoint(Vector2 screenPos, Camera cam)
        {
            var rect = _rect != null ? _rect : (RectTransform)transform;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, screenPos, cam);
        }

        /// <summary>Drive the visual state from the active drag: armed while dragging, hot while hovered.</summary>
        public void SetState(bool dragging, bool hovering)
        {
            _target = hovering ? hoverAlpha : dragging ? armedAlpha : idleAlpha;
        }

        private void Update()
        {
            if (Mathf.Approximately(_current, _target)) return;
            _current = _current < 0f
                ? _target
                : Mathf.MoveTowards(_current, _target, Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeSeconds));
            if (frame != null) { var c = frame.color; c.a = _current; frame.color = c; }
            if (label != null) { var c = label.color; c.a = Mathf.Clamp01(_current * 2.2f); label.color = c; }
        }
    }
}
