/// <summary>UI graphic whose material has an HDR _GlowBoost multiplier (values > 1 feed the
/// bloom post pass). Lets DashboardController pulse the glow without swapping materials —
/// swapping would strip the custom fill shader mid-highlight.</summary>
public interface IGlowBoost
{
    /// <summary>Resting HDR multiplier (serialized, tuned in the inspector).</summary>
    float BaseGlowBoost { get; }

    /// <summary>Multiplier currently applied. Reset to <see cref="BaseGlowBoost"/> when a pulse ends.</summary>
    float GlowBoost { get; set; }
}
