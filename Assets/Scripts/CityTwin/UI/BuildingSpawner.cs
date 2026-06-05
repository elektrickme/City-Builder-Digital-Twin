using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.UI
{
    /// <summary>Spawns a building marker visual when a tile is added and removes it when the tile is removed. Place on Game Instance; assign content root and marker prefab.</summary>
    public class BuildingSpawner : MonoBehaviour
    {
        [Tooltip("Root to parent spawned markers (e.g. a RectTransform for the table area).")]
        [SerializeField] private RectTransform contentRoot;

        [Tooltip("Prefab to instantiate per building (will be positioned at TUIO coordinates).")]
        [SerializeField] private GameObject buildingMarkerPrefab;

        [Header("Coordinates")]
        [Tooltip("TUIO typically sends 0-1. Table size maps that range to local position.")]
        [SerializeField] private Vector2 tableSize = new Vector2(300f, 300f);

        [Tooltip("If true and contentRoot is a RectTransform, use contentRoot.rect.size as the mapping size (recommended to keep hubs/buildings in the same space).")]
        [SerializeField] private bool useContentRootRectSize = true;

        [Tooltip("Enable so TUIO bottom (y≈1) appears at bottom of table. TUIO uses top-left origin; Unity UI uses bottom-left.")]
        [SerializeField] private bool flipY = true;

        [Tooltip("Enable when Content Root is center-anchored. Maps TUIO (0.5, 0.5) to local (0,0) so center of simulator = center of table.")]
        [SerializeField] private bool centerOrigin = true;

        [Tooltip("Extra offset (in Content Root local units) added to every spawned/moved marker. Use to nudge markers if they sit slightly off from the board center.")]
        [SerializeField] private Vector2 markerPositionOffset = Vector2.zero;

        private readonly Dictionary<string, GameObject> _spawned = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, float> _debugHaloScaleByBuildingId = new Dictionary<string, float>();

        /// <summary>Fires after a building marker is instantiated (engineTileId, buildingId, marker GameObject).</summary>
        public event System.Action<string, string, GameObject> OnTileSpawned;

        public const float DebugHaloMultiplierMin = 0.25f;
        public const float DebugHaloMultiplierMax = 5f;
        public const float DebugHaloMultiplierDefault = 1f;

        private void Awake()
        {
            if (contentRoot == null) contentRoot = GetComponentInChildren<RectTransform>(true);
        }

        /// <summary>Convert raw TUIO (0-1) position to content root local space, applying flipY and centerOrigin.</summary>
        public Vector2 TuioToLocalPosition(Vector2 tuioRaw)
        {
            Vector2 pos = tuioRaw;
            if (flipY) pos.y = 1f - pos.y;
            return TuioToLocal(pos);
        }

        /// <summary>
        /// Convert a content/simulation local position back into TUIO-like 0..1 coordinates.
        /// Useful for debug tools that place in local space but need to reuse Spawn/Move logic.
        /// </summary>
        public Vector2 LocalToTuioPosition(Vector2 localPos)
        {
            Vector2 size = GetMappingSize();
            if (size.x <= 0.0001f || size.y <= 0.0001f) return localPos;

            Vector2 pos;
            if (centerOrigin)
                pos = new Vector2((localPos.x / size.x) + 0.5f, (localPos.y / size.y) + 0.5f);
            else
                pos = new Vector2(localPos.x / size.x, localPos.y / size.y);

            if (flipY) pos.y = 1f - pos.y;
            return pos;
        }

        /// <summary>Convert any world position to the center-anchored coordinate space used by
        /// TuioToLocal and building markers (0,0 = center of contentRoot rect).
        /// Accounts for contentRoot pivot so results are consistent regardless of pivot setting.</summary>
        public Vector2 WorldToContentLocal(Vector3 worldPos)
        {
            if (contentRoot == null) return new Vector2(worldPos.x, worldPos.y);
            Vector3 local = contentRoot.InverseTransformPoint(worldPos);
            Vector2 pivotCorrection = (new Vector2(0.5f, 0.5f) - contentRoot.pivot) * contentRoot.rect.size;
            return new Vector2(local.x, local.y) - pivotCorrection;
        }

        public RectTransform ContentRoot => contentRoot;

        private Vector2 GetMappingSize()
        {
            if (useContentRootRectSize && contentRoot != null)
            {
                var s = contentRoot.rect.size;
                if (s.x > 0.0001f && s.y > 0.0001f) return s;
            }
            return tableSize;
        }

        private Vector2 TuioToLocal(Vector2 pos)
        {
            Vector2 size = GetMappingSize();
            if (centerOrigin)
                return new Vector2((pos.x - 0.5f) * size.x, (pos.y - 0.5f) * size.y);
            return new Vector2(pos.x * size.x, pos.y * size.y);
        }

        /// <summary>Spawn a building marker at the tile pose. Call after simulation AddTile.</summary>
        public void SpawnBuilding(TilePose pose, string engineTileId)
        {
            Debug.Log($"[BuildingSpawner] SpawnBuilding buildingId={pose.BuildingId} engineTileId={engineTileId}");
            if (buildingMarkerPrefab == null) { Debug.LogWarning("[BuildingSpawner] buildingMarkerPrefab is not assigned. Assign in Inspector."); return; }
            if (contentRoot == null) { Debug.LogWarning("[BuildingSpawner] contentRoot is not assigned. Assign a RectTransform (e.g. table area)."); return; }
            if (string.IsNullOrEmpty(engineTileId)) { Debug.LogWarning("[BuildingSpawner] engineTileId is empty."); return; }
            if (_spawned.ContainsKey(engineTileId)) { Debug.Log($"[BuildingSpawner] Already spawned for {engineTileId}, skipping."); return; }

            GameObject instance = Instantiate(buildingMarkerPrefab, contentRoot);
            instance.name = $"{pose.BuildingId}_{engineTileId}";

            Vector2 pos = pose.Position;
            if (flipY) pos.y = 1f - pos.y;
            Vector2 localPos = TuioToLocal(pos) + markerPositionOffset;

            if (instance.transform is RectTransform rt)
            {
                rt.anchoredPosition = localPos;
                rt.localRotation = Quaternion.Euler(0f, 0f, -pose.Rotation * Mathf.Rad2Deg);
            }
            else
            {
                instance.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
                instance.transform.localRotation = Quaternion.Euler(0f, 0f, -pose.Rotation * Mathf.Rad2Deg);
            }

            var display = instance.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display != null)
            {
                display.SetBuilding(pose.BuildingId);
                display.SetRuntimeHaloMultiplier(GetDebugHaloScale(pose.BuildingId));
            }

            _spawned[engineTileId] = instance;
            Debug.Log($"[BuildingSpawner] Spawned {pose.BuildingId} at ({localPos.x:F0},{localPos.y:F0})");
            OnTileSpawned?.Invoke(engineTileId, pose.BuildingId, instance);
        }

        /// <summary>Move an existing building marker to the new pose (e.g. TUIO position update).</summary>
        public void MoveBuilding(TilePose pose, string engineTileId)
        {
            if (contentRoot == null || string.IsNullOrEmpty(engineTileId)) return;
            if (!_spawned.TryGetValue(engineTileId, out GameObject go) || go == null) return;

            Vector2 pos = pose.Position;
            if (flipY) pos.y = 1f - pos.y;
            Vector2 localPos = TuioToLocal(pos) + markerPositionOffset;

            if (go.transform is RectTransform rt)
            {
                rt.anchoredPosition = localPos;
                rt.localRotation = Quaternion.Euler(0f, 0f, -pose.Rotation * Mathf.Rad2Deg);
            }
            else
            {
                go.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
                go.transform.localRotation = Quaternion.Euler(0f, 0f, -pose.Rotation * Mathf.Rad2Deg);
            }
        }

        /// <summary>Remove the building marker for this engine tile id. Call when tile is removed.</summary>
        public void RemoveBuilding(string engineTileId)
        {
            if (string.IsNullOrEmpty(engineTileId)) return;
            if (!_spawned.TryGetValue(engineTileId, out GameObject go)) return;
            _spawned.Remove(engineTileId);
            if (go != null) Destroy(go);
        }

        /// <summary>Get the local-space position of a spawned building marker (relative to its parent contentRoot). Returns false if not found.</summary>
        public bool TryGetMarkerPosition(string engineTileId, out Vector2 localPos)
        {
            localPos = Vector2.zero;
            if (string.IsNullOrEmpty(engineTileId)) return false;
            if (!_spawned.TryGetValue(engineTileId, out GameObject go) || go == null) return false;
            if (go.transform is RectTransform rt)
                localPos = rt.anchoredPosition;
            else
                localPos = new Vector2(go.transform.localPosition.x, go.transform.localPosition.y);
            return true;
        }

        /// <summary>Get the marker position in the center-anchored space of the given RectTransform.
        /// Corrects for pivot so (0,0) = center of inSpace rect, consistent with TuioToLocal.</summary>
        public bool TryGetMarkerPositionIn(string engineTileId, RectTransform inSpace, out Vector2 localPos)
        {
            localPos = Vector2.zero;
            if (string.IsNullOrEmpty(engineTileId) || inSpace == null) return false;
            if (!_spawned.TryGetValue(engineTileId, out GameObject go) || go == null) return false;
            Vector3 worldPos = go.transform.position;
            Vector3 local3d = inSpace.InverseTransformPoint(worldPos);
            Vector2 pivotCorrection = (new Vector2(0.5f, 0.5f) - inSpace.pivot) * inSpace.rect.size;
            localPos = new Vector2(local3d.x, local3d.y) - pivotCorrection;
            return true;
        }

        public bool TryGetEstimatedBuildingRadius(string buildingId, out float radius)
        {
            radius = 0f;
            if (buildingMarkerPrefab == null) return false;

            var display = buildingMarkerPrefab.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display == null) return false;

            // GetVisualRadiusForBuilding is rect/halo-field based and ignores the prefab root's
            // transform scale, but the halo renders through it — fold it back in so the connection
            // reach matches the visible halo when the prefab is scaled.
            float prefabScale = buildingMarkerPrefab.transform.localScale.x;
            radius = display.GetVisualRadiusForBuilding(buildingId) * GetDebugHaloScale(buildingId) * prefabScale;
            return radius > 0.001f;
        }

        /// <summary>Prefab halo radius without debug scale (for picker preview normalization).</summary>
        public bool TryGetBaseVisualRadius(string buildingId, out float radius)
        {
            radius = 0f;
            if (buildingMarkerPrefab == null) return false;
            var display = buildingMarkerPrefab.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display == null) return false;
            radius = display.GetVisualRadiusForBuilding(buildingId);
            return radius > 0.001f;
        }

        public float GetDebugHaloScale(string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId)) return DebugHaloMultiplierDefault;
            return _debugHaloScaleByBuildingId.TryGetValue(buildingId, out float v) ? v : DebugHaloMultiplierDefault;
        }

        public void SetDebugHaloScale(string buildingId, float multiplier)
        {
            if (string.IsNullOrEmpty(buildingId)) return;
            multiplier = Mathf.Clamp(multiplier, DebugHaloMultiplierMin, DebugHaloMultiplierMax);
            _debugHaloScaleByBuildingId[buildingId] = multiplier;
            ApplyDebugHaloScaleToSpawnedMarkers(buildingId);
        }

        public void ClearDebugHaloScales()
        {
            _debugHaloScaleByBuildingId.Clear();
            ApplyDebugHaloScaleToSpawnedMarkers(null);
        }

        private static string ParseBuildingIdFromSpawnName(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return null;
            int u = instanceName.IndexOf('_');
            return u > 0 ? instanceName.Substring(0, u) : null;
        }

        private void ApplyDebugHaloScaleToSpawnedMarkers(string onlyBuildingIdOrNull)
        {
            bool touched = false;
            foreach (var kv in _spawned)
            {
                var go = kv.Value;
                if (go == null) continue;
                string bid = ParseBuildingIdFromSpawnName(go.name);
                if (string.IsNullOrEmpty(bid)) continue;
                if (onlyBuildingIdOrNull != null && !string.Equals(bid, onlyBuildingIdOrNull, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var display = go.GetComponentInChildren<BuildingMarkerDisplay>(true);
                if (display != null)
                {
                    display.SetRuntimeHaloMultiplier(GetDebugHaloScale(bid));
                    touched = true;
                }
            }

            if (touched)
                Canvas.ForceUpdateCanvases();
        }

        public bool TryGetPreviewSprite(string buildingId, out Sprite sprite)
        {
            sprite = null;
            if (buildingMarkerPrefab == null) return false;
            var display = buildingMarkerPrefab.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display == null) return false;
            sprite = display.TryGetCatalogSprite(buildingId);
            return sprite != null;
        }

        public bool TryGetMarkerVisualRadius(string engineTileId, out float radius)
        {
            radius = 0f;
            if (contentRoot == null) return false;
            if (!TryGetMarkerDisplay(engineTileId, out var display)) return false;
            return display.TryGetCurrentVisualRadius(contentRoot, out radius);
        }

        public bool TryGetMarkerDisplay(string engineTileId, out BuildingMarkerDisplay display)
        {
            display = null;
            if (string.IsNullOrEmpty(engineTileId)) return false;
            if (!_spawned.TryGetValue(engineTileId, out GameObject go) || go == null) return false;

            display = go.GetComponentInChildren<BuildingMarkerDisplay>(true);
            return display != null;
        }

        public void SetMarkerPlacementInvalid(string engineTileId, bool isInvalid, Color invalidColor)
        {
            if (!TryGetMarkerDisplay(engineTileId, out var display)) return;
            display.SetPlacementInvalid(isInvalid, invalidColor);
        }

        public void SetMarkerConnectionState(string engineTileId, MarkerConnectionState state)
        {
            if (!TryGetMarkerDisplay(engineTileId, out var display)) return;
            display.SetConnectionState(state);
        }

        public void SetMarkerOverBudget(string engineTileId, bool isOverBudget)
        {
            if (!TryGetMarkerDisplay(engineTileId, out var display)) return;
            display.SetOverBudget(isOverBudget);
        }

        /// <summary>Remove all spawned markers (e.g. on reset).</summary>
        public void ClearAll()
        {
            foreach (var kv in _spawned)
            {
                if (kv.Value != null) Destroy(kv.Value);
            }
            _spawned.Clear();
        }
    }
}
