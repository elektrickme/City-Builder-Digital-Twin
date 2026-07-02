using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using CityTwin.Core;
using CityTwin.Config;
using CityTwin.Localization;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>Intro sequence, runtime tips, end band messages. JSON-driven; no statics.</summary>
    public class TooltipService : MonoBehaviour
    {
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private LocalizationService localization;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private SimulationEngine simulationEngine;
        [Header("UI")]
        [SerializeField] private TextMeshProUGUI statusBarText;
        [SerializeField] private EndScreenController endScreen;
        [Tooltip("Localization key the status bar switches to when the session ends (end-screen header).")]
        [SerializeField] private string endHeaderKey = "report.title";

        private int _introKeyIndex;
        private float _nextIntroTime;

        private void Awake()
        {
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true);
            if (localization == null) localization = GetComponentInChildren<LocalizationService>(true);
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true);
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
            if (endScreen == null) endScreen = GetComponentInChildren<EndScreenController>(true);
        }

        private void OnEnable()
        {
            if (sessionTimer != null)
            {
                sessionTimer.OnPhaseChanged += OnPhaseChanged;
                sessionTimer.OnTimerEnded += OnTimerEnded;
            }
        }

        private void OnDisable()
        {
            if (sessionTimer != null)
            {
                sessionTimer.OnPhaseChanged -= OnPhaseChanged;
                sessionTimer.OnTimerEnded -= OnTimerEnded;
            }
        }

        private void Update()
        {
            if (sessionTimer != null && sessionTimer.CurrentPhase == SessionTimer.Phase.Gameplay && sessionTimer.IsRunning)
            {
                if (Time.time >= _nextIntroTime && configLoader?.Config?.Tooltips?.introKeys != null && _introKeyIndex < configLoader.Config.Tooltips.introKeys.Length)
                {
                    string key = configLoader.Config.Tooltips.introKeys[_introKeyIndex++];
                    if (statusBarText != null && localization != null)
                        statusBarText.text = localization.GetString(key);
                    _nextIntroTime = Time.time + 8f;
                }
            }
        }

        private void OnPhaseChanged(SessionTimer.Phase phase)
        {
            if (phase == SessionTimer.Phase.Gameplay)
            {
                // A new session just started (fresh game or restart) — hide the end screen and reset intro sequence.
                endScreen?.Hide();
                _introKeyIndex = 0;
                _nextIntroTime = 0f;
            }
            if (phase == SessionTimer.Phase.Gameplay && statusBarText != null && localization != null)
                statusBarText.text = localization.GetString("ui.timer");
        }

        private void OnTimerEnded()
        {
            // Flip the status bar from the gameplay label (ui.timer) to the end-screen header.
            if (statusBarText != null && localization != null && !string.IsNullOrEmpty(endHeaderKey))
                statusBarText.text = localization.GetString(endHeaderKey);

            int qol = simulationEngine != null ? Mathf.RoundToInt(simulationEngine.Qol) : 0;
            var cfg = configLoader?.Config?.EndMessages;
            string title = string.Empty;
            string body = string.Empty;
            if (cfg != null && localization != null)
            {
                foreach (var msg in cfg)
                {
                    if (qol >= msg.min && qol <= msg.max)
                    {
                        title = localization.GetString(msg.titleKey);
                        body = localization.GetString(msg.bodyKey);
                        break;
                    }
                }
            }
            endScreen?.Show(title, body);
        }
    }
}
