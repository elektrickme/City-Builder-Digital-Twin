using extOSC;
using UnityEngine;
using CityTwin.Localization;
using CityTwin.Config;

namespace CityTwin.Core
{
    /// <summary>
    /// Root component for the master game instance prefab. Each copy of the prefab represents one game instance.
    /// Set InstanceId (0-3) and ListenPort (e.g. 3333 for TUIO simulator, 9001-9004 for multi-instance) per copy.
    /// Call ApplyOscConfig to override port/host from game_config.json at runtime. No statics.
    /// </summary>
    public class GameInstanceRoot : MonoBehaviour
    {
        [Tooltip("Quadrant/instance index 0-3. Used to identify this instance and select OSC source from config.")]
        [Range(0, 3)]
        [SerializeField] private int instanceId = 0;
        
        [SerializeField]
        private OSCReceiver oscReceiver;

        [Tooltip("UDP port this instance listens on for OSC. Use 3333 for TUIO simulator; 9001–9004 for multi-instance. Overridden at runtime if game_config.json has a matching OSC source.")]
        [SerializeField] private int listenPort = 3333;

        [SerializeField] private string host;

        [Tooltip("Per-instance localization. Used by LocalizedLabel and others. Assign on prefab or leave null to resolve from sibling.")]
        [SerializeField] private LocalizationService localizationService;

        /// <summary>Quadrant/instance index (0-3).</summary>
        public int InstanceId => instanceId;

        /// <summary>UDP port for this instance's OSC receiver. Any valid port 1024–65535.</summary>
        public int ListenPort => listenPort;

        /// <summary>Per-instance localization for this game instance. Resolved once from sibling if not assigned.</summary>
        public LocalizationService LocalizationService => _localizationService ??= localizationService != null ? localizationService : GetComponentInChildren<LocalizationService>(true);
        private LocalizationService _localizationService;


        private void OnValidate()
        {
            instanceId = Mathf.Clamp(instanceId, 0, 3);
            listenPort = Mathf.Clamp(listenPort, 1, 65535);
            SetPortAndHost(listenPort, host);
            
        }

        void Awake()
        {
            _localizationService = localizationService != null ? localizationService : GetComponentInChildren<LocalizationService>(true);
            SetPortAndHost(listenPort, host);
        }

        /// <summary>Apply OSC source from game_config.json. Matches by instanceId index into osc.sources[].
        /// Call after config is loaded (e.g. from GameInstanceCoordinator.OnEnable).</summary>
        public void ApplyOscConfig(GameConfig config)
        {
            if (config?.Osc?.sources == null || config.Osc.sources.Length == 0) return;
            if (instanceId < 0 || instanceId >= config.Osc.sources.Length)
            {
                Debug.LogWarning($"[GameInstanceRoot] instanceId {instanceId} out of range for osc.sources (length={config.Osc.sources.Length}). Keeping inspector values.");
                return;
            }

            var source = config.Osc.sources[instanceId];
            if (source.listenPort > 0)
                listenPort = Mathf.Clamp(source.listenPort, 1024, 65535);
            if (!string.IsNullOrEmpty(source.expectedSenderIp))
                host = source.expectedSenderIp;

            SetPortAndHost(listenPort, host);
        }

        private void SetPortAndHost(int _port, string _host)
        {
            if (oscReceiver != null)
            {
                oscReceiver.LocalPort = _port;
                oscReceiver.LocalHost = _host;
            }
        }
    }
}
