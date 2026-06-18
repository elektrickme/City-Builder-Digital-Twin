using System;
using System.Collections;
using UnityEngine;
using DG.Tweening;
using CityTwin.Config;
using CityTwin.Localization;

namespace CityTwin.UI
{
    /// <summary>
    /// Cycles through child TutorialPopup GameObjects one at a time.
    /// Each step displays localized text for a config-driven duration, with DOTween fade/pop in and out.
    /// Fires OnTutorialComplete when all steps finish.
    /// </summary>
    public class TutorialSequenceController : MonoBehaviour
    {
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private LocalizationService localization;

        [Tooltip("Child TutorialPopup components in display order. If empty, auto-collected from children at Awake.")]
        [SerializeField] private TutorialPopup[] popups;

        private Coroutine _sequenceRoutine;
        private bool _isRunning;

        public bool IsRunning => _isRunning;

        public event Action OnTutorialComplete;

        private void Awake()
        {
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true) ?? GetComponentInParent<GameConfigLoader>();
            if (localization == null) localization = GetComponentInChildren<LocalizationService>(true) ?? GetComponentInParent<LocalizationService>();

            if (popups == null || popups.Length == 0)
                popups = GetComponentsInChildren<TutorialPopup>(true);

            HideAll();
        }

        public void StartTutorial()
        {
            if (_sequenceRoutine != null)
                StopCoroutine(_sequenceRoutine);

            _sequenceRoutine = StartCoroutine(RunSequence());
        }

        public void StopTutorial()
        {
            if (_sequenceRoutine != null)
            {
                StopCoroutine(_sequenceRoutine);
                _sequenceRoutine = null;
            }
            _isRunning = false;
            HideAll();
        }

        private IEnumerator RunSequence()
        {
            _isRunning = true;
            HideAll();

            var steps = configLoader?.Config?.Tutorial?.steps;
            if (steps == null || steps.Length == 0 || popups == null || popups.Length == 0)
            {
                Debug.LogWarning("[TutorialSequence] No tutorial steps or popups configured — skipping tutorial.");
                _isRunning = false;
                _sequenceRoutine = null;
                OnTutorialComplete?.Invoke();
                yield break;
            }

            int count = Mathf.Min(steps.Length, popups.Length);

            for (int i = 0; i < count; i++)
            {
                var step = steps[i];
                var popup = popups[i];

                string text = localization != null ? localization.GetString(step.textKey) : step.textKey;
                popup.SetText(text);

                var showTween = popup.PlayShowTween();
                if (showTween != null)
                    yield return showTween.WaitForCompletion(true);

                float duration = step.durationSeconds > 0 ? step.durationSeconds : 5f;
                yield return new WaitForSeconds(duration);

                var hideTween = popup.PlayHideTween();
                if (hideTween != null)
                    yield return hideTween.WaitForCompletion(true);
            }

            _isRunning = false;
            _sequenceRoutine = null;
            OnTutorialComplete?.Invoke();
        }

        private void HideAll()
        {
            if (popups == null) return;
            foreach (var p in popups)
            {
                if (p != null)
                    p.HideImmediate();
            }
        }
    }
}
