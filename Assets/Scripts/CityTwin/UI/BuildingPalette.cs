using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using CityTwin.Config;
using CityTwin.Core;

namespace CityTwin.UI
{
    /// <summary>
    /// Play-build tray of building types shown in the black space above the table fan.
    /// Users drag an item from the tray onto the map to place that building; releasing
    /// outside the blue fan bounds (see <see cref="TableBounds"/>) discards the drop.
    ///
    /// Placement goes through GameInstanceCoordinator.TryProcessTileUpdate — the exact
    /// path physical TUIO tiles use — so budget, overlap validation, and scoring all
    /// behave identically. Placed buildings are registered with MouseBuildingTester so
    /// they can then be dragged around (and discarded off-table) with the mouse.
    ///
    /// Items build themselves from the loaded game config; leave buildingIds empty to
    /// show the whole catalog. All references auto-resolve from the owning game instance.
    /// </summary>
    public class BuildingPalette : MonoBehaviour
    {
        [Header("References (auto-found in game instance if null)")]
        [SerializeField] private GameInstanceCoordinator coordinator;
        [SerializeField] private BuildingSpawner buildingSpawner;
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private TableBounds tableBounds;
        [SerializeField] private MouseBuildingTester mouseTester;

        [Header("Content")]
        [Tooltip("Building ids to show, in order. Empty = every building in the loaded config.")]
        [SerializeField] private string[] buildingIds;
        [Tooltip("Ids hidden from the tray when showing the full catalog. Museum is hidden by default because it currently reuses the circus art (no museum sprite exists yet).")]
        [SerializeField] private string[] excludedBuildingIds = { "museum" };

        [Header("Layout — two corner clusters")]
        [Tooltip("Center of the left tile cluster in palette local space (palette sits at canvas center).")]
        [SerializeField] private Vector2 leftClusterCenter = new Vector2(-1055f, 545f);
        [SerializeField] private Vector2 rightClusterCenter = new Vector2(1055f, 545f);
        [Tooltip("Tiles per row within a cluster.")]
        [SerializeField] private int clusterColumns = 3;
        [SerializeField] private float columnSpacing = 104f;
        [SerializeField] private float rowSpacing = 102f;
        [SerializeField] private float itemSize = 78f;
        [SerializeField] private float iconInset = 10f;
        [SerializeField] private Color itemBackground = new Color(0f, 0.09f, 0.18f, 0.6f);
        [SerializeField] private Color priceColor = new Color(0.45f, 0.85f, 1f, 0.95f);
        [SerializeField] private float priceFontSize = 15f;
        [Tooltip("How far below each tile the price label hangs, in px.")]
        [SerializeField] private float priceLabelDrop = 6f;
        [SerializeField] private float ghostAlpha = 0.7f;

        private readonly List<GameObject> _items = new List<GameObject>();
        private bool _built;
        private int _nextTileId;
        private Canvas _canvas;
        private RectTransform _ghost;

        private void Awake()
        {
            if (coordinator == null) coordinator = GetComponentInParent<GameInstanceCoordinator>(true);
            if (tableBounds == null) tableBounds = GetComponentInParent<TableBounds>(true);
            Transform searchRoot = coordinator != null ? coordinator.transform : transform.root;
            if (buildingSpawner == null) buildingSpawner = searchRoot.GetComponentInChildren<BuildingSpawner>(true);
            if (buildingSpawner == null && coordinator != null) buildingSpawner = coordinator.GetComponent<BuildingSpawner>();
            if (configLoader == null) configLoader = searchRoot.GetComponentInChildren<GameConfigLoader>(true);
            if (configLoader == null && coordinator != null) configLoader = coordinator.GetComponent<GameConfigLoader>();
            if (mouseTester == null) mouseTester = searchRoot.GetComponentInChildren<MouseBuildingTester>(true);
            _canvas = GetComponentInParent<Canvas>();
        }

        private void Update()
        {
            if (!_built)
            {
                var buildings = configLoader != null && configLoader.Config != null ? configLoader.Config.Buildings : null;
                if (buildings != null && buildings.Length > 0)
                    Build(buildings);
            }
        }

        private void Build(BuildingDefinition[] catalog)
        {
            _built = true;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i] != null) Destroy(_items[i]);
            _items.Clear();

            var defs = new List<BuildingDefinition>();
            if (buildingIds != null && buildingIds.Length > 0)
            {
                // Explicit list: show exactly what was asked for, no exclusions.
                for (int i = 0; i < buildingIds.Length; i++)
                {
                    var def = FindDef(catalog, buildingIds[i]);
                    if (def != null) defs.Add(def);
                }
            }
            else
            {
                for (int i = 0; i < catalog.Length; i++)
                {
                    if (catalog[i] == null || IsExcluded(catalog[i].Id)) continue;
                    defs.Add(catalog[i]);
                }
            }
            if (defs.Count == 0) return;

            // First half in the left corner cluster, second half in the right.
            int leftCount = (defs.Count + 1) / 2;
            for (int i = 0; i < defs.Count; i++)
            {
                bool onLeft = i < leftCount;
                int idx = onLeft ? i : i - leftCount;
                int total = onLeft ? leftCount : defs.Count - leftCount;
                Vector2 pos = ClusterPosition(onLeft ? leftClusterCenter : rightClusterCenter, idx, total);
                _items.Add(CreateItem(defs[i], pos));
            }
        }

        private bool IsExcluded(string id)
        {
            if (excludedBuildingIds == null) return false;
            for (int i = 0; i < excludedBuildingIds.Length; i++)
                if (excludedBuildingIds[i] == id) return true;
            return false;
        }

        /// <summary>Grid position inside a cluster: rows of clusterColumns, each row centered on the cluster.</summary>
        private Vector2 ClusterPosition(Vector2 center, int index, int total)
        {
            int cols = Mathf.Max(1, clusterColumns);
            int row = index / cols;
            int col = index % cols;
            int rowStart = row * cols;
            int rowCount = Mathf.Min(cols, total - rowStart);
            float x = center.x + (col - (rowCount - 1) * 0.5f) * columnSpacing;
            float y = center.y - row * rowSpacing;
            return new Vector2(x, y);
        }

        private static BuildingDefinition FindDef(BuildingDefinition[] catalog, string id)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i] != null && catalog[i].Id == id) return catalog[i];
            return null;
        }

        private GameObject CreateItem(BuildingDefinition def, Vector2 anchoredPos)
        {
            var go = new GameObject("Palette_" + def.Id, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(transform, false);
            rt.sizeDelta = new Vector2(itemSize, itemSize);
            rt.anchoredPosition = anchoredPos;

            var bg = go.AddComponent<Image>();
            bg.color = itemBackground;
            bg.raycastTarget = true; // drag surface

            // Icon
            var iconGo = new GameObject("Icon", typeof(RectTransform));
            var iconRt = (RectTransform)iconGo.transform;
            iconRt.SetParent(rt, false);
            iconRt.anchorMin = Vector2.zero;
            iconRt.anchorMax = Vector2.one;
            iconRt.offsetMin = new Vector2(iconInset, iconInset);
            iconRt.offsetMax = new Vector2(-iconInset, -iconInset);
            var icon = iconGo.AddComponent<Image>();
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            if (buildingSpawner != null && buildingSpawner.TryGetPreviewSprite(def.Id, out Sprite sprite) && sprite != null)
                icon.sprite = sprite;
            else
                icon.color = new Color(1f, 1f, 1f, 0.15f);

            // Price label sits just BELOW the tile so it never overlaps the icon.
            float priceH = priceFontSize + 6f;
            var priceBgGo = new GameObject("PriceBackdrop", typeof(RectTransform));
            var priceBgRt = (RectTransform)priceBgGo.transform;
            priceBgRt.SetParent(rt, false);
            priceBgRt.anchorMin = new Vector2(0f, 0f);
            priceBgRt.anchorMax = new Vector2(1f, 0f);
            priceBgRt.pivot = new Vector2(0.5f, 1f);          // top pivot: hangs downward
            priceBgRt.anchoredPosition = new Vector2(0f, -priceLabelDrop); // below the tile bottom
            priceBgRt.sizeDelta = new Vector2(0f, priceH);
            var priceBg = priceBgGo.AddComponent<Image>();
            priceBg.raycastTarget = false;
            priceBg.color = new Color(0f, 0f, 0f, 0.55f);

            var priceGo = new GameObject("Price", typeof(RectTransform));
            var priceRt = (RectTransform)priceGo.transform;
            priceRt.SetParent(priceBgRt, false);
            priceRt.anchorMin = Vector2.zero;
            priceRt.anchorMax = Vector2.one;
            priceRt.offsetMin = Vector2.zero;
            priceRt.offsetMax = Vector2.zero;
            var price = priceGo.AddComponent<TextMeshProUGUI>();
            price.raycastTarget = false;
            price.text = "$" + def.Price;
            price.fontSize = priceFontSize;
            price.color = priceColor;
            price.alignment = TextAlignmentOptions.Center;

            var item = go.AddComponent<PaletteItem>();
            item.Init(this, def);
            return go;
        }

        private Camera EventCamera(PointerEventData eventData)
        {
            if (eventData != null && eventData.pressEventCamera != null) return eventData.pressEventCamera;
            return _canvas != null ? _canvas.worldCamera : null;
        }

        // ── drag flow (called by PaletteItem) ──

        internal void BeginGhost(BuildingDefinition def, PointerEventData eventData)
        {
            EndGhostVisual();

            var go = new GameObject("PaletteGhost_" + def.Id, typeof(RectTransform));
            _ghost = (RectTransform)go.transform;
            _ghost.SetParent(transform, false);
            _ghost.sizeDelta = new Vector2(itemSize, itemSize);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.preserveAspect = true;
            if (buildingSpawner != null && buildingSpawner.TryGetPreviewSprite(def.Id, out Sprite sprite) && sprite != null)
                img.sprite = sprite;
            var c = img.color;
            c.a = ghostAlpha;
            img.color = c;

            MoveGhost(eventData);
        }

        internal void MoveGhost(PointerEventData eventData)
        {
            if (_ghost == null) return;
            var parent = (RectTransform)transform;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position, EventCamera(eventData), out Vector2 local))
                _ghost.anchoredPosition = local;

            // Tint red-ish while over a discard zone so the outcome is predictable.
            var img = _ghost.GetComponent<Image>();
            if (img != null && tableBounds != null)
            {
                bool inside = tableBounds.ContainsScreenPoint(eventData.position, EventCamera(eventData));
                img.color = inside
                    ? new Color(1f, 1f, 1f, ghostAlpha)
                    : new Color(1f, 0.45f, 0.45f, ghostAlpha * 0.8f);
            }
        }

        internal void EndGhost(BuildingDefinition def, PointerEventData eventData)
        {
            EndGhostVisual();
            if (coordinator == null || buildingSpawner == null || buildingSpawner.ContentRoot == null)
                return;

            Camera cam = EventCamera(eventData);

            // Dropped outside the blue fan → discard silently.
            if (tableBounds != null && !tableBounds.ContainsScreenPoint(eventData.position, cam))
                return;

            RectTransform root = buildingSpawner.ContentRoot;
            if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(root, eventData.position, cam, out Vector3 world))
                return;

            Vector2 local = buildingSpawner.WorldToContentLocal(world);
            Vector2 tuio = buildingSpawner.LocalToTuioPosition(local);
            string tileId = "palette_" + GetInstanceID() + "_" + _nextTileId++;
            var pose = new TilePose(tuio, 0f, def.Id, 0, tileId);

            if (!coordinator.TryProcessTileUpdate(pose, out string engineId) || string.IsNullOrEmpty(engineId))
                return; // rejected (budget, overlap, ...)

            // Hand the placed building to MouseBuildingTester so it can be dragged/discarded.
            if (mouseTester != null)
            {
                RectTransform marker = FindMarker(root, def.Id + "_" + engineId);
                mouseTester.RegisterExternalTile(tileId, engineId, marker, def.Id);
            }
        }

        private void EndGhostVisual()
        {
            if (_ghost != null)
            {
                Destroy(_ghost.gameObject);
                _ghost = null;
            }
        }

        private static RectTransform FindMarker(RectTransform root, string instanceName)
        {
            var direct = root.Find(instanceName) as RectTransform;
            if (direct != null) return direct;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == instanceName) return all[i] as RectTransform;
            return null;
        }

        /// <summary>Drag handler living on each tray item.</summary>
        private class PaletteItem : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            private BuildingPalette _palette;
            private BuildingDefinition _def;

            public void Init(BuildingPalette palette, BuildingDefinition def)
            {
                _palette = palette;
                _def = def;
            }

            public void OnBeginDrag(PointerEventData eventData) => _palette?.BeginGhost(_def, eventData);
            public void OnDrag(PointerEventData eventData) => _palette?.MoveGhost(eventData);
            public void OnEndDrag(PointerEventData eventData) => _palette?.EndGhost(_def, eventData);
        }
    }
}
