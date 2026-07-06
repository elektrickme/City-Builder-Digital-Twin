using UnityEngine;
using UnityEngine.EventSystems;
using DG.Tweening;

namespace CityTwin.UI
{
    /// <summary>
    /// Lightweight hover/press feedback for UI buttons: grows slightly on hover, dips on press,
    /// springs back on release. Works with mouse and touch through the EventSystem; no Button
    /// component required (raycastable Graphic is enough).
    /// </summary>
    public class UIButtonFx : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        [SerializeField] private float hoverScale = 1.07f;
        [SerializeField] private float pressScale = 0.93f;
        [SerializeField] private float tweenDuration = 0.14f;
        [SerializeField] private Ease ease = Ease.OutQuad;

        private Vector3 _baseScale;
        private bool _hovered;
        private bool _captured;

        private void Awake()
        {
            _baseScale = transform.localScale;
        }

        private void OnDisable()
        {
            transform.DOKill(false);
            transform.localScale = _baseScale;
            _hovered = false;
            _captured = false;
        }

        public void OnPointerEnter(PointerEventData _)
        {
            _hovered = true;
            if (!_captured) TweenTo(_baseScale * hoverScale);
        }

        public void OnPointerExit(PointerEventData _)
        {
            _hovered = false;
            if (!_captured) TweenTo(_baseScale);
        }

        public void OnPointerDown(PointerEventData _)
        {
            _captured = true;
            TweenTo(_baseScale * pressScale);
        }

        public void OnPointerUp(PointerEventData _)
        {
            _captured = false;
            // release: spring back to hover (still over the button) or rest with a little overshoot
            transform.DOKill(false);
            transform.DOScale(_hovered ? _baseScale * hoverScale : _baseScale, tweenDuration * 1.6f)
                .SetEase(Ease.OutBack)
                .SetUpdate(true)
                .SetTarget(transform);
        }

        private void TweenTo(Vector3 target)
        {
            transform.DOKill(false);
            transform.DOScale(target, tweenDuration).SetEase(ease).SetUpdate(true).SetTarget(transform);
        }
    }
}
