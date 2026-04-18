using XREngine.Extensions;
using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Data.Rendering
{
    /// <summary>
    /// Describes how any number of bones affect a vertex when transformed.
    /// Contains no actual transformation information.
    /// </summary>
    public class VertexWeightGroup : XRBase
    {
        //TODO: use event dictionary. bone index -> weight

        private int _weightLimit = -1;
        private Dictionary<int, float> _weights;
        private List<int>? _lockedIndices = null;

        /// <summary>
        /// The maximum number of weights that can be applied to a vertex.
        /// A value of 0 or less means no limit.
        /// </summary>
        public int WeightLimit
        {
            get => _weightLimit;
            set => SetField(ref _weightLimit, value);
        }

        /// <summary>
        /// The list of bone weights that affect the vertex.
        /// Key is bone index (into UtilizedBones list), value is the weight.
        /// </summary>
        public Dictionary<int, float> Weights
        {
            get => _weights;
            set => SetField(ref _weights, value);
        }

        /// <summary>
        /// The indices of the weights that are locked and cannot be modified.
        /// </summary>
        public List<int>? Locked
        {
            get => _lockedIndices;
            set => SetField(ref _lockedIndices, value);
        }

        public VertexWeightGroup(int boneIndex, int weightLimit = -1)
        {
            _weightLimit = weightLimit;
            _weights = new EventDictionary<int, float> { { boneIndex, 1.0f } };
        }

        public VertexWeightGroup(IDictionary<int, float> weights, int weightLimit = -1)
        {
            _weightLimit = weightLimit;
            _weights = new EventDictionary<int, float>(weights);
        }

        /// <summary>
        /// Locks a weight at the specified index so it cannot be modified.
        /// </summary>
        /// <param name="boneIndex"></param>
        public void Lock(int boneIndex)
        {
            _lockedIndices ??= [];
            _lockedIndices.Add(boneIndex);
        }

        /// <summary>
        /// Unlocks a weight at the specified index so it can be modified.
        /// </summary>
        /// <param name="boneIndex"></param>
        public void Unlock(int boneIndex)
        {
            if (_lockedIndices is null)
                return;

            _lockedIndices.Remove(boneIndex);
            if (_lockedIndices.Count == 0)
                _lockedIndices = null;
        }

        /// <summary>
        /// Checks if a weight at the specified index is locked.
        /// </summary>
        /// <param name="boneIndex"></param>
        /// <returns></returns>
        public bool IsLocked(int boneIndex)
            => _lockedIndices?.Contains(boneIndex) ?? false;

        /// <summary>
        /// Reduces the number of weights down to the weight limit by removing the smallest weights and normalizing the rest.
        /// </summary>
        public void Optimize()
        {
            RemoveWeights(false);
            Normalize();
        }

        private bool RemoveWeights(bool normalizeHere = true)
        {
            if (WeightLimit < 0 || Weights.Count <= WeightLimit)
                return false;

            int[] keysToRemove = new int[Weights.Count - WeightLimit];
            keysToRemove.Fill(-1);

            var boneIndices = Weights.Keys.ToList();
            //Collect the indices of the smallest weights in order of most to least small
            for (int i = 0; i < keysToRemove.Length; ++i)
            {
                float minWeight = float.MaxValue;
                int minIndex = -1;
                for (int j = 0; j < boneIndices.Count; ++j)
                {
                    var boneIndex = boneIndices[j];

                    if (IsLocked(boneIndex))
                        continue;

                    if (Weights[boneIndex] <= minWeight && !keysToRemove.Contains(j))
                    {
                        minWeight = Weights[boneIndex];
                        minIndex = j;
                    }
                }
                keysToRemove[i] = minIndex;
            }

            foreach (int index in keysToRemove)
                Weights.Remove(boneIndices[index]);

            if (normalizeHere)
                Normalize();

            return true;
        }

        public static bool Optimize<T>(IDictionary<T, (float weight, Matrix4x4 invBindMatrix)> weights, int maxWeightCount) where T : notnull
        {
            var boneIndices = weights.Keys.ToList();
            for (int i = 0; i < boneIndices.Count; i++)
            {
                T? boneIndex = boneIndices[i];
                var pair = weights[boneIndex];
                float weight = MathF.Round(pair.weight, 3);
                if (weight < 0.001f)
                {
                    weights.Remove(boneIndex);
                    boneIndices.RemoveAt(i);
                }
                else
                    weights[boneIndex] = (weight, pair.invBindMatrix);
            }

            if (maxWeightCount < weights.Count && maxWeightCount >= 0)
            {
                int[] keysToRemove = new int[weights.Count - maxWeightCount];
                keysToRemove.Fill(-1);

                //Collect the indices of the smallest weights in order of most to least small
                for (int i = 0; i < keysToRemove.Length; ++i)
                {
                    float minWeight = float.MaxValue;
                    int minIndex = -1;
                    for (int j = 0; j < boneIndices.Count; ++j)
                    {
                        var boneIndex = boneIndices[j];
                        var (weight, _) = weights[boneIndex];
                        if (weight <= minWeight && !keysToRemove.Contains(j))
                        {
                            minWeight = weight;
                            minIndex = j;
                        }
                    }
                    keysToRemove[i] = minIndex;
                }

                foreach (int index in keysToRemove)
                    weights.Remove(boneIndices[index]);
            }

            Normalize(weights);

            return true;
        }

        public static void Normalize<T>(IDictionary<T, (float weight, Matrix4x4 invBindMatrix)> weights, int weightDecimalPlaces = 6) where T : notnull
        {
            if (weights.Count == 0)
                return;

            float denom = 0.0f, num = 1.0f;
            foreach (T boneIndex in weights.Keys)
                denom += weights[boneIndex].weight;
            
            //Don't do anything if all weights are locked
            if (denom <= 0.0f || num <= 0.0f)
                return;

            foreach (T boneIndex in weights.Keys)
            {
                var (weight, invBindMatrix) = weights[boneIndex];
                weights[boneIndex] = (MathF.Round(weight / denom * num, weightDecimalPlaces), invBindMatrix);
            }
            
            //if (weights.Any(w => float.IsNaN(w.Value) || float.IsInfinity(w.Value)))
            //    throw new InvalidOperationException("Vertex weight normalization resulted in NaN or Infinity.");

        }

        /// <summary>
        /// Makes sure all weights add up to 1.0f.
        /// Does not modify any locked weights.
        /// </summary>
        public void Normalize(int weightDecimalPlaces = 6)
        {
            float denom = 0.0f, num = 1.0f;
            foreach (int boneIndex in Weights.Keys)
            {
                float bw = Weights[boneIndex];
                if (IsLocked(boneIndex))
                    num -= bw;
                else
                    denom += bw;
            }

            //Don't do anything if all weights are locked
            if (denom <= 0.0f || num <= 0.0f)
                return;

            foreach (int boneIndex in Weights.Keys)
            {
                //Only normalize unlocked weights used in the calculation
                if (IsLocked(boneIndex))
                    continue;

                Weights[boneIndex] = MathF.Round(Weights[boneIndex] / denom * num, weightDecimalPlaces);
            }
        }

        public static bool operator ==(VertexWeightGroup left, VertexWeightGroup right)
        {
            if (left is null)
                return right is null;
            return left.Equals(right);
        }

        public static bool operator !=(VertexWeightGroup left, VertexWeightGroup right)
        {
            if (left is null)
                return right is not null;
            return !left.Equals(right);
        }

        public override bool Equals(object? obj)
        {
            if (obj is not VertexWeightGroup other ||
                Weights.Count != other.Weights.Count)
                return false;

            for (int i = 0; i < Weights.Count; ++i)
                if (Weights[i] != other.Weights[i])
                    return false;

            return true;
        }

        public override int GetHashCode()
            => Weights.GetHashCode();
    }
}