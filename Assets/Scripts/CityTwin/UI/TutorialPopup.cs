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

        [Header("Split avatar/bubble (auto-found by child name)")]
        [Tooltip("The assistant avatar; shown once and kept on screen while only the bubble swaps between steps.")]
        [SerializeField] private RectTransform avatar;
        [SerializeField] private string avatarName = "Assistant Icon";
        [SerializeField] private RectTransform bubble;
        [SerializeField] private string bubbleName = "Assistant Bubble";

        private RectTransform _rect;
        private Transform _tweenTarget;
        private CanvasGroup _avatarGroup, _bubbleGroup;

        public TextMeshProUGUI Label => label;

        /// <summary>True when the popup has separate avatar/bubble parts that can animate independently.</summary>
        public bool HasSplitParts => avatar != null && bubble != null;

        /// <summary>Background Image of the speech bubble — style template for runtime-built bubbles.
        /// Lives on a layout-ignored "BG" child so the sprite never dictates the bubble's height.</summary>
        public UnityEngine.UI.Image BubbleBackgroundImage
        {
            get
            {
                if (bubble == null) return null;
                var own = bubble.GetComponent<UnityEngine.UI.Image>();
                return own != null ? own : bubble.GetComponentInChildren<UnityEngine.UI.Image>(true);
            }
        }

        private void Awake()
        {
            _rect = transform as RectTransform;
            _tweenTarget = transform;
            if (canvasGroup == null)
                canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();

            if (avatar == null) avatar = FindChild(avatarName);
            if (bubble == null) bubble = FindChild(bubbleName);
            if (avatar != null) _avatarGroup = GetOrAddGroup(avatar.gameObject);
            if (bubble != null) _bubbleGroup = GetOrAddGroup(bubble.gameObject);
        }

        private RectTransform FindChild(string childName)
        {
            foreach (var rt in GetComponentsInChildren<RectTransform>(true))
                if (rt != transform && rt.name == childName) return rt;
            return null;
        }

        private static CanvasGroup GetOrAddGroup(GameObject go)
        {
            var cg = go.GetComponent<CanvasGroup>();
            return cg != null ? cg : go.AddComponent<CanvasGroup>();
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
            if (_avatarGroup != null) { DOTween.Kill(_avatarGroup); _avatarGroup.alpha = 1f; }
            if (_bubbleGroup != null) { DOTween.Kill(_bubbleGroup); _bubbleGroup.alpha = 1f; }
            if (avatar != null) avatar.localScale = Vector3.one;
            if (bubble != null) bubble.localScale = Vector3.one;
            if (canvasGroup != null)
                canvasGroup.alpha = 0f;
            if (_rect != null)
                _rect.localScale = Vector3.one;
            gameObject.SetActive(false);
        }

        /// <summary>Show only the avatar (bubble hidden): first beat of the assistant's entrance.</summary>
        public Tween PlayAvatarShowTween()
        {
            gameObject.SetActive(true);
            if (canvasGroup != null) canvasGroup.alpha = 1f;
            if (_rect != null) _rect.localScale = Vector3.one;
            if (_bubbleGroup != null) _bubbleGroup.alpha = 0f;
            if (_avatarGroup == null) return null;

            if (!animate) { _avatarGroup.alpha = 1f; return null; }

            _avatarGroup.DOKill(false);
            _avatarGroup.alpha = 0f;
            avatar.localScale = Vector3.one * popScaleFrom;
            var seq = DOTween.Sequence().SetTarget(_avatarGroup);
            seq.Join(_avatarGroup.DOFade(1f, fadeInDuration).SetEase(fadeEase));
            seq.Join(avatar.DOScale(1f, fadeInDuration).SetEase(popEase));
            return seq;
        }

        /// <summary>Pop just the speech bubble in; the avatar (and popup root) stay as they are.</summary>
        public Tween PlayBubbleShowTween()
        {
            gameObject.SetActive(true);
            if (canvasGroup != null) canvasGroup.alpha = 1f;
            if (_avatarGroup != null) _avatarGroup.alpha = 1f;
            if (_bubbleGroup == null) return PlayShowTween();

            if (!animate) { _bubbleGroup.alpha = 1f; return null; }

            _bubbleGroup.DOKill(false);
            _bubbleGroup.alpha = 0f;
            bubble.localScale = Vector3.one * popScaleFrom;
            var seq = DOTween.Sequence().SetTarget(_bubbleGroup);
            seq.Join(_bubbleGroup.DOFade(1f, fadeInDuration).SetEase(fadeEase));
            seq.Join(bubble.DOScale(1f, fadeInDuration).SetEase(popEase));
            return seq;
        }

        /// <summary>Fade just the speech bubble out; the avatar stays on screen between steps.</summary>
        public Tween PlayBubbleHideTween()
        {
            if (_bubbleGroup == null) return PlayHideTween();
            if (!animate) { _bubbleGroup.alpha = 0f; return null; }

            _bubbleGroup.DOKill(false);
            var seq = DOTween.Sequence().SetTarget(_bubbleGroup);
            seq.Join(_bubbleGroup.DOFade(0f, fadeOutDuration).SetEase(Ease.InQuad));
            seq.Join(bubble.DOScale(popScaleFrom, fadeOutDuration).SetEase(Ease.InQuad));
            seq.OnComplete(() => { if (bubble != null) bubble.localScale = Vector3.one; });
            return seq;
        }

        /// <summary>Fade in + slight scale pop. Returns null when <see cref="animate"/> is false.</summary>
        public Tween PlayShowTween()
        {
            gameObject.SetActive(true);
            if (_avatarGroup != null) _avatarGroup.alpha = 1f;
            if (_bubbleGroup != null) _bubbleGroup.alpha = 1f;

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
