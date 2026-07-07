using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace CityTwin.UI
{
    /// <summary>
    /// Entrance animation for the start screen: children fade + pop in staggered whenever the
    /// screen is (re)shown, and the main logo art gets a gentle idle float afterwards.
    /// Put on the Start Screen root; animates its direct children in sibling order.
    /// </summary>
    public class StartScreenIntroAnimator : MonoBehaviour
    {
        [Tooltip("Silence after load before the logo appears.")]
        [SerializeField] private float startDelay = 2f;
        [Tooltip("Extra pause between the logo landing and the first language button.")]
        [SerializeField] private float logoToButtonsDelay = 1f;
        [Tooltip("Gap between consecutive language buttons popping in.")]
        [SerializeField] private float stagger = 0.5f;
        [SerializeField] private float fadeDuration = 1f;
        [SerializeField] private float popScaleFrom = 0.88f;
        [Tooltip("Child that gets the slow idle float after entering (usually the logo art). Auto: first child.")]
        [SerializeField] private RectTransform floatTarget;
        [SerializeField] private float floatAmplitude = 8f;
        [SerializeField] private float floatPeriod = 3.2f;

        private readonly List<(RectTransform rt, CanvasGroup cg, Vector3 scale, Vector2 pos)> _items =
            new List<(RectTransform, CanvasGroup, Vector3, Vector2)>();

        private void Awake()
        {
            foreach (Transform c in transform)
            {
                var rt = c as RectTransform;
                if (rt == null) continue;
                var cg = c.GetComponent<CanvasGroup>();
                if (cg == null) cg = c.gameObject.AddComponent<CanvasGroup>();
                _items.Add((rt, cg, rt.localScale, rt.anchoredPosition));
            }
            if (floatTarget == null && _items.Count > 0)
                floatTarget = _items[0].rt;
        }

        private void OnEnable()
        {
            float delay = startDelay; // quiet beat after load
            for (int i = 0; i < _items.Count; i++)
            {
                var (rt, cg, scale, _) = _items[i];
                if (i == 1) delay += fadeDuration + logoToButtonsDelay; // logo lands, breath, then languages
                rt.DOKill(false);
                DOTween.Kill(cg);
                cg.alpha = 0f;
                rt.localScale = scale * popScaleFrom;

                cg.DOFade(1f, fadeDuration).SetDelay(delay).SetEase(Ease.OutQuad).SetUpdate(true).SetTarget(cg);
                rt.DOScale(scale, fadeDuration).SetDelay(delay).SetEase(Ease.OutBack).SetUpdate(true).SetTarget(rt);
                if (i > 0) delay += stagger;
            }

            if (floatTarget != null)
            {
                Vector2 basePos = BasePosOf(floatTarget);
                floatTarget.DOAnchorPosY(basePos.y + floatAmplitude, floatPeriod * 0.5f)
                    .SetDelay(delay + 0.2f)
                    .SetEase(Ease.InOutSine)
                    .SetLoops(-1, LoopType.Yoyo)
                    .SetUpdate(true)
                    .SetTarget(floatTarget);
            }
        }

        private void OnDisable()
        {
            foreach (var (rt, cg, scale, pos) in _items)
            {
                if (rt == null) continue;
                rt.DOKill(false);
                DOTween.Kill(cg);
                rt.localScale = scale;
                rt.anchoredPosition = pos;
                cg.alpha = 1f;
            }
        }

        private Vector2 BasePosOf(RectTransform rt)
        {
            foreach (var it in _items)
                if (it.rt == rt) return it.pos;
            return rt.anchoredPosition;
        }
    }
}
