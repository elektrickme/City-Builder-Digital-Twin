using UnityEngine;
using UnityEngine.UI;
using CityTwin.Core;

namespace CityTwin.UI
{
    /// <summary>Instantly restarts this game instance (clear tiles, new hub layout, fresh timer)
    /// when the attached Button is clicked. Playtest-only affordance; lives in the Playtest UI
    /// folder that gets disabled for the real installation.</summary>
    [RequireComponent(typeof(Button))]
    public class RestartGameButton : MonoBehaviour
    {
        [SerializeField] private GameInstanceCoordinator coordinator;

        private void Awake()
        {
            if (coordinator == null)
            {
                var root = GetComponentInParent<GameInstanceRoot>(true);
                if (root != null)
                    coordinator = root.GetComponentInChildren<GameInstanceCoordinator>(true);
            }
            if (coordinator == null)
                coordinator = GetComponentInParent<GameInstanceCoordinator>();

            GetComponent<Button>().onClick.AddListener(Restart);
        }

        private void Restart()
        {
            if (coordinator != null) coordinator.RestartGame();
            else Debug.LogWarning("[RestartGameButton] No GameInstanceCoordinator found; restart ignored.", this);
        }
    }
}
