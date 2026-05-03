using UnityEngine;
using TMPro;
using DG.Tweening;

namespace CityTwin.UI
{
    /// <summary>
    /// Single tutorial speech bubble. TMP for text; CanvasGroup + RectTransform scale for DOTween fade/pop.
    /// </summary>
    public class TutorialPopup : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI label;
        [SerializeField] private CanvasGroup canvasGroup;

        [Header("DOTween (fade + pop)")]
        [SerializeField] private bool animate = true;
        [SerializeField] private float fadeInDuration = 0.38f;
        [SerializeField] private float fadeOutDuration = 0.28f;
        [SerializeField] private float popScaleFrom = 0.88f;
        [SerializeField] private Ease fadeEase = Ease.OutQuad;
        [SerializeField] private Ease popEase = Ease.OutBack;

        private RectTransform _rect;
        private Transform _tweenTarget;

        public TextMeshProUGUI Label => label;

        private void Awake()
        {
            _rect = transform as RectTransform;
            _tweenTarget = transform;
            if (canvasGroup == null)
                canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();
        }

        private void OnDestroy()
        {
            if (_tweenTarget != null)
                _tweenTarget.DOKill(false);
        }

        public void SetText(string text)
        {
            if (label != null)
                label.text = text;
        }

        /// <summary>Instant hide + reset (used between steps, StopTutorial, HideAll).</summary>
        public void HideImmediate()
        {
            if (_tweenTarget != null)
                _tweenTarget.DOKill(false);
            if (canvasGroup != null)
                canvasGroup.alpha = 0f;
            if (_rect != null)
                _rect.localScale = Vector3.one;
            gameObject.SetActive(false);
        }

        /// <summary>Fade in + slight scale pop. Returns null when <see cref="animate"/> is false.</summary>
        public Tween PlayShowTween()
        {
            gameObject.SetActive(true);

            if (!animate)
            {
                if (canvasGroup != null) canvasGroup.alpha = 1f;
                if (_rect != null) _rect.localScale = Vector3.one;
                return null;
            }

            _tweenTarget.DOKill(false);
            if (canvasGroup != null)
                canvasGroup.alpha = 0f;
            if (_rect != null)
                _rect.localScale = Vector3.one * popScaleFrom;

            var seq = DOTween.Sequence().SetTarget(_tweenTarget);
            if (canvasGroup != null)
                seq.Join(canvasGroup.DOFade(1f, fadeInDuration).SetEase(fadeEase));
            if (_rect != null)
                seq.Join(_rect.DOScale(1f, fadeInDuration).SetEase(popEase));
            return seq;
        }

        /// <summary>Fade out then deactivate. Returns null when <see cref="animate"/> is false.</summary>
        public Tween PlayHideTween()
        {
            if (!animate)
            {
                HideImmediate();
                return null;
            }

            _tweenTarget.DOKill(false);
            var seq = DOTween.Sequence().SetTarget(_tweenTarget);
            if (canvasGroup != null)
                seq.Join(canvasGroup.DOFade(0f, fadeOutDuration).SetEase(Ease.InQuad));
            if (_rect != null)
                seq.Join(_rect.DOScale(popScaleFrom, fadeOutDuration).SetEase(Ease.InQuad));
            seq.OnComplete(() =>
            {
                gameObject.SetActive(false);
                if (_rect != null)
                    _rect.localScale = Vector3.one;
            });
            return seq;
        }

        /// <summary>Non-tutorial callers (e.g. inactivity): animated show.</summary>
        public void Show()
        {
            PlayShowTween();
        }

        /// <summary>Non-tutorial callers: instant dismiss.</summary>
        public void Hide()
        {
            HideImmediate();
        }
    }
}
