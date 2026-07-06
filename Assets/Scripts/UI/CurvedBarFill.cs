using UnityEngine;
using UnityEngine.UI;

[ExecuteInEditMode]
public class CurvedBarFill : MonoBehaviour, IGlowBoost
{
    [SerializeField] private RawImage barImage;
    [Range(0f, 1f)] public float fill = 0.7f;
    [Range(0f, 0.6f)] public float capSize = 0.05f;
    public float aspect = 5.0f;
    [Tooltip("HDR color multiplier. > 1 pushes the bar over 1.0 so the bloom post pass halos it.")]
    [Range(1f, 6f)] public float glowBoost = 2.4f;

    private Material _matInstance;

    // Runtime glow actually written to the material: pulses (tutorial highlights) tween this and
    // it snaps back to the serialized base when they finish.
    private float _runtimeGlow = -1f;

    public float BaseGlowBoost => glowBoost;
    public float GlowBoost
    {
        get => _runtimeGlow > 0f ? _runtimeGlow : glowBoost;
        set => _runtimeGlow = value;
    }

    void OnEnable()
    {
        if (barImage != null)
        {
            _matInstance = new Material(barImage.material);
            barImage.material = _matInstance;
            ApplyProperties();
        }
    }

    void OnValidate()
    {
        // Auto-calculate aspect from texture if available
        if (barImage != null && barImage.texture != null)
        {
            aspect = (float)barImage.texture.width / barImage.texture.height;
        }
        ApplyProperties();
    }

    void Update()
    {
        ApplyProperties();
    }

    private void ApplyProperties()
    {
        if (_matInstance == null) return;
        _matInstance.SetFloat("_Fill", fill);
        _matInstance.SetFloat("_CapSize", capSize);
        _matInstance.SetFloat("_Aspect", aspect);
        _matInstance.SetFloat("_GlowBoost", GlowBoost);
    }

    void OnDestroy()
    {
        if (_matInstance != null)
        {
            if (Application.isPlaying)
                Destroy(_matInstance);
            else
                DestroyImmediate(_matInstance);
        }
    }
}
