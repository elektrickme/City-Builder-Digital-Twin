using System;
using UnityEngine;
using CityTwin.Config;

namespace CityTwin.Core
{
    /// <summary>Per-instance session timer: intro then gameplay then end. No statics.</summary>
    public class SessionTimer : MonoBehaviour
    {
        [SerializeField] private int gameplaySeconds = 270;

        private float _remainingSeconds;
        private Phase _phase = Phase.Gameplay;
        private bool _running;

        public enum Phase { Gameplay, End }
        public Phase CurrentPhase => _phase;
        public float RemainingSeconds => _remainingSeconds;
        public bool IsRunning => _running;

        public event Action<Phase> OnPhaseChanged;
        public event Action OnTimerEnded;

        public void SetFromConfig(GameConfig config)
        {
            if (config?.Session == null) return;
            gameplaySeconds = config.Session.gameplaySeconds;
        }

        public void StartSession()
        {
            _phase = Phase.Gameplay;
            _remainingSeconds = gameplaySeconds;
            _running = true;
            OnPhaseChanged?.Invoke(_phase);
        }

        public void Stop()
        {
            _running = false;
        }

        /// <summary>Configured session length in seconds (debug/playtest readout).</summary>
        public int GameplaySeconds => gameplaySeconds;

        /// <summary>Debug/playtest: set time left on the current countdown. The HUD reflects it next frame.</summary>
        public void SetRemainingSeconds(float seconds)
        {
            _remainingSeconds = Mathf.Max(0f, seconds);
        }

        /// <summary>Debug/playtest: set session length and reset the current countdown to it.</summary>
        public void SetGameplaySeconds(int seconds)
        {
            gameplaySeconds = Mathf.Max(0, seconds);
            _remainingSeconds = gameplaySeconds;
        }

        private void Update()
        {
            if (!_running) return;
            _remainingSeconds -= Time.deltaTime;
            if (_remainingSeconds <= 0)
            {
                _phase = Phase.End;
                _running = false;
                OnPhaseChanged?.Invoke(_phase);
                OnTimerEnded?.Invoke();
            }
        }

        public string FormatTime()
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(_remainingSeconds));
            int m = total / 60;
            int s = total % 60;
            return $"{m:D2}:{s:D2}";
        }
    }
}
