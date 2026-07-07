using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>
    /// Turns the map background into an interactive liquid surface. Runs a tiny GPU
    /// height-field ripple simulation (one Blit/frame into a small ping-pong RT) and
    /// feeds the result to a custom UI shader on the map Image, which refracts the
    /// sprite along the wave slope and adds a specular glint.
    ///
    /// Scene objects (building markers) disturb the surface continuously: their
    /// position is splatted into the sim every frame, so a stationary building emits
    /// a gentle idle ripple and a moving one leaves a wake. Deliberately light so it
    /// can run on all four game instances at once.
    ///
    /// Put this on the Game Instance root (next to BuildingSpawner). It auto-finds the
    /// "Main BG" Image and the sibling BuildingSpawner if they are not assigned.
    /// Parameters live in <see cref="LiquidTuning"/> and are normally driven centrally
    /// by a <see cref="LiquidSurfaceControl"/> on a Simulation Control object.
    /// </summary>
    [DisallowMultipleComponent]
    public class LiquidSurface : MonoBehaviour
    {
        private const int MaxSplats = 32; // must match MAX_SPLATS in LiquidWaveUpdate.shader

        [Header("References")]
        [Tooltip("The map Image to distort. Auto-found by name if left null.")]
        [SerializeField] private Image mapImage;
        [SerializeField] private string mapImageName = "Main BG";
        [Tooltip("Source of building markers that disturb the surface. Auto-resolved from this object if null.")]
        [SerializeField] private BuildingSpawner buildingSpawner;

        [Tooltip("Local fallback parameters. Overridden at runtime by a LiquidSurfaceControl if one drives this surface.")]
        [SerializeField] private LiquidTuning tuning = new LiquidTuning();

        private RectTransform _mapRect;
        private RenderTexture _rtA, _rtB;
        private Material _simMat, _displayMat;
        private Material _originalMaterial;
        private int _simW, _simH;
        private int _appliedResolution;
        private float _simAccumulator;
        private const int MaxStepsPerFrame = 4;

        private struct Interactor { public GameObject go; public Vector2 lastUv; public bool hasLast; public float nextBlip, blipEnd, blipDur; }
        private readonly List<Interactor> _interactors = new List<Interactor>();
        private readonly Vector4[] _splats = new Vector4[MaxSplats];

        // Shader property ids
        private static readonly int IdSplatData = Shader.PropertyToID("_SplatData");
        private static readonly int IdSplatCount = Shader.PropertyToID("_SplatCount");
        private static readonly int IdTexelSize = Shader.PropertyToID("_TexelSize");
        private static readonly int IdWaveSpeed = Shader.PropertyToID("_WaveSpeed");
        private static readonly int IdDamping = Shader.PropertyToID("_Damping");
        private static readonly int IdAspectHW = Shader.PropertyToID("_AspectHW");
        private static readonly int IdHeightTex = Shader.PropertyToID("_HeightTex");
        private static readonly int IdHeightTexel = Shader.PropertyToID("_HeightTexel");
        private static readonly int IdDisplace = Shader.PropertyToID("_DisplaceStrength");
        private static readonly int IdNormalStr = Shader.PropertyToID("_NormalStrength");
        private static readonly int IdGloss = Shader.PropertyToID("_Gloss");
        private static readonly int IdSpecStr = Shader.PropertyToID("_SpecStrength");
        private static readonly int IdSpecTint = Shader.PropertyToID("_SpecTint");
        private static readonly int IdLightDir = Shader.PropertyToID("_LightDir");

        /// <summary>Parameters currently in use by this surface.</summary>
        public LiquidTuning Tuning => tuning;

        private void Awake()
        {
            ResolveRefs();
        }

        private void OnEnable()
        {
            if (buildingSpawner != null) buildingSpawner.OnTileSpawned += HandleTileSpawned;
        }

        private void OnDisable()
        {
            if (buildingSpawner != null) buildingSpawner.OnTileSpawned -= HandleTileSpawned;
        }

        private void Start()
        {
            if (!Setup())
                enabled = false;
        }

        private void ResolveRefs()
        {
            if (buildingSpawner == null)
                buildingSpawner = GetComponentInParent<BuildingSpawner>() ?? GetComponentInChildren<BuildingSpawner>(true);

            if (mapImage == null)
            {
                var images = GetComponentsInChildren<Image>(true);
                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i].name == mapImageName) { mapImage = images[i]; break; }
                }
            }
        }

        private bool Setup()
        {
            if (mapImage == null)
            {
                Debug.LogError($"[LiquidSurface] No map Image named '{mapImageName}' found — disabling.", this);
                return false;
            }

            _mapRect = mapImage.rectTransform;

            var simShader = Shader.Find("Hidden/CityTwin/LiquidWaveUpdate");
            var uiShader = Shader.Find("CityTwin/UI/LiquidMapDistort");
            if (simShader == null || uiShader == null)
            {
                Debug.LogError("[LiquidSurface] Liquid shaders not found — did they compile?", this);
                return false;
            }

            _simMat = new Material(simShader) { hideFlags = HideFlags.HideAndDontSave };
            _displayMat = new Material(uiShader) { hideFlags = HideFlags.HideAndDontSave };

            RebuildTargets();
            PushLookParams();

            _originalMaterial = mapImage.material;
            mapImage.material = _displayMat;
            return true;
        }

        // (Re)create the ping-pong sim targets at the resolution in the current tuning.
        private void RebuildTargets()
        {
            float w = Mathf.Max(1f, _mapRect.rect.width);
            float h = Mathf.Max(1f, _mapRect.rect.height);
            int res = Mathf.Max(8, tuning.resolution);
            if (w >= h) { _simW = res; _simH = Mathf.Max(8, Mathf.RoundToInt(res * h / w)); }
            else { _simH = res; _simW = Mathf.Max(8, Mathf.RoundToInt(res * w / h)); }

            if (_rtA != null) _rtA.Release();
            if (_rtB != null) _rtB.Release();
            _rtA = CreateRT();
            _rtB = CreateRT();
            Graphics.Blit(Texture2D.blackTexture, _rtA);
            Graphics.Blit(Texture2D.blackTexture, _rtB);

            if (_displayMat != null)
                _displayMat.SetVector(IdHeightTexel, new Vector4(1f / _simW, 1f / _simH, 0, 0));

            _appliedResolution = tuning.resolution;
            _simAccumulator = 0f;
        }

        private RenderTexture CreateRT()
        {
            var rt = new RenderTexture(_simW, _simH, 0, RenderTextureFormat.RGHalf)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "LiquidSurfaceState"
            };
            rt.Create();
            return rt;
        }

        private void PushLookParams()
        {
            if (_displayMat == null) return;
            _displayMat.SetFloat(IdDisplace, tuning.displaceStrength);
            _displayMat.SetFloat(IdNormalStr, tuning.normalStrength);
            _displayMat.SetFloat(IdGloss, tuning.gloss);
            _displayMat.SetFloat(IdSpecStr, tuning.specStrength);
            _displayMat.SetColor(IdSpecTint, tuning.specTint);
            _displayMat.SetVector(IdLightDir, new Vector4(tuning.lightDir.x, tuning.lightDir.y, 0, 0));
        }

        /// <summary>Drive this surface from a shared tuning object (central control). Applies live.</summary>
        public void ApplyTuning(LiquidTuning t)
        {
            if (t == null) return;
            tuning = t;
            if (_displayMat == null) return; // not set up yet; Setup will read it
            PushLookParams();
            if (_appliedResolution != tuning.resolution)
                RebuildTargets();
        }

        private void HandleTileSpawned(string engineTileId, string buildingId, GameObject marker)
        {
            if (marker != null)
                _interactors.Add(NewInteractor(marker));
        }

        /// <summary>Manually register any transform (e.g. a hub) as a surface disturber.</summary>
        public void AddInteractor(GameObject go)
        {
            if (go != null) _interactors.Add(NewInteractor(go));
        }

        /// <summary>Static interactors don't hum constantly — each one emits a short ripple "blip"
        /// at its own random moments (one here, one there), so the idle surface feels alive without
        /// a metronome. Movement still ripples continuously via the wake term.</summary>
        private static Interactor NewInteractor(GameObject go) => new Interactor
        {
            go = go,
            hasLast = false,
            nextBlip = Time.time + Random.Range(1f, 9f) // stagger the very first blips
        };

        // One-off decaying ripple bursts (e.g. the first tile landing on the table).
        private struct SplashBurst { public Vector3 worldPos; public float radiusMul, strength, endTime, duration; }
        private readonly List<SplashBurst> _bursts = new List<SplashBurst>();

        /// <summary>Emit a big ripple at a world position that decays over <paramref name="duration"/> seconds.
        /// Radius is relative to the tuned splat radius, so it scales with the surface's look.</summary>
        public void Splash(Vector3 worldPos, float radiusMultiplier = 3f, float strength = 2.5f, float duration = 1.5f)
        {
            _bursts.Add(new SplashBurst
            {
                worldPos = worldPos,
                radiusMul = Mathf.Max(0.1f, radiusMultiplier),
                strength = strength,
                duration = Mathf.Max(0.05f, duration),
                endTime = Time.time + Mathf.Max(0.05f, duration)
            });
        }

        private void LateUpdate()
        {
            if (_simMat == null || _rtA == null) return;

            // Fixed-timestep: run a stable number of sim steps regardless of framerate,
            // so wave speed looks the same at 60 or 144 fps. Catches up to MaxStepsPerFrame.
            float step = 1f / Mathf.Max(1f, tuning.simHz);
            _simAccumulator += Time.deltaTime;
            int steps = Mathf.FloorToInt(_simAccumulator / step);
            if (steps <= 0) { _displayMat.SetTexture(IdHeightTex, _rtA); return; }
            if (steps > MaxStepsPerFrame) { steps = MaxStepsPerFrame; _simAccumulator = 0f; }
            else _simAccumulator -= steps * step;

            int count = BuildSplats();

            _simMat.SetVectorArray(IdSplatData, _splats);
            _simMat.SetVector(IdTexelSize, new Vector4(1f / _simW, 1f / _simH, 0, 0));
            _simMat.SetFloat(IdWaveSpeed, tuning.waveSpeed);
            _simMat.SetFloat(IdDamping, tuning.damping);
            _simMat.SetFloat(IdAspectHW, (float)_simH / _simW);

            // Inject disturbances on the first step only (per-frame energy); the remaining
            // steps just propagate, so extra catch-up steps don't over-inject.
            _simMat.SetInt(IdSplatCount, count);
            Graphics.Blit(_rtA, _rtB, _simMat);
            (_rtA, _rtB) = (_rtB, _rtA);

            if (steps > 1)
            {
                _simMat.SetInt(IdSplatCount, 0);
                for (int i = 1; i < steps; i++)
                {
                    Graphics.Blit(_rtA, _rtB, _simMat);
                    (_rtA, _rtB) = (_rtB, _rtA);
                }
            }

            _displayMat.SetTexture(IdHeightTex, _rtA); // _rtA holds the newest state
        }

        private int BuildSplats()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            int n = 0;

            for (int i = _interactors.Count - 1; i >= 0; i--)
            {
                var it = _interactors[i];
                if (it.go == null) { _interactors.RemoveAt(i); continue; }

                Vector2 uv = WorldToMapUv(it.go.transform.position);

                // ignore anything well outside the map
                if (uv.x < -0.1f || uv.x > 1.1f || uv.y < -0.1f || uv.y > 1.1f)
                {
                    it.hasLast = false;
                    _interactors[i] = it;
                    continue;
                }

                float speed = it.hasLast ? (uv - it.lastUv).magnitude / dt : 0f;
                it.lastUv = uv;
                it.hasLast = true;

                // Sporadic idle: silent most of the time, then a single soft ripple blip, then
                // silence again. Each interactor rolls its own dice, so blips wander around the
                // map one by one instead of everything pulsing on a shared clock.
                float idleFactor = 0f;
                if (Time.time < it.blipEnd)
                {
                    float prog = 1f - (it.blipEnd - Time.time) / Mathf.Max(0.01f, it.blipDur);
                    idleFactor = Mathf.Sin(prog * Mathf.PI) * 2.2f; // soft in-out, boosted to read as a distinct pulse
                }
                else if (Time.time >= it.nextBlip)
                {
                    it.blipDur = Random.Range(0.35f, 0.7f);
                    it.blipEnd = Time.time + it.blipDur;
                    it.nextBlip = it.blipEnd + Random.Range(5f, 14f);
                }
                _interactors[i] = it;

                if (n < MaxSplats)
                {
                    float wake = tuning.moveStrength * Mathf.Clamp01(speed / Mathf.Max(1e-4f, tuning.moveSpeedRef));
                    float strength = (tuning.idleStrength * idleFactor + wake) * dt * 60f; // frame-rate independent
                    _splats[n++] = new Vector4(uv.x, uv.y, tuning.splatRadius, strength);
                }
            }

            // Splash bursts: strong at impact, easing out quadratically over their lifetime.
            for (int i = _bursts.Count - 1; i >= 0; i--)
            {
                var b = _bursts[i];
                float remaining = (b.endTime - Time.time) / b.duration;
                if (remaining <= 0f) { _bursts.RemoveAt(i); continue; }
                if (n >= MaxSplats) break;
                Vector2 buv = WorldToMapUv(b.worldPos);
                float bStrength = b.strength * remaining * remaining * dt * 60f;
                _splats[n++] = new Vector4(buv.x, buv.y, tuning.splatRadius * b.radiusMul, bStrength);
            }

            return n;
        }

        private Vector2 WorldToMapUv(Vector3 worldPos)
        {
            Vector3 local = _mapRect.InverseTransformPoint(worldPos);
            Rect r = _mapRect.rect;
            return new Vector2((local.x - r.xMin) / r.width, (local.y - r.yMin) / r.height);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying) ApplyTuning(tuning);
        }
#endif

        private void OnDestroy()
        {
            if (mapImage != null && mapImage.material == _displayMat)
                mapImage.material = _originalMaterial;
            if (_rtA != null) _rtA.Release();
            if (_rtB != null) _rtB.Release();
            if (_simMat != null) Destroy(_simMat);
            if (_displayMat != null) Destroy(_displayMat);
        }
    }
}
