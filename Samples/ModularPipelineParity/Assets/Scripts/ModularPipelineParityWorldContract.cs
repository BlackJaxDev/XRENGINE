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
        foreach (ModularPipelineParityPawnComponent pawn in pawns)
            switch (pawn.ProfileKey)
            {
                case "clear-a" when first is null: first = pawn; break;
                case "clear-b" when second is null: second = pawn; break;
                case "quad" when quadPawn is null: quadPawn = pawn; break;
                default: throw new InvalidOperationException($"Unexpected or repeated modular camera profile '{pawn.ProfileKey}'.");
            }

        if (pawns.Count != 3 || first is null || second is null || quadPawn is null)
            throw new InvalidOperationException("The saved modular world must contain its three authored camera pawns.");

        CameraComponent a = RequireCamera(first);
        CameraComponent b = RequireCamera(second);
        CameraComponent c = RequireCamera(quadPawn);
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
        return [first, second, quadPawn];
    }

    private static CameraComponent RequireCamera(ModularPipelineParityPawnComponent pawn)
        => pawn.SceneNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException($"The '{pawn.ProfileKey}' pawn lost its saved camera.");
}
