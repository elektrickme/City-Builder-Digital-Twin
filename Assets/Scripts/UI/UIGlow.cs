using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>Gives any UI Graphic an HDR glow the bloom post pass can pick up (values > 1 glow).
/// TMP text: instances the font material and multiplies _FaceColor above 1 (vertex colors clamp at 1).
/// Image/RawImage on a stock material: swaps in the CityTwin/UI/GlowBoost shader.
/// Graphics whose material already exposes _GlowBoost keep their shader; only the value is driven.
/// Implements IGlowBoost so DashboardController highlight pulses can sweep it.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Graphic))]
public class UIGlow : MonoBehaviour, IGlowBoost
{
    [Tooltip("HDR color multiplier. > 1 pushes the element over 1.0 so the bloom post pass halos it.")]
    [Range(1f, 6f)] public float glowBoost = 2f;

    private static Shader _glowShader;
    private static readonly int GlowBoostId = Shader.PropertyToID("_GlowBoost");
    private static readonly int FaceColorId = Shader.PropertyToID("_FaceColor");

    private TMP_Text _tmp;
    private Graphic _graphic;
    private Material _originalMat; // image material to restore on disable
    private Material _mat;         // our instanced material
    private Color _baseFace = Color.white;
    private bool _applied;

    // Runtime glow actually applied: pulses tween this and it snaps back to the serialized base.
    private float _runtimeGlow = -1f;

    public float BaseGlowBoost => glowBoost;
    public float GlowBoost
    {
        get => _runtimeGlow > 0f ? _runtimeGlow : glowBoost;
        set { _runtimeGlow = value; ApplyValue(); }
    }

    private void OnEnable()
    {
        Setup();
        ApplyValue();
    }

    private void Setup()
    {
        if (_applied) return;
        _graphic = GetComponent<Graphic>();
        _tmp = _graphic as TMP_Text;

        if (_tmp != null)
        {
            // fontMaterial is a per-text instance owned (and destroyed) by TMP.
            _mat = _tmp.fontMaterial;
            if (_mat == null || !_mat.HasProperty(FaceColorId)) { _mat = null; return; }
            _baseFace = _mat.GetColor(FaceColorId);
            _applied = true;
            return;
        }

        Material src = _graphic.material;
        if (src != null && src.HasProperty(GlowBoostId))
        {
            _originalMat = src;
            _mat = new Material(src);
        }
        else
        {
            if (_glowShader == null) _glowShader = Shader.Find("CityTwin/UI/GlowBoost");
            if (_glowShader == null) return;
            _originalMat = src;
            _mat = new Material(_glowShader);
        }
        _graphic.material = _mat;
        _applied = true;
    }

    private void ApplyValue()
    {
        if (!_applied || _mat == null) return;
        float boost = GlowBoost;
        if (_tmp != null)
        {
            _mat.SetColor(FaceColorId, new Color(_baseFace.r * boost, _baseFace.g * boost, _baseFace.b * boost, _baseFace.a));
        }
        else
        {
            _mat.SetFloat(GlowBoostId, boost);
        }
    }

    private void OnValidate()
    {
        if (_applied) ApplyValue();
    }

    private void OnDisable()
    {
        // Leave the graphic exactly as found so toggling the component is side-effect free.
        if (_tmp != null && _mat != null) _mat.SetColor(FaceColorId, _baseFace);
        else if (_graphic != null && _graphic.material == _mat) _graphic.material = _originalMat;
    }

    private void OnDestroy()
    {
        // TMP owns its fontMaterial instance; only image materials are ours to destroy.
        if (_tmp == null && _mat != null) Destroy(_mat);
    }
}
