using System.Collections;
using System.Numerics;
using XREngine.Data.Core;
using static XREngine.Components.Animation.IKSolverVR.SolverTransforms;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
public partial class IKSolverVR
    {
        public partial class SolverTransforms(
            Transform? root,
            Transform? hips,
            Transform? spine,
            Transform? chest,
            Transform? neck,
            Transform? head,
            Transform? leftShoulder,
            Transform? leftArm,
            Transform? leftElbow,
            Transform? leftWrist,
            Transform? rightShoulder,
            Transform? rightArm,
            Transform? rightElbow,
            Transform? rightWrist,
            Transform? leftLeg,
            Transform? leftKnee,
            Transform? leftFoot,
            Transform? leftToes,
            Transform? rightLeg,
            Transform? rightKnee,
            Transform? rightFoot,
            Transform? rightToes) : XRBase, IEnumerable<TransformPoses>
        {
            public class SolverTransformsSide(
                Transform? shoulder,
                Transform? arm,
                Transform? elbow,
                Transform? wrist,
                Transform? leg,
                Transform? knee,
                Transform? foot,
                Transform? toes)
            {
                public SolverTransformsArm Arm { get; } = new SolverTransformsArm(shoulder, arm, elbow, wrist);
                public SolverTransformsLeg Leg { get; } = new SolverTransformsLeg(leg, knee, foot, toes);
            }
            public class SolverTransformsLeg(
                Transform? leg,
                Transform? knee,
                Transform? foot,
                Transform? toes)
            {
                public TransformPoses Leg { get; } = leg;
                public TransformPoses Knee { get; } = new(knee, false, true);
                public TransformPoses Foot { get; } = new(foot, false, true);
                public TransformPoses Toes { get; } = new(toes, false, true);
            }
            public class SolverTransformsArm(
                Transform? shoulder,
                Transform? arm,
                Transform? elbow,
                Transform? wrist)
            {
                public TransformPoses Shoulder { get; } = shoulder;
                public TransformPoses Arm { get; } = arm;
                public TransformPoses Elbow { get; } = new(elbow, false, true);
                public TransformPoses Wrist { get; } = new(wrist, false, true);
            }

            public TransformPoses Root { get; } = new(root, true, false);
            public TransformPoses Hips { get; } = new(hips, true, false);
            public TransformPoses Spine { get; } = spine;
            public TransformPoses Chest { get; } = chest;
            public TransformPoses Neck { get; } = neck;
            public TransformPoses Head { get; } = head;
            public SolverTransformsSide Left { get; } = new SolverTransformsSide(
                    leftShoulder,
                    leftArm,
                    leftElbow,
                    leftWrist,
                    leftLeg,
                    leftKnee,
                    leftFoot,
                    leftToes);
            public SolverTransformsSide Right { get; } = new SolverTransformsSide(
                    rightShoulder,
                    rightArm,
                    rightElbow,
                    rightWrist,
                    rightLeg,
                    rightKnee,
                    rightFoot,
                    rightToes);

            public bool HasChest => Chest.Transform is not null;
            public bool HasNeck => Neck.Transform is not null;
            public bool HasShoulders => Left.Arm.Shoulder.Transform is not null && Right.Arm.Shoulder.Transform is not null;
            public bool HasToes => Left.Leg.Toes.Transform is not null && Right.Leg.Toes.Transform is not null;
            public bool HasLegs => Left.Leg.Leg.Transform is not null && Right.Leg.Leg.Transform is not null;
            public bool HasArms => Left.Arm.Arm.Transform is not null && Right.Arm.Arm.Transform is not null;

            public static int Count => 22; // Total number of transforms

            public enum ESolverTransformIndex
            {
                Root,
                Hips,
                Spine,
                Chest,
                Neck,
                Head,
                LeftShoulder,
                LeftArm,
                LeftElbow,
                LeftWrist,
                RightShoulder,
                RightArm,
                RightElbow,
                RightWrist,
                LeftLeg,
                LeftKnee,
                LeftFoot,
                LeftToes,
                RightLeg,
                RightKnee,
                RightFoot,
                RightToes
            }

            public TransformPoses this[ESolverTransformIndex index] => index switch
            {
                ESolverTransformIndex.Root => Root,
                ESolverTransformIndex.Hips => Hips,
                ESolverTransformIndex.Spine => Spine,
                ESolverTransformIndex.Chest => Chest,
                ESolverTransformIndex.Neck => Neck,
                ESolverTransformIndex.Head => Head,
                ESolverTransformIndex.LeftShoulder => Left.Arm.Shoulder,
                ESolverTransformIndex.LeftArm => Left.Arm.Arm,
                ESolverTransformIndex.LeftElbow => Left.Arm.Elbow,
                ESolverTransformIndex.LeftWrist => Left.Arm.Wrist,
                ESolverTransformIndex.RightShoulder => Right.Arm.Shoulder,
                ESolverTransformIndex.RightArm => Right.Arm.Arm,
                ESolverTransformIndex.RightElbow => Right.Arm.Elbow,
                ESolverTransformIndex.RightWrist => Right.Arm.Wrist,
                ESolverTransformIndex.LeftLeg => Left.Leg.Leg,
                ESolverTransformIndex.LeftKnee => Left.Leg.Knee,
                ESolverTransformIndex.LeftFoot => Left.Leg.Foot,
                ESolverTransformIndex.LeftToes => Left.Leg.Toes,
                ESolverTransformIndex.RightLeg => Right.Leg.Leg,
                ESolverTransformIndex.RightKnee => Right.Leg.Knee,
                ESolverTransformIndex.RightFoot => Right.Leg.Foot,
                ESolverTransformIndex.RightToes => Right.Leg.Toes,
                _ => throw new ArgumentOutOfRangeException(nameof(index), "Invalid index for SolverTransforms."),
            };
            public TransformPoses this[int i]
            {
                get => i switch
                {
                    0 => Root,
                    1 => Hips,
                    2 => Spine,
                    3 => Chest,
                    4 => Neck,
                    5 => Head,
                    6 => Left.Arm.Shoulder,
                    7 => Left.Arm.Arm,
                    8 => Left.Arm.Elbow,
                    9 => Left.Arm.Wrist,
                    10 => Right.Arm.Shoulder,
                    11 => Right.Arm.Arm,
                    12 => Right.Arm.Elbow,
                    13 => Right.Arm.Wrist,
                    14 => Left.Leg.Leg,
                    15 => Left.Leg.Knee,
                    16 => Left.Leg.Foot,
                    17 => Left.Leg.Toes,
                    18 => Right.Leg.Leg,
                    19 => Right.Leg.Knee,
                    20 => Right.Leg.Foot,
                    21 => Right.Leg.Toes,
                    _ => throw new IndexOutOfRangeException("Invalid index for SolverTransforms."),
                };
            }

            public IEnumerator<TransformPoses> GetEnumerator()
            {
                yield return Root;
                yield return Hips;
                yield return Spine;
                yield return Chest;
                yield return Neck;
                yield return Head;
                yield return Left.Arm.Shoulder;
                yield return Left.Arm.Arm;
                yield return Left.Arm.Elbow;
                yield return Left.Arm.Wrist;
                yield return Right.Arm.Shoulder;
                yield return Right.Arm.Arm;
                yield return Right.Arm.Elbow;
                yield return Right.Arm.Wrist;
                yield return Left.Leg.Leg;
                yield return Left.Leg.Knee;
                yield return Left.Leg.Foot;
                yield return Left.Leg.Toes;
                yield return Right.Leg.Leg;
                yield return Right.Leg.Knee;
                yield return Right.Leg.Foot;
                yield return Right.Leg.Toes;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public void StoreLocalState()
            {
                foreach (TransformPoses pose in this)
                {
                    pose.Transform?.RecalculateMatrices(true);
                    pose.GetDefaultLocal();
                    //pose.ResetSolvedToDefault();
                }
            }

            public bool TryGetTransformPoses(ESolverTransformIndex iIndex, out TransformPoses? tfm)
            {
                if (iIndex < 0 || (int)iIndex >= Count)
                {
                    tfm = null;
                    return false;
                }
                tfm = this[iIndex];
                return tfm is not null;
            }
        }
    }
}
