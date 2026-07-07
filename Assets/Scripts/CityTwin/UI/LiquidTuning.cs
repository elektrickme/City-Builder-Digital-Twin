using UnityEngine;

namespace CityTwin.UI
{
    /// <summary>
    /// All tunable parameters for the liquid map surface, in one place so they can be
    /// driven centrally from a Simulation Control object (see LiquidSurfaceControl) and
    /// shared by every game instance. Edit these live in the inspector while playing.
    /// </summary>
    [System.Serializable]
    public class LiquidTuning
    {
        [Header("Simulation")]
        [Tooltip("Sim resolution on the longer axis. Short axis derived from map aspect. Higher = finer ripples, still cheap.")]
        [Range(64, 1024)] public int resolution = 512;
        [Tooltip("Wave propagation speed (c^2 term). Lower = slower, calmer waves. Keep below 0.5 or the sim goes unstable.")]
        [Range(0.05f, 0.49f)] public float waveSpeed = 0.14f;
        [Tooltip("Energy retained per step. Lower = ripples fade faster.")]
        [Range(0.90f, 0.999f)] public float damping = 0.992f;
        [Tooltip("Fixed simulation rate (Hz). Decouples wave speed from framerate so it looks identical at 60 or 144 fps.")]
        [Range(15f, 120f)] public float simHz = 60f;

        [Header("Object disturbance")]
        [Tooltip("Splat radius in UV units.")]
        [Range(0.005f, 0.15f)] public float splatRadius = 0.04f;
        [Tooltip("Constant ripple every object emits while sitting still.")]
        public float idleStrength = 0.010f;
        [Tooltip("Extra ripple scaled by how fast the object is moving (wake).")]
        public float moveStrength = 0.060f;
        [Tooltip("UV/second speed that counts as 'full' movement for the wake term.")]
        public float moveSpeedRef = 0.6f;

        [Header("Look")]
        [Tooltip("How far the map refracts along the wave slope, in UV units.")]
        [Range(0f, 0.15f)] public float displaceStrength = 0.03f;
        public float normalStrength = 2.5f;
        public float gloss = 40f;
        [Range(0f, 2f)] public float specStrength = 0.5f;
        public Color specTint = Color.white;
        public Vector2 lightDir = new Vector2(0.5f, 0.6f);
    }
}
