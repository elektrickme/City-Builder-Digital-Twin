using System.Collections;
using UnityEngine;
using CityTwin.Simulation;
using CityTwin.UI;

namespace CityTwin.Core
{
    /// <summary>
    /// Drives the end-of-session restart flow for a single game instance:
    ///   1. Session timer ends -> wait for the play field to be cleared of all tiles.
    ///   2. Once empty, count down "Restarting in N..." and call GameInstanceCoordinator.RestartGame().
    ///   3. If a tile is placed during the countdown, reset back to step 1.
    /// All UI is driven through EndScreenController — this component holds no UI fields.
    /// </summary>
    public class RestartFlowController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private GameInstanceCoordinator coordinator;
        [Tooltip("End screen that owns the overlay panel and restart status text.")]
        [SerializeField] private EndScreenController endScreen;

        [Header("Messages")]
        [Tooltip("Shown while the player still has tiles on the play field after the game ends.")]
        [SerializeField] private string removeTilesMessage = "Please remove all tiles from the play field";
        [Tooltip("Format string for the countdown. {0} = seconds remaining.")]
        [SerializeField] private string countdownFormat = "Restarting in {0}...";

        [Header("Timing")]
        [Tooltip("How many seconds the countdown runs after the field is confirmed empty.")]
        [SerializeField] private int countdownSeconds = 3;

        private enum FlowState { Idle, WaitingForEmpty, CountingDown }
        private FlowState _state = FlowState.Idle;
        private Coroutine _countdownRoutine;

        private void Awake()
        {
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true) ?? GetComponentInParent<SessionTimer>();
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true) ?? GetComponentInParent<SimulationEngine>();
            if (coordinator == null) coordinator = GetComponentInChildren<GameInstanceCoordinator>(true) ?? GetComponentInParent<GameInstanceCoordinator>();
            if (endScreen == null) endScreen = GetComponentInChildren<EndScreenController>(true) ?? GetComponentInParent<EndScreenController>();
        }

        private void OnEnable()
        {
            if (sessionTimer != null)
                sessionTimer.OnTimerEnded += HandleTimerEnded;
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged += HandleMetricsChanged;
        }

        private void OnDisable()
        {
            if (sessionTimer != null)
                sessionTimer.OnTimerEnded -= HandleTimerEnded;
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged -= HandleMetricsChanged;

            if (_countdownRoutine != null)
            {
                StopCoroutine(_countdownRoutine);
                _countdownRoutine = null;
            }
            _state = FlowState.Idle;
        }

        private void HandleTimerEnded()
        {
            EnterWaitingForEmpty();
        }

        private void HandleMetricsChanged()
        {
            if (_state == FlowState.Idle) return;

            int tileCount = simulationEngine != null ? simulationEngine.PlacedTileCount : 0;

            if (_state == FlowState.WaitingForEmpty && tileCount == 0)
            {
                StartCountdown();
                return;
            }

            if (_state == FlowState.CountingDown && tileCount > 0)
            {
                // Player placed a tile during the countdown — reset to waiting.
                if (_countdownRoutine != null)
                {
                    StopCoroutine(_countdownRoutine);
                    _countdownRoutine = null;
                }
                EnterWaitingForEmpty();
            }
        }

        private void EnterWaitingForEmpty()
        {
            _state = FlowState.WaitingForEmpty;
            ShowMessage(removeTilesMessage);

            // If the field is already empty (no tiles ever placed, or all removed before timer end),
            // jump straight to the countdown.
            int tileCount = simulationEngine != null ? simulationEngine.PlacedTileCount : 0;
            if (tileCount == 0)
                StartCountdown();
        }

        private void StartCountdown()
        {
            if (_countdownRoutine != null)
                StopCoroutine(_countdownRoutine);
            _countdownRoutine = StartCoroutine(CountdownRoutine());
        }

        private IEnumerator CountdownRoutine()
        {
            _state = FlowState.CountingDown;
            for (int n = countdownSeconds; n > 0; n--)
            {
                ShowMessage(string.Format(countdownFormat, n));
                yield return new WaitForSeconds(1f);

                // Abort if state changed (tile was placed -> HandleMetricsChanged reset us).
                if (_state != FlowState.CountingDown)
                    yield break;
            }

            _countdownRoutine = null;
            _state = FlowState.Idle;
            coordinator?.RestartGame();
        }

        private void ShowMessage(string message)
        {
            endScreen?.SetRestartStatus(message);
        }
    }
}