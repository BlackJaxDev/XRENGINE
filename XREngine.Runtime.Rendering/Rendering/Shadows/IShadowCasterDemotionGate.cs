using XREngine.Data.Geometry;

namespace XREngine.Scene;

/// <summary>
/// Decides whether a demotion batch may move a quiet caster back into the static caster tiles in
/// the current plan pass. The lights collection implements it: it returns true only when every
/// cascade whose tile the caster box meets draws its static tile in this pass anyway.
/// </summary>
internal interface IShadowCasterDemotionGate
{
    bool CanDemote(in AABB worldBounds);
}
