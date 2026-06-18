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

        [Header("Presence debounce")]
        [Tooltip("Keep a marker for this long after it stops being reported, to ride out tracking flicker (e.g. TUIO dropping a frame). Should comfortably exceed the TUIO frame interval; 0 = remove almost immediately.")]
        [Range(0f, 2f)]
        [SerializeField] private float removeGraceSeconds = 0.25f;
        [Tooltip("Require a new marker to be seen continuously for this long before it is placed, to reject spurious one-frame detections. 0 = place immediately.")]
        [Range(0f, 1f)]
        [SerializeField] private float addConfirmSeconds = 0f;

        public event Action<TilePose> OnTileUpdated;
        public event Action<string> OnTileRemoved;

        [SerializeField] private GameInstanceRoot _instanceRoot;
        [SerializeField] private OSCReceiver _receiver;
        private IOSCBind _bind;
        private readonly Dictionary<int, string> _sessionToTileId = new Dictionary<int, string>();
        private readonly Dictionary<int, Vector2> _sessionToSmoothedPos = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, float> _lastSeen = new Dictionary<int, float>();
        private readonly Dictionary<int, PendingAdd> _pendingAdd = new Dictionary<int, PendingAdd>();
        private readonly List<int> _promoteScratch = new List<int>();
        private readonly List<int> _dropScratch = new List<int>();
        private int _nextLocalId;

        private struct PendingAdd { public float FirstSeen; public Vector2 Pos; public string BuildingId; public int SourceId; }

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
            // TUIO 2Dobj "set" = "set", sessionId(int), classId(int), x(float), y(float), [angle...].
            // Guard against truncated or wrongly-typed packets - this is untrusted network input and
            // indexing Values[1..4] blindly would throw on a malformed message.
            if (msg.Values.Count < 5) return;
            if (msg.Values[1].Type != OSCValueType.Int || msg.Values[2].Type != OSCValueType.Int) return;

            int sessionId = msg.Values[1].IntValue;
            int classId = msg.Values[2].IntValue;
            float x = msg.Values[3].FloatValue;
            float y = msg.Values[4].FloatValue;

            string buildingId = ResolveBuildingId(classId);
            int sourceId = _instanceRoot != null ? _instanceRoot.InstanceId : 0;

            Vector2 rawPos = new Vector2(x, y);
            Vector2 pos = rawPos;
            if (smoothPosition)
            {
                // EMA: smoothed = prev + alpha * (raw - prev). First sample seeds with the raw value.
                if (_sessionToSmoothedPos.TryGetValue(sessionId, out Vector2 prev))
                    pos = prev + positionSmoothingAlpha * (rawPos - prev);
                _sessionToSmoothedPos[sessionId] = pos;
            }

            // Mark presence; this also cancels any pending grace removal for an already-placed marker.
            _lastSeen[sessionId] = Time.time;

            if (_sessionToTileId.TryGetValue(sessionId, out string tileId))
            {
                OnTileUpdated?.Invoke(new TilePose(pos, 0f, buildingId, sourceId, tileId));
            }
            else if (addConfirmSeconds <= 0f)
            {
                tileId = NewTileId();
                _sessionToTileId[sessionId] = tileId;
                OnTileUpdated?.Invoke(new TilePose(pos, 0f, buildingId, sourceId, tileId));
            }
            else
            {
                // Hold as pending; placed in Update once it has been seen for addConfirmSeconds.
                if (!_pendingAdd.TryGetValue(sessionId, out var pend))
                    pend = new PendingAdd { FirstSeen = Time.time };
                pend.Pos = pos;
                pend.BuildingId = buildingId;
                pend.SourceId = sourceId;
                _pendingAdd[sessionId] = pend;
            }
        }

        private void HandleAlive(OSCMessage msg)
        {
            // "alive" lists every currently-present session each frame. Refresh their last-seen time;
            // the grace-based removal and add-confirmation are handled in Update().
            float now = Time.time;
            for (int i = 1; i < msg.Values.Count; i++)
            {
                if (msg.Values[i].Type == OSCValueType.Int)
                    _lastSeen[msg.Values[i].IntValue] = now;
            }
        }

        private void Update()
        {
            float now = Time.time;

            // Promote pending adds once they have persisted long enough; drop ghosts that vanished first.
            if (_pendingAdd.Count > 0)
            {
                _promoteScratch.Clear();
                _dropScratch.Clear();
                foreach (var kv in _pendingAdd)
                {
                    float seen = _lastSeen.TryGetValue(kv.Key, out var ls) ? ls : 0f;
                    if (now - seen > removeGraceSeconds) _dropScratch.Add(kv.Key);
                    else if (now - kv.Value.FirstSeen >= addConfirmSeconds) _promoteScratch.Add(kv.Key);
                }
                for (int i = 0; i < _dropScratch.Count; i++)
                {
                    int sid = _dropScratch[i];
                    _pendingAdd.Remove(sid);
                    _sessionToSmoothedPos.Remove(sid);
                    _lastSeen.Remove(sid);
                }
                for (int i = 0; i < _promoteScratch.Count; i++)
                {
                    int sid = _promoteScratch[i];
                    var pend = _pendingAdd[sid];
                    _pendingAdd.Remove(sid);
                    string tileId = NewTileId();
                    _sessionToTileId[sid] = tileId;
                    OnTileUpdated?.Invoke(new TilePose(pend.Pos, 0f, pend.BuildingId, pend.SourceId, tileId));
                }
            }

            // Grace removal: drop placed markers whose presence lapsed beyond the grace window.
            if (_sessionToTileId.Count > 0)
            {
                _dropScratch.Clear();
                foreach (var kv in _sessionToTileId)
                {
                    float seen = _lastSeen.TryGetValue(kv.Key, out var ls) ? ls : 0f;
                    if (now - seen > removeGraceSeconds) _dropScratch.Add(kv.Key);
                }
                for (int i = 0; i < _dropScratch.Count; i++)
                {
                    int sid = _dropScratch[i];
                    string tileId = _sessionToTileId[sid];
                    _sessionToTileId.Remove(sid);
                    _sessionToSmoothedPos.Remove(sid);
                    _lastSeen.Remove(sid);
                    OnTileRemoved?.Invoke(tileId);
                }
            }
        }

        private string NewTileId() => $"osc_{(_instanceRoot != null ? _instanceRoot.InstanceId : 0)}_{_nextLocalId++}";

        /// <summary>Forget all tracked TUIO sessions. Used by per-instance restart flows so
        /// physical tiles still on the table are treated as new placements after restart.</summary>
        public void ClearSessions()
        {
            _sessionToTileId.Clear();
            _sessionToSmoothedPos.Clear();
            _lastSeen.Clear();
            _pendingAdd.Clear();
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
