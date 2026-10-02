using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.RenderBench;

internal static partial class RuntimeDataLayoutScenario
{
    private static void MeasureTransforms(RenderBenchOptions options, RuntimeMeasurementAsset fixture, string root, RuntimeDataLayoutReport report)
    {
        string contentHash = HashFixture(fixture, root);
        XRPrefabSource template = Load(fixture, root) as XRPrefabSource
            ?? throw new InvalidDataException("The transform fixture must contain an avatar prefab.");
        try
        {
            foreach (int avatars in Populations)
            for (int repeat = 0; repeat < options.ScenarioRepeats; repeat++)
            {
                using RenderBenchProductionScene scene = new(options, EOcclusionCullingMode.Disabled, useAdvancedPipeline: true) { MeasurePublication = true };
                SceneNode?[] instances = new SceneNode?[avatars];
                try
                {
                    List<Transform> animated = [];
                    Stack<TransformBase> pending = new();
                    int transformCount = 0;
                    for (int avatar = 0; avatar < avatars; avatar++)
                    {
                        SceneNode instance = template.Instantiate(scene.WorldHost.CoreWorld, scene.World.Scenes[0].RootNodes[0]);
                        instances[avatar] = instance;
                        if (instance.Transform is Transform placement)
                            placement.Translation += new Vector3((avatar % 8) * 2.0f - 7.0f, 0, (avatar / 8) * 2.0f);
                        pending.Push(instance.Transform);
                        while (pending.TryPop(out TransformBase? transform))
                        {
                            transformCount++;
                            if (transform is Transform local)
                                animated.Add(local);
                            for (int child = 0; child < transform.Children.Count; child++)
                                pending.Push(transform.Children[child]);
                        }
                    }
                    Transform[] transforms = animated.ToArray();
                    Quaternion[] rotations = new Quaternion[transforms.Length];
                    for (int i = 0; i < transforms.Length; i++)
                        rotations[i] = transforms[i].Rotation;
                    int frames = options.ScenarioFrames;
                    double[] propagation = new double[frames];
                    double[] publication = new double[frames];
                    double[] canonicalPublication = new double[frames];
                    double[] production = new double[frames];
                    long propagationBytes = 0, publicationBytes = 0, canonicalPublicationBytes = 0, productionBytes = 0;
                    TransformHierarchyCounters counters = default;
                    for (int frame = -options.WarmupFrames; frame < frames; frame++)
                    {
                        for (int i = 0; i < transforms.Length; i++)
                            transforms[i].Rotation = Quaternion.Normalize(rotations[i] * Quaternion.CreateFromAxisAngle(
                                Vector3.UnitY, 0.05f * MathF.Sin((frame + options.WarmupFrames) / 60.0f + i * 0.1f)));
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        long started = Stopwatch.GetTimestamp();
                        scene.WorldHost.CoreWorld.ProcessDirtyTransforms(ELoopType.Sequential);
                        double propagationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        long propagationAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        allocated = GC.GetAllocatedBytesForCurrentThread();
                        started = Stopwatch.GetTimestamp();
                        scene.WorldHost.CoreWorld.TransformHierarchy.PublishRenderMatrices();
                        double publicationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        long publicationAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        counters = scene.WorldHost.CoreWorld.TransformHierarchy.Counters;
                        allocated = GC.GetAllocatedBytesForCurrentThread();
                        started = Stopwatch.GetTimestamp();
                        scene.SubmitStep(1.0 / 60.0, allowAdmissionRetry: frame < 0);
                        double productionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        long productionAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        if (frame < 0)
                            continue;
                        propagation[frame] = propagationMs;
                        publication[frame] = publicationMs;
                        canonicalPublication[frame] = scene.LastPublicationMilliseconds;
                        canonicalPublicationBytes += scene.LastPublicationAllocatedBytes;
                        production[frame] = productionMs;
                        propagationBytes += propagationAllocation;
                        publicationBytes += publicationAllocation;
                        productionBytes += productionAllocation;
                    }
                    report.Transforms.Add(new(fixture.Identity, contentHash, avatars, transformCount, transforms.Length, repeat + 1, frames,
                        Percentile(propagation, 0.5), Percentile(propagation, 0.95), propagation[^1], propagationBytes,
                        Percentile(publication, 0.5), Percentile(publication, 0.95), publication[^1], publicationBytes,
                        Percentile(canonicalPublication, 0.5), Percentile(canonicalPublication, 0.95), canonicalPublication[^1], canonicalPublicationBytes,
                        Percentile(production, 0.5), Percentile(production, 0.95), production[^1], productionBytes, counters));
                }
                finally
                {
                    foreach (SceneNode? instance in instances)
                        instance?.Destroy(now: true);
                    XRObjectBase.ProcessPendingDestructions();
                }
            }
        }
        finally
        {
            Unload(template);
        }
    }
}
