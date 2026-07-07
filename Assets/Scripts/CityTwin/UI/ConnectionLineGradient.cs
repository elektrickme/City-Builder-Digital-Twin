using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>
    /// Mesh effect that re-tessellates a connection line's quad into segments and
    /// multiplies a gradient along its length. Add next to the Image on a connection
    /// prefab root. The gradient multiplies the base color HubConnectionRenderer sets,
    /// so per-connection tints (close/far/hub) are preserved.
    /// Default: bright at both ends where the line plugs into hubs, softer mid-span.
    /// </summary>
    [RequireComponent(typeof(Graphic))]
    public class ConnectionLineGradient : BaseMeshEffect
    {
        [Tooltip("Multiplied over the line's length (t=0 at 'from', t=1 at 'to').")]
        [SerializeField] private Gradient gradient = DefaultGradient();
        [Tooltip("Quad subdivisions along the length so mid-gradient keys show.")]
        [Range(2, 64)]
        [SerializeField] private int segments = 24;

        private static Gradient DefaultGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.45f, 0.5f),
                    new GradientAlphaKey(1f, 1f)
                });
            return g;
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh.currentVertCount == 0) return;

            // Capture bounds + base color from the source quad.
            UIVertex v = default;
            float xMin = float.MaxValue, xMax = float.MinValue;
            float yMin = float.MaxValue, yMax = float.MinValue;
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                if (v.position.x < xMin) xMin = v.position.x;
                if (v.position.x > xMax) xMax = v.position.x;
                if (v.position.y < yMin) yMin = v.position.y;
                if (v.position.y > yMax) yMax = v.position.y;
            }
            Color baseColor = v.color;
            if (xMax - xMin < 0.01f) return;

            vh.Clear();
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float x = Mathf.Lerp(xMin, xMax, t);
                Color32 c = baseColor * gradient.Evaluate(t);

                vh.AddVert(new Vector3(x, yMax, 0f), c, new Vector2(t, 1f));
                vh.AddVert(new Vector3(x, yMin, 0f), c, new Vector2(t, 0f));

                if (i > 0)
                {
                    int b = i * 2;
                    vh.AddTriangle(b - 2, b - 1, b + 1);
                    vh.AddTriangle(b - 2, b + 1, b);
                }
            }
        }
    }
}
