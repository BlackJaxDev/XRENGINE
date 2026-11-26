using XREngine;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Scene;

namespace ModularPipelineParity;

/// <summary>Checks source identity after saved and cooked world hydration.</summary>
public static class ModularPipelineParityWorldContract
{
    public static ModularPipelineParityPawnComponent[] Validate(XRWorld world)
    {
        List<ModularPipelineParityPawnComponent> pawns = [];
        foreach (XRScene scene in world.Scenes)
            foreach (SceneNode root in scene.RootNodes)
                pawns.AddRange(root.FindAllDescendantComponents<ModularPipelineParityPawnComponent>());
        return ValidatePawns(pawns);
    }

    public static ModularPipelineParityPawnComponent[] Validate(RuntimeWorld world)
    {
        List<ModularPipelineParityPawnComponent> pawns = [];
        foreach (SceneNode root in world.RootNodes)
            pawns.AddRange(root.FindAllDescendantComponents<ModularPipelineParityPawnComponent>());
        return ValidatePawns(pawns);
    }

    private static ModularPipelineParityPawnComponent[] ValidatePawns(
        List<ModularPipelineParityPawnComponent> pawns)
    {
        ModularPipelineParityPawnComponent? first = null;
        ModularPipelineParityPawnComponent? second = null;
        ModularPipelineParityPawnComponent? quadPawn = null;
        ModularPipelineParityPawnComponent? msaaCpuPawn = null;
        ModularPipelineParityPawnComponent? msaaGpuPawn = null;
        foreach (ModularPipelineParityPawnComponent pawn in pawns)
            switch (pawn.ProfileKey)
            {
                case "clear-a" when first is null: first = pawn; break;
                case "clear-b" when second is null: second = pawn; break;
                case "quad" when quadPawn is null: quadPawn = pawn; break;
                case "msaa-cpu" when msaaCpuPawn is null: msaaCpuPawn = pawn; break;
                case "msaa-gpu" when msaaGpuPawn is null: msaaGpuPawn = pawn; break;
                default: throw new InvalidOperationException($"Unexpected or repeated modular camera profile '{pawn.ProfileKey}'.");
            }

        if (pawns.Count != 5 || first is null || second is null || quadPawn is null ||
            msaaCpuPawn is null || msaaGpuPawn is null)
            throw new InvalidOperationException("The saved modular world must contain its five authored camera pawns.");

        CameraComponent a = RequireCamera(first);
        CameraComponent b = RequireCamera(second);
        CameraComponent c = RequireCamera(quadPawn);
        CameraComponent d = RequireCamera(msaaCpuPawn);
        CameraComponent e = RequireCamera(msaaGpuPawn);
        if (a.RenderPipelineSource is not ModularClearRenderPipeline clear ||
            !ReferenceEquals(clear, b.RenderPipelineSource) ||
            clear.ID != Guid.Parse("a09559ee-79e9-4bcc-9adf-43cf250bc1db") ||
            !clear.OutputClearColor.Equals(new ColorF4(0.04f, 0.32f, 0.68f, 1.0f), 0.0001f))
            throw new InvalidOperationException("The two saved clear cameras lost their one shared authored source.");
        if (c.RenderPipelineSource is not ModularQuadRenderPipeline quad ||
            quad.ID != Guid.Parse("8c583188-43ac-4cd4-8b54-9673cb216585") ||
            !quad.OutputClearColor.Equals(new ColorF4(0.07f, 0.08f, 0.14f, 1.0f), 0.0001f) ||
            quad.DeclaredRequirements?.Programs.ContainsKey(ModularQuadRenderPipeline.BindingKey) != true)
            throw new InvalidOperationException("The saved quad camera lost its independent program declaration.");
        if (a.AntiAliasingModeOverride != EAntiAliasingMode.None ||
            b.AntiAliasingModeOverride != EAntiAliasingMode.None ||
            c.AntiAliasingModeOverride != EAntiAliasingMode.None)
            throw new InvalidOperationException("The saved first-stage cameras must request AA None explicitly.");
        if (d.RenderPipelineSource is not ModularMsaaRenderPipeline cpu ||
            e.RenderPipelineSource is not ModularMsaaRenderPipeline gpu ||
            ReferenceEquals(cpu, gpu) ||
            cpu.ID != Guid.Parse("2edcc7a6-710e-43d4-8d6f-f8474a7e35f1") ||
            gpu.ID != Guid.Parse("1929e9c7-6702-4d6c-8d95-8369e286bf66") ||
            cpu.MeshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect ||
            gpu.MeshSubmissionStrategy != EMeshSubmissionStrategy.GpuIndirectZeroReadback ||
            !HasMsaaPrograms(cpu) || !HasMsaaPrograms(gpu))
            throw new InvalidOperationException("The saved MSAA cameras lost their distinct authored source, strategy, or scoped programs.");
        if (d.AntiAliasingModeOverride != EAntiAliasingMode.Msaa ||
            e.AntiAliasingModeOverride != EAntiAliasingMode.Msaa ||
            d.MsaaSampleCountOverride != 4 || e.MsaaSampleCountOverride != 4)
            throw new InvalidOperationException("The saved MSAA cameras must request four samples explicitly.");
        return [first, second, quadPawn, msaaCpuPawn, msaaGpuPawn];
    }

    private static bool HasMsaaPrograms(ModularMsaaRenderPipeline pipeline)
        => pipeline.DeclaredRequirements?.Programs.ContainsKey(ModularMsaaRenderPipeline.SceneBindingKey) == true &&
            pipeline.DeclaredRequirements.Programs.ContainsKey(ModularMsaaRenderPipeline.PresentBindingKey);

    public static CustomMsaaSceneComponent[] ValidateSceneParts(XRWorld world)
    {
        List<CustomMsaaSceneComponent> parts = [];
        foreach (XRScene scene in world.Scenes)
            foreach (SceneNode root in scene.RootNodes)
                parts.AddRange(root.FindAllDescendantComponents<CustomMsaaSceneComponent>());
        return ValidateParts(parts);
    }

    public static CustomMsaaSceneComponent[] ValidateSceneParts(RuntimeWorld world)
    {
        List<CustomMsaaSceneComponent> parts = [];
        foreach (SceneNode root in world.RootNodes)
            parts.AddRange(root.FindAllDescendantComponents<CustomMsaaSceneComponent>());
        return ValidateParts(parts);
    }

    private static CustomMsaaSceneComponent[] ValidateParts(List<CustomMsaaSceneComponent> parts)
    {
        CustomMsaaSceneComponent? opaque = null;
        CustomMsaaSceneComponent? far = null;
        CustomMsaaSceneComponent? near = null;
        foreach (CustomMsaaSceneComponent part in parts)
            switch (part.ScenePart)
            {
                case ECustomMsaaScenePart.OpaqueSlope when opaque is null: opaque = part; break;
                case ECustomMsaaScenePart.FarAlpha when far is null: far = part; break;
                case ECustomMsaaScenePart.NearAlpha when near is null: near = part; break;
                default: throw new InvalidOperationException($"Unexpected or repeated modular scene part '{part.ScenePart}'.");
            }
        if (parts.Count != 3 || opaque is null || far is null || near is null)
            throw new InvalidOperationException("The saved modular MSAA scene must contain all three authored mesh parts.");
        return [opaque, far, near];
    }

    private static CameraComponent RequireCamera(ModularPipelineParityPawnComponent pawn)
        => pawn.SceneNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException($"The '{pawn.ProfileKey}' pawn lost its saved camera.");
}
