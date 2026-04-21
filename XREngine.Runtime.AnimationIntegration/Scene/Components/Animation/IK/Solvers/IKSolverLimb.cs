using XREngine.Extensions;
using MathNet.Numerics;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Extends IKSolverTrigonometric to add automatic bend and rotation modes.
    /// </summary>
    [Serializable]
    public class IKSolverLimb : IKSolverTrigonometric
    {
        /// <summary>
        /// The AvatarIKGoal of this solver.
        /// </summary>
        public ELimbEndEffector _goal;
        /// <summary>
        /// Bend normal modifier.
        /// </summary>
        public ELimbBendModifier _bendModifier = ELimbBendModifier.Animation;
        /// <summary>
        /// Weight of maintaining the rotation of the third bone as it was before solving.
        /// </summary>
        [Range(0.0f, 1.0f)]
        public float _maintainRotationWeight;
        /// <summary>
        /// Weight of bend normal modifier.
        /// </summary>
        [Range(0.0f, 1.0f)]
        public float _bendModifierWeight = 1.0f;
        /// <summary>
        /// The bend goal Transform.
        /// </summary>
        public TransformBase? _bendGoal;

        /// <summary>
        /// When true, renders debug lines for each arm axis direction from bone1.
        /// Direction vectors are shown as solid lines, Axis vectors as dashed/offset lines.
        /// </summary>
        public bool DebugDrawAxisDirections { get; set; }

        /// <summary>
        /// Used to record rotation of the last bone for one frame. 
        /// If MaintainRotation is not called and maintainRotationWeight > 0, the solver will maintain the rotation of the last bone as it was before solving the %IK.
        /// You will probably need this if you wanted to maintain the animated rotation of a foot despite of any other %IK solver that manipulates its parents' rotation.
        /// So you would call %MaintainRotation() in LateUpdate() after animation and before updating the Spine %IK solver that would change the foot's rotation.
        /// </summary>
        public void MaintainRotation()
        {
            if (!Initialized || _bone3._transform == null)
                return;

            // Store the current rotation of the last bone
            _maintainRotation = _bone3._transform.WorldRotation;
            _maintainRotationFor1Frame = true;
        }

        /// <summary>
        /// If Auto Bend is on "Animation', %MaintainBend() can be used to set the bend axis relative to the first bone's rotation.
        /// </summary>
        public void MaintainBend()
        {
            if (!Initialized)
                return;

            _animationNormal = _bone1.GetBendNormalFromCurrentRotation();
            _maintainBendFor1Frame = true;
        }

        protected override void PreInitialize()
        {
            if (_bone1?._transform == null || _bone2?._transform == null || _bone3?._transform == null)
            {
                Debug.LogWarning("Trying to initiate IKSolverLimb with null bones.");
                return;
            }
            if (_root == null)
            {
                Debug.LogWarning("Root bone is null in IKSolverLimb.");
                return;
            }

            _defaultRootRotation = _root.WorldRotation;

            if (_bone1._transform.Parent != null)
                _parentDefaultRotation = Quaternion.Inverse(_defaultRootRotation) * _bone1._transform.Parent.WorldRotation;
            
            if (_bone3.RotationLimit != null)
                _bone3.RotationLimit.IsActive = false;

            // Limb-specific bend direction orientation:
            // hands and feet both use forward-solving orientation.
            _bendDirectionSign = _goal == ELimbEndEffector.LeftHand || _goal == ELimbEndEffector.RightHand ||
                                 _goal == ELimbEndEffector.LeftFoot || _goal == ELimbEndEffector.RightFoot
                ? 1.0f
                : -1.0f;

            // Legs need the yDirection (cross product) negated so knees bend forward.
            // Arms also need it negated so elbows bend backward (anatomically correct).
            NegateYDirection = _goal == ELimbEndEffector.LeftFoot || _goal == ELimbEndEffector.RightFoot ||
                               _goal == ELimbEndEffector.LeftHand || _goal == ELimbEndEffector.RightHand;

            _bone3DefaultRotation = _bone3._transform.WorldRotation;

            var bone1Position = _bone1._transform.WorldTranslation;
            var bone2Position = _bone2._transform.WorldTranslation;
            var bone3Position = _bone3._transform.WorldTranslation;

            // Set bend plane to current (cant use the public SetBendPlaneToCurrent() method here because the solver has not initialized yet)
            Vector3 initialNormal = Vector3.Cross(
                bone2Position - bone1Position,
                bone3Position - bone2Position).Normalized();

            // A default T-pose arm can be near-colinear, producing a zero cross product.
            // Build a stable fallback normal orthogonal to the limb direction.
            // Fallback order: Up ? Right ? Forward.
            // Right is tried before Forward so that vertical limbs (legs) get a
            // bend normal along +X, which via Cross(+X, -Y) = -Z produces a
            // forward-pointing yDirection in GetBendDirection (knees bend forward).
            if (initialNormal.LengthSquared() < 1e-8f)
            {
                Vector3 limbDirection = (bone2Position - bone1Position).Normalized();
                Vector3 fallback = Globals.Up - Vector3.Dot(Globals.Up, limbDirection) * limbDirection;

                if (fallback.LengthSquared() < 1e-8f)
                    fallback = Globals.Right - Vector3.Dot(Globals.Right, limbDirection) * limbDirection;

                if (fallback.LengthSquared() < 1e-8f)
                    fallback = Globals.Forward - Vector3.Dot(Globals.Forward, limbDirection) * limbDirection;

                initialNormal = fallback.Normalized();
            }

            if (initialNormal.LengthSquared() < 1e-8f)
                initialNormal = BendNormal == Vector3.Zero ? Globals.Right : BendNormal;

            _animationNormal = BendNormal = initialNormal;

            AssignArmAxisDirs(ref _axisDirectionsLeft);
            AssignArmAxisDirs(ref _axisDirectionsRight);
        }

        protected override void PreUpdate()
        {
            if (IKPositionWeight > 0)
            {
                // Clamping weights
                _bendModifierWeight = _bendModifierWeight.Clamp(0f, 1f);
                _maintainRotationWeight = _maintainRotationWeight.Clamp(0f, 1f);

                // Storing the bendNormal for reverting after solving
                _previousBendNormal = BendNormal;

                // Modifying bendNormal
                BendNormal = GetModifiedBendNormal();
            }

            if (_maintainRotationWeight * IKPositionWeight > 0)
            {
                if (_bone3._transform == null)
                    return;

                // Storing bone3 rotation
                _bone3RotationBeforeSolve = _maintainRotationFor1Frame 
                    ? _maintainRotation
                    : _bone3._transform.WorldRotation;

                _maintainRotationFor1Frame = false;
            }
        }

        protected override void PostSolve()
        {
            // Revert bendNormal to what it was before solving
            if (IKPositionWeight > 0)
                BendNormal = _previousBendNormal;

            // Auto rotation modes
            if (_maintainRotationWeight * IKPositionWeight > 0)
                _bone3._transform?.SetWorldRotation(Quaternion.Slerp(
                    _bone3._transform.WorldRotation,
                    _bone3RotationBeforeSolve,
                    _maintainRotationWeight * IKPositionWeight));
        }

        /// <summary>
        /// Axis direction contains an arm bend axis for a specific IKPosition direction from the first bone. Used in Arm BendModifier mode.
        /// </summary>
        /// <param name="direction"></param>
        /// <param name="axis"></param>
        [Serializable]
        public class AxisDirection(string name, Vector3 direction, Vector3 axis)
        {
            public string Name { get; set; } = name;
            public Vector3 Direction { get; set; } = direction.Normalized();
            public Vector3 Axis { get; set; } = axis.Normalized();
            public float Dot { get; set; } = 0;
        }

        public IKSolverLimb() { }
        public IKSolverLimb(ELimbEndEffector goal)
            => _goal = goal;

        private bool
            _maintainBendFor1Frame,
            _maintainRotationFor1Frame;

        private Quaternion
            _defaultRootRotation,
            _parentDefaultRotation,
            _bone3RotationBeforeSolve,
            _maintainRotation,
            _bone3DefaultRotation;

        private Vector3
            _previousBendNormal,
            _animationNormal;

        public List<AxisDirection> _axisDirectionsLeft = [];
        public List<AxisDirection> _axisDirectionsRight = [];

        private List<AxisDirection> ArmAxisDirections
            => _goal == ELimbEndEffector.LeftHand 
                ? _axisDirectionsLeft
                : _axisDirectionsRight;

        /// <summary>
        /// Stores the axis directions for the arm bend modifier and are arbitrary based on what looks natural.
        /// Z-components are negated relative to the original Unity (LH, +Z forward)
        /// values because this engine uses RH with -Z forward.
        /// </summary>
        /// <param name="axisDirections"></param>
        private static void AssignArmAxisDirs(ref List<AxisDirection> axisDirections)
        {
            axisDirections =
            [
                new("Default",
                    Vector3.Zero,
                    new Vector3(-1f, 0f, 0f)),

                new("Behind Head",
                    new Vector3(0.5f, 0f, -0.2f),
                    new Vector3(-0.5f, 1f, 1f)),

                new("Arm Twist",
                    new Vector3(-0.5f, -1f, -0.2f),
                    new Vector3(0f, -0.5f, -1f)),

                new("Cross Heart",
                    new Vector3(-0.5f, -0.5f, 1f),
                    new Vector3(-1f, 1f, -1f)),
            ];
        }

        private Vector3 GetModifiedBendNormal()
        {
            float weight = _bendModifierWeight;
            if (weight <= 0)
                return BendNormal;

            switch (_bendModifier)
            {
                // Animation Bend Mode attempts to maintain the bend axis as it is in the animation
                case ELimbBendModifier.Animation:
                    {
                        if (!_maintainBendFor1Frame)
                            MaintainBend();

                        _maintainBendFor1Frame = false;

                        return Vector3.Lerp(BendNormal, _animationNormal, weight);
                    }
                // Bending relative to the parent of the first bone
                case ELimbBendModifier.Parent:
                    {
                        if (_bone1._transform == null)
                            return BendNormal;

                        if (_bone1._transform.Parent == null)
                            return BendNormal;

                        Quaternion parentRotation = _bone1._transform.Parent.WorldRotation * Quaternion.Inverse(_parentDefaultRotation);
                        return Vector3.Transform(BendNormal, Quaternion.Slerp(Quaternion.Identity, parentRotation * Quaternion.Inverse(_defaultRootRotation), weight));
                    }
                // Bending relative to IKRotation
                case ELimbBendModifier.Target:
                    {
                        if (XRMath.Approx(weight, 0.0f))
                            return BendNormal;

                        Quaternion targetRotation = GetWorldIKRotation() * Quaternion.Inverse(_bone3DefaultRotation);

                        if (XRMath.Approx(weight, 1.0f))
                            return Vector3.Transform(BendNormal, targetRotation);

                        return Vector3.Transform(BendNormal, Quaternion.Slerp(Quaternion.Identity, targetRotation, weight)).Normalized();
                    }
                // Anatomic Arm
                case ELimbBendModifier.Arm:
                    {
                        if (_bone1._transform == null || _bone1._transform.Parent == null || weight.AlmostEqual(0.0f))
                            return BendNormal;

                        if (_goal == ELimbEndEffector.LeftFoot ||
                            _goal == ELimbEndEffector.RightFoot)
                        {
                            Debug.LogWarning("Trying to use the 'Arm' bend modifier on a leg.");
                            return BendNormal;
                        }

                        Vector3 direction = (GetWorldIKPosition() - _bone1._transform.WorldTranslation).Normalized();

                        //Convert direction to default world space
                        var localToParentRot = _bone1._transform.Parent.WorldRotation * Quaternion.Inverse(_parentDefaultRotation);
                        direction = Vector3.Transform(direction, Quaternion.Inverse(localToParentRot));

                        //Invert direction for left hand
                        if (_goal == ELimbEndEffector.LeftHand)
                            direction.X = -direction.X;

                        //Calculate dot products for all AxisDirections
                        var armDirs = ArmAxisDirections;
                        for (int i = 1; i < armDirs.Count; i++)
                        {
                            var armDir = armDirs[i];
                            float dot = Vector3.Dot(armDir.Direction, direction).Clamp(0.0f, 1.0f);
                            armDir.Dot = Interp.Float(dot, EFloatInterpolationMode.InOutQuintic);
                        }

                        //Sum up the arm bend axis
                        Vector3 sum = armDirs[0].Axis;
                        for (int i = 1; i < armDirs.Count; i++)
                            sum = XRMath.Slerp(sum, armDirs[i].Axis, armDirs[i].Dot);

                        //Invert sum for left hand
                        if (_goal == ELimbEndEffector.LeftHand)
                            sum = -sum;
                        
                        //Convert sum back to parent space
                        Vector3 armBendNormal = Vector3.Transform(sum, localToParentRot);

                        // Debug visualization of axis directions
                        if (DebugDrawAxisDirections && _bone1._transform != null && _bone2._transform != null)
                        {
                            Vector3 origin = _bone1._transform.WorldTranslation;
                            float lineLen = MathF.Sqrt(_bone1._lengthSquared);
                            ColorF4[] dirColors = [ColorF4.Yellow, ColorF4.Cyan, ColorF4.Magenta, ColorF4.Orange];
                            ColorF4[] axisColors = [ColorF4.LightGreen, ColorF4.LightBlue, ColorF4.LightRed, ColorF4.White];

                            for (int i = 0; i < armDirs.Count; i++)
                            {
                                var ad = armDirs[i];
                                var dirColor = i < dirColors.Length ? dirColors[i] : ColorF4.Gray;
                                var axColor = i < axisColors.Length ? axisColors[i] : ColorF4.Gray;

                                // Direction vectors: transform from default space back to world
                                if (ad.Direction.LengthSquared() > 1e-8f)
                                {
                                    Vector3 worldDir = Vector3.Transform(
                                        _goal == ELimbEndEffector.LeftHand ? new(-ad.Direction.X, ad.Direction.Y, ad.Direction.Z) : ad.Direction,
                                        localToParentRot);
                                    Engine.Rendering.Debug.RenderLine(origin, origin + worldDir * lineLen, dirColor);
                                    Engine.Rendering.Debug.RenderPoint(origin + worldDir * lineLen, dirColor);
                                }

                                // Axis vectors: the bend axis that would be used at this direction
                                Vector3 axisWorld = _goal == ELimbEndEffector.LeftHand ? -ad.Axis : ad.Axis;
                                axisWorld = Vector3.Transform(axisWorld, localToParentRot);
                                Vector3 axisOrigin = origin + Vector3.UnitY * 0.02f * (i + 1);
                                Engine.Rendering.Debug.RenderLine(axisOrigin, axisOrigin + axisWorld * lineLen * 0.7f, axColor);
                            }

                            // Also show the final blended bend normal
                            Engine.Rendering.Debug.RenderLine(origin, origin + armBendNormal * lineLen * 1.2f, ColorF4.Red);
                            Engine.Rendering.Debug.RenderPoint(origin + armBendNormal * lineLen * 1.2f, ColorF4.Red);
                        }

                        if (weight.AlmostEqual(1.0f))
                            return armBendNormal;

                        return Vector3.Lerp(BendNormal, armBendNormal, weight);
                    }
                // Bending towards the bend goal Transform
                case ELimbBendModifier.Goal:
                    {
                        if (_bendGoal == null)
                        {
                            Debug.LogWarning("Trying to use the 'Goal' Bend Modifier, but the Bend Goal is unassigned.");
                            return BendNormal;
                        }

                        if (_bone1._transform is null)
                            return BendNormal;

                        Vector3 normal = Vector3.Cross(
                            _bendGoal.WorldTranslation - _bone1._transform.WorldTranslation,
                            GetWorldIKPosition() - _bone1._transform.WorldTranslation);

                        if (normal == Vector3.Zero)
                            return BendNormal;

                        if (weight >= 1f)
                            return normal;

                        return Vector3.Lerp(BendNormal, normal, weight);
                    }
                default:
                    return BendNormal;
            }
        }
    }
}
