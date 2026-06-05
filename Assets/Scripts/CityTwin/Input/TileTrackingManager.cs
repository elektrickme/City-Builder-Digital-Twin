using System;
using System.Collections.Generic;
using UnityEngine;
using extOSC;
using CityTwin.Core;
using extOSC.Core;

namespace CityTwin.Input
{
    /// <summary>Per-instance OSC tile tracking. One receiver per instance; port from GameInstanceRoot. No statics.</summary>
    [RequireComponent(typeof(OSCReceiver))]
    public class TileTrackingManager : MonoBehaviour, ITileSource
    {
        [Tooltip("Optional: map TUIO classId to building id. Empty = use classId as string.")]
        [SerializeField] private List<ClassIdToBuilding> classIdToBuilding = new List<ClassIdToBuilding>();

        [Tooltip("If true, Local Port is set from Game Instance Root's Listen Port each OnEnable. If false, the OSC Receiver's Inspector port is left as-is.")]
        [SerializeField] private bool useInstancePort = true;

        [Header("Local testing")]
        [Tooltip("When enabled, any TUIO classId not in the mapping list is treated as this building (e.g. TUIO simulator classId 33 → garden). Disable for production.")]
        [SerializeField] private bool useLocalTestingFallback = false;
        [Tooltip("Building id to use when Use Local Testing Fallback is on and classId has no mapping (e.g. garden, park).")]
        [SerializeField] private string localTestingBuildingId = "garden";

        [Header("Smoothing (EMA)")]
        [Tooltip("Smooth incoming TUIO positions with an exponential moving average to reduce jitter. Disable to use raw positions.")]
        [SerializeField] private bool smoothPosition = true;
        [Tooltip("EMA weight for the newest sample (0-1). Lower = smoother but more lag; higher = more responsive but more jitter.")]
        [Range(0.01f, 1f)]
        [SerializeField] private float positionSmoothingAlpha = 0.3f;

        public event Action<TilePose> OnTileUpdated;
        public event Action<string> OnTileRemoved;

        [SerializeField] private GameInstanceRoot _instanceRoot;
        [SerializeField] private OSCReceiver _receiver;
        private IOSCBind _bind;
        private readonly Dictionary<int, string> _sessionToTileId = new Dictionary<int, string>();
        private readonly Dictionary<int, Vector2> _sessionToSmoothedPos = new Dictionary<int, Vector2>();
        private HashSet<int> _lastAlive = new HashSet<int>();
        private int _nextLocalId;

        [Serializable]
        public class ClassIdToBuilding
        {
            public int classId;
            public string buildingId;
        }

        private void Awake()
        {
            _instanceRoot = GetComponent<GameInstanceRoot>();
            _receiver = GetComponent<OSCReceiver>();
        }

        private void OnEnable()
        {
            if (useInstancePort)
            {
                int port = _instanceRoot != null ? _instanceRoot.ListenPort : 3333;
                _receiver.LocalPort = Mathf.Clamp(port, 1024, 65535);
            }
            _bind = _receiver.Bind("/tuio/2Dobj", OnTuio2DObj);
        }

        private void OnDisable()
        {
            if (_receiver != null && _bind != null)
                _receiver.Unbind(_bind);
        }

        private void OnTuio2DObj(OSCMessage msg)
        {
            if (msg.Values.Count < 1 || msg.Values[0].Type != OSCValueType.String) return;
            string cmd = msg.Values[0].StringValue;
            switch (cmd)
            {
                case "set":
                    HandleSet(msg);
                    break;
                case "alive":
                    HandleAlive(msg);
                    break;
            }
        }

        private void HandleSet(OSCMessage msg)
        {
            int sessionId = msg.Values[1].IntValue;
            int classId = msg.Values[2].IntValue;
            float x = msg.Values[3].FloatValue;
            float y = msg.Values[4].FloatValue;

            string buildingId = ResolveBuildingId(classId);
            if (!_sessionToTileId.TryGetValue(sessionId, out string tileId))
            {
                tileId = $"osc_{_instanceRoot?.InstanceId ?? 0}_{_nextLocalId++}";
                _sessionToTileId[sessionId] = tileId;
                Debug.LogError($"[TileTracking] Placing building type '{buildingId}' (classId={classId}) → sessionId={sessionId} tileId={tileId}");
            }

            Vector2 rawPos = new Vector2(x, y);
            Vector2 pos = rawPos;
            if (smoothPosition)
            {
                // EMA: smoothed = prev + alpha * (raw - prev). First sample seeds with the raw value.
                if (_sessionToSmoothedPos.TryGetValue(sessionId, out Vector2 prev))
                    pos = prev + positionSmoothingAlpha * (rawPos - prev);
                _sessionToSmoothedPos[sessionId] = pos;
            }

            int sourceId = _instanceRoot != null ? _instanceRoot.InstanceId : 0;
            var pose = new TilePose(pos, 0f, buildingId, sourceId, tileId);
            //Debug.Log($"[TileTracking] TUIO set → buildingId={buildingId} pos=({x:F2},{y:F2}) tileId={tileId} (classId={classId})");
            OnTileUpdated?.Invoke(pose);
        }

        private void HandleAlive(OSCMessage msg)
        {
            var alive = new HashSet<int>();
            for (int i = 1; i < msg.Values.Count; i++)
            {
                if (msg.Values[i].Type == OSCValueType.Int)
                    alive.Add(msg.Values[i].IntValue);
            }
            foreach (int sessionId in _lastAlive)
            {
                if (!alive.Contains(sessionId) && _sessionToTileId.TryGetValue(sessionId, out string tileId))
                {
                    _sessionToTileId.Remove(sessionId);
                    _sessionToSmoothedPos.Remove(sessionId);
                    OnTileRemoved?.Invoke(tileId);
                }
            }
            _lastAlive = alive;
        }

        /// <summary>Forget all tracked TUIO sessions. Used by per-instance restart flows so
        /// physical tiles still on the table are treated as new placements after restart.</summary>
        public void ClearSessions()
        {
            _sessionToTileId.Clear();
            _sessionToSmoothedPos.Clear();
            _lastAlive.Clear();
        }

        private string ResolveBuildingId(int classId)
        {
            foreach (var m in classIdToBuilding)
                if (m.classId == classId) return m.buildingId ?? classId.ToString();
            if (useLocalTestingFallback && !string.IsNullOrEmpty(localTestingBuildingId))
                return localTestingBuildingId;
            return classId.ToString();
        }
    }
}
