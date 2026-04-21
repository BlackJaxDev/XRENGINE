using System.Numerics;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
public static partial class VRIKCalibrator
    {
public partial class CalibrationData
        {
            [Serializable]
            public class Target
            {
                private readonly bool _validTransform;
				private Vector3 _localPosition;
				private Quaternion _localRotation;

                public bool Used => _validTransform;
                public Vector3 LocalPosition
                {
                    get => _localPosition;
                    set => _localPosition = value;
                }
                public Quaternion LocalRotation
                {
                    get => _localRotation;
                    set => _localRotation = value;
                }

                public Target(Transform? t)
                {
                    _validTransform = t != null;
                    if (!_validTransform)
                        return;

                    _localPosition = t!.Translation;
                    _localRotation = t!.Rotation;
                }

                public void ApplyTo(Transform t)
                {
                    if (!_validTransform)
                        return;

                    t.Translation = _localPosition;
                    t.Rotation = _localRotation;
                }
            }
        }
    }
}
