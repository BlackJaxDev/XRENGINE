using Extensions;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    public partial class IKSolverVR
    {
        /// <summary>
        /// Spine solver for IKSolverVR.
        /// </summary>
        [System.Serializable]
        public class Spine : BodyPart
        {
            /// <summary>
            /// The head target. This should not be the camera Transform itself, but a child GameObject parented to it so you could adjust its position/rotation to match the orientation of the head bone. The best practice for setup would be to move the camera to the avatar's eyes, duplicate the avatar's head bone and parent it to the camera. Then assign the duplicate to this slot.
            /// </summary>
            public Transform? _headTarget;

            /// <summary>
            /// Positional weight of the head target. Note that if you have nulled the headTarget, the head will still be pulled to the last position of the headTarget until you set this value to 0.
            /// </summary>
            [Range(0f, 1f)]
            public float _positionWeight = 1f;

            /// <summary>
            /// Rotational weight of the head target. Note that if you have nulled the headTarget, the head will still be rotated to the last rotation of the headTarget until you set this value to 0.
            /// </summary>
            [Range(0f, 1f)]
            public float _rotationWeight = 1f;

            /// <summary>
            /// Clamps head rotation. Value of 0.5 allows 90 degrees of rotation for the head relative to the headTarget. Value of 0 allows 180 degrees and value of 1 means head rotation will be locked to the target.
            /// </summary>
            [Range(0f, 1f)]
            public float _headClampWeight = 0.6f;

            /// <summary>
            /// Minimum height of the head from the root of the character.
            /// </summary>
            public float _minHeadHeight = 0.8f;

            /// <summary>
            /// Allows for more natural locomotion animation for 3rd person networked avatars by inheriting vertical head bob motion from the animation while head target height is close to head bone height.
            /// </summary>
            [Range(0f, 1f)]
            public float _useAnimatedHeadHeightWeight;

            /// <summary>
            /// If abs(head target height - head bone height) < this value, will use head bone height as head target Y.
            /// </summary>
            public float _useAnimatedHeadHeightRange = 0.1f;

            /// <summary>
            /// Falloff range for the 'Use Animated Head Height Range' effect above. If head target height from head bone height is greater than useAnimatedHeadHeightRange + animatedHeadHeightBlend, then the head will be vertically locked to the head target again.
            /// </summary>
            public float _animatedHeadHeightBlend = 0.3f;

            /// <summary>
            /// The pelvis target (optional), useful for seated rigs or if you had an additional tracker on the backpack or belt are. The best practice for setup would be to duplicate the avatar's pelvis bone and parenting it to the pelvis tracker. Then assign the duplicate to this slot.
            /// </summary>
            public Transform? _hipsTarget;

            /// <summary>
            /// Positional weight of the pelvis target. Note that if you have nulled the pelvisTarget, the pelvis will still be pulled to the last position of the pelvisTarget until you set this value to 0.
            /// </summary>
            [Range(0f, 1f)]
            public float _pelvisPositionWeight;

            /// <summary>
            /// Rotational weight of the pelvis target. Note that if you have nulled the pelvisTarget, the pelvis will still be rotated to the last rotation of the pelvisTarget until you set this value to 0.
            /// </summary>
            [Range(0f, 1f)]
            public float _pelvisRotationWeight;

            /// <summary>
            /// How much will the pelvis maintain its animated position?
            /// </summary>
            [Range(0f, 1f)]
            public float _maintainPelvisPosition = 0.2f;

            /// <summary>
            /// If chestGoalWeight is greater than 0, the chest will be turned towards this Transform.
            /// </summary>
            public Transform? _chestGoal;

            /// <summary>
            /// Weight of turning the chest towards the chestGoal.
            /// </summary>
            [Range(0f, 1f)]
            public float _chestGoalWeight;

            /// <summary>
            /// Clamps chest rotation. Value of 0.5 allows 90 degrees of rotation for the chest relative to the head. Value of 0 allows 180 degrees and value of 1 means the chest will be locked relative to the head.
            /// </summary>
            [Range(0f, 1f)]
            public float _chestClampWeight = 0.5f;

            /// <summary>
            /// The amount of rotation applied to the chest based on hand positions.
            /// </summary>
            [Range(0f, 1f)]
            public float _rotateChestByHands = 1f;

            /// <summary>
            /// Determines how much the body will follow the position of the head.
            /// </summary>
            [Range(0f, 1f)]
            public float _bodyPosStiffness = 0.55f;

            /// <summary>
            /// Determines how much the body will follow the rotation of the head.
            /// </summary>
            [Range(0f, 1f)]
            public float _bodyRotStiffness = 0.1f;

            /// <summary>
            /// Determines how much the chest will rotate to the rotation of the head.
            /// </summary>
            [Range(0f, 1f)]
            public float _neckStiffness = 0.2f;

            /// <summary>
            /// Moves the body horizontally along -character.forward axis by that value when the player is crouching.
            /// </summary>
            public float _moveBodyBackWhenCrouching = 0.5f;

            /// <summary>
            /// Will automatically rotate the root of the character if the head target has turned past this angle.
            /// </summary>
            [Range(0f, 180f)]
            public float _maxRootAngle = 25f;

            /// <summary>
            /// Angular offset for root heading. Adjust this value to turn the root relative to the HMD around the vertical axis. Usefulf for fighting or shooting games where you would sometimes want the avatar to stand at an angled stance.
            /// </summary>
            [Range(-180f, 180f)]
            public float _rootHeadingOffset;

            /// <summary>
            /// Target position of the head. Will be overwritten if target is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 IKPositionHead;

            /// <summary>
            /// Target rotation of the head. Will be overwritten if target is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Quaternion IKRotationHead = Quaternion.Identity;

            /// <summary>
            /// Target position of the pelvis. Will be overwritten if target is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 IKPositionPelvis;

            /// <summary>
            /// Target rotation of the pelvis. Will be overwritten if target is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Quaternion IKRotationPelvis = Quaternion.Identity;

            /// <summary>
            /// The goal position for the chest. If chestGoalWeight > 0, the chest will be turned towards this position.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 _goalPositionChest;

            /// <summary>
            /// Position offset of the pelvis. Will be applied on top of pelvis target position and reset to Vector3.zero after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 _pelvisPositionOffset;

            /// <summary>
            /// Position offset of the chest. Will be reset to Vector3.zero after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 _chestPositionOffset;

            /// <summary>
            /// Position offset of the head. Will be applied on top of head target position and reset to Vector3.zero after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 _headPositionOffset;

            /// <summary>
            /// Rotation offset of the pelvis. Will be reset to Quaternion.identity after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Quaternion _pelvisRotationOffset = Quaternion.Identity;

            /// <summary>
            /// Rotation offset of the chest. Will be reset to Quaternion.identity after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Quaternion _chestRotationOffset = Quaternion.Identity;

            /// <summary>
            /// Rotation offset of the head. Will be applied on top of head target rotation and reset to Quaternion.identity after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Quaternion _headRotationOffset = Quaternion.Identity;

            internal VirtualBone Hips => _bones[_pelvisIndex];
            internal VirtualBone FirstSpineBone => _bones[_spineIndex];
            internal VirtualBone Chest => _hasChest ? _bones[_chestIndex] : _bones[_spineIndex];
            internal VirtualBone Head => _bones[_headIndex];
            private VirtualBone Neck => _bones[_neckIndex];

            [NonSerialized][HideInInspector]
            public Vector3 _faceDirection;
            [NonSerialized][HideInInspector]
            internal Vector3 _headPosition;

            internal Quaternion AnchorRotation { get; private set; }
            internal Quaternion AnchorRelativeToHead { get; private set; }

            private Quaternion _headRotation = Quaternion.Identity;
            private Quaternion _pelvisRotation = Quaternion.Identity;
            private Quaternion _anchorRelativeToPelvis = Quaternion.Identity;
            private Quaternion _pelvisRelativeRotation = Quaternion.Identity;
            private Quaternion _chestRelativeRotation = Quaternion.Identity;
            private Vector3 _headDeltaPosition;
            private Quaternion _pelvisDeltaRotation = Quaternion.Identity;
            private Quaternion _chestTargetRotation = Quaternion.Identity;
            private int _pelvisIndex = 0, _spineIndex = 1, _chestIndex = -1, _neckIndex = -1, _headIndex = -1;
            private float length;
            private bool _hasChest;
            private bool _hasNeck;
            private bool _hasLegs;
            private float _headHeight;
            private float _sizeMlp;
            private Vector3 _chestForward;

            protected override void OnRead(Vector3[] positions, Quaternion[] rotations, bool hasChest, bool hasNeck, bool hasShoulders, bool hasToes, bool hasLegs, int rootIndex, int index)
            {
                Vector3 pelvisPos = positions[index];
                Quaternion pelvisRot = rotations[index];
                Vector3 spinePos = positions[index + 1];
                Quaternion spineRot = rotations[index + 1];
                Vector3 chestPos = positions[index + 2];
                Quaternion chestRot = rotations[index + 2];
                Vector3 neckPos = positions[index + 3];
                Quaternion neckRot = rotations[index + 3];
                Vector3 headPos = positions[index + 4];
                Quaternion headRot = rotations[index + 4];

                this._hasLegs = hasLegs;

                if (!hasChest)
                {
                    chestPos = spinePos;
                    chestRot = spineRot;
                }

                if (!_initialized)
                {
                    _hasChest = hasChest;
                    _hasNeck = hasNeck;
                    _headHeight = XRMath.ExtractVertical(headPos - positions[0], rotations[0].Rotate(Globals.Up), 1f).Length();

                    int boneCount = 3;
                    if (hasChest)
                        boneCount++;
                    if (hasNeck)
                        boneCount++;
                    _bones = new VirtualBone[boneCount];

                    _chestIndex = hasChest ? 2 : 1;

                    _neckIndex = 1;
                    if (hasChest)
                        _neckIndex++;
                    if (hasNeck)
                        _neckIndex++;

                    _headIndex = 2;
                    if (hasChest)
                        _headIndex++;
                    if (hasNeck) 
                        _headIndex++;

                    _bones[0] = new VirtualBone(pelvisPos, pelvisRot);
                    _bones[1] = new VirtualBone(spinePos, spineRot);

                    if (hasChest)
                        _bones[_chestIndex] = new VirtualBone(chestPos, chestRot);

                    if (hasNeck)
                        _bones[_neckIndex] = new VirtualBone(neckPos, neckRot);

                    _bones[_headIndex] = new VirtualBone(headPos, headRot);

                    _pelvisRotationOffset = Quaternion.Identity;
                    _chestRotationOffset = Quaternion.Identity;
                    _headRotationOffset = Quaternion.Identity;

                    AnchorRelativeToHead = Quaternion.Inverse(headRot) * rotations[0];
                    _anchorRelativeToPelvis = Quaternion.Inverse(pelvisRot) * rotations[0];

                    _faceDirection = rotations[0].Rotate(Globals.Forward);

                    IKPositionHead = headPos;
                    IKRotationHead = headRot;
                    IKPositionPelvis = pelvisPos;
                    IKRotationPelvis = pelvisRot;
                    _goalPositionChest = chestPos + rotations[0].Rotate(Globals.Forward);
                }

                // Forward and up axes
                _pelvisRelativeRotation = Quaternion.Inverse(headRot) * pelvisRot;
                _chestRelativeRotation = Quaternion.Inverse(headRot) * chestRot;

                _chestForward = Quaternion.Inverse(chestRot).Rotate((rotations[0].Rotate(Globals.Forward)));

                _bones[0].Read(pelvisPos, pelvisRot);
                _bones[1].Read(spinePos, spineRot);

                if (hasChest)
                    _bones[_chestIndex].Read(chestPos, chestRot);

                if (hasNeck)
                    _bones[_neckIndex].Read(neckPos, neckRot);

                _bones[_headIndex].Read(headPos, headRot);

                float spineLength = Vector3.Distance(pelvisPos, headPos);
                _sizeMlp = spineLength / 0.7f;
            }

            public override void PreSolve(float scale)
            {
                if (_headTarget != null)
                {
                    IKPositionHead = _headTarget.WorldTranslation;
                    IKRotationHead = _headTarget.WorldRotation;
                }

                if (_chestGoal != null)
                {
                    _goalPositionChest = _chestGoal.WorldTranslation;
                }

                if (_hipsTarget != null)
                {
                    IKPositionPelvis = _hipsTarget.WorldTranslation;
                    IKRotationPelvis = _hipsTarget.WorldRotation;
                }

                // Use animated head height range
                if (_useAnimatedHeadHeightWeight > 0f && _useAnimatedHeadHeightRange > 0f)
                {
                    Vector3 rootUp = _rootRotation.Rotate(Globals.Up);

                    if (_animatedHeadHeightBlend > 0f)
                    {
                        float headTargetVOffset = XRMath.ExtractVertical(IKPositionHead - Head._solverPosition, rootUp, 1f).Length();
                        float abs = MathF.Abs(headTargetVOffset);
                        abs = MathF.Max(abs - _useAnimatedHeadHeightRange * scale, 0f);
                        float f = Interp.Lerp(0f, 1f, abs / (_animatedHeadHeightBlend * scale));
                        f = Interp.Float(1f - f, EFloatInterpolationMode.InOutSine);

                        Vector3 toHeadPos = Head._solverPosition - IKPositionHead;
                        IKPositionHead += XRMath.ExtractVertical(toHeadPos, rootUp, f * _useAnimatedHeadHeightWeight);
                    }
                    else
                    {
                        IKPositionHead += XRMath.ExtractVertical(Head._solverPosition - IKPositionHead, rootUp, _useAnimatedHeadHeightWeight);
                    }
                }

                _headPosition = XRMath.Lerp(Head._solverPosition, IKPositionHead, _positionWeight);
                _headRotation = XRMath.Lerp(Head._solverRotation, IKRotationHead, _rotationWeight);

                _pelvisRotation = XRMath.Lerp(Hips._solverRotation, IKRotationPelvis, _rotationWeight);
            }

            public override void ApplyOffsets(float scale)
            {
                _headPosition += _headPositionOffset;

                float mHH = _minHeadHeight * scale;

                Vector3 rootUp = _rootRotation.Rotate(Globals.Up);
                if (rootUp == Globals.Up)
                    _headPosition.Y = Math.Max(_rootPosition.Y + mHH, _headPosition.Y);
                else
                {
                    Vector3 toHead = _headPosition - _rootPosition;
                    Vector3 hor = XRMath.ExtractHorizontal(toHead, rootUp, 1f);
                    Vector3 ver = toHead - hor;
                    float dot = Vector3.Dot(ver, rootUp);
                    if (dot > 0f)
                    {
                        if (ver.Length() < mHH)
                            ver = ver.Normalized() * mHH;
                    }
                    else
                    {
                        ver = -ver.Normalized() * mHH;
                    }

                    _headPosition = _rootPosition + hor + ver;
                }

                _headRotation = _headRotationOffset * _headRotation;

                _headDeltaPosition = _headPosition - Head._solverPosition;
                _pelvisDeltaRotation = XRMath.FromToRotation(Hips._solverRotation, _headRotation * _pelvisRelativeRotation);

                if (_pelvisRotationWeight <= 0f)
                    AnchorRotation = _headRotation * AnchorRelativeToHead;
                else if (_pelvisRotationWeight > 0f && _pelvisRotationWeight < 1f)
                    AnchorRotation = Quaternion.Lerp(_headRotation * AnchorRelativeToHead, _pelvisRotation * _anchorRelativeToPelvis, _pelvisRotationWeight);
                else if (_pelvisRotationWeight >= 1f)
                    AnchorRotation = _pelvisRotation * _anchorRelativeToPelvis;
            }

            private void CalculateChestTargetRotation(VirtualBone rootBone, Arm[] arms)
            {
                _chestTargetRotation = _headRotation * _chestRelativeRotation;

                // Use hands to adjust c
                if (arms[0] != null)
                    AdjustChestByHands(ref _chestTargetRotation, arms);

                _faceDirection = Vector3.Cross(AnchorRotation.Rotate(Globals.Right), rootBone._readRotation.Rotate(Globals.Up)) + AnchorRotation.Rotate(Globals.Forward);
            }

            public void Solve(AnimStateMachineComponent? animator, VirtualBone rootBone, Leg[] legs, Arm[] arms, float scale)
            {
                CalculateChestTargetRotation(rootBone, arms);

                // Root rotation
                if (_maxRootAngle < 180f)
                {
                    Vector3 f = _faceDirection;

                    if (_rootHeadingOffset != 0f)
                        f = Quaternion.CreateFromAxisAngle(Globals.Up, _rootHeadingOffset).Rotate(f);

                    Vector3 faceDirLocal = Quaternion.Inverse(rootBone._solverRotation).Rotate(f);

                    float angle = float.RadiansToDegrees(MathF.Atan2(faceDirLocal.X, faceDirLocal.Z));

                    float rotation = 0f;
                    float maxAngle = _maxRootAngle;

                    if (angle > maxAngle)
                        rotation = angle - maxAngle;
                    if (angle < -maxAngle)
                        rotation = angle + maxAngle;

                    Quaternion fix = Quaternion.CreateFromAxisAngle(rootBone._readRotation.Rotate(Globals.Up), float.DegreesToRadians(rotation));

                    if (animator != null && animator.IsActive)
                    {
                        // Rotate root around animator.pivotPosition
                        Vector3 pivot = animator.applyRootMotion
                            ? animator.pivotPosition
                            : animator.Transform.WorldTranslation;
                        Vector3 dir = rootBone._solverPosition - pivot;
                        rootBone._solverPosition = pivot + fix.Rotate(dir);
                    }

                    // Rotate root
                    rootBone._solverRotation = fix * rootBone._solverRotation;
                }

                Vector3 animatedPelvisPos = Hips._solverPosition;
                Vector3 rootUp = rootBone._solverRotation.Rotate(Globals.Up);

                // Translate pelvis to make the head's position & rotation match with the head target
                TranslatePelvis(legs, _headDeltaPosition, _pelvisDeltaRotation, scale);

                FABRIKPass(animatedPelvisPos, rootUp, _positionWeight);

                // Bend the spine to look towards chest target rotation
                Bend(_bones, _pelvisIndex, _chestIndex, _chestTargetRotation, _chestRotationOffset, _chestClampWeight, false, _neckStiffness * _rotationWeight);

                if (_lod < 1 && _chestGoalWeight > 0f)
                {
                    var v1 = _bones[_chestIndex]._solverRotation.Rotate(_chestForward);
                    var v2 = _goalPositionChest - _bones[_chestIndex]._solverPosition;
                    Quaternion c = XRMath.RotationBetweenVectors(v1, v2) * _bones[_chestIndex]._solverRotation;
                    Bend(_bones, _pelvisIndex, _chestIndex, c, _chestRotationOffset, _chestClampWeight, false, _chestGoalWeight * _rotationWeight);
                }

                InverseTranslateToHead(legs, false, false, Vector3.Zero, _positionWeight);

                if (_lod < 1)
                    FABRIKPass(animatedPelvisPos, rootUp, _positionWeight);

                Bend(_bones, _neckIndex, _headIndex, _headRotation, _headClampWeight, true, _rotationWeight);

                SolvePelvis();
            }

            private void FABRIKPass(Vector3 animatedPelvisPos, Vector3 rootUp, float weight)
            {
                Vector3 startPos = Vector3.Lerp(Hips._solverPosition, animatedPelvisPos, _maintainPelvisPosition) + _pelvisPositionOffset;// - chestPositionOffset;
                Vector3 endPos = _headPosition - _chestPositionOffset;
                //Vector3 startOffset = rootUp * (bones[bones.Length - 1].solverPosition - bones[0].solverPosition).magnitude;
                Vector3 startOffset = Vector3.Zero;// (bones[bones.Length - 1].solverPosition - bones[0].solverPosition) * weight;

                float dist = Vector3.Distance(_bones[0]._solverPosition, _bones[_bones.Length - 1]._solverPosition);

                VirtualBone.SolveFABRIK(_bones, startPos, endPos, weight, 1f, 1, dist, startOffset);
            }

            private void SolvePelvis()
            {
                // Pelvis target
                if (_pelvisPositionWeight > 0f)
                {
                    Quaternion headSolverRotation = Head._solverRotation;

                    Vector3 delta = (IKPositionPelvis + _pelvisPositionOffset - Hips._solverPosition) * _pelvisPositionWeight;
                    foreach (VirtualBone bone in _bones)
                        bone._solverPosition += delta;

                    Vector3 bendNormal = AnchorRotation.Rotate(Globals.Right);

                    if (_hasChest && _hasNeck)
                    {
                        VirtualBone.SolveTrigonometric(_bones, _spineIndex, _chestIndex, _headIndex, _headPosition, bendNormal, _pelvisPositionWeight * 0.9f);
                        VirtualBone.SolveTrigonometric(_bones, _chestIndex, _neckIndex, _headIndex, _headPosition, bendNormal, _pelvisPositionWeight);
                    }
                    else if (_hasChest && !_hasNeck)
                        VirtualBone.SolveTrigonometric(_bones, _spineIndex, _chestIndex, _headIndex, _headPosition, bendNormal, _pelvisPositionWeight);
                    else if (!_hasChest && _hasNeck)
                        VirtualBone.SolveTrigonometric(_bones, _spineIndex, _neckIndex, _headIndex, _headPosition, bendNormal, _pelvisPositionWeight);
                    else if (!_hasNeck && !_hasChest)
                        VirtualBone.SolveTrigonometric(_bones, _pelvisIndex, _spineIndex, _headIndex, _headPosition, bendNormal, _pelvisPositionWeight);

                    Head._solverRotation = headSolverRotation;
                }

                /* FIK v 1.9 - pelvis rotation to pelvis target was not working right
                // Pelvis target
                if (pelvisPositionWeight > 0f) {
                    Quaternion headSolverRotation = head.solverRotation;

                    Vector3 delta = ((IKPositionPelvis + pelvisPositionOffset) - pelvis.solverPosition) * pelvisPositionWeight;
                    foreach (VirtualBone bone in bones) bone.solverPosition += delta;

                    Vector3 bendNormal = anchorRotation * Vector3.right;

                    if (hasChest && hasNeck) {
                        VirtualBone.SolveTrigonometric(bones, pelvisIndex, spineIndex, headIndex, headPosition, bendNormal, pelvisPositionWeight * 0.6f);
                        VirtualBone.SolveTrigonometric(bones, pelvisIndex, chestIndex, headIndex, headPosition, bendNormal, pelvisPositionWeight * 0.6f);
                        VirtualBone.SolveTrigonometric(bones, pelvisIndex, neckIndex, headIndex, headPosition, bendNormal, pelvisPositionWeight * 1f);
                    } else if (hasChest && !hasNeck) {
                        VirtualBone.SolveTrigonometric(bones, pelvisIndex, spineIndex, headIndex, headPosition, bendNormal, pelvisPositionWeight * 0.75f);
                        VirtualBone.SolveTrigonometric(bones, pelvisIndex, chestIndex, headIndex, headPosition, bendNormal, pelvisPositionWeight * 1f);
                    }
                    else if (!hasChest && hasNeck) {
                        VirtualBone.SolveTrigonometric(bones, pelvisIndex, spineIndex, headIndex, headPosition, bendNormal, pelvisPositionWeight * 0.75f);
                        VirtualBone.SolveTrigonometric(bones, pelvisIndex, neckIndex, headIndex, headPosition, bendNormal, pelvisPositionWeight * 1f);
                    } else if (!hasNeck && !hasChest) {
                        VirtualBone.SolveTrigonometric(bones, pelvisIndex, spineIndex, headIndex, headPosition, bendNormal, pelvisPositionWeight);
                    }

                    head.solverRotation = headSolverRotation;
                }
                */
            }

            public override void Write(ref Vector3[] solvedPositions, ref Quaternion[] solvedRotations)
            {
                // Pelvis
                solvedPositions[_index] = _bones[0]._solverPosition;
                solvedRotations[_index] = _bones[0]._solverRotation;

                // Spine
                solvedRotations[_index + 1] = _bones[1]._solverRotation;

                // Chest
                if (_hasChest)
                    solvedRotations[_index + 2] = _bones[_chestIndex]._solverRotation;

                // Neck
                if (_hasNeck)
                    solvedRotations[_index + 3] = _bones[_neckIndex]._solverRotation;

                // Head
                solvedRotations[_index + 4] = _bones[_headIndex]._solverRotation;
            }

            public override void ResetOffsets()
            {
                // Reset offsets to zero
                _pelvisPositionOffset = Vector3.Zero;
                _chestPositionOffset = Vector3.Zero;
                _headPositionOffset = Vector3.Zero;
                _pelvisRotationOffset = Quaternion.Identity;
                _chestRotationOffset = Quaternion.Identity;
                _headRotationOffset = Quaternion.Identity;
            }

            private void AdjustChestByHands(ref Quaternion chestTargetRotation, Arm[] arms)
            {
                if (_lod > 0)
                    return;

                Quaternion h = Quaternion.Inverse(AnchorRotation);

                Vector3 pLeft = h.Rotate(arms[0].Position - _headPosition) / _sizeMlp;
                Vector3 pRight = h.Rotate(arms[1].Position - _headPosition) / _sizeMlp;

                Vector3 c = Globals.Forward;
                c.X += pLeft.X * MathF.Abs(pLeft.X);
                c.X += pLeft.Z * MathF.Abs(pLeft.Z);
                c.X += pRight.X * MathF.Abs(pRight.X);
                c.X -= pRight.Z * MathF.Abs(pRight.Z);
                c.X *= 5f * _rotateChestByHands;

                float angle = MathF.Atan2(c.X, c.Z);
                Quaternion q = Quaternion.CreateFromAxisAngle(_rootRotation.Rotate(Globals.Up), angle);

                chestTargetRotation = q * chestTargetRotation;

                Vector3 t = Globals.Up;
                t.X += pLeft.Y;
                t.X -= pRight.Y;
                t.X *= 0.5f * _rotateChestByHands;

                angle = MathF.Atan2(t.X, t.Y);
                q = Quaternion.CreateFromAxisAngle(_rootRotation.Rotate(Globals.Backward), angle);

                chestTargetRotation = q * chestTargetRotation;
            }

            // Move the pelvis so that the head would remain fixed to the anchor
            public void InverseTranslateToHead(Leg[] legs, bool limited, bool useCurrentLegMag, Vector3 offset, float w)
            {
                Vector3 delta = (_headPosition + offset - Head._solverPosition) * w;// * (1f - pelvisPositionWeight); This makes the head lose its target when pelvisPositionWeight is between 0 and 1.

                Vector3 p = Hips._solverPosition + delta;
                MovePosition(limited ? LimitPelvisPosition(legs, p, useCurrentLegMag) : p);
            }

            // Move and rotate the pelvis
            private void TranslatePelvis(Leg[] legs, Vector3 deltaPosition, Quaternion deltaRotation, float scale)
            {
                // Rotation
                Vector3 p = Head._solverPosition;

                deltaRotation = XRMath.ClampRotation(deltaRotation, _chestClampWeight, 2);

                Quaternion r = Quaternion.Slerp(Quaternion.Identity, deltaRotation, _bodyRotStiffness * _rotationWeight);
                r = Quaternion.Slerp(r, XRMath.FromToRotation(Hips._solverRotation, IKRotationPelvis), _pelvisRotationWeight);
                VirtualBone.RotateAroundPoint(_bones, 0, Hips._solverPosition, _pelvisRotationOffset * r);

                deltaPosition -= Head._solverPosition - p;

                // Position
                // Move the body back when head is moving down
                Vector3 m = _rootRotation.Rotate(Globals.Forward);
                float deltaY = XRMath.ExtractVertical(deltaPosition, _rootRotation.Rotate(Globals.Up), 1f).Length();
                if (scale > 0f) deltaY /= scale;
                float backOffset = deltaY * -_moveBodyBackWhenCrouching * _headHeight;
                deltaPosition += m * backOffset;

                MovePosition(LimitPelvisPosition(legs, Hips._solverPosition + deltaPosition * _bodyPosStiffness * _positionWeight, false));
            }

            // Limit the position of the pelvis so that the feet/toes would remain fixed
            private Vector3 LimitPelvisPosition(Leg[] legs, Vector3 pelvisPosition, bool useCurrentLegMag, int it = 2)
            {
                if (!_hasLegs) return pelvisPosition;

                // Cache leg current mag
                if (useCurrentLegMag)
                {
                    foreach (Leg leg in legs)
                    {
                        leg._currentMag = MathF.Max(Vector3.Distance(leg.Thigh._solverPosition, leg.LastBone._solverPosition), leg._currentMag);
                    }
                }

                // Solve a 3-point constraint
                for (int i = 0; i < it; i++)
                {
                    foreach (Leg leg in legs)
                    {
                        Vector3 delta = pelvisPosition - Hips._solverPosition;
                        Vector3 wantedThighPos = leg.Thigh._solverPosition + delta;
                        Vector3 toWantedThighPos = wantedThighPos - leg.Position;
                        float maxMag = useCurrentLegMag ? leg._currentMag : leg.Length;
                        Vector3 limitedThighPos = leg.Position + toWantedThighPos.ClampMagnitude(maxMag);
                        pelvisPosition += limitedThighPos - wantedThighPos;

                        // TODO rotate pelvis to accommodate, rotate the spine back then
                    }
                }

                return pelvisPosition;
            }

            // Bending the spine to the head effector
            private static void Bend(VirtualBone[] bones, int firstIndex, int lastIndex, Quaternion targetRotation, float clampWeight, bool uniformWeight, float w)
            {
                if (w <= 0f)
                    return;

                if (bones.Length == 0)
                    return;

                int bonesCount = (lastIndex + 1) - firstIndex;
                if (bonesCount < 1)
                    return;

                Quaternion r = XRMath.FromToRotation(bones[lastIndex]._solverRotation, targetRotation);
                r = XRMath.ClampRotation(r, clampWeight, 2);

                float step = uniformWeight ? 1f / bonesCount : 0f;

                for (int i = firstIndex; i < lastIndex + 1; i++)
                {
                    if (!uniformWeight)
                        step = (((i - firstIndex) + 1) / (float)bonesCount).Clamp(0f, 1f);
                    VirtualBone.RotateAroundPoint(bones, i, bones[i]._solverPosition, Quaternion.Slerp(Quaternion.Identity, r, step * w));
                }
            }

            // Bending the spine to the head effector
            private static void Bend(VirtualBone[] bones, int firstIndex, int lastIndex, Quaternion targetRotation, Quaternion rotationOffset, float clampWeight, bool uniformWeight, float w)
            {
                if (w <= 0f) return;
                if (bones.Length == 0) return;
                int bonesCount = (lastIndex + 1) - firstIndex;
                if (bonesCount < 1) return;

                Quaternion r = XRMath.FromToRotation(bones[lastIndex]._solverRotation, targetRotation);
                r = XRMath.ClampRotation(r, clampWeight, 2);
                float step = uniformWeight ? 1f / bonesCount : 0f;

                for (int i = firstIndex; i < lastIndex + 1; i++)
                {

                    if (!uniformWeight)
                    {
                        if (bonesCount == 1)
                        {
                            step = 1f;
                        }
                        else if (bonesCount == 2)
                        {
                            step = i == 0 ? 0.2f : 0.8f;
                        }
                        else if (bonesCount == 3)
                        {
                            if (i == 0) step = 0.15f;
                            else if (i == 1) step = 0.4f;
                            else step = 0.45f;
                        }
                        else if (bonesCount > 3)
                        {
                            step = 1f / bonesCount;
                        }
                    }

                    //if (!uniformWeight)
                    //  step = Mathf.Clamp(((i - firstIndex) + 1) / bonesCount, 0, 1f);
                    VirtualBone.RotateAroundPoint(bones, i, bones[i]._solverPosition, Quaternion.Slerp(Quaternion.Slerp(Quaternion.Identity, rotationOffset, step), r, step * w));
                }
            }
        }
    }
}
