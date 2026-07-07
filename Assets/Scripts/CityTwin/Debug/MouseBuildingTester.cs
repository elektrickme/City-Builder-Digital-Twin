using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using CityTwin.Core;
using CityTwin.Config;
using CityTwin.Localization;
using CityTwin.Simulation;
using CityTwin.UI;


/// <summary>
/// Simple editor/play-mode helper:
/// - Press 1/2/3/4 to select a building id.
/// - Left-click on the table area to spawn that building.
/// - Drag with left mouse to move an existing building.
///
/// This emulates OSC/TUIO updates through GameInstanceCoordinator so budget and
/// placement/removal logic match physical marker behavior in the Unity editor.
/// </summary>
public class MouseBuildingTester : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private SimulationEngine simulationEngine;
    [SerializeField] private GameInstanceCoordinator coordinator;
    [SerializeField] private GameConfigLoader configLoader;
    [SerializeField] private LocalizationService localization;
    [SerializeField] private BuildingSpawner buildingSpawner;
    [SerializeField] private InactivityPopupController inactivityPopup;
    [SerializeField] private RectTransform tableArea;
    [SerializeField] private GameObject markerPrefab;
    [Tooltip("UI camera used for ScreenPointToLocalPointInRectangle. Leave null to use Camera.main.")]
    [SerializeField] private Camera uiCamera;
    [Tooltip("Optional fan-shaped table bounds. When set, releasing a dragged building outside the bounds discards it (removes it from the table).")]
    [SerializeField] private TableBounds tableBounds;
    [Tooltip("Optional rectangular drop-to-clear zone. Releasing a dragged building inside it removes the building — more reliable than aiming outside the fan bounds. Auto-resolved.")]
    [SerializeField] private ClearDropZone clearZone;
    [Tooltip("Optional road layout editor toggled from this menu. Tile input pauses while it is active.")]
    [SerializeField] private RoadNetworkEditor roadEditor;

    [Header("Building ids for number keys")]
    [SerializeField] private string key1BuildingId = "garden";
    [SerializeField] private string key2BuildingId = "office";
    [SerializeField] private string key3BuildingId = "hospital";
    [SerializeField] private string key4BuildingId = "school";

    [Header("Debug picker UI")]
    [SerializeField] private bool enableBackquotePicker = true;
    [SerializeField] private Key togglePickerKey = Key.Backquote;
    [SerializeField] private bool pauseInputWhilePickerOpen = true;
    [Tooltip("Auto-save all playtest settings to game_config.json when the menu is closed, so they persist across play sessions instead of resetting to config defaults.")]
    [SerializeField] private bool persistSettingsOnClose = true;
    [Tooltip("Master IMGUI scale for the whole debug menu (also adjustable from the bar when the menu is open).")]
    [SerializeField, Range(0.5f, 5f)] private float pickerUiScale = 1f;

    private const float MenuScaleMin = 0.5f;
    private const float MenuScaleMax = 5f;
    private const float MasterScaleBarScreenH = 38f;


    private class ActiveTile
    {
        public string DebugTileId;
        public string EngineId;
        public RectTransform Marker;
        public string BuildingId;
    }

    private readonly List<ActiveTile> _tiles = new List<ActiveTile>();
    private ActiveTile _dragging;
    private Vector2 _dragOffset;
    private Vector2 _pressScreenPos;
    private string _currentBuildingId;
    private int _nextDebugTileId;

    private bool _pickerOpen;
    private Vector2 _pickerScroll;
    // Collapsible-section state + compact mode (descriptions hidden unless requested).
    private readonly System.Collections.Generic.Dictionary<string, bool> _sectionOpen =
        new System.Collections.Generic.Dictionary<string, bool>();
    private static bool _showDetails;
    // Basic mode shows only the client-facing balance knobs; expert mode shows everything.
    private bool _expertMode;

    /// <summary>Collapsible section header. Returns true while the section is open.</summary>
    private bool DrawFoldout(string title, string subtitle, bool defaultOpen = false)
    {
        if (!_sectionOpen.TryGetValue(title, out bool open)) open = defaultOpen;
        var style = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 12 };
        style.normal.textColor = new Color(0.3f, 0.85f, 1f);
        bool next = GUILayout.Toggle(open, (open ? "▼  " : "▶  ") + title, style, GUILayout.Height(24f));
        if (next != open) _sectionOpen[title] = next;
        if (next && _showDetails && !string.IsNullOrEmpty(subtitle))
        {
            var st = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 10 };
            st.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            GUILayout.Label(subtitle, st);
        }
        return next;
    }
    private string _pickerFilter = "";
    private Texture2D _pickerRingTexture;
    private bool _advancedOpen;
    private string _lastSaveMessage;
    private bool _lastSaveOk = true;
    private bool _awaitingImport;

    private void Awake()
    {
        if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
        if (coordinator == null) coordinator = GetComponentInChildren<GameInstanceCoordinator>(true);
        if (coordinator == null) coordinator = GetComponentInParent<GameInstanceCoordinator>(true);
        if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true);
        if (localization == null) localization = GetComponentInChildren<LocalizationService>(true);
        if (buildingSpawner == null) buildingSpawner = GetComponentInChildren<BuildingSpawner>(true);
        if (inactivityPopup == null) inactivityPopup = GetComponentInChildren<InactivityPopupController>(true);
        if (inactivityPopup == null) inactivityPopup = GetComponentInParent<InactivityPopupController>(true);
        // Markers live under the spawner's ContentRoot and all TUIO mapping uses its rect,
        // so click mapping MUST use that same rect — override any stale serialized value.
        if (buildingSpawner != null && buildingSpawner.ContentRoot != null) tableArea = buildingSpawner.ContentRoot;
        if (tableArea == null) tableArea = GetComponentInChildren<RectTransform>(true);
        if (uiCamera == null) uiCamera = Camera.main;
        if (tableBounds == null) tableBounds = GetComponentInParent<TableBounds>(true);
        if (clearZone == null)
        {
            var giRoot = GetComponentInParent<GameInstanceRoot>(true);
            if (giRoot != null) clearZone = giRoot.GetComponentInChildren<ClearDropZone>(true);
        }
        if (roadEditor == null)
        {
            roadEditor = GetComponentInParent<RoadNetworkEditor>(true);
            if (roadEditor == null && coordinator != null)
                roadEditor = coordinator.GetComponentInChildren<RoadNetworkEditor>(true);
        }
    }

    /// <summary>
    /// Track a tile placed by another system (e.g. BuildingPalette drag-drop) so it gets
    /// the same mouse drag / ESC-delete / off-table discard handling as tiles placed here.
    /// </summary>
    public void RegisterExternalTile(string debugTileId, string engineId, RectTransform marker, string buildingId)
    {
        if (string.IsNullOrEmpty(engineId) && string.IsNullOrEmpty(debugTileId)) return;
        _tiles.Add(new ActiveTile
        {
            DebugTileId = debugTileId,
            EngineId = engineId,
            Marker = marker,
            BuildingId = buildingId
        });
    }

    private void Update()
    {
        // Drain a pending config import (async on WebGL: user is picking a file).
        if (_awaitingImport && ConfigFileIO.TryTakeImport(out string importedJson))
        {
            _awaitingImport = false;
            _lastSaveOk = coordinator != null && coordinator.ImportConfigDebug(importedJson);
            _lastSaveMessage = _lastSaveOk
                ? $"Imported config  ({System.DateTime.Now:HH:mm:ss})"
                : "Import failed (bad JSON?) - see console.";
        }

        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        if (enableBackquotePicker && WasPressedThisFrame(keyboard, togglePickerKey))
        {
            _pickerOpen = !_pickerOpen;
            // Persist tweaks when the menu closes so settings survive play-mode restarts.
            if (!_pickerOpen && persistSettingsOnClose && coordinator != null)
            {
                _lastSaveOk = coordinator.SaveConfigDebug();
                _lastSaveMessage = _lastSaveOk
                    ? $"Settings auto-saved on close  ({System.DateTime.Now:HH:mm:ss})"
                    : "Auto-save failed (on web use Export) - see console.";
            }
        }

        if (_pickerOpen && pauseInputWhilePickerOpen)
            return;

        // Road editor owns the mouse while it's active — no tile spawning/dragging.
        if (roadEditor != null && roadEditor.EditModeActive)
            return;

        // Hotkeys 1-4 select building id
        if (keyboard.digit1Key.wasPressedThisFrame) _currentBuildingId = key1BuildingId;
        if (keyboard.digit2Key.wasPressedThisFrame) _currentBuildingId = key2BuildingId;
        if (keyboard.digit3Key.wasPressedThisFrame) _currentBuildingId = key3BuildingId;
        if (keyboard.digit4Key.wasPressedThisFrame) _currentBuildingId = key4BuildingId;

        Vector2 screenPos = mouse.position.ReadValue();

        if (mouse.leftButton.wasPressedThisFrame)
            OnMouseDown(screenPos);

        if (mouse.leftButton.isPressed)
            OnMouseDrag(screenPos);

        // Clear-zone feedback: armed while any building is dragged, hot while hovering the zone.
        if (clearZone != null)
        {
            bool draggingNow = mouse.leftButton.isPressed && _dragging != null;
            clearZone.SetState(draggingNow, draggingNow && clearZone.ContainsScreenPoint(screenPos, uiCamera));
        }

        // Out-of-board indicator: the dragged pin's halo turns red while it sits outside the
        // fan bounds or over the clear zone — i.e. wherever releasing would discard it.
        if (mouse.leftButton.isPressed && _dragging != null && _dragging.Marker != null)
        {
            bool wouldDiscard =
                (clearZone != null && clearZone.ContainsScreenPoint(screenPos, uiCamera)) ||
                (tableBounds != null && !tableBounds.ContainsWorld(_dragging.Marker.position));
            var display = _dragging.Marker.GetComponent<BuildingMarkerDisplay>();
            if (display != null) display.SetPlacementInvalid(wouldDiscard, DiscardIndicatorColor);
        }

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            // Dropped on the clear zone → remove the building (checked first: it is the reliable,
            // explicit affordance; the fan-bounds test below stays as the fallback).
            if (_dragging != null && clearZone != null &&
                clearZone.ContainsScreenPoint(screenPos, uiCamera))
            {
                RemoveActiveTile(_dragging);
            }
            // Dropped outside the fan-shaped table bounds → discard the building.
            else if (_dragging != null && tableBounds != null && _dragging.Marker != null &&
                !tableBounds.ContainsWorld(_dragging.Marker.position))
            {
                RemoveActiveTile(_dragging);
            }
            // Demo affordance: a simple TAP (no drag) on a red over-budget block removes it —
            // simulating the player lifting the physical tile off the table.
            else if (_dragging != null && !string.IsNullOrEmpty(_dragging.EngineId) &&
                     _dragging.EngineId.StartsWith("ob:") &&
                     (screenPos - _pressScreenPos).sqrMagnitude < 8f * 8f)
            {
                RemoveActiveTile(_dragging);
            }
            // Kept on the board: clear the red discard indicator.
            if (_dragging != null && _dragging.Marker != null)
            {
                var display = _dragging.Marker.GetComponent<BuildingMarkerDisplay>();
                if (display != null) display.SetPlacementInvalid(false, DiscardIndicatorColor);
            }
            _dragging = null;
        }
    }

    /// <summary>Halo tint while a dragged pin hovers a discard area (outside the fan / clear zone).</summary>
    private static readonly Color DiscardIndicatorColor = new Color(1f, 0.12f, 0.3f, 0.95f);

    private void OnMouseDown(Vector2 screenPos)
    {
        if (tableArea == null || simulationEngine == null) return;
        // If we don't have a BuildingSpawner, we need markerPrefab for the fallback debug visuals.
        if (buildingSpawner == null && markerPrefab == null) return;

            var keyboard = Keyboard.current;
            bool deleteMode = keyboard != null && keyboard.escapeKey.isPressed;

            // First, check if we clicked on an existing marker
            for (int i = 0; i < _tiles.Count; i++)
        {
                var tile = _tiles[i];
            if (tile.Marker == null) continue;
            if (IsPointOnMarker(tile, screenPos))
            {
                    if (deleteMode)
                {
                        // Remove from simulation and destroy marker when ESC is held.
                        RemoveActiveTile(tile);
                        _dragging = null;
                        return;
                    }
                    else
                    {
                        // Start dragging this marker.
                        _dragging = tile;
                        _pressScreenPos = screenPos;
                        if (TryScreenToContentLocal(screenPos, out var local))
                        {
                            _dragOffset = tile.Marker.anchoredPosition - local;
                        }
                        return;
                }
            }
        }

        // Otherwise, spawn a new tile if we have a building selected
        if (string.IsNullOrEmpty(_currentBuildingId)) return;

        if (!TryScreenToContentLocal(screenPos, out var spawnLocal))
            return;

        string engineId = null;
        string debugTileId = $"debug_mouse_{_nextDebugTileId++}";
        bool useCoordinatorPath = coordinator != null && buildingSpawner != null;
        if (useCoordinatorPath)
        {
            Vector2 tuioPos = buildingSpawner.LocalToTuioPosition(spawnLocal);
            var debugPose = new TilePose(tuioPos, 0f, _currentBuildingId, 0, debugTileId);
            bool placed = coordinator.TryProcessTileUpdate(debugPose, out engineId);
            if (!placed || string.IsNullOrEmpty(engineId))
                return;
        }
        else
        {
            // Fallback direct simulation path for scenes without coordinator/building spawner.
            var simPose = new TilePose(spawnLocal, 0f, _currentBuildingId, 0, null);
            engineId = simulationEngine.AddTile(simPose);
            if (string.IsNullOrEmpty(engineId)) return;
        }

        // Resolve marker created by coordinator/spawner (instance name = "{BuildingId}_{engineTileId}").
        RectTransform rt = null;
        if (buildingSpawner != null && buildingSpawner.ContentRoot != null)
        {
            if (!useCoordinatorPath)
            {
                Vector2 tuioPos = buildingSpawner.LocalToTuioPosition(spawnLocal);
                var spawnerPose = new TilePose(tuioPos, 0f, _currentBuildingId, 0, null);
                buildingSpawner.SpawnBuilding(spawnerPose, engineId);
            }

            string instanceName = $"{_currentBuildingId}_{engineId}";
            rt = buildingSpawner.ContentRoot.Find(instanceName) as RectTransform;
            if (rt == null)
                rt = FindMarkerRecursive(instanceName);
        }

        // Fallback: old debug marker spawning (connections won't draw correctly).
        if (rt == null)
        {
            if (markerPrefab == null) return;
            var go = Object.Instantiate(markerPrefab, tableArea);
            rt = go.GetComponent<RectTransform>();
            if (rt != null) rt.anchoredPosition = spawnLocal;

            var display = go.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display != null) display.SetBuilding(_currentBuildingId);
        }

        var active = new ActiveTile
        {
            DebugTileId = debugTileId,
            EngineId = engineId,
            Marker = rt,
            BuildingId = _currentBuildingId
        };
        _tiles.Add(active);
        _dragging = active;
        _dragOffset = Vector2.zero;
    }

    /// <summary>
    /// Convert a screen point into the marker coordinate space (spawner ContentRoot,
    /// center-origin — pivot-safe via WorldToContentLocal). Falls back to tableArea local.
    /// </summary>
    private bool TryScreenToContentLocal(Vector2 screenPos, out Vector2 local)
    {
        if (buildingSpawner != null && buildingSpawner.ContentRoot != null)
        {
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(
                    buildingSpawner.ContentRoot, screenPos, uiCamera, out Vector3 world))
            {
                local = buildingSpawner.WorldToContentLocal(world);
                return true;
            }
            local = default;
            return false;
        }
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(tableArea, screenPos, uiCamera, out local);
    }

    /// <summary>
    /// Click hit test for a placed marker. The rect test alone fails when the marker
    /// prefab's root rect is tiny (visuals live in children), so also accept clicks
    /// within the marker's visual radius (fallback 50 px) around its center.
    /// </summary>
    private bool IsPointOnMarker(ActiveTile tile, Vector2 screenPos)
    {
        if (RectTransformUtility.RectangleContainsScreenPoint(tile.Marker, screenPos, uiCamera))
            return true;

        if (!TryScreenToContentLocal(screenPos, out Vector2 local))
            return false;

        float grabRadius = 50f;
        if (buildingSpawner != null && !string.IsNullOrEmpty(tile.EngineId) &&
            buildingSpawner.TryGetMarkerVisualRadius(tile.EngineId, out float visualRadius) && visualRadius > 1f)
        {
            grabRadius = visualRadius;
        }
        return (tile.Marker.anchoredPosition - local).sqrMagnitude <= grabRadius * grabRadius;
    }

    /// <summary>Remove a tracked tile from simulation/spawner and destroy its marker.</summary>
    private void RemoveActiveTile(ActiveTile tile)
    {
        if (tile == null) return;
        if (coordinator != null && !string.IsNullOrEmpty(tile.DebugTileId))
        {
            coordinator.TryProcessTileRemoval(tile.DebugTileId);
        }
        else if (!string.IsNullOrEmpty(tile.EngineId))
        {
            simulationEngine.RemoveTile(tile.EngineId);
            buildingSpawner?.RemoveBuilding(tile.EngineId);
        }
        if (tile.Marker != null) Object.Destroy(tile.Marker.gameObject);
        _tiles.Remove(tile);
    }

    private void OnMouseDrag(Vector2 screenPos)
    {
        if (_dragging == null || simulationEngine == null || tableArea == null)
            return;

        if (!TryScreenToContentLocal(screenPos, out var local))
            return;

        Vector2 targetLocal = local + _dragOffset;
        bool useCoordinatorPath = coordinator != null && buildingSpawner != null && !string.IsNullOrEmpty(_dragging.DebugTileId);
        if (useCoordinatorPath)
        {
            Vector2 tuioPos = buildingSpawner.LocalToTuioPosition(targetLocal);
            var debugPose = new TilePose(tuioPos, 0f, _dragging.BuildingId, 0, _dragging.DebugTileId);
            coordinator.TryProcessTileUpdate(debugPose, out _);
        }
        else
        {
            simulationEngine.UpdateTilePosition(_dragging.EngineId, targetLocal, 0f);

            // Keep marker in sync (when using BuildingSpawner we must move via it because it owns the registered marker).
            if (buildingSpawner != null)
            {
                Vector2 tuioPos = buildingSpawner.LocalToTuioPosition(targetLocal);
                var spawnerPose = new TilePose(tuioPos, 0f, _dragging.BuildingId, 0, null);
                buildingSpawner.MoveBuilding(spawnerPose, _dragging.EngineId);
            }
            else
            {
                _dragging.Marker.anchoredPosition = targetLocal;
            }
        }
    }

    private void OnGUI()
    {
        if (!enableBackquotePicker || !_pickerOpen) return;

        const float pad = 10f;
        DrawDebugMenuMasterScaleBar(pad);

        float scale = Mathf.Clamp(pickerUiScale, MenuScaleMin, MenuScaleMax);
        pickerUiScale = scale;
        float inv = 1f / scale;

        var oldMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f)) * oldMatrix;

        float reservedTopScreen = pad + MasterScaleBarScreenH;
        float topLogical = reservedTopScreen * inv;
        float logicalScreenH = Screen.height * inv;
        float rightWidth = Mathf.Min(440f, (Screen.width * inv) * 0.45f);
        float height = Mathf.Max(200f, logicalScreenH - topLogical - pad);

        // Left side of the screen stays clean: the building catalog now lives inside the
        // Playtesting Controls panel (expert mode). Palette drag-drop covers placement.
        var buildings = configLoader != null ? configLoader.Config?.Buildings : null;
        BuildingDefinition selectedDef = null;
        if (buildings != null && !string.IsNullOrEmpty(_currentBuildingId))
        {
            for (int si = 0; si < buildings.Length; si++)
            {
                var cand = buildings[si];
                if (cand != null && cand.Id == _currentBuildingId) { selectedDef = cand; break; }
            }
        }

        // ── Right panel: Scoring Parameter Sliders ──
        if (simulationEngine != null)
        {
            float rightX = (Screen.width * inv) - rightWidth - pad;
            var rightRect = new Rect(rightX, topLogical, rightWidth, height);

            // Dark semi-transparent background
            var bgTex = Texture2D.whiteTexture;
            var prevColor = GUI.color;
            GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
            GUI.DrawTexture(rightRect, bgTex);
            GUI.color = prevColor;

            // Title
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter
            };
            titleStyle.normal.textColor = new Color(0.3f, 0.85f, 1f);
            GUI.Label(new Rect(rightRect.x, rightRect.y + 6f, rightRect.width, 28f), "Playtesting Controls", titleStyle);

            float rightInnerH = rightRect.height - 32f - pad;
            GUILayout.BeginArea(new Rect(rightRect.x + pad, rightRect.y + 32f, rightRect.width - pad * 2f, rightInnerH));
            GUILayout.BeginVertical(GUILayout.Height(rightInnerH));
            _slidersScroll = GUILayout.BeginScrollView(_slidersScroll, GUI.skin.box, GUILayout.ExpandHeight(true));

            bool changed = false;

            GUILayout.BeginHorizontal();
            _showDetails = GUILayout.Toggle(_showDetails, " Help & descriptions", GUILayout.Height(20f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(64f)))
                _pickerOpen = false;
            GUILayout.EndHorizontal();
            if (_showDetails) DrawHelpBox();

            DrawSaveRow();

            // Road edit mode lives at the top level so it exists in BOTH menu modes — and in
            // builds, so the map can be re-laid-out on the installation hardware itself.
            if (roadEditor != null &&
                GUILayout.Button(roadEditor.EditModeActive ? "Close Road Editor" : "Road Editor (drag cities & road ends)", GUILayout.Height(28f)))
            {
                roadEditor.ToggleEditMode();
                _pickerOpen = false; // hand the screen over to the road editor handles
            }

            _expertMode = GUILayout.Toggle(_expertMode,
                _expertMode ? " Expert mode — showing everything" : " Expert mode — off (client basics only)",
                GUI.skin.button, GUILayout.Height(24f));

            // ── Basic: the handful of knobs a client needs to balance the game ──
            if (!_expertMode)
            {
                if (DrawFoldout("Game balance (basic)", "The few knobs that matter. Use Save config to persist.", true))
                {
                    if (buildingSpawner != null)
                        changed |= DrawSlider("Building radius (all)",
                            "Master multiplier on every building's reach: marker size, connection range, footprint.",
                            buildingSpawner.GetDebugHaloMasterScale(),
                            BuildingSpawner.DebugHaloMultiplierMin, BuildingSpawner.DebugHaloMultiplierMax,
                            v => { buildingSpawner.SetDebugHaloMasterScale(v); simulationEngine?.RefreshAllTileConnections(); });
                    changed |= DrawSlider("QOL inequality penalty",
                        "City QOL -= penalty x (best hub - worst hub). Higher punishes uneven cities.",
                        simulationEngine.QolBalancePenalty, 0f, 2f, v => simulationEngine.QolBalancePenalty = v);
                    changed |= DrawSlider("QOL maximum cap",
                        "Hard ceiling on the final score.",
                        simulationEngine.QolCap, 1f, 100f, v => simulationEngine.QolCap = v);
                    if (coordinator != null)
                    {
                        DrawIntSlider("Starting budget",
                            "Money each round starts with.",
                            coordinator.DebugStartingBudget, 0, 20000, v => coordinator.SetStartingBudgetDebug(v));
                        DrawIntSlider("Session length (s)",
                            "Total round time in seconds.",
                            coordinator.DebugSessionLength, 30, 900, v => coordinator.SetSessionLengthDebug(v));
                        if (configLoader != null && configLoader.Config != null && configLoader.Config.Stops != null)
                            DrawSlider("Bus stop spacing",
                                "Distance between stops along roads. Lower = denser network.",
                                configLoader.Config.Stops.spacing, 20f, 600f, v => coordinator.SetBusStopDensityDebug(v));
                        if (buildingSpawner != null)
                            DrawSlider("Tile scale",
                                "Cosmetic size of placed building tiles.",
                                coordinator.DebugTileScale, BuildingSpawner.TileScaleMin, BuildingSpawner.TileScaleMax,
                                v => coordinator.SetTileScaleDebug(v));
                    }
                }
            }
            else
            {

            // Building catalog (moved from the old left panel)
            if (DrawFoldout("Building catalog", "Click a row to select for mouse placement. Keys 1-4 also pick.", false))
                DrawBuildingCatalog(buildings, selectedDef);

            // Playtesting
            bool playtestingOpen = DrawFoldout("Playtesting", "Live balancing knobs. Use Save config to persist; otherwise they reset on restart / relaunch.", true);
            if (playtestingOpen)
            {

            if (coordinator != null)
            {
                DrawIntSlider("Starting budget",
                    "Money each round starts with (saved to config). Setting it also tops up your current budget so you can test now.",
                    coordinator.DebugStartingBudget, 0, 20000, v => coordinator.SetStartingBudgetDebug(v));

                DrawIntSlider("Session length (s)",
                    "Total round time in seconds. Resets the countdown to this value.",
                    coordinator.DebugSessionLength, 30, 900, v => coordinator.SetSessionLengthDebug(v));

                DrawIntSlider("Time remaining (s)",
                    "Seconds left on the current countdown. Set to 0 to trigger the end screen.",
                    Mathf.RoundToInt(coordinator.DebugTimeRemaining), 0, 900, v => coordinator.SetTimeRemainingDebug(v));

                if (configLoader != null && configLoader.Config != null && configLoader.Config.Stops != null)
                {
                    int stopCount = simulationEngine != null && simulationEngine.TransitGraph != null
                        ? simulationEngine.TransitGraph.Stops.Count : 0;
                    DrawSlider("Bus stop spacing",
                        $"Distance between stops along roads. Lower = denser. Stop count ~ road length / spacing. Now: {stopCount} stops.",
                        configLoader.Config.Stops.spacing, 20f, 600f, v => coordinator.SetBusStopDensityDebug(v));
                    DrawSlider("Bus stop removal",
                        "Fraction of generated stops randomly dropped (real-world sparsity). 0 = keep all, 1 = remove all.",
                        configLoader.Config.Stops.removalRate, 0f, 1f, v => coordinator.SetBusStopRemovalDebug(v));
                    DrawSlider("Bus stop seed",
                        "Reseeds the layout: which stops the removal drops and how jitter offsets them. Same seed = same pattern.",
                        configLoader.Config.Stops.seed, 0, 9999, v => coordinator.SetBusStopSeedDebug(Mathf.RoundToInt(v)));
                }

                if (configLoader != null && configLoader.Config != null && configLoader.Config.Accessibility != null)
                {
                    DrawSlider("Connection removal",
                        "Fraction of building connection lines (to stops/hubs) randomly hidden to thin a dense network. Visual only — scoring is unaffected. 0 = show all, 1 = hide all.",
                        configLoader.Config.Accessibility.connectionRemovalRate, 0f, 1f, v => coordinator.SetConnectionRemovalDebug(v));
                }
            }
            else
            {
                GUILayout.Label("Assign a GameInstanceCoordinator to enable budget / time / bus-stop controls.", GUI.skin.box);
            }

            if (inactivityPopup != null)
            {
                DrawIntSlider("Inactivity timeout (s)",
                    "Idle seconds with no building activity before the inactivity popup appears.",
                    Mathf.RoundToInt(inactivityPopup.TimeoutSeconds), 5, 300, v => inactivityPopup.TimeoutSeconds = v);
            }

            DrawDisabledRow("Map select (A / B / C / D)", "Coming soon - build the map presets first.");
            } // end Playtesting

            // Quality of Life
            if (DrawFoldout("Quality of Life", "How the city QOL score is graded and balanced."))
            {
            changed |= DrawSlider("QOL Inequality Penalty",
                "City QOL -= penalty x (best hub - worst hub). Higher punishes uneven cities; rewards spreading quality.",
                simulationEngine.QolBalancePenalty, 0f, 2f, v => simulationEngine.QolBalancePenalty = v);
            changed |= DrawSlider("QOL Maximum Cap",
                "Hard ceiling on final QOL. QOL can never exceed this, so pass bands above it are unreachable.",
                simulationEngine.QolCap, 1f, 100f, v => simulationEngine.QolCap = v);
            }

            // Building reach
            if (DrawFoldout("Building reach (impact radius)", "How far each building size searches for bus stops to score through."))
            {
            changed |= DrawSlider("Impact Radius - Small",
                "Stop-search radius for small buildings.",
                simulationEngine.ImpactRadiusSmall, 1f, 400f, v => simulationEngine.ImpactRadiusSmall = v);
            changed |= DrawSlider("Impact Radius - Medium",
                "Stop-search radius for medium buildings.",
                simulationEngine.ImpactRadiusMedium, 1f, 400f, v => simulationEngine.ImpactRadiusMedium = v);
            changed |= DrawSlider("Impact Radius - Large",
                "Stop-search radius for large buildings.",
                simulationEngine.ImpactRadiusLarge, 1f, 400f, v => simulationEngine.ImpactRadiusLarge = v);
            }

            // Tile + halo visuals in one section
            if (coordinator != null && buildingSpawner != null &&
                DrawFoldout("Tile & halo scale", "Tile scale is cosmetic; halo affects marker size, connection reach, and footprint for ALL buildings."))
            {
                DrawSlider("Tile scale (all tiles)",
                    "Visual scale multiplier for every placed building marker.",
                    coordinator.DebugTileScale, BuildingSpawner.TileScaleMin, BuildingSpawner.TileScaleMax,
                    v => coordinator.SetTileScaleDebug(v));
                changed |= DrawSlider("Halo - Master (all)",
                    "Global halo multiplier applied on top of the per-size values.",
                    buildingSpawner.GetDebugHaloMasterScale(),
                    BuildingSpawner.DebugHaloMultiplierMin,
                    BuildingSpawner.DebugHaloMultiplierMax,
                    v => { buildingSpawner.SetDebugHaloMasterScale(v); simulationEngine?.RefreshAllTileConnections(); });
                changed |= DrawHaloSizeSlider("Halo - Small", BuildingSpawner.HaloSizeSmall);
                changed |= DrawHaloSizeSlider("Halo - Medium", BuildingSpawner.HaloSizeMedium);
                changed |= DrawHaloSizeSlider("Halo - Large", BuildingSpawner.HaloSizeLarge);
            }

            // Selected building (picked in the left panel): the 4 scores. Halo is tuned by size above.
            if (selectedDef != null &&
                DrawFoldout($"Selected building ({selectedDef.Id})", "Per-building scores feed the completion score. Halo is tuned by size in the section above.", true))
            {
                if (buildingSpawner != null && buildingSpawner.TryGetEstimatedBuildingRadius(selectedDef.Id, out float haloPxRight))
                    GUILayout.Label($"Effective halo radius ~{haloPxRight:F0}px (size: {selectedDef.ImpactSize})", GUI.skin.label);

                if (selectedDef.BaseValues != null)
                {
                    var bv = selectedDef.BaseValues;
                    changed |= DrawSlider("Environment", "This building's Environment contribution.", bv.environment, -100f, 100f, v => bv.environment = v);
                    changed |= DrawSlider("Economy", "This building's Economy contribution.", bv.economy, -100f, 100f, v => bv.economy = v);
                    changed |= DrawSlider("Health & Safety", "This building's Health/Safety contribution.", bv.healthSafety, -100f, 100f, v => bv.healthSafety = v);
                    changed |= DrawSlider("Culture & Edu", "This building's Culture/Education contribution.", bv.cultureEdu, -100f, 100f, v => bv.cultureEdu = v);
                }
            }

            // QOL pass thresholds
            if (DrawFoldout("QOL pass thresholds", "Final QOL when the timer ends picks an end-screen band. Drag a cutoff to move where each band starts."))
                DrawQolBands();

            // Advanced (dev), collapsed by default
            if (DrawFoldout("Advanced (dev) - scoring internals", "Scoring-curve internals. Already dialed in - usually leave these alone."))
            {
                changed |= DrawSlider("Score Normalizer (Norm)",
                    "1 full contribution = NORM raw -> 100%. Higher = harder to fill pillars.",
                    simulationEngine.Norm, 1f, 500f, v => simulationEngine.Norm = v);
                changed |= DrawSlider("Building Strength (Influence Ref Base)",
                    "Divides building base value. Higher = weaker per-building impact.",
                    simulationEngine.InfluenceRefBase, 0.1f, 100f, v => simulationEngine.InfluenceRefBase = v);
                changed |= DrawSlider("Decay Reference Distance (m)",
                    "Distance where decay curve = 50%. Higher = buildings reach further.",
                    simulationEngine.InfluenceReferenceMeters, 0.1f, 100f, v => simulationEngine.InfluenceReferenceMeters = v);
                changed |= DrawSlider("Distance Decay Steepness",
                    "Exponent for falloff curve. Higher = sharper drop-off with distance.",
                    simulationEngine.DistanceExponent, 0f, 5f, v => simulationEngine.DistanceExponent = v);
                changed |= DrawSlider("Min Effective Distance",
                    "Floor in game units. Prevents near-zero path from inflating score.",
                    simulationEngine.DistanceFloor, 0f, 200f, v => simulationEngine.DistanceFloor = v);
                changed |= DrawSlider("Units-to-Meters Scale",
                    "Game units / scale = meters. Controls how far 1 unit feels.",
                    simulationEngine.DistanceScale, 1f, 500f, v => simulationEngine.DistanceScale = v);
                changed |= DrawSlider("Max Road Network Reach",
                    "Max path length along roads for scoring. Beyond = no influence.",
                    simulationEngine.MaxRoadDistance, 1f, 600f, v => simulationEngine.MaxRoadDistance = v);
                changed |= DrawSlider("Road Connection Range",
                    "Max distance from building to road to count as connected.",
                    simulationEngine.RoadConnectRange, 1f, 1000f, v => simulationEngine.RoadConnectRange = v);
            }

            } // end expert mode

            if (changed)
                simulationEngine.RecalculateMetrics();

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        GUI.matrix = oldMatrix;
    }

    /// <summary>Drawn in screen space (not affected by <see cref="pickerUiScale"/>) so the control stays usable at any zoom.</summary>
    private void DrawDebugMenuMasterScaleBar(float pad)
    {
        Matrix4x4 prev = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;

        float w = Mathf.Max(120f, Screen.width - pad * 2f);
        var barRect = new Rect(pad, pad, w, MasterScaleBarScreenH);
        var oc = GUI.color;
        GUI.color = new Color(0.06f, 0.07f, 0.09f, 0.94f);
        GUI.DrawTexture(barRect, Texture2D.whiteTexture);
        GUI.color = oc;

        GUILayout.BeginArea(new Rect(barRect.x + 8f, barRect.y + 6f, barRect.width - 16f, barRect.height - 12f));
        GUILayout.BeginHorizontal();
        var labelStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        GUILayout.Label("Menu scale", labelStyle, GUILayout.Width(92f));
        pickerUiScale = GUILayout.HorizontalSlider(pickerUiScale, MenuScaleMin, MenuScaleMax, GUILayout.ExpandWidth(true));
        pickerUiScale = Mathf.Clamp(pickerUiScale, MenuScaleMin, MenuScaleMax);
        GUILayout.Label($"{pickerUiScale:F2}×", GUILayout.Width(48f));
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        GUI.matrix = prev;
    }

    /// <summary>Compact building catalog inside the controls panel: filter, selection preview,
    /// and one clickable row per building (fixed-height inner scroll).</summary>
    private void DrawBuildingCatalog(BuildingDefinition[] buildings, BuildingDefinition selectedDef)
    {
        if (buildings == null || buildings.Length == 0)
        {
            GUILayout.Label("No buildings found. Ensure `GameConfigLoader` is present and loaded.", GUI.skin.box);
            return;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Filter", GUILayout.Width(40f));
        _pickerFilter = GUILayout.TextField(_pickerFilter ?? "", GUILayout.MinWidth(90f));
        if (GUILayout.Button("Reset halos", GUILayout.Width(88f)) && buildingSpawner != null)
        {
            buildingSpawner.ClearDebugHaloScales();
            simulationEngine?.RefreshAllTileConnections();
        }
        GUILayout.EndHorizontal();

        DrawPickerSelectionPreview(selectedDef);

        _pickerScroll = GUILayout.BeginScrollView(_pickerScroll, GUI.skin.box, GUILayout.Height(240f));
        var rowStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = 12 };
        var subStyle = new GUIStyle(GUI.skin.label) { fontSize = 10 };
        subStyle.normal.textColor = new Color(0.75f, 0.78f, 0.85f);

        for (int i = 0; i < buildings.Length; i++)
        {
            var b = buildings[i];
            if (b == null || string.IsNullOrEmpty(b.Id)) continue;

            string name = GetBuildingDisplayName(b);
            if (!PassesFilter(b, name, _pickerFilter)) continue;

            bool selected = _currentBuildingId == b.Id;
            var oldColor = GUI.color;
            if (selected) GUI.color = new Color(0.65f, 0.9f, 1f, 1f);

            GUILayout.BeginHorizontal(GUILayout.Height(26f));
            var thumbRect = GUILayoutUtility.GetRect(24f, 24f, GUILayout.Width(24f), GUILayout.Height(24f));
            DrawPickerListThumb(thumbRect, b.Id);

            float haloPx = 0f;
            buildingSpawner?.TryGetEstimatedBuildingRadius(b.Id, out haloPx);
            float impactR = 0f;
            simulationEngine?.TryGetImpactSearchRadius(b.Id, out impactR);
            string row = $"{(selected ? "● " : "")}{name}   ${b.Price}   halo {haloPx:F0}   stops {impactR:F0}";
            if (GUILayout.Button(row, rowStyle, GUILayout.Height(24f), GUILayout.ExpandWidth(true)))
                _currentBuildingId = b.Id;
            GUILayout.EndHorizontal();

            if ((selected || _showDetails) && b.BaseValues != null)
                GUILayout.Label(
                    $"     {b.ImpactSize} | imp {b.Importance:0.00} | Env {b.BaseValues.environment}  Eco {b.BaseValues.economy}  Safe {b.BaseValues.healthSafety}  Cul {b.BaseValues.cultureEdu}",
                    subStyle);

            GUI.color = oldColor;
        }
        GUILayout.EndScrollView();
    }

    private void DrawPickerSelectionPreview(BuildingDefinition selected)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        if (selected == null)
        {
            GUILayout.Label(string.IsNullOrEmpty(_currentBuildingId)
                ? "No building selected — pick one below or use keys 1–4."
                : $"Unknown id '{_currentBuildingId}' — not in loaded config.");
            GUILayout.EndVertical();
            return;
        }

        var titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
        GUILayout.Label($"{GetBuildingDisplayName(selected)}  ({selected.Id})", titleStyle);

        GUILayout.BeginHorizontal();
        var previewRect = GUILayoutUtility.GetRect(120f, 120f, GUILayout.Width(120f), GUILayout.Height(120f));
        float haloPx = 32f;
        if (buildingSpawner != null)
            buildingSpawner.TryGetEstimatedBuildingRadius(selected.Id, out haloPx);
        DrawPickerPreviewSquare(previewRect, selected.Id, haloPx);

        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        if (buildingSpawner != null)
        {
            buildingSpawner.TryGetEstimatedBuildingRadius(selected.Id, out haloPx);
            GUILayout.Label($"Halo radius ~{haloPx:F0}px (tune on the right)", GUI.skin.label);
        }

        if (simulationEngine != null && simulationEngine.TryGetImpactSearchRadius(selected.Id, out float impactR))
            GUILayout.Label($"Stop search radius ({selected.ImpactSize}): {impactR:F0} units", GUI.skin.label);

        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
    }

    private void DrawPickerListThumb(Rect r, string buildingId)
    {
        var oc = GUI.color;
        GUI.color = new Color(0.15f, 0.16f, 0.2f, 1f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = oc;

        if (buildingSpawner != null && buildingSpawner.TryGetPreviewSprite(buildingId, out var sp) && sp != null)
        {
            float inset = 4f;
            var ir = new Rect(r.x + inset, r.y + inset, r.width - inset * 2f, r.height - inset * 2f);
            DrawPickerSprite(sp, ir);
        }
        else
        {
            var small = new GUIStyle(GUI.skin.label) { fontSize = 9, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Label(r, buildingId.Length > 10 ? buildingId.Substring(0, 10) + "…" : buildingId, small);
        }
    }

    private void DrawPickerPreviewSquare(Rect r, string buildingId, float haloWorldPx)
    {
        var oc = GUI.color;
        GUI.color = new Color(0.1f, 0.11f, 0.14f, 1f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = oc;

        Vector2 center = r.center;
        float t = Mathf.InverseLerp(8f, 150f, Mathf.Clamp(haloWorldPx, 8f, 200f));
        float ringDiameter = Mathf.Lerp(28f, Mathf.Min(r.width, r.height) - 6f, t);
        var ringRect = new Rect(center.x - ringDiameter * 0.5f, center.y - ringDiameter * 0.5f, ringDiameter, ringDiameter);
        GUI.color = new Color(0.2f, 0.92f, 0.55f, 0.9f);
        GUI.DrawTexture(ringRect, GetPickerRingTexture(), ScaleMode.StretchToFill);
        GUI.color = oc;

        if (buildingSpawner != null && buildingSpawner.TryGetPreviewSprite(buildingId, out var sp) && sp != null)
        {
            float iw = Mathf.Min(76f, r.width * 0.62f);
            DrawPickerSprite(sp, new Rect(center.x - iw * 0.5f, center.y - iw * 0.5f, iw, iw));
        }
    }

    private static void DrawPickerSprite(Sprite sprite, Rect r)
    {
        if (sprite == null || sprite.texture == null) return;
        Rect tr = sprite.textureRect;
        float tw = sprite.texture.width;
        float th = sprite.texture.height;
        var uv = new Rect(tr.x / tw, tr.y / th, tr.width / tw, tr.height / th);
        GUI.DrawTextureWithTexCoords(r, sprite.texture, uv);
    }

    private Texture2D GetPickerRingTexture()
    {
        if (_pickerRingTexture != null) return _pickerRingTexture;

        const int n = 128;
        _pickerRingTexture = new Texture2D(n, n, TextureFormat.ARGB32, false);
        float cx = (n - 1) * 0.5f;
        float cy = (n - 1) * 0.5f;
        float inner = n * 0.36f;
        float outer = n * 0.48f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy));
                float a = d >= inner && d <= outer ? 0.95f : 0f;
                _pickerRingTexture.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        _pickerRingTexture.Apply();
        _pickerRingTexture.hideFlags = HideFlags.DontSave;
        return _pickerRingTexture;
    }

    private string GetBuildingDisplayName(BuildingDefinition b)
    {
        if (b == null) return "(null)";
        if (localization != null && !string.IsNullOrEmpty(b.LocalizationKey))
        {
            string s = localization.GetString(b.LocalizationKey);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return string.IsNullOrEmpty(b.Id) ? "(unnamed)" : b.Id;
    }

    private static bool PassesFilter(BuildingDefinition b, string displayName, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        filter = filter.Trim();
        if (b == null) return false;

        if (!string.IsNullOrEmpty(displayName) && displayName.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (!string.IsNullOrEmpty(b.Id) && b.Id.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (!string.IsNullOrEmpty(b.Category) && b.Category.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (!string.IsNullOrEmpty(b.ImpactSize) && b.ImpactSize.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    private static bool WasPressedThisFrame(Keyboard keyboard, Key key)
    {
        if (keyboard == null) return false;
        // Generic lookup so whichever Key you set in the inspector will toggle reliably.
        var control = keyboard[key];
        return control != null && control.wasPressedThisFrame;
    }

    private RectTransform FindMarkerRecursive(string instanceName)
    {
        if (buildingSpawner == null || buildingSpawner.ContentRoot == null) return null;
        var all = buildingSpawner.ContentRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].name == instanceName)
                return all[i] as RectTransform;
        }
        return null;
    }

    private Vector2 _slidersScroll;

    private void DrawSaveRow()
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save to file", GUILayout.Height(26f), GUILayout.Width(96f)))
        {
            _lastSaveOk = coordinator != null && coordinator.SaveConfigDebug();
            _lastSaveMessage = _lastSaveOk
                ? $"Saved to game_config.json  ({System.DateTime.Now:HH:mm:ss})"
                : (coordinator == null ? "No coordinator found - cannot save." : "Save failed (on web use Export) - see console.");
        }
        if (GUILayout.Button("Export", GUILayout.Height(26f), GUILayout.Width(80f)))
        {
            string json = coordinator != null ? coordinator.ExportConfigDebug() : null;
            if (!string.IsNullOrEmpty(json))
            {
                ConfigFileIO.Export("game_config.json", json);
                _lastSaveOk = true;
                _lastSaveMessage = $"Exported game_config.json  ({System.DateTime.Now:HH:mm:ss})";
            }
            else { _lastSaveOk = false; _lastSaveMessage = "Export failed - no config loaded."; }
        }
        if (GUILayout.Button("Import", GUILayout.Height(26f), GUILayout.Width(80f)))
        {
            ConfigFileIO.BeginImport();
            _awaitingImport = true;
            _lastSaveOk = true;
            _lastSaveMessage = "Import: choose a JSON file...";
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        var msgStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 10 };
        if (string.IsNullOrEmpty(_lastSaveMessage))
        {
            msgStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            GUILayout.Label("Save writes StreamingAssets/game_config.json (Editor/desktop). Export/Import download/upload a JSON file - use these on web.", msgStyle);
        }
        else
        {
            msgStyle.normal.textColor = _lastSaveOk ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.6f, 0.5f);
            GUILayout.Label(_lastSaveMessage, msgStyle);
        }
        GUILayout.EndVertical();
    }

    private bool DrawHaloSizeSlider(string label, string sizeKey)
    {
        if (buildingSpawner == null) return false;
        return DrawSlider(label,
            "1x = prefab/catalog default. Higher = bigger halo and longer reach for this size.",
            buildingSpawner.GetDebugHaloScaleForSize(sizeKey),
            BuildingSpawner.DebugHaloMultiplierMin,
            BuildingSpawner.DebugHaloMultiplierMax,
            v =>
            {
                buildingSpawner.SetDebugHaloScaleForSize(sizeKey, v);
                simulationEngine?.RefreshAllTileConnections();
            });
    }

    private void DrawHelpBox()
    {
        var s = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.UpperLeft, fontSize = 11 };
        s.normal.textColor = new Color(0.85f, 0.9f, 1f);
        GUILayout.Label(
            "How to use\n" +
            $"- Toggle this menu with the {togglePickerKey} key.\n" +
            "- Playtesting = knobs to tune while balancing. Advanced (dev) = scoring internals, already dialed in.\n" +
            "- Changes apply live. Use 'Save config to file' to persist; otherwise they reset on restart / relaunch.\n" +
            "- Keys 1-4 pick a building; click the table to place; drag to move; hold ESC + click to delete.",
            s);
    }

    private static void DrawSectionHeader(string title, string subtitle)
    {
        GUILayout.Space(4f);
        var t = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
        t.normal.textColor = new Color(0.3f, 0.85f, 1f);
        GUILayout.Label(title, t);
        if (!string.IsNullOrEmpty(subtitle))
        {
            var st = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 10 };
            st.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            GUILayout.Label(subtitle, st);
        }
    }

    private static void DrawDisabledRow(string label, string note)
    {
        var oc = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.45f);
        GUILayout.BeginVertical(GUI.skin.box);
        var l = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        GUILayout.Label(label, l);
        var d = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true, fontStyle = FontStyle.Italic };
        GUILayout.Label(note, d);
        GUILayout.EndVertical();
        GUI.color = oc;
    }

    private void DrawQolBands()
    {
        var bands = configLoader != null && configLoader.Config != null ? configLoader.Config.EndMessages : null;
        if (bands == null || bands.Length == 0)
        {
            GUILayout.Label("No end-message bands found in config.", GUI.skin.box);
            return;
        }

        int curQol = simulationEngine != null ? Mathf.RoundToInt(simulationEngine.Qol) : -1;
        int activeBand = -1;
        for (int i = 0; i < bands.Length; i++)
            if (bands[i] != null && curQol >= bands[i].min && curQol <= bands[i].max) { activeBand = i; break; }

        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            if (b == null) continue;
            var oc = GUI.color;
            if (i == activeBand) GUI.color = new Color(0.3f, 1f, 0.5f, 1f);
            GUILayout.Label($"{(i == activeBand ? "> " : "   ")}Band {i + 1}: {b.min}-{b.max}  ({b.titleKey})");
            GUI.color = oc;
        }

        if (curQol >= 0)
            GUILayout.Label($"Current QOL = {curQol}  ->  " + (activeBand >= 0 ? $"Band {activeBand + 1}" : "no band"), GUI.skin.box);

        for (int i = 0; i < bands.Length - 1; i++)
        {
            if (bands[i] == null || bands[i + 1] == null) continue;
            int idx = i;
            DrawIntSlider($"Cutoff: Band {i + 1} -> {i + 2}",
                "QOL value where the next band begins.",
                bands[i].max, 0, 100, v =>
                {
                    v = Mathf.Clamp(v, bands[idx].min, bands[idx + 1].max);
                    bands[idx].max = v;
                    bands[idx + 1].min = v;
                });
        }
    }

    private static float DrawSliderRaw(string label, string description, float current, float min, float max, string valueText)
    {
        var labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.normal.textColor = Color.white;
        labelStyle.fontStyle = FontStyle.Bold;

        var descStyle = new GUIStyle(GUI.skin.label);
        descStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
        descStyle.fontSize = 10;
        descStyle.wordWrap = true;

        var valueStyle = new GUIStyle(GUI.skin.label);
        valueStyle.normal.textColor = new Color(1f, 0.9f, 0.3f);
        valueStyle.alignment = TextAnchor.MiddleRight;

        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.BeginHorizontal();
        GUILayout.Label(label, labelStyle, GUILayout.ExpandWidth(true));
        GUILayout.Label(valueText, valueStyle, GUILayout.Width(70f));
        GUILayout.EndHorizontal();

        // Compact mode: per-slider explanations only when 'Show help & descriptions' is on.
        if (_showDetails && !string.IsNullOrEmpty(description))
            GUILayout.Label(description, descStyle);

        // Filled bar behind slider
        var sliderRect = GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.horizontalSlider, GUILayout.Height(16f));
        float fill = Mathf.InverseLerp(min, max, current);
        var barColor = GUI.color;
        GUI.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
        GUI.DrawTexture(sliderRect, Texture2D.whiteTexture);
        GUI.color = new Color(0.3f, 0.75f, 1f, 0.6f);
        GUI.DrawTexture(new Rect(sliderRect.x, sliderRect.y, sliderRect.width * fill, sliderRect.height), Texture2D.whiteTexture);
        GUI.color = barColor;

        float next = GUI.HorizontalSlider(sliderRect, current, min, max);

        GUILayout.EndVertical();
        return next;
    }

    private static bool DrawSlider(string label, string description, float current, float min, float max, System.Action<float> setter)
    {
        float next = DrawSliderRaw(label, description, current, min, max, current.ToString("F2"));
        if (!Mathf.Approximately(next, current))
        {
            setter(next);
            return true;
        }
        return false;
    }

    private static bool DrawIntSlider(string label, string description, int current, int min, int max, System.Action<int> setter)
    {
        float next = DrawSliderRaw(label, description, current, min, max, current.ToString("0"));
        int rounded = Mathf.RoundToInt(next);
        if (rounded != current)
        {
            setter(rounded);
            return true;
        }
        return false;
    }
}

