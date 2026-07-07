using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using CityTwin.Core;
using CityTwin.UI;

/// <summary>
/// In-play road layout editor, opened from the backquote playtest menu.
///
/// Edit mode shows draggable handles for:
///  - hub (city) centers  — cyan; moving one regenerates the road network live,
///  - road extension endpoints — orange; dragging pins that end as a manual override.
/// Handles are clamped to the screen so endpoints near the fan edge stay grabbable.
///
/// Save writes StreamingAssets/road_layout.json — ONE shared file for all game
/// instances, versioned with the repo so layouts travel between machines. The layout
/// loads at runtime on every platform, and the editing UI works in builds too (opened
/// from the playtest menu), so the map can be tuned on the installation hardware.
/// </summary>
public class RoadNetworkEditor : MonoBehaviour
{
    [Header("References (auto-found in game instance if null)")]
    [SerializeField] private GameInstanceCoordinator coordinator;
    [SerializeField] private BuildingSpawner buildingSpawner;
    [SerializeField] private HubConnectionRenderer connectionRenderer;
    [SerializeField] private HubRegistry hubRegistry;
    [SerializeField] private Camera uiCamera;

    [Header("Handles")]
    [SerializeField] private float handleRadius = 15f;
    [SerializeField] private float screenEdgeMargin = 20f;

    public bool EditModeActive { get; private set; }

    private string _status = "";
    private bool _statusOk = true;

    // ── persistence data ──
    [System.Serializable] private class SavedV2 { public float x, y; }
    [System.Serializable] private class SavedExtension { public int hubMin, hubMax, end; public float x, y; }
    [System.Serializable] private class SavedWaypoint { public int hubMin, hubMax, seg, index; public float x, y; }
    [System.Serializable]
    private class SavedLayout
    {
        [Tooltip("Name of the hub layout preset this layout was built on. Pins the preset at startup/restart so the custom map always loads on the right hubs.")]
        public string preset = "";
        public List<SavedV2> hubs = new List<SavedV2>();
        public List<SavedExtension> extensions = new List<SavedExtension>();
        public List<SavedWaypoint> waypoints = new List<SavedWaypoint>();
    }

    /// <summary>All game instances share one layout file: the quadrants are copies of the same
    /// map, and the old per-instance naming (road_layout_&lt;GameObject name&gt;.json) meant a layout
    /// edited on instance 1 never reached the other three copies. Public so HubLayoutManager can
    /// peek the pinned preset name from the same file.</summary>
    public static string SharedLayoutPath => Path.Combine(Application.streamingAssetsPath, "road_layout.json");
    private static string SharedPath => SharedLayoutPath;

    /// <summary>Legacy per-instance file name, kept for migration of existing layouts.</summary>
    private string LegacyInstancePath
    {
        get
        {
            string safe = gameObject.name;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            safe = safe.Replace(' ', '_');
            return Path.Combine(Application.streamingAssetsPath, "road_layout_" + safe + ".json");
        }
    }

    /// <summary>Where to load from: the shared file when present, otherwise this instance's legacy
    /// file, otherwise ANY legacy road_layout_*.json (all quadrants share the same map, so the
    /// first instance's old file is a valid layout for every copy).</summary>
    private string ResolveLoadPath()
    {
        if (File.Exists(SharedPath)) return SharedPath;
        if (File.Exists(LegacyInstancePath)) return LegacyInstancePath;
        try
        {
            var candidates = Directory.GetFiles(Application.streamingAssetsPath, "road_layout_*.json");
            if (candidates.Length > 0)
            {
                System.Array.Sort(candidates);
                return candidates[0];
            }
        }
        catch { /* StreamingAssets may be unreadable on some platforms; fall through */ }
        return null;
    }

    private void Awake()
    {
        if (coordinator == null) coordinator = GetComponentInParent<GameInstanceCoordinator>(true);
        if (coordinator == null) coordinator = GetComponent<GameInstanceCoordinator>();
        Transform root = coordinator != null ? coordinator.transform : transform;
        if (buildingSpawner == null) buildingSpawner = root.GetComponentInChildren<BuildingSpawner>(true);
        if (connectionRenderer == null) connectionRenderer = root.GetComponentInChildren<HubConnectionRenderer>(true);
        if (hubRegistry == null) hubRegistry = root.GetComponentInChildren<HubRegistry>(true);
        if (uiCamera == null)
        {
            var canvas = GetComponentInParent<Canvas>();
            uiCamera = canvas != null && canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
        }
    }

    private HubLayoutManager _layoutManager;

    private IEnumerator Start()
    {
        yield return ApplyWhenHubsReady();

        // Round restarts re-pick the layout preset; re-apply the saved roads every time so
        // the custom map survives the whole day, not just the first round.
        _layoutManager = GetComponentInChildren<HubLayoutManager>(true);
        if (_layoutManager == null && coordinator != null)
            _layoutManager = coordinator.GetComponentInChildren<HubLayoutManager>(true);
        if (_layoutManager != null) _layoutManager.OnPresetActivated += HandlePresetActivated;
    }

    private void OnDestroy()
    {
        if (_layoutManager != null) _layoutManager.OnPresetActivated -= HandlePresetActivated;
    }

    private void HandlePresetActivated()
    {
        if (isActiveAndEnabled) StartCoroutine(ApplyWhenHubsReady());
    }

    private IEnumerator ApplyWhenHubsReady()
    {
        // Wait until hubs actually exist (config loads async) before applying the saved layout,
        // otherwise a hub-count mismatch drops the saved city positions.
        float timeout = Time.unscaledTime + 8f;
        while (Time.unscaledTime < timeout)
        {
            if (hubRegistry != null)
            {
                hubRegistry.FetchHubs();
                if (hubRegistry.Hubs != null && hubRegistry.Hubs.Count > 0) break;
            }
            yield return null;
        }
        yield return null;
        LoadAndApply();
    }

    public void ToggleEditMode()
    {
        // Available in builds too, so the map can be tuned on the installation hardware
        // straight from the playtest menu.
        EditModeActive = !EditModeActive;
        _drag = DragTarget.None;
    }

    // ── persistence ──

    public void SaveLayout()
    {
        if (hubRegistry == null || connectionRenderer == null) return;
        hubRegistry.FetchHubs();

        var data = new SavedLayout();

        // Record the active preset: it pins the map at startup/restart so this layout is
        // always applied to the hubs it was built on (instead of a random preset).
        var lm = _layoutManager != null ? _layoutManager : GetComponentInChildren<HubLayoutManager>(true);
        if (lm == null && coordinator != null) lm = coordinator.GetComponentInChildren<HubLayoutManager>(true);
        data.preset = lm != null && lm.ActivePreset != null ? lm.ActivePreset.gameObject.name : "";

        foreach (var hub in hubRegistry.Hubs)
        {
            var rt = hub.transform as RectTransform;
            Vector2 p = rt != null ? rt.anchoredPosition : (Vector2)hub.transform.localPosition;
            data.hubs.Add(new SavedV2 { x = p.x, y = p.y });
        }

        var overrides = new List<HubConnectionRenderer.ExtensionOverrideEntry>();
        connectionRenderer.GetExtensionOverrides(overrides);
        foreach (var o in overrides)
            data.extensions.Add(new SavedExtension { hubMin = o.hubMin, hubMax = o.hubMax, end = o.end, x = o.pos.x, y = o.pos.y });

        var wps = new List<HubConnectionRenderer.WaypointEntry>();
        connectionRenderer.GetWaypointEntries(wps);
        foreach (var w in wps)
            data.waypoints.Add(new SavedWaypoint { hubMin = w.hubMin, hubMax = w.hubMax, seg = w.seg, index = w.index, x = w.pos.x, y = w.pos.y });

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SharedPath));
            File.WriteAllText(SharedPath, JsonUtility.ToJson(data, true));
            // The shared file supersedes this instance's legacy file; remove it so it can never
            // shadow the shared layout on future loads.
            if (File.Exists(LegacyInstancePath))
            {
                File.Delete(LegacyInstancePath);
                if (File.Exists(LegacyInstancePath + ".meta")) File.Delete(LegacyInstancePath + ".meta");
            }
            _statusOk = true;
            _status = $"Saved (shared, all instances): {data.hubs.Count} hubs, {data.extensions.Count} ends, {data.waypoints.Count} bends"
                    + (string.IsNullOrEmpty(data.preset) ? "" : $" - map pinned to '{data.preset}'");
            Debug.Log("[RoadNetworkEditor] Saved shared road layout (preset '" + data.preset + "'): " + SharedPath);
        }
        catch (System.Exception e)
        {
            _statusOk = false;
            _status = "Save FAILED: " + e.Message;
            Debug.LogError("[RoadNetworkEditor] Save failed: " + e);
        }
    }

    public void LoadAndApply()
    {
        string loadPath = ResolveLoadPath();
        if (loadPath == null)
        {
            // Loud on purpose: a build that ships without the layout used to fail silently and
            // players just saw the default map. This line makes the player log diagnostic.
            Debug.LogWarning("[RoadNetworkEditor] No road layout found (looked for " + SharedPath
                + " and road_layout_*.json in " + Application.streamingAssetsPath + ") - using default map.");
            return;
        }
        if (hubRegistry == null || connectionRenderer == null) return;
        Debug.Log("[RoadNetworkEditor] Loading road layout: " + loadPath);

        SavedLayout data;
        try { data = JsonUtility.FromJson<SavedLayout>(File.ReadAllText(loadPath)); }
        catch (System.Exception e) { Debug.LogWarning("[RoadNetworkEditor] Failed to read layout: " + e.Message); return; }
        if (data == null) return;

        hubRegistry.FetchHubs();
        var hubs = hubRegistry.Hubs;
        if (data.hubs != null && data.hubs.Count == hubs.Count)
        {
            for (int i = 0; i < hubs.Count; i++)
            {
                var rt = hubs[i].transform as RectTransform;
                if (rt != null) rt.anchoredPosition = new Vector2(data.hubs[i].x, data.hubs[i].y);
                else hubs[i].transform.localPosition = new Vector3(data.hubs[i].x, data.hubs[i].y, hubs[i].transform.localPosition.z);
            }
        }
        else if (data.hubs != null && data.hubs.Count > 0)
        {
            Debug.LogWarning($"[RoadNetworkEditor] Saved hub count ({data.hubs.Count}) != current ({hubs.Count}); hub positions skipped.");
        }

        connectionRenderer.ClearExtensionOverrides();
        if (data.extensions != null)
            foreach (var o in data.extensions)
                connectionRenderer.SetExtensionOverride(o.hubMin, o.hubMax, o.end, new Vector2(o.x, o.y));

        // Waypoints are saved in list order, so appending rebuilds each sub-segment chain correctly.
        connectionRenderer.ClearWaypoints();
        if (data.waypoints != null)
            foreach (var w in data.waypoints)
                connectionRenderer.InsertWaypoint(w.hubMin, w.hubMax, w.seg, int.MaxValue, new Vector2(w.x, w.y));

        coordinator?.RegenerateRoadNetworkDebug();
        _statusOk = true;
        _status = $"Loaded: {data.hubs?.Count ?? 0} hubs, {data.extensions?.Count ?? 0} ends, {data.waypoints?.Count ?? 0} bends";
        Debug.Log("[RoadNetworkEditor] Applied saved road layout (" + (data.hubs?.Count ?? 0) + " hubs, "
            + (data.extensions?.Count ?? 0) + " endpoint overrides, " + (data.waypoints?.Count ?? 0) + " waypoints).");
    }

    // ── drag-editing UI (runtime IMGUI + Input System — works in builds too) ──

    private enum DragTarget { None, Hub, Extension, Waypoint }
    private DragTarget _drag = DragTarget.None;
    private int _dragHubIndex;
    private (int, int, int) _dragExtKey;
    private (int, int, int, int) _dragWpKey; // hubMin, hubMax, seg, index
    private float _nextRegenTime;

    // Selected road (hubMin, hubMax). Only the selected road's bends/midpoints are editable,
    // so dense overlapping roads never fight for the click. Click an orange end to select.
    private bool _hasSel;
    private (int, int) _sel;
    private readonly List<HubConnectionRenderer.HubLineSnapshot> _lines = new List<HubConnectionRenderer.HubLineSnapshot>();
    // Tagged road path for the editor: points + per-point (seg,index) + per-gap (seg,insertIndex).
    private readonly List<Vector2> _pathBuf = new List<Vector2>();
    private readonly List<(int seg, int idx)> _ptTag = new List<(int, int)>();
    private readonly List<(int seg, int insert)> _gapTag = new List<(int, int)>();

    // Content-local grab radii (≈ screen px; the content rect maps ~1:1 to on-screen fan width).
    private const float HubGrabContent = 55f;
    private const float DotGrabContent = 34f;

    /// <summary>
    /// All input runs here through the SAME mouse path as tile placement
    /// (Mouse.current + ScreenPointToWorldPointInRectangle), so hit-testing is exact.
    /// OnGUI only draws. Everything is compared in content-local space, where markers live.
    /// </summary>
    private void Update()
    {
        if (!EditModeActive) return;
        if (buildingSpawner == null || buildingSpawner.ContentRoot == null || connectionRenderer == null || hubRegistry == null) return;

        var mouse = Mouse.current;
        if (mouse == null) return;

        Vector2 screen = mouse.position.ReadValue();
        bool hasContent = RectTransformUtility.ScreenPointToWorldPointInRectangle(
            buildingSpawner.ContentRoot, screen, uiCamera, out Vector3 world);
        Vector2 mouseContent = hasContent ? buildingSpawner.WorldToContentLocal(world) : Vector2.zero;

        bool leftDown = mouse.leftButton.wasPressedThisFrame;
        bool leftHeld = mouse.leftButton.isPressed;
        bool leftUp = mouse.leftButton.wasReleasedThisFrame;
        bool rightDown = mouse.rightButton.wasPressedThisFrame;

        hubRegistry.FetchHubs();
        var hubs = hubRegistry.Hubs;
        connectionRenderer.GetHubLineSnapshots(_lines);

        // ── continue an active drag ──
        if (_drag != DragTarget.None)
        {
            if (leftHeld && hasContent)
            {
                if (_drag == DragTarget.Hub && _dragHubIndex < hubs.Count)
                {
                    hubs[_dragHubIndex].transform.position = world;
                    if (Time.unscaledTime >= _nextRegenTime)
                    {
                        _nextRegenTime = Time.unscaledTime + 0.15f;
                        coordinator?.RegenerateRoadNetworkDebug();
                    }
                }
                else if (_drag == DragTarget.Extension)
                {
                    connectionRenderer.SetExtensionOverride(_dragExtKey.Item1, _dragExtKey.Item2, _dragExtKey.Item3, mouseContent);
                    connectionRenderer.RefreshNow();
                }
                else if (_drag == DragTarget.Waypoint)
                {
                    connectionRenderer.SetWaypoint(_dragWpKey.Item1, _dragWpKey.Item2, _dragWpKey.Item3, _dragWpKey.Item4, mouseContent);
                    connectionRenderer.RefreshNow();
                }
            }
            if (leftUp)
            {
                if (_drag == DragTarget.Hub)
                    coordinator?.RegenerateRoadNetworkDebug();
                _drag = DragTarget.None;
            }
            return;
        }

        // ── panel buttons (handled here because IMGUI clicks are dead under the new Input System) ──
        if (leftDown && HandlePanelClick(screen))
            return;

        if (!hasContent) return;

        HubConnectionRenderer.HubLineSnapshot selLine = default;
        bool selValid = _hasSel && TryGetSelLine(out selLine);

        // ── right-click delete ──
        if (rightDown)
        {
            // orange (any road) → cull that extension end
            foreach (var line in _lines)
                if (TryPickExtension(line, mouseContent, out int end, out Vector2 hubPos))
                {
                    connectionRenderer.SetExtensionOverride(line.HubMin, line.HubMax, end, hubPos);
                    connectionRenderer.RefreshNow();
                    return;
                }
            // green (selected road only) → remove that bend
            if (selValid)
            {
                BuildTaggedPath(selLine);
                for (int i = 0; i < _pathBuf.Count; i++)
                    if (_ptTag[i].seg >= 0 && WithinDot(mouseContent, _pathBuf[i]))
                    {
                        connectionRenderer.RemoveWaypoint(_sel.Item1, _sel.Item2, _ptTag[i].seg, _ptTag[i].idx);
                        connectionRenderer.RefreshNow();
                        return;
                    }
            }
            return;
        }

        if (!leftDown) return;

        // ── 1) Editing the SELECTED road: only its bends and midpoints are pickable, so
        //        overlapping neighbor roads can never steal the click. Bends stay on the
        //        sub-segment (extension or core) they were placed on. ──
        if (selValid)
        {
            BuildTaggedPath(selLine);

            // existing green bends
            for (int i = 0; i < _pathBuf.Count; i++)
                if (_ptTag[i].seg >= 0 && WithinDot(mouseContent, _pathBuf[i]))
                {
                    _drag = DragTarget.Waypoint;
                    _dragWpKey = (_sel.Item1, _sel.Item2, _ptTag[i].seg, _ptTag[i].idx);
                    return;
                }

            // "+" midpoints → insert a bend into the correct sub-segment
            for (int s = 0; s < _pathBuf.Count - 1; s++)
            {
                if ((_pathBuf[s] - _pathBuf[s + 1]).sqrMagnitude < 64f) continue; // stub
                Vector2 mid = Vector2.Lerp(_pathBuf[s], _pathBuf[s + 1], 0.5f);
                if (WithinDot(mouseContent, mid))
                {
                    var (seg, insert) = _gapTag[s];
                    connectionRenderer.InsertWaypoint(_sel.Item1, _sel.Item2, seg, insert, mid);
                    connectionRenderer.RefreshNow();
                    _drag = DragTarget.Waypoint;
                    _dragWpKey = (_sel.Item1, _sel.Item2, seg, insert);
                    return;
                }
            }
        }

        // ── 2) Click any orange endpoint → select that road AND grab the end (spread out,
        //        unambiguous). This is how you pick which path to edit. ──
        foreach (var line in _lines)
            if (TryPickExtension(line, mouseContent, out int end, out _))
            {
                _sel = (line.HubMin, line.HubMax);
                _hasSel = true;
                _drag = DragTarget.Extension;
                _dragExtKey = (line.HubMin, line.HubMax, end);
                return;
            }

        // ── 3) Hubs (city centers) draggable anytime ──
        for (int i = 0; i < hubs.Count; i++)
        {
            Vector2 hubContent = buildingSpawner.WorldToContentLocal(hubs[i].transform.position);
            if ((mouseContent - hubContent).sqrMagnitude <= HubGrabContent * HubGrabContent)
            {
                _drag = DragTarget.Hub;
                _dragHubIndex = i;
                return;
            }
        }

        // ── 4) Clicked empty space → deselect ──
        _hasSel = false;
    }

    private bool TryGetSelLine(out HubConnectionRenderer.HubLineSnapshot selLine)
    {
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].HubMin == _sel.Item1 && _lines[i].HubMax == _sel.Item2)
            {
                selLine = _lines[i];
                return true;
            }
        selLine = default;
        return false;
    }

    private static bool WithinDot(Vector2 a, Vector2 b) => (a - b).sqrMagnitude <= DotGrabContent * DotGrabContent;

    /// <summary>Test both extension ends of a road against the (content-local) mouse.</summary>
    private static bool TryPickExtension(HubConnectionRenderer.HubLineSnapshot line, Vector2 mouseContent, out int end, out Vector2 hubPos)
    {
        if ((line.EndMin - line.HubMinPos).sqrMagnitude >= 4f && WithinDot(mouseContent, line.EndMin))
        {
            end = 0; hubPos = line.HubMinPos; return true;
        }
        if ((line.EndMax - line.HubMaxPos).sqrMagnitude >= 4f && WithinDot(mouseContent, line.EndMax))
        {
            end = 1; hubPos = line.HubMaxPos; return true;
        }
        end = -1; hubPos = default; return false;
    }

    private void OnGUI()
    {
        if (!EditModeActive) return;
        if (buildingSpawner == null || buildingSpawner.ContentRoot == null || connectionRenderer == null || hubRegistry == null) return;

        DrawPanel();

        hubRegistry.FetchHubs();
        var hubs = hubRegistry.Hubs;
        connectionRenderer.GetHubLineSnapshots(_lines);

        HubConnectionRenderer.HubLineSnapshot selLine = default;
        bool selValid = _hasSel && TryGetSelLine(out selLine);

        // Selected road only: "+" midpoints and green bends (keeps the dense center readable).
        if (selValid)
        {
            BuildTaggedPath(selLine);
            for (int s = 0; s < _pathBuf.Count - 1; s++)
            {
                if ((_pathBuf[s] - _pathBuf[s + 1]).sqrMagnitude < 64f) continue;
                Vector2 mid = Vector2.Lerp(_pathBuf[s], _pathBuf[s + 1], 0.5f);
                DrawSmallPlus(ClampToScreen(WorldToGui(ContentLocalToWorld(mid))));
            }
            for (int i = 0; i < _pathBuf.Count; i++)
                if (_ptTag[i].seg >= 0)
                    DrawHandle(ClampToScreen(WorldToGui(ContentLocalToWorld(_pathBuf[i]))), new Color(0.4f, 1f, 0.5f, 0.95f), "•");
        }

        // Orange extension ends for every road: selected = bright yellow, others = dim.
        foreach (var line in _lines)
        {
            bool isSel = selValid && line.HubMin == _sel.Item1 && line.HubMax == _sel.Item2;
            Color c = isSel ? new Color(1f, 0.95f, 0.25f, 1f) : new Color(1f, 0.6f, 0.2f, 0.55f);
            string label = line.HubMin + "·" + line.HubMax;
            if ((line.EndMin - line.HubMinPos).sqrMagnitude >= 4f)
                DrawHandle(ClampToScreen(WorldToGui(ContentLocalToWorld(line.EndMin))), c, label);
            if ((line.EndMax - line.HubMaxPos).sqrMagnitude >= 4f)
                DrawHandle(ClampToScreen(WorldToGui(ContentLocalToWorld(line.EndMax))), c, label);
        }

        // cyan hubs
        for (int i = 0; i < hubs.Count; i++)
            DrawHandle(ClampToScreen(WorldToGui(hubs[i].transform.position)), new Color(0.3f, 0.95f, 1f, 0.95f), "H" + i);
    }

    /// <summary>Small grey "+" marker drawn at a road segment midpoint (drag to insert a bend).</summary>
    private void DrawSmallPlus(Vector2 gui)
    {
        float r = handleRadius * 0.6f;
        var old = GUI.color;
        GUI.color = new Color(0.75f, 0.8f, 0.9f, 0.55f);
        GUI.DrawTexture(new Rect(gui.x - r, gui.y - 2f, r * 2f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(gui.x - 2f, gui.y - r, 4f, r * 2f), Texture2D.whiteTexture);
        GUI.color = old;
    }

    /// <summary>
    /// Build the road's editor path with tags:
    ///   _pathBuf : [EndMin, extMin bends..., HubMin, core bends..., HubMax, extMax bends..., EndMax]
    ///   _ptTag[i]: (seg,index) if that point is a bend, else (-1,-1) for nodes.
    ///   _gapTag[s]: (seg, insertIndex) — where a new bend dropped on gap s belongs.
    /// </summary>
    private void BuildTaggedPath(HubConnectionRenderer.HubLineSnapshot line)
    {
        _pathBuf.Clear();
        _ptTag.Clear();
        _gapTag.Clear();

        AddNode(line.EndMin);
        AddSeg(HubConnectionRenderer.SegExtMin, line.WpExtMin);
        AddNode(line.HubMinPos);
        AddSeg(HubConnectionRenderer.SegCore, line.WpCore);
        AddNode(line.HubMaxPos);
        AddSeg(HubConnectionRenderer.SegExtMax, line.WpExtMax);
        AddNode(line.EndMax);
    }

    private void AddNode(Vector2 p)
    {
        _pathBuf.Add(p);
        _ptTag.Add((-1, -1));
    }

    private void AddSeg(int seg, List<Vector2> wps)
    {
        int n = wps?.Count ?? 0;
        for (int k = 0; k < n; k++)
        {
            _gapTag.Add((seg, k));      // gap leading into this bend
            _pathBuf.Add(wps[k]);
            _ptTag.Add((seg, k));
        }
        _gapTag.Add((seg, n));          // gap from last point of this seg to the next node
    }

    private struct PanelButton { public Rect rect; public string label; public System.Action action; }
    private readonly List<PanelButton> _panelButtons = new List<PanelButton>();

    private float PanelX => 10f;
    private float PanelY => Screen.height * 0.5f - 170f;
    private const float PanelW = 288f;
    private const float ButtonsTopOffset = 168f; // below the info text

    /// <summary>Build the clickable button rects (GUI space). Used by both draw and click-handling.</summary>
    private void BuildPanelButtons()
    {
        _panelButtons.Clear();
        float x = PanelX + 12f, w = PanelW - 24f, h = 30f, gap = 5f;
        float y = PanelY + ButtonsTopOffset;

        void Add(string label, System.Action act)
        {
            _panelButtons.Add(new PanelButton { rect = new Rect(x, y, w, h), label = label, action = act });
            y += h + gap;
        }

        if (_hasSel) Add("Deselect road", () => _hasSel = false);
        Add("Save layout (all instances)", SaveLayout);
        Add("Reload saved", LoadAndApply);
        Add("Clear endpoint overrides", () => { connectionRenderer.ClearExtensionOverrides(); connectionRenderer.RefreshNow(); });
        Add("Clear all bend points", () => { connectionRenderer.ClearWaypoints(); connectionRenderer.RefreshNow(); });
        Add("Close", ToggleEditMode);
    }

    /// <summary>Screen-space (Mouse.current) click on a panel button. Returns true if the panel consumed it.</summary>
    private bool HandlePanelClick(Vector2 screenMouse)
    {
        BuildPanelButtons();
        Vector2 guiPt = new Vector2(screenMouse.x, Screen.height - screenMouse.y);

        for (int i = 0; i < _panelButtons.Count; i++)
            if (_panelButtons[i].rect.Contains(guiPt))
            {
                _panelButtons[i].action?.Invoke();
                return true;
            }

        // Swallow clicks anywhere over the panel box so they don't edit the map behind it.
        float bottom = _panelButtons.Count > 0 ? _panelButtons[_panelButtons.Count - 1].rect.yMax + 34f : PanelY + 300f;
        var panelRect = new Rect(PanelX, PanelY, PanelW, bottom - PanelY);
        return panelRect.Contains(guiPt);
    }

    private void DrawPanel()
    {
        BuildPanelButtons();
        float bottom = _panelButtons[_panelButtons.Count - 1].rect.yMax + 34f;
        var rect = new Rect(PanelX, PanelY, PanelW, bottom - PanelY);

        var bg = GUI.color;
        GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.92f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = bg;
        GUI.Box(rect, "ROAD EDITOR");

        float tx = PanelX + 12f, tw = PanelW - 24f;
        string selText = _hasSel ? ("Editing road  " + _sel.Item1 + "·" + _sel.Item2) : "No road selected";
        var selStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = true };
        selStyle.normal.textColor = _hasSel ? new Color(1f, 0.95f, 0.3f) : new Color(0.8f, 0.8f, 0.8f);
        GUI.Label(new Rect(tx, PanelY + 24f, tw, 20f), selText, selStyle);

        var help = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
        help.normal.textColor = new Color(0.85f, 0.9f, 1f);
        GUI.Label(new Rect(tx, PanelY + 46f, tw, 118f),
            "1) Click an ORANGE end to select a road.\n2) Drag its grey + to bend; drag/right-click its green dots.\nOrange: drag=move, right-click=delete end.\nCyan: drag city centers. Empty click=deselect.",
            help);

        // Buttons (drawn as boxes; clicks handled in Update via Mouse.current).
        var btnStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        btnStyle.normal.textColor = Color.white;
        for (int i = 0; i < _panelButtons.Count; i++)
        {
            GUI.color = new Color(0.16f, 0.2f, 0.28f, 1f);
            GUI.DrawTexture(_panelButtons[i].rect, Texture2D.whiteTexture);
            GUI.color = bg;
            GUI.Label(_panelButtons[i].rect, _panelButtons[i].label, btnStyle);
        }

        if (!string.IsNullOrEmpty(_status))
        {
            var st = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11, fontStyle = FontStyle.Bold };
            st.normal.textColor = _statusOk ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.5f, 0.4f);
            GUI.Label(new Rect(tx, bottom - 30f, tw, 26f), _status, st);
        }
    }

    private void DrawHandle(Vector2 gui, Color color, string label)
    {
        float r = handleRadius;
        var rect = new Rect(gui.x - r, gui.y - r, r * 2f, r * 2f);
        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), Texture2D.whiteTexture);
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.black;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 9 };
        GUI.Label(rect, label, style);
        GUI.color = old;
    }

    // ── space conversions ──

    private Vector3 ContentLocalToWorld(Vector2 contentLocal)
    {
        RectTransform root = buildingSpawner.ContentRoot;
        Vector2 pivotCorrection = (new Vector2(0.5f, 0.5f) - root.pivot) * root.rect.size;
        Vector2 local = contentLocal + pivotCorrection;
        return root.TransformPoint(new Vector3(local.x, local.y, 0f));
    }

    private Vector2 WorldToGui(Vector3 world)
    {
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(uiCamera, world);
        return new Vector2(screen.x, Screen.height - screen.y);
    }

    private Vector2 ClampToScreen(Vector2 gui)
    {
        return new Vector2(
            Mathf.Clamp(gui.x, screenEdgeMargin, Screen.width - screenEdgeMargin),
            Mathf.Clamp(gui.y, screenEdgeMargin, Screen.height - screenEdgeMargin));
    }
}
