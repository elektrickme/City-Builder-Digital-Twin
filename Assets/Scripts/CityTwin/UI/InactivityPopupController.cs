using UnityEngine;
using CityTwin.Config;
using CityTwin.Core;
using CityTwin.Input;
using CityTwin.Localization;

namespace CityTwin.UI
{
    /// <summary>
    /// Shows a popup after X seconds of no tile activity (place/move/remove).
    /// Resets and hides on any tile event or when the session timer ends.
    /// </summary>
    public class InactivityPopupController : MonoBehaviour
    {
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private LocalizationService localization;
        [SerializeField] private TileTrackingManager tileTracking;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private GameInstanceCoordinator coordinator;

        [Header("UI")]
        [SerializeField] private TutorialPopup popup;

        private float _timeoutSeconds = 30f;
        private string _textKey = "ui.inactivity";
        private float _idleTimer;
        private bool _popupVisible;
        private bool _active;

        /// <summary>Live inactivity timeout in seconds. The debug/playtest menu reads and writes this.</summary>
        public float TimeoutSeconds { get => _timeoutSeconds; set => _timeoutSeconds = Mathf.Max(1f, value); }

        private void Awake()
        {
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true) ?? GetComponentInParent<GameConfigLoader>();
            if (localization == null) localization = GetComponentInChildren<LocalizationService>(true) ?? GetComponentInParent<LocalizationService>();
            if (tileTracking == null) tileTracking = GetComponentInChildren<TileTrackingManager>(true) ?? GetComponentInParent<TileTrackingManager>();
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true) ?? GetComponentInParent<SessionTimer>();
            if (coordinator == null) coordinator = GetComponentInChildren<GameInstanceCoordinator>(true) ?? GetComponentInParent<GameInstanceCoordinator>();
        }

        private void OnEnable()
        {
            if (coordinator != null)
                coordinator.OnTileActivity += OnActivity;
            if (sessionTimer != null)
                sessionTimer.OnTimerEnded += OnSessionEnded;

            ApplyConfig();
        }

        private void OnDisable()
        {
            if (coordinator != null)
                coordinator.OnTileActivity -= OnActivity;
            if (sessionTimer != null)
                sessionTimer.OnTimerEnded -= OnSessionEnded;
        }

        public void SetFromConfig(GameConfig config)
        {
            if (config?.Inactivity == null) return;
            _timeoutSeconds = config.Inactivity.timeoutSeconds > 0 ? config.Inactivity.timeoutSeconds : 30f;
            _textKey = config.Inactivity.textKey ?? "ui.inactivity";
        }

        /// <summary>Enable inactivity tracking. Call after tutorial finishes and gameplay starts.</summary>
        public void Activate()
        {
            _active = true;
            ResetTimer();
        }

        /// <summary>Disable inactivity tracking and hide popup.</summary>
        public void Deactivate()
        {
            _active = false;
            HidePopup();
        }

        private void Update()
        {
            if (!_active) return;
            if (sessionTimer != null && sessionTimer.CurrentPhase == SessionTimer.Phase.End) return;

            _idleTimer += Time.deltaTime;

            if (!_popupVisible && _idleTimer >= _timeoutSeconds)
                ShowPopup();
        }

        private void OnActivity()
        {
            ResetTimer();
        }

        private void OnSessionEnded()
        {
            Deactivate();
        }

        private void ResetTimer()
        {
            _idleTimer = 0f;
            HidePopup();
        }

        private void ShowPopup()
        {
            if (popup == null) return;
            string text = localization != null ? localization.GetString(_textKey) : _textKey;
            popup.SetText(text);
            popup.Show();
            _popupVisible = true;
        }

        private void HidePopup()
        {
            if (popup == null) return;
            popup.Hide();
            _popupVisible = false;
        }

        private void ApplyConfig()
        {
            if (configLoader?.Config != null)
                SetFromConfig(configLoader.Config);
        }
    }
}
