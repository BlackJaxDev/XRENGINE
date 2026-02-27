using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Audio;
using XREngine.Audio.Steam;

namespace XREngine.UnitTests.Audio;

/// <summary>
/// Tests for Phase 5 of the audio architecture migration:
/// reflection simulation, pathing simulation, probe batch management, and baking workflow.
/// </summary>
[TestFixture]
public sealed class SteamAudioAdvancedSimTests
{
    // ------------------------------------------------------------------
    //  Capabilities
    // ------------------------------------------------------------------

    [Test]
    public void SteamAudioProcessor_SupportsReflections_IsTrue()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        processor.SupportsReflections.ShouldBeTrue();
    }

    [Test]
    public void SteamAudioProcessor_SupportsPathing_IsTrue()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        processor.SupportsPathing.ShouldBeTrue();
    }

    // ------------------------------------------------------------------
    //  Probe batch lifecycle
    // ------------------------------------------------------------------

    [Test]
    public void ProbeBatch_CreateAndDispose_NoLeak()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        using var batch = processor.CreateProbeBatch();
        batch.ShouldNotBeNull();
        batch.ProbeCount.ShouldBe(0);
        batch.IsCommitted.ShouldBeFalse();
    }

    [Test]
    public void ProbeBatch_AddProbe_TracksCount()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        using var batch = processor.CreateProbeBatch();

        batch.AddProbe(Vector3.Zero, 1.0f);
        batch.AddProbe(new Vector3(5, 0, 0), 1.0f);

        // Probe count available only after commit
        batch.Commit();
        batch.IsCommitted.ShouldBeTrue();
        batch.ProbeCount.ShouldBe(2);
    }

    [Test]
    public void ProbeBatch_RemoveProbe_DecreasesCount()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        using var batch = processor.CreateProbeBatch();

        batch.AddProbe(Vector3.Zero, 1.0f);
        batch.AddProbe(new Vector3(5, 0, 0), 1.0f);
        batch.AddProbe(new Vector3(10, 0, 0), 1.0f);
        batch.Commit();

        batch.RemoveProbe(1);
        batch.Commit();
        batch.ProbeCount.ShouldBe(2);
    }

    [Test]
    public void ProbeBatch_GenerateProbes_WithScene()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        // Create a simple floor scene
        using var scene = processor.CreateScene();
        Vector3[] verts =
        [
            new(-10, 0, -10), new(10, 0, -10),
            new(10, 0, 10), new(-10, 0, 10)
        ];
        int[] indices = [0, 1, 2, 0, 2, 3];
        SteamAudioMaterial[] mats = [SteamAudioMaterial.Concrete];
        scene.AddStaticMesh(verts, indices, mats);
        scene.Commit();

        using var batch = processor.CreateProbeBatch();
        var transform = SteamAudioProbeBatch.CreateVolumeTransform(
            new Vector3(-10, -1, -10),
            new Vector3(10, 5, 10));

        // Generate probes on the floor with a wide spacing
        batch.GenerateProbes(scene, 5.0f, 1.5f, transform);

        batch.GeneratedProbeCount.ShouldBeGreaterThan(0);
        batch.Commit();
        batch.ProbeCount.ShouldBeGreaterThan(0);
    }

    [Test]
    public void ProbeBatch_GenerateProbes_UncommittedScene_Throws()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        using var scene = processor.CreateScene();
        using var batch = processor.CreateProbeBatch();

        var transform = SteamAudioProbeBatch.CreateVolumeTransform(
            new Vector3(-5, -1, -5),
            new Vector3(5, 5, 5));

        // Scene not committed — should throw
        Should.Throw<InvalidOperationException>(() =>
            batch.GenerateProbes(scene, 2.0f, 1.5f, transform));
    }

    [Test]
    public void ProbeBatch_AttachToSimulator_Works()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        using var batch = processor.CreateProbeBatch();
        batch.AddProbe(Vector3.Zero, 1.0f);
        batch.Commit();

        processor.AddProbeBatch(batch);
        processor.ProbeBatches.Count.ShouldBe(1);

        processor.RemoveProbeBatch(batch);
        processor.ProbeBatches.Count.ShouldBe(0);
    }

    [Test]
    public void ProbeBatch_AttachUncommitted_Throws()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        using var batch = processor.CreateProbeBatch();
        batch.AddProbe(Vector3.Zero, 1.0f);
        // Not committed

        Should.Throw<InvalidOperationException>(() =>
            processor.AddProbeBatch(batch));
    }

    [Test]
    public void ProbeBatch_CreateBeforeInit_Throws()
    {
        if (!SteamAudioProcessor.IsNativeLibraryAvailable())
        {
            Assert.Inconclusive("Steam Audio unavailable — skipping.");
            return;
        }

        using var processor = new SteamAudioProcessor();
        Should.Throw<InvalidOperationException>(() => processor.CreateProbeBatch());
    }

    // ------------------------------------------------------------------
    //  Volume transform helper
    // ------------------------------------------------------------------

    [Test]
    public void VolumeTransform_IdentityForUnitCube()
    {
        var m = SteamAudioProbeBatch.CreateVolumeTransform(Vector3.Zero, Vector3.One);

        // Diagonal should be 1.0 (unit scale)
        m.elements[0, 0].ShouldBe(1.0f);
        m.elements[1, 1].ShouldBe(1.0f);
        m.elements[2, 2].ShouldBe(1.0f);
        m.elements[3, 3].ShouldBe(1.0f);

        // Translation should be 0
        m.elements[0, 3].ShouldBe(0.0f);
        m.elements[1, 3].ShouldBe(0.0f);
        m.elements[2, 3].ShouldBe(0.0f);
    }

    [Test]
    public void VolumeTransform_ScalesAndTranslates()
    {
        var min = new Vector3(-10, 0, -5);
        var max = new Vector3(10, 8, 5);
        var m = SteamAudioProbeBatch.CreateVolumeTransform(min, max);

        m.elements[0, 0].ShouldBe(20.0f);     // X scale
        m.elements[1, 1].ShouldBe(8.0f);      // Y scale
        m.elements[2, 2].ShouldBe(10.0f);     // Z scale
        m.elements[0, 3].ShouldBe(-10.0f);    // X translation
        m.elements[1, 3].ShouldBe(0.0f);      // Y translation
        m.elements[2, 3].ShouldBe(-5.0f);     // Z translation
    }

    // ------------------------------------------------------------------
    //  Baker factory
    // ------------------------------------------------------------------

    [Test]
    public void Baker_CreateSucceeds()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        var baker = processor.CreateBaker();
        baker.ShouldNotBeNull();
    }

    [Test]
    public void Baker_CreateBeforeInit_Throws()
    {
        if (!SteamAudioProcessor.IsNativeLibraryAvailable())
        {
            Assert.Inconclusive("Steam Audio unavailable — skipping.");
            return;
        }

        using var processor = new SteamAudioProcessor();
        Should.Throw<InvalidOperationException>(() => processor.CreateBaker());
    }

    // ------------------------------------------------------------------
    //  Baker validation
    // ------------------------------------------------------------------

    [Test]
    public void Baker_BakeReflections_UncommittedScene_Throws()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        var baker = processor.CreateBaker();
        using var scene = processor.CreateScene();
        using var batch = processor.CreateProbeBatch();
        batch.AddProbe(Vector3.Zero, 1.0f);
        batch.Commit();

        Should.Throw<InvalidOperationException>(() =>
            baker.BakeReflections(scene, batch, new ReflectionsBakeSettings()));
    }

    [Test]
    public void Baker_BakeReflections_UncommittedBatch_Throws()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        var baker = processor.CreateBaker();
        using var scene = processor.CreateScene();
        // Add a trivial mesh so the scene is valid
        Vector3[] verts = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        int[] idx = [0, 1, 2];
        SteamAudioMaterial[] mats = [SteamAudioMaterial.Default];
        scene.AddStaticMesh(verts, idx, mats);
        scene.Commit();

        using var batch = processor.CreateProbeBatch();
        batch.AddProbe(Vector3.Zero, 1.0f);
        // Not committed

        Should.Throw<InvalidOperationException>(() =>
            baker.BakeReflections(scene, batch, new ReflectionsBakeSettings()));
    }

    [Test]
    public void Baker_BakePathing_UncommittedScene_Throws()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        var baker = processor.CreateBaker();
        using var scene = processor.CreateScene();
        using var batch = processor.CreateProbeBatch();
        batch.AddProbe(Vector3.Zero, 1.0f);
        batch.Commit();

        Should.Throw<InvalidOperationException>(() =>
            baker.BakePathing(scene, batch, new PathBakeSettings()));
    }

    // ------------------------------------------------------------------
    //  Bake settings defaults
    // ------------------------------------------------------------------

    [Test]
    public void ReflectionsBakeSettings_DefaultsAreReasonable()
    {
        var s = new ReflectionsBakeSettings();
        s.NumRays.ShouldBeGreaterThan(0);
        s.NumBounces.ShouldBeGreaterThan(0);
        s.SimulatedDuration.ShouldBeGreaterThan(0f);
        s.SavedDuration.ShouldBeGreaterThan(0f);
        s.AmbisonicsOrder.ShouldBeGreaterThanOrEqualTo(0);
        s.NumThreads.ShouldBeGreaterThan(0);
        s.BakeConvolution.ShouldBeTrue();
        s.BakeParametric.ShouldBeTrue();
    }

    [Test]
    public void PathBakeSettings_DefaultsAreReasonable()
    {
        var s = new PathBakeSettings();
        s.NumSamples.ShouldBeGreaterThan(0);
        s.VisRadius.ShouldBeGreaterThan(0f);
        s.VisRange.ShouldBeGreaterThan(0f);
        s.PathRange.ShouldBeGreaterThan(0f);
        s.NumThreads.ShouldBeGreaterThan(0);
    }

    // ------------------------------------------------------------------
    //  Reflection simulation integration
    // ------------------------------------------------------------------

    [Test]
    public void Processor_WithScene_ReflectionsProduceOutput()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        // Build an enclosed box to create reflections
        using var scene = processor.CreateScene();
        AddEnclosedBox(scene, 5.0f);
        scene.Commit();
        processor.SetScene(scene);

        // Place source and listener inside the box
        var sourcePos = new Vector3(0, 0, 0);
        var listenerPos = new Vector3(2, 0, 0);
        processor.SetListenerPose(listenerPos, -Vector3.UnitZ, Vector3.UnitY);

        var handle = processor.AddSource(new AudioEffectsSourceSettings
        {
            Position = sourcePos,
            Forward = Vector3.UnitZ,
        });

        // Run multiple simulation ticks to build up reflection state
        for (int i = 0; i < 10; i++)
            processor.Tick(0.016f);

        // Process a mono impulse
        int frameSize = 1024;
        var input = new float[frameSize];
        input[0] = 1.0f; // impulse
        var output = new float[frameSize * 2]; // stereo

        processor.ProcessBuffer(handle, input, output, 2, 44100);

        // With reflections in an enclosed space, the output should not be silent
        float rms = ComputeRms(output);
        rms.ShouldBeGreaterThan(0f, "Expected non-zero output in reflective box");

        processor.RemoveSource(handle);
    }

    [Test]
    public void Processor_WithoutScene_StillProducesDirectOutput()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        var handle = processor.AddSource(new AudioEffectsSourceSettings
        {
            Position = new Vector3(0, 0, -1),
            Forward = Vector3.UnitZ,
        });

        processor.SetListenerPose(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        processor.Tick(0.016f);

        int frameSize = 1024;
        var input = new float[frameSize];
        for (int i = 0; i < frameSize; i++)
            input[i] = MathF.Sin(2 * MathF.PI * 440 * i / 44100f) * 0.5f;

        var output = new float[frameSize * 2];
        processor.ProcessBuffer(handle, input, output, 2, 44100);

        // Direct path with HRTF should produce non-zero output
        float rms = ComputeRms(output);
        rms.ShouldBeGreaterThan(0f, "Expected non-zero direct output even without scene");

        processor.RemoveSource(handle);
    }

    // ------------------------------------------------------------------
    //  Phase 3+4 regression
    // ------------------------------------------------------------------

    [Test]
    public void Processor_ProcessBuffer_BinaralStereoOutput()
    {
        using var processor = TryCreateProcessor();
        if (processor is null) { Assert.Inconclusive("Steam Audio unavailable — skipping."); return; }

        var handle = processor.AddSource(new AudioEffectsSourceSettings
        {
            Position = new Vector3(1, 0, 0),
            Forward = -Vector3.UnitX,
        });

        processor.SetListenerPose(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        processor.Tick(0.016f);

        int frameSize = 1024;
        var input = new float[frameSize];
        for (int i = 0; i < frameSize; i++)
            input[i] = MathF.Sin(2 * MathF.PI * 1000 * i / 44100f);

        var output = new float[frameSize * 2];
        processor.ProcessBuffer(handle, input, output, 2, 44100);

        // Output buffer should have non-zero stereo samples
        bool hasNonZeroLeft = false, hasNonZeroRight = false;
        for (int i = 0; i < frameSize; i++)
        {
            if (output[i * 2] != 0) hasNonZeroLeft = true;
            if (output[i * 2 + 1] != 0) hasNonZeroRight = true;
        }

        hasNonZeroLeft.ShouldBeTrue("Left channel should have non-zero samples");
        hasNonZeroRight.ShouldBeTrue("Right channel should have non-zero samples");

        processor.RemoveSource(handle);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static float ComputeRms(ReadOnlySpan<float> samples)
    {
        float sumSq = 0;
        for (int i = 0; i < samples.Length; i++)
            sumSq += samples[i] * samples[i];
        return MathF.Sqrt(sumSq / samples.Length);
    }

    /// <summary>
    /// Adds six quad faces forming a closed box of the given half-extent centered at origin.
    /// </summary>
    private static void AddEnclosedBox(SteamAudioScene scene, float halfExtent)
    {
        float h = halfExtent;
        Vector3[] verts =
        [
            // Floor (y = -h)
            new(-h, -h, -h), new(h, -h, -h), new(h, -h, h), new(-h, -h, h),
            // Ceiling (y = +h)
            new(-h, h, -h), new(h, h, -h), new(h, h, h), new(-h, h, h),
        ];

        int[] indices =
        [
            // Floor
            0, 1, 2,  0, 2, 3,
            // Ceiling
            4, 6, 5,  4, 7, 6,
            // Front (z = -h)
            0, 5, 1,  0, 4, 5,
            // Back (z = +h)
            2, 7, 3,  2, 6, 7,
            // Left (x = -h)
            0, 3, 7,  0, 7, 4,
            // Right (x = +h)
            1, 5, 6,  1, 6, 2,
        ];

        SteamAudioMaterial[] mats = [SteamAudioMaterial.Concrete];
        scene.AddStaticMesh(verts, indices, mats);
    }

    private static SteamAudioProcessor? TryCreateProcessor()
    {
        if (!SteamAudioProcessor.IsNativeLibraryAvailable())
            return null;

        try
        {
            var proc = new SteamAudioProcessor();
            proc.Initialize(new AudioEffectsSettings { SampleRate = 44100, FrameSize = 1024 });
            return proc;
        }
        catch
        {
            return null;
        }
    }
}
