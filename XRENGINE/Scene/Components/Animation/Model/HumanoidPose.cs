using XREngine.Data.Core;
using XREngine.Scene.Components.Animation;

namespace XREngine.Animation
{
    /// <summary>
    /// Represents a pose of a humanoid character.
    /// </summary>
    public class HumanoidPose : XRBase
    {
        public Dictionary<string, BoneFrame> BoneFrames { get; set; } = [];

        public void AddBoneFrame(BoneFrame anim)
        {
            if (!BoneFrames.TryAdd(anim._name, anim))
                BoneFrames[anim._name] = anim;
        }
        public void RemoveBoneFrame(string boneName)
        {
            BoneFrames.Remove(boneName);
        }
        public void UpdateSkeleton(HumanoidComponent skeleton)
        {
            foreach (BoneFrame b in BoneFrames.Values)
                b.UpdateSkeleton(skeleton);
        }
        /// <summary>
        /// Returns all bone names that exist in this and the other.
        /// </summary>
        public IEnumerable<string> BoneNamesUnion(HumanoidPose other)
        {
            string[] theseNames = new string[BoneFrames.Keys.Count];
            BoneFrames.Keys.CopyTo(theseNames, 0);
            string[] thoseNames = new string[other.BoneFrames.Keys.Count];
            other.BoneFrames.Keys.CopyTo(thoseNames, 0);
            return theseNames.Intersect(thoseNames);
        }
        /// <summary>
        /// Returns all bone names that exist in this and the other.
        /// </summary>
        public IEnumerable<string> BoneNamesUnion(SkeletalAnimation other)
        {
            string[] theseNames = new string[BoneFrames.Keys.Count];
            BoneFrames.Keys.CopyTo(theseNames, 0);
            string[] thoseNames = new string[other.BoneAnimations.Keys.Count];
            other.BoneAnimations.Keys.CopyTo(thoseNames, 0);
            return theseNames.Intersect(thoseNames);
        }
        public HumanoidPose BlendedWith(HumanoidPose? other, float otherWeight)
        {
            if (other is null)
                return this;

            HumanoidPose blendedFrame = new();
            var union = BoneNamesUnion(other);
            foreach (string name in union)
            {
                if (BoneFrames.ContainsKey(name))
                {
                    if (other.BoneFrames.TryGetValue(name, out BoneFrame? value))
                        blendedFrame.AddBoneFrame(value.BlendedWith(value, otherWeight));
                    else
                        blendedFrame.AddBoneFrame(BoneFrames[name].BlendedWith(null, otherWeight));
                }
                else
                {
                    if (other.BoneFrames.TryGetValue(name, out BoneFrame? value))
                        blendedFrame.AddBoneFrame(value.BlendedWith(null, 1.0f - otherWeight));
                }
            }
            return blendedFrame;
        }
        public HumanoidPose BlendedWith(SkeletalAnimation other, float frameIndex, float otherWeight)
        {
            HumanoidPose blendedFrame = new();
            foreach (string name in BoneNamesUnion(other))
            {
                if (BoneFrames.ContainsKey(name))
                {
                    if (other.BoneAnimations.TryGetValue(name, out BoneAnimation? value))
                        blendedFrame.AddBoneFrame(BoneFrames[name].BlendedWith(value, frameIndex, otherWeight));
                    else
                        blendedFrame.AddBoneFrame(BoneFrames[name].BlendedWith(null, otherWeight));
                }
                else
                {
                    if (other.BoneAnimations.TryGetValue(name, out BoneAnimation? value))
                        blendedFrame.AddBoneFrame(value.BlendedWith(frameIndex, null, 1.0f - otherWeight));
                }
            }
            return blendedFrame;
        }

        public void BlendWith(HumanoidPose? other, float otherWeight)
        {
            if (other is null)
                return;
            foreach (string name in BoneNamesUnion(other))
            {
                if (BoneFrames.ContainsKey(name))
                {
                    if (other.BoneFrames.TryGetValue(name, out BoneFrame? value))
                        value.BlendWith(value, otherWeight);
                    else
                        BoneFrames[name].BlendWith(null, otherWeight);
                }
                else
                {
                    if (other.BoneFrames.TryGetValue(name, out BoneFrame? value))
                        AddBoneFrame(value.BlendedWith(null, 1.0f - otherWeight));

                    //else, neither has a bone with this name, ignore it
                }
            }
        }
        public void BlendWith(SkeletalAnimation other, float frameIndex, float otherWeight)
        {
            foreach (string name in BoneNamesUnion(other))
            {
                if (BoneFrames.ContainsKey(name))
                {
                    if (other.BoneAnimations.TryGetValue(name, out BoneAnimation? value))
                        BoneFrames[name].BlendedWith(value, frameIndex, otherWeight);
                    else
                        BoneFrames[name].BlendedWith(null, otherWeight);
                }
                else
                {
                    if (other.BoneAnimations.TryGetValue(name, out BoneAnimation? value))
                        value.BlendedWith(frameIndex, null, 1.0f - otherWeight);
                }
            }
        }
    }
}
