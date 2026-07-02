using UnityEngine;
using TMPro;
using DG.Tweening;

namespace CityTwin.UI
{
    /// <summary>
    /// Owns the end-of-session screen UI: the overlay panel, title/body text,
    /// and the restart status line used by RestartFlowController.
    /// Other components (TooltipService, RestartFlowController) drive this via its public API
    /// instead of holding direct references to the UI fields.
    /// </summary>
    public class EndScreenController : MonoBehaviour
    {
        [SerializeField] private DashboardController _dashboardController;
        
        [Header("End Screen UI")]
        [Tooltip("Root GameObject of the end screen overlay. Toggled on when the session ends, off on restart.")]
        [SerializeField] private GameObject endPanel;
        [SerializeField] private TextMeshProUGUI endTitleText;
        [SerializeField] private TextMeshProUGUI endBodyText;
        [SerializeField] private TextMeshProUGUI QOLText;

        [Header("Restart Flow UI")]
        [Tooltip("Text shown during the restart flow (remove-tiles prompt, then countdown).")]
        [SerializeField] private TextMeshProUGUI restartStatusText;

        [Header("Restart Status Pulse")]
        [Tooltip("Gently pulse the restart status line while it shows a message (e.g. the clear-tiles prompt).")]
        [SerializeField] private bool pulseRestartStatus = true;
        [Tooltip("Peak scale of the pulse relative to the resting scale.")]
        [SerializeField] private float pulseScaleAmount = 1.06f;
        [Tooltip("Seconds for one half of the pulse (grow or shrink). Full breath = twice this.")]
        [SerializeField] private float pulseHalfPeriodSeconds = 0.6f;

        [Header("End Report Cards")]
        [Tooltip("Body text of the Balance card (feedback.* localization). Set by TutorialSequenceController.")]
        [SerializeField] private TextMeshProUGUI balanceBodyText;
        [Tooltip("Body text of the Strategic card (reaction.*.access.v2 localization). Set by TutorialSequenceController.")]
        [SerializeField] private TextMeshProUGUI strategicBodyText;

        public bool IsVisible => endPanel != null && endPanel.activeSelf;

        /// <summary>Activate the overlay and fill in the final title/body text. Clears any prior restart status.</summary>
        public void Show(string title, string body)
        {
            if (endPanel != null) endPanel.SetActive(true);
            if (endTitleText != null) endTitleText.text = title ?? string.Empty;
            if (endBodyText != null) endBodyText.text = body ?? string.Empty;
            
            //Set QOL Score
            QOLText.text = Mathf.RoundToInt(_dashboardController.DisplayQol).ToString();
            
            SetRestartStatus(string.Empty);
        }

        /// <summary>Hide the overlay and clear the restart status.</summary>
        public void Hide()
        {
            if (endPanel != null) endPanel.SetActive(false);
            SetRestartStatus(string.Empty);
        }

        /// <summary>Update the restart status line (e.g. "Please remove all tiles" / "Restarting in 3...").
        /// Pulses while non-empty; text changes mid-pulse do not restart the loop.</summary>
        public void SetRestartStatus(string message)
        {
            if (restartStatusText == null) return;
            restartStatusText.text = message ?? string.Empty;
            if (!string.IsNullOrEmpty(message) && pulseRestartStatus) StartStatusPulse();
            else StopStatusPulse();
        }

        private Tween _statusPulse;
        private Vector3 _statusBaseScale = Vector3.one;
        private bool _statusBaseScaleCaptured;

        private void StartStatusPulse()
        {
            if (_statusPulse != null && _statusPulse.IsActive()) return;   // already breathing

            Transform t = restartStatusText.transform;
            // Capture the resting scale once, before any pulse, so repeated prompts never compound.
            if (!_statusBaseScaleCaptured)
            {
                _statusBaseScale = t.localScale;
                _statusBaseScaleCaptured = true;
            }
            t.localScale = _statusBaseScale;
            _statusPulse = t.DOScale(_statusBaseScale * pulseScaleAmount, pulseHalfPeriodSeconds)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .SetUpdate(true)
                .SetTarget(t);
        }

        private void StopStatusPulse()
        {
            if (_statusPulse != null) { _statusPulse.Kill(); _statusPulse = null; }
            if (restartStatusText != null && _statusBaseScaleCaptured)
                restartStatusText.transform.localScale = _statusBaseScale;
        }

        private void OnDisable()
        {
            StopStatusPulse();
        }

        /// <summary>Fill the end-report card bodies (Balance, Strategic). QOL is rendered by <see cref="Show"/>;
        /// this only populates the cards, so call order with Show does not matter. Card titles are static UI.</summary>
        public void SetReport(string balanceBody, string strategicBody)
        {
            if (balanceBodyText != null) balanceBodyText.text = balanceBody ?? string.Empty;
            if (strategicBodyText != null) strategicBodyText.text = strategicBody ?? string.Empty;
        }
    }
}
