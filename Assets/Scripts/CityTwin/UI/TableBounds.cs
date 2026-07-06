using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>
    /// "Is this point on the table?" test against the fan-shaped map background art.
    /// A point counts as inside when the background sprite's alpha at that point exceeds
    /// the threshold, so the playable bounds always match the visible blue fan exactly —
    /// no hand-tuned polygons, and it adapts if the art changes.
    ///
    /// Add to a game instance root. The background Image is auto-found by name in children
    /// if not assigned. Requires the sprite texture to be Read/Write enabled (set on
    /// Section 3.png's import settings); if unreadable, falls back to the rectangular rect.
    /// </summary>
    public class TableBounds : MonoBehaviour
    {
        [Tooltip("Fan-shaped background image whose alpha defines the playable table area. Auto-found by name if left null.")]
        [SerializeField] private Image boundsImage;
        [SerializeField] private string boundsImageName = "Main BG";
        [Tooltip("Sprite alpha at or above this counts as inside the table.")]
        [Range(0f, 1f)]
        [SerializeField] private float alphaThreshold = 0.35f;
        [Tooltip("Pixels darker than this (max RGB channel) count as OUTSIDE even when opaque — culls the black shadow band some fan art has around the visible map.")]
        [Range(0f, 1f)]
        [SerializeField] private float minBrightness = 0.06f;

        [Header("Road block mask (optional)")]
        [Tooltip("Alpha cutout aligned 1:1 with the bounds image rect: opaque pixels = road extensions may not enter (dashboard, assistant, badges...). Author it in any image editor over a screenshot of the fan. Must be Read/Write enabled.")]
        [SerializeField] private Texture2D roadBlockMask;
        [Tooltip("Mask alpha at or above this blocks roads.")]
        [Range(0f, 1f)]
        [SerializeField] private float blockMaskThreshold = 0.5f;

        private bool _warnedUnreadable;
        private bool _warnedMaskUnreadable;

        /// <summary>True when a paintable road-block mask is assigned (rect blockers become optional extras).</summary>
        public bool HasRoadBlockMask => roadBlockMask != null;

        private void Awake()
        {
            ResolveImage();
        }

        private void ResolveImage()
        {
            if (boundsImage != null) return;
            var images = GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i].name == boundsImageName) { boundsImage = images[i]; return; }
            }
        }

        /// <summary>True when the world-space point lies on the visible (non-transparent) table art.</summary>
        public bool ContainsWorld(Vector3 worldPos)
        {
            ResolveImage();
            if (boundsImage == null || boundsImage.sprite == null)
                return true; // no mask available — never block placements

            RectTransform rt = boundsImage.rectTransform;
            Vector3 local = rt.InverseTransformPoint(worldPos);
            Rect rect = rt.rect;
            if (!rect.Contains(local))
                return false;

            Sprite sprite = boundsImage.sprite;
            Texture2D tex = sprite.texture;
            if (tex == null)
                return true;
            if (!tex.isReadable)
            {
                if (!_warnedUnreadable)
                {
                    _warnedUnreadable = true;
                    Debug.LogWarning($"[TableBounds] '{tex.name}' is not Read/Write enabled — falling back to rectangular bounds. Enable Read/Write in its import settings for exact fan-shaped bounds.", this);
                }
                return true;
            }

            Rect tr = sprite.textureRect;
            float u = (tr.x + (local.x - rect.xMin) / rect.width * tr.width) / tex.width;
            float v = (tr.y + (local.y - rect.yMin) / rect.height * tr.height) / tex.height;
            Color c = tex.GetPixelBilinear(u, v);
            return c.a >= alphaThreshold && c.maxColorComponent >= minBrightness;
        }

        /// <summary>
        /// True when the world-space point falls on an opaque area of the road-block mask.
        /// The mask shares the bounds image's rect, so it lines up with the fan art pixel-for-pixel.
        /// </summary>
        public bool IsRoadBlocked(Vector3 worldPos)
        {
            if (roadBlockMask == null) return false;
            ResolveImage();
            if (boundsImage == null) return false;

            RectTransform rt = boundsImage.rectTransform;
            Vector3 local = rt.InverseTransformPoint(worldPos);
            Rect rect = rt.rect;
            if (!rect.Contains(local)) return false;

            if (!roadBlockMask.isReadable)
            {
                if (!_warnedMaskUnreadable)
                {
                    _warnedMaskUnreadable = true;
                    Debug.LogWarning($"[TableBounds] Road block mask '{roadBlockMask.name}' is not Read/Write enabled — ignoring it.", this);
                }
                return false;
            }

            float u = (local.x - rect.xMin) / rect.width;
            float v = (local.y - rect.yMin) / rect.height;
            return roadBlockMask.GetPixelBilinear(u, v).a >= blockMaskThreshold;
        }

        /// <summary>Screen-point convenience overload.</summary>
        public bool ContainsScreenPoint(Vector2 screenPos, Camera uiCamera)
        {
            ResolveImage();
            if (boundsImage == null) return true;
            if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(
                    boundsImage.rectTransform, screenPos, uiCamera, out Vector3 world))
                return false;
            return ContainsWorld(world);
        }
    }
}
