using Extensions;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Animation;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Components.Animation
{
    public partial class IKSolverVR
    {
        [Serializable]
        public partial class Locomotion
        {
            [Serializable]
            public enum Mode
            {
                Procedural = 0,
                Animated = 1,
            }

            /// <summary>
            /// Procedural (legacy) or animated locomotion.
            /// </summary>
            public Mode _mode;

            /// <summary>
            /// Used for blending in/out of procedural/animated locomotion.
            /// </summary>
            [Range(0f, 1f)]
            public float _weight = 1.0f;

            public void Initialize(AnimStateMachineComponent? animator, Vector3[] positions, Quaternion[] rotations, bool hasToes, float scale)
            {
                //Initiate_Procedural(positions, rotations, hasToes, scale);
                Initiate_Animated(animator, positions);
            }

            public void Reset(Vector3[] positions, Quaternion[] rotations)
            {
                //Reset_Procedural(positions, rotations);
                Reset_Animated(positions);
            }

            public void Relax()
            {
                //Relax_Procedural();
            }

            public void AddDeltaRotation(Quaternion delta, Vector3 pivot)
            {
                //AddDeltaRotation_Procedural(delta, pivot);
                AddDeltaRotation_Animated(delta, pivot);
            }

            public void AddDeltaPosition(Vector3 delta)
            {
                //AddDeltaPosition_Procedural(delta);
                AddDeltaPosition_Animated(delta);
            }

            /// <summary>
            /// Start moving (horizontal distance to HMD + HMD velocity) threshold.
            /// </summary>
            public float _moveThreshold = 0.3f;

            /// <summary>
            /// Minimum locomotion animation speed.
            /// </summary>
            public float _minAnimationSpeed = 0.2f;

            /// <summary>
            /// Maximum locomotion animation speed.
            /// </summary>
            public float _maxAnimationSpeed = 3f;

            /// <summary>
            /// Smoothing time for Vector3.SmoothDamping 'VRIK_Horizontal' and 'VRIK_Vertical' parameters.
            /// Larger values make animation smoother, but less responsive.
            /// </summary>
            public float _animationSmoothTime = 0.1f;

            /// <summary>
            /// X and Z standing offset from the horizontal position of the HMD.
            /// </summary>
            public Vector2 _standOffset;

            /// <summary>
            /// Lerp root towards the horizontal position of the HMD with this speed while moving.
            /// </summary>
            public float _rootLerpSpeedWhileMoving = 30f;

            /// <summary>
            /// Lerp root towards the horizontal position of the HMD with this speed while in transition from locomotion to idle state.
            /// </summary>
            public float _rootLerpSpeedWhileStopping = 10f;

            /// <summary>
            /// Lerp root towards the horizontal position of the HMD with this speed while turning on spot.
            /// </summary>
            public float _rootLerpSpeedWhileTurning = 10f;

            /// <summary>
            /// Max horizontal distance from the root to the HMD.
            /// </summary>
            public float _maxRootOffset = 0.5f;

            /// <summary>
            /// Max root angle from head forward while moving (ik.solver.spine.maxRootAngle).
            /// </summary>
            public float _maxRootAngleMoving = 10f;

            /// <summary>
            /// Max root angle from head forward while standing (ik.solver.spine.maxRootAngle.
            /// </summary>
            public float _maxRootAngleStanding = 90f;

            /// <summary>
            /// Multiplies "VRIK_Horizontal" and "VRIK_Vertical" parameters. Larger values make steps longer and animation slower.
            /// </summary>
            public float _stepLengthMlp = 1f;

            private AnimStateMachineComponent? _animator;
            private Vector3 _velocityLocal, _velocityLocalV;
            private Vector3 _lastCorrection;
            private Vector3 _lastHeadTargetPos;
            private Vector3 _lastSpeedRootPos;
            private Vector3 _lastEndRootPos;
            private float _rootLerpSpeed, _rootVelocityV;
            private float _animSpeed = 1f;
            private float _animSpeedV;
            private float _stopMoveTimer;
            private float _turn;
            private float _maxRootAngleV;
            private float _currentAnimationSmoothTime = 0.05f;
            private bool _isMoving;
            private bool _firstFrame = true;

            private string PARAM_Horizontal = "VRIK_Horizontal";
            private string PARAM_Vertical = "VRIK_Vertical";
            private string PARAM_IsMoving = "VRIK_IsMoving";
            private string PARAM_Speed = "VRIK_Speed";
            private string PARAM_Turn = "VRIK_Turn";
            private string PARAM_Stop = "VRIK_Stop";

            public void Initiate_Animated(AnimStateMachineComponent? animator, Vector3[] positions)
            {
                _animator = animator;

                if (animator == null && _mode == Mode.Animated)
                    Debug.LogWarning("VRIK is in Animated locomotion mode, but cannot find Animator on the VRIK root SceneNode.");
                
                ResetParams(positions);
            }

            private void ResetParams(Vector3[] positions)
            {
                _lastHeadTargetPos = positions[5];
                _lastSpeedRootPos = positions[0];
                _lastEndRootPos = _lastSpeedRootPos;
                _lastCorrection = Vector3.Zero;
                _isMoving = false;
                _currentAnimationSmoothTime = 0.05f;
                _stopMoveTimer = 1f;
            }

            public void Reset_Animated(Vector3[] positions)
            {
                ResetParams(positions);

                if (_animator == null)
                    return;

                if (!_firstFrame)
                {
                    _animator.SetFloat(PARAM_Horizontal, 0f);
                    _animator.SetFloat(PARAM_Vertical, 0f);
                    _animator.SetBool(PARAM_IsMoving, false);
                    _animator.SetFloat(PARAM_Speed, 1f);
                    _animator.SetFloat(PARAM_Turn, 0f);
                }
            }

            private void AddDeltaRotation_Animated(Quaternion delta, Vector3 pivot)
            {
                Vector3 toLastEndRootPos = _lastEndRootPos - pivot;
                _lastEndRootPos = pivot + delta.Rotate(toLastEndRootPos);

                Vector3 toLastSpeedRootPos = _lastSpeedRootPos - pivot;
                _lastSpeedRootPos = pivot + delta.Rotate(toLastSpeedRootPos);

                Vector3 toLastHeadTargetPos = _lastHeadTargetPos - pivot;
                _lastHeadTargetPos = pivot + delta.Rotate(toLastHeadTargetPos);
            }

            private void AddDeltaPosition_Animated(Vector3 delta)
            {
                _lastEndRootPos += delta;
                _lastSpeedRootPos += delta;
                _lastHeadTargetPos += delta;
            }

            private float _lastVelLocalMag;
            public LayerMask _blockingLayers;
            public float _raycastRadius;

            public void Solve_Animated(IKSolverVR solver, float scale, float deltaTime)
            {
                if (_animator == null)
                {
                    //Debug.LogWarning("VRIK cannot find Animator on the VRIK root SceneNode.");
                    return;
                }

                if (deltaTime <= 0f)
                    return;

                // Root up vector
                var rb = solver.RootBone;
                if (rb is null)
                    return;

                Vector3 rootUp = rb.SolverRotation.Rotate(Globals.Up).Normalized();

                // Substract any motion from parent transforms
                Vector3 externalDelta = rb.SolverPosition - _lastEndRootPos;
                externalDelta -= _animator.StateMachine?.DeltaPosition ?? Vector3.Zero;

                // Head target position
                Vector3 headTargetPos = solver._spine._headPosition;
                Vector3 standOffsetWorld = rb.SolverRotation.Rotate(new Vector3(_standOffset.X, 0f, _standOffset.Y) * scale);
                headTargetPos += standOffsetWorld;

                if (_firstFrame)
                {
                    _lastHeadTargetPos = headTargetPos;
                    _firstFrame = false;
                }

                // Head target velocity
                Vector3 headTargetVelocity = (headTargetPos - _lastHeadTargetPos) / deltaTime;
                _lastHeadTargetPos = headTargetPos;
                headTargetVelocity = XRMath.Flatten(headTargetVelocity, rootUp);

                // Head target offset
                Vector3 offset = headTargetPos - rb.SolverPosition;
                offset -= externalDelta;
                offset -= _lastCorrection;
                offset = XRMath.Flatten(offset, rootUp);

                // Turning
                Vector3 headForward = (solver._spine.IKRotationHead * solver._spine._anchorRelativeToHead).Rotate(Globals.Forward);
                headForward.Y = 0f;
                Vector3 headForwardLocal = Quaternion.Inverse(rb.SolverRotation).Rotate(headForward);
                float angle = float.RadiansToDegrees(MathF.Atan2(headForwardLocal.X, headForwardLocal.Z));
                angle += solver._spine.RootHeadingOffset;
                float turnTarget = angle / 90f;
                bool isTurning = true;
                if (MathF.Abs(turnTarget) < 0.2f)
                {
                    turnTarget = 0f;
                    isTurning = false;
                }

                _turn = Interp.Lerp(_turn, turnTarget, Engine.Delta * 3f);
                _animator.SetFloat(PARAM_Turn, _turn * 2f);

                // Local Velocity, animation smoothing
                Vector3 velocityLocalTarget = Quaternion.Inverse(solver.RootBone!.InputRotation).Rotate(headTargetVelocity + offset);
                velocityLocalTarget *= _weight * _stepLengthMlp;

                float animationSmoothTimeTarget = isTurning && !_isMoving ? 0.2f : _animationSmoothTime;
                _currentAnimationSmoothTime = Interp.Lerp(_currentAnimationSmoothTime, animationSmoothTimeTarget, deltaTime * 20f);

                _velocityLocal = XRMath.SmoothDamp(_velocityLocal, velocityLocalTarget, ref _velocityLocalV, _currentAnimationSmoothTime, float.PositiveInfinity, deltaTime);
                float velLocalMag = _velocityLocal.Length() / _stepLengthMlp;

                //animator.SetBool("VRIK_StartWithRightFoot", velocityLocal.x >= 0f);
                _animator.SetFloat(PARAM_Horizontal, _velocityLocal.X / scale);
                _animator.SetFloat(PARAM_Vertical, _velocityLocal.Z / scale);

                // Is Moving
                float m = _moveThreshold * scale;
                if (_isMoving)
                    m *= 0.9f;

                bool isMovingRaw = _velocityLocal.LengthSquared() > m * m;
                if (isMovingRaw)
                    _stopMoveTimer = 0f;
                else
                    _stopMoveTimer += deltaTime;

                _isMoving = _stopMoveTimer < 0.05f;

                // Max root angle
                float maxRootAngleTarget = _isMoving ? _maxRootAngleMoving : _maxRootAngleStanding;
                solver._spine.MaxRootAngle = XRMath.SmoothDamp(
                    solver._spine.MaxRootAngle,
                    maxRootAngleTarget,
                    ref _maxRootAngleV,
                    0.2f,
                    float.PositiveInfinity,
                    deltaTime);

                _animator.SetBool(PARAM_IsMoving, _isMoving);

                // Animation speed
                Vector3 currentRootPos = rb.SolverPosition;
                currentRootPos -= externalDelta;
                currentRootPos -= _lastCorrection;

                Vector3 rootVelocity = (currentRootPos - _lastSpeedRootPos) / deltaTime;
                _lastSpeedRootPos = rb.SolverPosition;
                float rootVelocityMag = rootVelocity.Length();

                float animSpeedTarget = _minAnimationSpeed;
                if (rootVelocityMag > 0f && isMovingRaw)
                    animSpeedTarget = _animSpeed * (velLocalMag / rootVelocityMag);
                
                animSpeedTarget = animSpeedTarget.Clamp(_minAnimationSpeed, _maxAnimationSpeed);
                _animSpeed = XRMath.SmoothDamp(_animSpeed, animSpeedTarget, ref _animSpeedV, 0.05f, float.PositiveInfinity, deltaTime);
                _animSpeed = Interp.Lerp(1f, _animSpeed, _weight);

                _animator.SetFloat(PARAM_Speed, _animSpeed);

                // Is Stopping
                AnimStateTransition? transInfo = _animator.StateMachine?.GetCurrentTransition(0);
                bool isStopping = transInfo?.NameEquals(PARAM_Stop) ?? false;

                // Root lerp speed
                float rootLerpSpeedTarget = 0;
                if (_isMoving)
                    rootLerpSpeedTarget = _rootLerpSpeedWhileMoving;
                if (isStopping)
                    rootLerpSpeedTarget = _rootLerpSpeedWhileStopping;
                if (isTurning) 
                    rootLerpSpeedTarget = _rootLerpSpeedWhileTurning;

                rootLerpSpeedTarget *= MathF.Max(headTargetVelocity.Length(), 0.2f);
                _rootLerpSpeed = Interp.Lerp(_rootLerpSpeed, rootLerpSpeedTarget, deltaTime * 20f);

                // Root lerp and limits
                headTargetPos += XRMath.ExtractVertical(rb.SolverPosition - headTargetPos, rootUp, 1f);

                if (_maxRootOffset > 0f)
                {
                    // Lerp towards head target position
                    Vector3 p = rb.SolverPosition;

                    if (_rootLerpSpeed > 0f)
                        rb.SolverPosition = Vector3.Lerp(rb.SolverPosition, headTargetPos, _rootLerpSpeed * deltaTime * _weight);
                    
                    _lastCorrection = rb.SolverPosition - p;

                    // Max offset
                    offset = headTargetPos - rb.SolverPosition;
                    offset = XRMath.Flatten(offset, rootUp);
                    float offsetMag = offset.Length();

                    if (offsetMag > _maxRootOffset)
                    {
                        _lastCorrection += (offset - (offset / offsetMag) * _maxRootOffset) * _weight;
                        rb.SolverPosition += _lastCorrection;
                    }
                }
                else
                {
                    // Snap to head target position
                    _lastCorrection = (headTargetPos - rb.SolverPosition) * _weight;
                    rb.SolverPosition += _lastCorrection;
                }

                _lastEndRootPos = rb.SolverPosition;
            }
        }
    }
}
