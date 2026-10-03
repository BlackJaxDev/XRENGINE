using XREngine.Scene.Transforms;

[assembly: XREngine.RuntimeAnimationBinding(typeof(Transform), nameof(Transform.Translation))]
[assembly: XREngine.RuntimeAnimationBinding(typeof(Transform), nameof(Transform.Rotation))]
[assembly: XREngine.RuntimeAnimationBinding(typeof(Transform), nameof(Transform.Scale))]
[assembly: XREngine.RuntimeMemberAccess(typeof(Transform), nameof(Transform.Translation))]
[assembly: XREngine.RuntimeMemberAccess(typeof(Transform), nameof(Transform.SetX))]
[assembly: XREngine.RuntimeMemberAccess(typeof(XREngine.Scene.Transforms.TransformBase), nameof(XREngine.Scene.Transforms.TransformBase.LocalMatrixChanged))]
[assembly: XREngine.RuntimeMemberAccess(typeof(XREngine.Components.PhysicsChainBoxCollider), nameof(XREngine.Components.PhysicsChainBoxCollider.ColliderTransform))]
