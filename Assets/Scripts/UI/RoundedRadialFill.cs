using UnityEngine;
using UnityEngine.UI;

[ExecuteInEditMode]
public class RoundedRadialFill : MonoBehaviour, IGlowBoost
{
    [SerializeField] private RawImage ringImage;
    [Range(0f, 1f)] public float fill = 0.75f;
    [Range(0.5f, 3f)] public float endCapScale = 1.5f;
    [Tooltip("HDR color multiplier. > 1 pushes the ring over 1.0 so the bloom post pass halos it.")]
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
        if (ringImage != null)
        {
            _matInstance = new Material(ringImage.material);
            ringImage.material = _matInstance;
            ApplyProperties();
        }
    }

    void OnValidate()
    {
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
        _matInstance.SetFloat("_EndCapScale", endCapScale);
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
