using System.ComponentModel;
using XREngine.Rendering.Models.Gaussian.Cache;

namespace XREngine.Components.Lights;

/// <summary>
/// Gives the camera path of the preview loop that a gaussian preview bake records for the content that holds this
/// component. Each key gives the camera position and rotation at one time of the loop. The camera moves in a straight
/// line and turns smoothly from key to key, and after the last key it goes back to the first key at the end of the loop.
/// With no keys, the loop camera orbits the content.
/// </summary>
[Serializable]
public class PreviewCameraPathComponent : XRComponent
{
    private GaussianCacheCameraKey[] _keys = [];

    /// <summary>
    /// The camera path keys, in time order. A key time is in seconds from the start of the loop, from 0 to the loop
    /// length. The camera looks along its local −Z axis, with its local +Y axis up. An empty array means no path.
    /// </summary>
    [Category("Preview")]
    public GaussianCacheCameraKey[] Keys
    {
        get => _keys;
        set => SetField(ref _keys, value ?? []);
    }
}
