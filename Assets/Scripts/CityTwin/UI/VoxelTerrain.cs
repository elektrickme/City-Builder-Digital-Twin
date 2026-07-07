using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.UI
{
    /// <summary>
    /// Voxelized terrain driven by the map PNG: every grid cell becomes an instanced cube
    /// whose height comes from pixel luminance and whose color comes from the pixel itself.
    /// Rendered with Graphics.RenderMeshInstanced so it works under the URP 2D renderer
    /// (no VFX Graph / no extra camera). Transparent pixels (outside the fan) spawn nothing.
    /// </summary>
    [ExecuteAlways]
    public class VoxelTerrain : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Map image that drives the terrain. Must be Read/Write enabled.")]
        [SerializeField] private Texture2D heightMap;
        [Tooltip("Pixels with alpha below this are skipped (transparent border around the fan).")]
        [Range(0f, 1f)]
        [SerializeField] private float alphaCutoff = 0.35f;

        [Header("Grid")]
        [Tooltip("Voxel columns across the map width. Height count follows the image aspect.")]
        [Range(16, 320)]
        [SerializeField] private int resolutionX = 160;

        [Header("Shape (local units, scale with transform)")]
        [Tooltip("Terrain footprint width; depth follows the image aspect.")]
        [SerializeField] private float worldWidth = 20f;
        [Tooltip("Max voxel height at full luminance.")]
        [SerializeField] private float elevation = 1.5f;
        [Tooltip("Minimum voxel height so even dark areas get a thin base slab.")]
        [SerializeField] private float baseHeight = 0.05f;
        [Tooltip("0 = voxels touch, 0.5 = half-size gaps.")]
        [Range(0f, 0.9f)]
        [SerializeField] private float gap = 0.08f;

        [Header("Color")]
        [Tooltip("Extra brightness multiplier applied to the sampled pixel color.")]
        [SerializeField] private float colorBoost = 1.25f;
        [Tooltip("Blend sampled color toward this tint by height (0 = never, 1 = peaks fully tinted).")]
        [SerializeField] private Color peakTint = new Color(0.3f, 0.9f, 1f, 1f);
        [Range(0f, 1f)]
        [SerializeField] private float peakTintStrength = 0.35f;

        [SerializeField] private Material material;

        private const int BatchSize = 1023;
        private static readonly int VoxelColorId = Shader.PropertyToID("_VoxelColor");

        private Mesh _cube;
        private readonly List<Matrix4x4[]> _batches = new List<Matrix4x4[]>();
        private readonly List<Vector4[]> _batchColors = new List<Vector4[]>();
        private readonly List<int> _batchCounts = new List<int>();
        private MaterialPropertyBlock _mpb;
        private bool _dirty = true;
        private Matrix4x4 _builtLocalToWorld;

        public Texture2D HeightMap
        {
            get => heightMap;
            set { heightMap = value; _dirty = true; }
        }

        public float Elevation
        {
            get => elevation;
            set { elevation = value; _dirty = true; }
        }

        private void OnEnable()
        {
            _dirty = true;
            if (_cube == null)
            {
                var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cube = temp.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(temp);
            }
        }

        private void OnValidate()
        {
            _dirty = true;
        }

        private void Update()
        {
            if (heightMap == null || material == null || _cube == null) return;

            if (_dirty || transform.localToWorldMatrix != _builtLocalToWorld)
                Rebuild();

            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            var rp = new RenderParams(material)
            {
                worldBounds = new Bounds(transform.position, Vector3.one * (worldWidth * 2f + elevation * 2f)),
                shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
                receiveShadows = false
            };

            for (int i = 0; i < _batches.Count; i++)
            {
                _mpb.Clear();
                _mpb.SetVectorArray(VoxelColorId, _batchColors[i]);
                rp.matProps = _mpb;
                Graphics.RenderMeshInstanced(rp, _cube, 0, _batches[i], _batchCounts[i]);
            }
        }

        private void Rebuild()
        {
            _dirty = false;
            _builtLocalToWorld = transform.localToWorldMatrix;
            _batches.Clear();
            _batchColors.Clear();
            _batchCounts.Clear();

            if (!heightMap.isReadable)
            {
                Debug.LogWarning($"[VoxelTerrain] '{heightMap.name}' is not Read/Write enabled — no terrain.", this);
                return;
            }

            float aspect = (float)heightMap.height / heightMap.width;
            int resX = Mathf.Max(2, resolutionX);
            int resZ = Mathf.Max(2, Mathf.RoundToInt(resX * aspect));

            float cellW = worldWidth / resX;
            float depth = worldWidth * aspect;
            float cellD = depth / resZ;
            float voxelW = cellW * (1f - gap);
            float voxelD = cellD * (1f - gap);
            Vector3 origin = new Vector3(-worldWidth * 0.5f + cellW * 0.5f, 0f, -depth * 0.5f + cellD * 0.5f);

            var matrices = new Matrix4x4[BatchSize];
            var colors = new Vector4[BatchSize];
            int n = 0;

            for (int z = 0; z < resZ; z++)
            {
                float v = (z + 0.5f) / resZ;
                for (int x = 0; x < resX; x++)
                {
                    float u = (x + 0.5f) / resX;
                    Color px = heightMap.GetPixelBilinear(u, v);
                    if (px.a < alphaCutoff) continue;

                    float lum = px.maxColorComponent;
                    float h = baseHeight + lum * elevation;

                    Vector3 pos = origin + new Vector3(x * cellW, h * 0.5f, z * cellD);
                    matrices[n] = _builtLocalToWorld * Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(voxelW, h, voxelD));

                    Color c = px * colorBoost;
                    c = Color.Lerp(c, peakTint, lum * peakTintStrength);
                    c.a = 1f;
                    colors[n] = c;
                    n++;

                    if (n == BatchSize)
                    {
                        _batches.Add(matrices);
                        _batchColors.Add(colors);
                        _batchCounts.Add(n);
                        matrices = new Matrix4x4[BatchSize];
                        colors = new Vector4[BatchSize];
                        n = 0;
                    }
                }
            }

            if (n > 0)
            {
                _batches.Add(matrices);
                _batchColors.Add(colors);
                _batchCounts.Add(n);
            }
        }
    }
}
