using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Finds the residency assets that the components of one scene node use. The manifest bake and the approximate
/// manifest call it for each node that they visit. A test can supply a fake collector.
/// </summary>
public interface ICutsceneResidencyAssetCollector
{
    /// <summary>
    /// Adds the assets of the components of a node to a list. Do not visit the child nodes; the caller visits them.
    /// The world matrices of the node are current when the caller calls this method.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="destination">The list that receives the assets. The method does not clear it.</param>
    void Collect(SceneNode node, List<CutsceneResidencyAsset> destination);
}
