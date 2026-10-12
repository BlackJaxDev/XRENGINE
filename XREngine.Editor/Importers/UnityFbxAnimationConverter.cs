using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Fbx;

namespace XREngine.Scene.Importers;

/// <summary>
/// Converts source model motion into the final Unity model bind chart.
/// All sampling and allocation occur before scene publication.
/// </summary>
internal sealed class UnityFbxAnimationConverter
{
    private readonly FbxSemanticDocument _source;
    private readonly SceneNode _root;
    private readonly CancellationToken _cancellation;
    private readonly SceneNode[] _targets;
    private readonly int[] _order;
    private readonly int[] _targetParents;
    private readonly Matrix4x4[] _restCorrections;
    private readonly Matrix4x4 _chart;

    public IReadOnlyList<SceneNode> Targets => _targets;
    public List<(SceneNode Target, string Channel)> MorphTargets { get; } = [];

    public UnityFbxAnimationConverter(FbxSemanticDocument source, SceneNode root,
        bool preserveHierarchy, CancellationToken cancellation)
    {
        _source = source;
        _root = root;
        _cancellation = cancellation;
        IReadOnlyList<FbxIntermediateNode> nodes = source.IntermediateScene.Nodes;
        _targets = new SceneNode[nodes.Count];
        _targetParents = new int[nodes.Count];
        _restCorrections = new Matrix4x4[nodes.Count];
        _chart = (source.GlobalSettings!.AxisSystem.UpAxis.AxisIndex == 2
            ? Matrix4x4.CreateRotationX(-MathF.PI * 0.5f) : Matrix4x4.Identity)
            * Matrix4x4.CreateScale(-1.0f, 1.0f, -1.0f);
        var order = new List<int>(nodes.Count);
        int rootCount = nodes.Count(static node => node.ParentNodeIndex is null);
        for (int index = 0; index < nodes.Count; index++)
            if (nodes[index].ParentNodeIndex is null)
                Map(index, root, !preserveHierarchy && rootCount == 1, order);
        if (order.Count != nodes.Count)
            throw new InvalidDataException("Source animation hierarchy contains unreachable nodes.");
        _order = order.ToArray();
        var targetIndices = new Dictionary<SceneNode, int>();
        foreach (int index in _order)
            if (!targetIndices.TryAdd(_targets[index], index))
                throw new InvalidDataException($"Source animation nodes have an ambiguous target '{_targets[index].Name}'.");
        var sourceRest = new Matrix4x4[nodes.Count];
        foreach (int index in _order)
        {
            FbxIntermediateNode node = nodes[index];
            sourceRest[index] = node.LocalTransform * (node.ParentNodeIndex is { } parent
                ? sourceRest[parent] : Matrix4x4.Identity);
            Matrix4x4 canonicalRest = sourceRest[index] * _chart;
            _restCorrections[index] = _targets[index].Transform.WorldMatrix * Inverse(canonicalRest, node.Name);
            _targetParents[index] = _targets[index].Parent is { } targetParent &&
                targetIndices.TryGetValue(targetParent, out int parentIndex) ? parentIndex : -1;
        }
    }

    public AnimationClip Convert(FbxAnimationStackBinding stack)
    {
        _cancellation.ThrowIfCancellationRequested();
        var bindings = stack.NodeAnimations.ToDictionary(static binding => binding.ModelObjectId);
        SortedSet<long> keyTimes = [.. stack.EnumerateKeyTimes()];
        if (keyTimes.Count == 0)
            throw new InvalidDataException($"Animation '{stack.Name}' has no keys.");
        long start = keyTimes.Min;
        long end = keyTimes.Max;
        double seconds = (end - (double)start) / FbxAnimationTime.TicksPerSecond;
        if (!double.IsFinite(seconds) || seconds < 0.0d || seconds > 3600.0d)
            throw new InvalidDataException($"Animation '{stack.Name}' exceeds the supported one-hour duration.");
        float duration = (float)seconds;
        long step = FbxAnimationTime.TicksPerSecond / 120;
        for (long time = start; time < end;)
        {
            _cancellation.ThrowIfCancellationRequested();
            keyTimes.Add(time);
            if (end - time <= step)
                break;
            time += step;
        }
        if (keyTimes.Count > 16384 || (long)keyTimes.Count * _targets.Length > 2000000)
            throw new InvalidDataException($"Animation '{stack.Name}' exceeds the supported sample count.");

        var samples = new SortedDictionary<long, Matrix4x4[]>();
        long[] initialTimes = keyTimes.ToArray();
        foreach (long time in initialTimes)
        {
            _cancellation.ThrowIfCancellationRequested();
            samples.Add(time, Sample(time, bindings));
        }
        for (int index = 1; index < initialTimes.Length; index++)
            Refine(initialTimes[index - 1], initialTimes[index], 0);

        float[] times = samples.Keys.Select(time => FbxAnimationTime.ToSeconds(time - start)).ToArray();
        var builder = new UnityModelAnimationBuilder(_root);
        foreach (int index in _order)
        {
            Matrix4x4 rest = _targets[index].Transform.LocalMatrix;
            Matrix4x4[] matrices = samples.Values.Select(sample => sample[index]).ToArray();
            if (!bindings.ContainsKey(_source.IntermediateScene.Nodes[index].ObjectId) &&
                matrices.All(matrix => Close(matrix, rest, 1e-5f)))
                continue;
            if (matrices.All(matrix => Close(matrix, matrices[0], 1e-5f)))
                builder.AddTransform(_targets[index], [0.0f], [matrices[0]], duration);
            else
                builder.AddTransform(_targets[index], times, matrices, duration);
        }
        AddBlendShapes(builder, stack, start, end, duration);
        return builder.Build(stack.Name, duration);

        void Refine(long first, long last, int depth)
        {
            _cancellation.ThrowIfCancellationRequested();
            long middle = first + (last - first) / 2;
            if (middle == first || middle == last)
                return;
            Matrix4x4[] actual = Sample(middle, bindings);
            Matrix4x4[] left = samples[first];
            Matrix4x4[] right = samples[last];
            // Matrix endpoints and midpoints alone can hide complete Euler turns.
            // Limit every source Euler-axis step before accepting quaternion sampling.
            bool accepted = !stack.NodeAnimations.Any(binding =>
                TurnsTooFar(binding.RotationX) || TurnsTooFar(binding.RotationY) || TurnsTooFar(binding.RotationZ));
            for (int index = 0; index < actual.Length && accepted; index++)
            {
                Matrix4x4.Decompose(left[index], out Vector3 sa, out Quaternion ra, out Vector3 ta);
                Matrix4x4.Decompose(right[index], out Vector3 sb, out Quaternion rb, out Vector3 tb);
                float fraction = (float)((middle - first) / (double)(last - first));
                Matrix4x4 interpolated = Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, fraction)) *
                    Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, fraction)) *
                    Matrix4x4.CreateTranslation(Vector3.Lerp(ta, tb, fraction));
                accepted = Close(actual[index], interpolated, 1e-4f);
            }
            if (accepted)
                return;
            if (depth >= 8 || samples.Count >= 16384 || (long)(samples.Count + 1) * _targets.Length > 2000000)
                throw new InvalidDataException($"Animation '{stack.Name}' cannot meet the TRS sampling tolerance.");
            samples.Add(middle, actual);
            Refine(first, middle, depth + 1);
            Refine(middle, last, depth + 1);

            bool TurnsTooFar(FbxScalarCurve? curve)
                => curve is { HasKeys: true } && MathF.Abs(curve.Evaluate(last) - curve.Evaluate(first)) > 90.0f;
        }
    }

    private void Map(int index, SceneNode parent, bool collapsedRoot, List<int> order)
    {
        FbxIntermediateNode node = _source.IntermediateScene.Nodes[index];
        SceneNode target = parent;
        if (!collapsedRoot)
        {
            SceneNode[] matches = parent.Transform.Children.Select(static child => child.SceneNode)
                .OfType<SceneNode>().Where(child => string.Equals(child.Name, node.Name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"Animation node '{node.Name}' has no unique child under '{parent.Name}'.");
            target = matches[0];
        }
        _targets[index] = target;
        order.Add(index);
        foreach (int child in node.ChildNodeIndices)
            Map(child, target, false, order);
    }

    private Matrix4x4[] Sample(long time, IReadOnlyDictionary<long, FbxNodeAnimationBinding> bindings)
    {
        var sourceWorlds = new Matrix4x4[_targets.Length];
        var targetWorlds = new Matrix4x4[_targets.Length];
        var localMatrices = new Matrix4x4[_targets.Length];
        foreach (int index in _order)
        {
            FbxIntermediateNode node = _source.IntermediateScene.Nodes[index];
            FbxTransformSemantics transform = node.TransformSemantics;
            Matrix4x4 local = node.LocalTransform;
            if (bindings.TryGetValue(node.ObjectId, out FbxNodeAnimationBinding? binding))
            {
                Vector3 translation = new(Value(binding.TranslationX, transform.LocalTranslation.X),
                    Value(binding.TranslationY, transform.LocalTranslation.Y), Value(binding.TranslationZ, transform.LocalTranslation.Z));
                Vector3 rotation = new(Value(binding.RotationX, transform.LocalRotationDegrees.X),
                    Value(binding.RotationY, transform.LocalRotationDegrees.Y), Value(binding.RotationZ, transform.LocalRotationDegrees.Z));
                Vector3 scale = new(Value(binding.ScaleX, transform.LocalScaling.X),
                    Value(binding.ScaleY, transform.LocalScaling.Y), Value(binding.ScaleZ, transform.LocalScaling.Z));
                local = Matrix4x4.CreateScale(scale) * FbxTransformSemantics.CreateRotationMatrix(rotation, transform.RotationOrder) *
                    Matrix4x4.CreateTranslation(translation);
            }
            sourceWorlds[index] = local * (node.ParentNodeIndex is { } parent ? sourceWorlds[parent] : Matrix4x4.Identity);
            // The correction maps the canonical source rest to the metadata rest.
            // Its right-side motion delta is identical for both geometry producers.
            targetWorlds[index] = _restCorrections[index] * sourceWorlds[index] * _chart;
            Matrix4x4 parentWorld = _targetParents[index] >= 0 ? targetWorlds[_targetParents[index]] :
                _targets[index].Parent?.Transform.WorldMatrix ?? Matrix4x4.Identity;
            Matrix4x4 targetLocal = targetWorlds[index] * Inverse(parentWorld, node.Name);
            if (!Matrix4x4.Decompose(targetLocal, out Vector3 s, out Quaternion r, out Vector3 t) ||
                !Close(targetLocal, Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t), 1e-4f))
                throw new InvalidDataException($"Animation node '{node.Name}' has a singular, sheared, or non-finite sample at {FbxAnimationTime.ToSeconds(time):R} seconds.");
            localMatrices[index] = targetLocal;
        }
        return localMatrices;

        float Value(FbxScalarCurve? curve, float fallback) => curve is { HasKeys: true } ? curve.Evaluate(time) : fallback;
    }

    private void AddBlendShapes(UnityModelAnimationBuilder builder, FbxAnimationStackBinding stack, long start, long end, float duration)
    {
        var nodeIndices = _source.IntermediateScene.Nodes.Select((node, index) => (node.ObjectId, index))
            .ToDictionary(static item => item.ObjectId, static item => item.index);
        var assignedChannels = new Dictionary<(SceneNode Target, string Name), long>();
        foreach (FbxBlendShapeAnimationBinding binding in stack.BlendShapeAnimations)
        {
            FbxIntermediateMesh mesh = _source.IntermediateScene.Meshes.Single(mesh => mesh.ObjectId == binding.GeometryObjectId);
            if (!(binding.FullWeight > 0.0f) || !float.IsFinite(binding.FullWeight))
                throw new InvalidDataException($"Blendshape '{binding.BlendShapeName}' has an invalid full weight.");
            foreach (long modelId in mesh.ModelObjectIds)
            {
                SceneNode target = _targets[nodeIndices[modelId]];
                ModelComponent? component = target.GetComponent<ModelComponent>();
                if (component?.Model is not { } model)
                    throw new InvalidDataException($"Blendshape animation target '{target.Name}' has no channel '{binding.BlendShapeName}'.");
                string channelName = ResolveBlendShapeName(model, binding);
                if (!assignedChannels.TryAdd((target, channelName), binding.ChannelObjectId))
                    throw new InvalidDataException($"Blendshape animation target '{target.Name}' has ambiguous channel '{channelName}'.");
                var curve = new PropAnimFloat(duration, false, true);
                curve.Keyframes.PreInfinityMode = EKeyframeInfinityMode.Clamp;
                curve.Keyframes.PostInfinityMode = EKeyframeInfinityMode.Clamp;
                // FBX curves hold their endpoint outside their own key range.
                SortedSet<long> times = [start, end, .. binding.WeightCurve.KeyTimes];
                foreach (long time in times)
                    curve.Keyframes.Add(new FloatKeyframe
                    {
                        Second = FbxAnimationTime.ToSeconds(time - start),
                        InValue = binding.WeightCurve.Evaluate(time) / binding.FullWeight,
                        OutValue = binding.WeightCurve.Evaluate(time) / binding.FullWeight,
                        InterpolationTypeIn = EVectorInterpType.Linear,
                        InterpolationTypeOut = EVectorInterpType.Linear,
                    });
                builder.AddBlendShape(target, channelName, curve);
                MorphTargets.Add((target, channelName));
            }
        }
    }

    private string ResolveBlendShapeName(XREngine.Rendering.Models.Model model, FbxBlendShapeAnimationBinding binding)
    {
        FbxIntermediateBlendShape channel = _source.IntermediateScene.BlendShapes.Single(
            shape => shape.ObjectId == binding.ChannelObjectId);
        var candidates = new HashSet<string>(StringComparer.Ordinal) { binding.BlendShapeName };
        foreach (long connectedId in channel.ConnectedObjectIds)
            if (_source.TryGetObject(connectedId, out FbxSceneObject connected) &&
                connected.Category == FbxObjectCategory.Geometry && connected.Subclass == "Shape")
                candidates.Add($"{binding.BlendShapeName}.{connected.DisplayName}");

        // Assimp can join channel and Shape names. Resolve only the connected
        // source Shape on this model geometry; never strip an arbitrary suffix.
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subMesh in model.Meshes)
            foreach (var lod in subMesh.LODs)
                if (lod.Mesh is { } mesh)
                    foreach (string name in mesh.BlendshapeNames)
                        if (candidates.Contains(name))
                            matches.Add(name);
        if (matches.Count != 1)
            throw new InvalidDataException($"Blendshape '{binding.BlendShapeName}' has {matches.Count} matching imported channels on its source model geometry.");
        return matches.Single();
    }

    private static Matrix4x4 Inverse(Matrix4x4 matrix, string name)
        => Matrix4x4.Invert(matrix, out Matrix4x4 inverse) && Close(matrix * inverse, Matrix4x4.Identity, 1e-4f)
            ? inverse : throw new InvalidDataException($"Animation node '{name}' has a singular or non-finite matrix.");

    private static bool Close(Matrix4x4 a, Matrix4x4 b, float tolerance)
    {
        ReadOnlySpan<float> left = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref a.M11, 16);
        ReadOnlySpan<float> right = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref b.M11, 16);
        for (int index = 0; index < 16; index++)
            if (!float.IsFinite(left[index]) || !float.IsFinite(right[index]) ||
                MathF.Abs(left[index] - right[index]) > tolerance * MathF.Max(1.0f, MathF.Abs(left[index])))
                return false;
        return true;
    }
}
