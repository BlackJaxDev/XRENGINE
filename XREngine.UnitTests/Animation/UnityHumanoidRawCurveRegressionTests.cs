using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;
using XREngine.Data.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class UnityHumanoidRawCurveRegressionTests
{
    [Test]
    public void ImportedSexyWalkHumanoidClip_ReplaysUnityRawCurvesAcrossWholeClip()
    {
        string repoRoot = FindRepositoryRoot();
        string clipPath = Path.Combine(repoRoot, "Assets", "Walks", "Sexy Walk.anim");
        string fixturePath = Path.Combine(repoRoot, "XREngine.UnitTests", "TestData", "SexyWalkHumanoidRawAudit.compact.json");

        var fixture = LoadFixture(fixturePath);
        var clip = AnimYamlImporter.Import(clipPath);

        var root = new SceneNode("Root", new Transform());
        var clipComponent = root.AddComponent<AnimationClipComponent>()!;
        clipComponent.Animation = clip;
        var humanoid = root.AddComponent<HumanoidComponent>()!;

        var summaries = new List<string>(fixture.Channels.Count);
        foreach (var channel in fixture.Channels)
        {
            UnityHumanoidMuscleMap.TryGetValue(channel.Name, out EHumanoidValue humanoidValue).ShouldBeTrue($"Missing humanoid mapping for '{channel.Name}'.");

            float maxError = 0.0f;
            float errorSum = 0.0f;
            foreach (var sample in channel.Samples)
            {
                clipComponent.EvaluateAtTime(sample.TimeSeconds);

                humanoid.TryGetMuscleValue(humanoidValue, out float engineValue).ShouldBeTrue($"Humanoid did not record '{channel.Name}'.");

                float error = MathF.Abs(engineValue - sample.Raw);
                errorSum += error;
                maxError = MathF.Max(maxError, error);
            }

            float meanError = errorSum / channel.Samples.Count;
            summaries.Add($"{channel.Name}: mean={meanError:F6}, max={maxError:F6}");
            meanError.ShouldBeLessThan(0.01f, string.Join(Environment.NewLine, summaries));
            maxError.ShouldBeLessThan(0.05f, string.Join(Environment.NewLine, summaries));
        }
    }

    private static UnityHumanoidRawAuditFixture LoadFixture(string fixturePath)
    {
        string json = File.ReadAllText(fixturePath);
        return JsonSerializer.Deserialize<UnityHumanoidRawAuditFixture>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
        }) ?? throw new InvalidOperationException($"Failed to deserialize raw-curve fixture '{fixturePath}'.");
    }

    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);

        while (true)
        {
            if (File.Exists(Path.Combine(current, "XRENGINE.sln")))
                return current;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
                break;

            current = parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root containing XRENGINE.sln.");
    }

    private sealed class UnityHumanoidRawAuditFixture
    {
        [JsonPropertyName("Channels")]
        public required List<UnityHumanoidRawAuditChannel> Channels { get; init; }
    }

    private sealed class UnityHumanoidRawAuditChannel
    {
        [JsonPropertyName("Name")]
        public required string Name { get; init; }

        [JsonPropertyName("Samples")]
        public required List<UnityHumanoidRawAuditSample> Samples { get; init; }
    }

    private sealed class UnityHumanoidRawAuditSample
    {
        [JsonPropertyName("t")]
        public required float TimeSeconds { get; init; }

        [JsonPropertyName("r")]
        public required float Raw { get; init; }
    }
}