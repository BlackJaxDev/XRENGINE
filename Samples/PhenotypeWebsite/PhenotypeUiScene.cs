using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace PhenotypeWebsite;

/// <summary>Builds a small native screen-space UI for a host scene.</summary>
public static class PhenotypeUiScene
{
    /// <summary>
    /// Adds a canvas to a scene node that is already in the host world.
    /// The host owns the camera, pawn, font, and returned scene node.
    /// </summary>
    public static SceneNode Create(SceneNode sceneParent, CameraComponent camera, PawnComponent pawn, FontGlyphSet font)
    {
        ArgumentNullException.ThrowIfNull(sceneParent);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(pawn);
        ArgumentNullException.ThrowIfNull(font);

        var root = new SceneNode(sceneParent, "Phenotype UI");
        var canvas = root.AddComponent<UICanvasComponent>()!;
        canvas.CanvasTransform.DrawSpace = ECanvasDrawSpace.Screen;
        canvas.CanvasTransform.SetSize(new Vector2(1280.0f, 720.0f));
        canvas.CanvasTransform.Padding = Vector4.Zero;

        var panelNode = new SceneNode(root, "Introduction panel");
        var panelTransform = panelNode.SetTransform<UIBoundableTransform>();
        Place(panelTransform, 660.0f, 390.0f, 0.0f, 0.0f);
        AddBackground(panelNode, new ColorF4(0.055f, 0.09f, 0.16f, 0.96f));

        AddText(panelNode, "Title", "PHENOTYPE ENGINE", font, 42.0f,
            new ColorF4(0.92f, 0.96f, 1.0f, 1.0f), 590.0f, 70.0f, 0.0f, 115.0f);
        AddText(panelNode, "Description", "Build and explore interactive worlds.", font, 22.0f,
            new ColorF4(0.68f, 0.79f, 0.91f, 1.0f), 590.0f, 55.0f, 0.0f, 40.0f);
        var status = AddText(panelNode, "Status", "Ready to explore.", font, 20.0f,
            new ColorF4(0.76f, 0.9f, 0.88f, 1.0f), 590.0f, 45.0f, 0.0f, -125.0f);

        var buttonNode = panelNode.NewChild<UIButtonComponent, UIMaterialComponent>(
            out var button, out var background, "Explore button");
        Place(buttonNode.GetTransformAs<UIBoundableTransform>(true)!, 220.0f, 54.0f, 0.0f, -50.0f);
        var buttonColor = new ColorF4(0.12f, 0.43f, 0.61f, 1.0f);
        button.DefaultBackgroundColor = buttonColor;
        button.HighlightBackgroundColor = new ColorF4(0.19f, 0.59f, 0.77f, 1.0f);
        button.DefaultTextColor = ColorF4.White;
        button.HighlightTextColor = ColorF4.White;
        background.Material = ColorMaterial(buttonColor);
        AddText(buttonNode, "Button label", "Explore", font, 22.0f,
            ColorF4.White, 200.0f, 48.0f, 0.0f, 0.0f);
        button.RegisterClickActions(_ => status.Text = "Explore selected.");

        var input = root.AddComponent<UICanvasInputComponent>()!;
        input.Canvas = canvas;
        input.OwningPawn = pawn;
        pawn.UserInterfaceInput = input;
        camera.UserInterface = canvas;
        return root;
    }

    private static UITextComponent AddText(
        SceneNode parent, string name, string value, FontGlyphSet font, float fontSize,
        ColorF4 color, float width, float height, float x, float y)
    {
        var node = parent.NewChild<UITextComponent>(out var text, name);
        Place(node.GetTransformAs<UIBoundableTransform>(true)!, width, height, x, y);
        text.Font = font;
        text.Text = value;
        text.FontSize = fontSize;
        text.Color = color;
        text.HorizontalAlignment = EHorizontalAlignment.Center;
        text.VerticalAlignment = EVerticalAlignment.Center;
        return text;
    }

    private static void AddBackground(SceneNode node, ColorF4 color)
        => node.AddComponent<UIMaterialComponent>()!.Material = ColorMaterial(color);

    private static XRMaterial ColorMaterial(ColorF4 color)
    {
        var material = XRMaterial.CreateUnlitColorMaterialForward(color);
        material.EnableTransparency();
        return material;
    }

    private static void Place(UIBoundableTransform transform, float width, float height, float x, float y)
    {
        transform.MinAnchor = new Vector2(0.5f, 0.5f);
        transform.MaxAnchor = new Vector2(0.5f, 0.5f);
        transform.NormalizedPivot = new Vector2(0.5f, 0.5f);
        transform.Width = width;
        transform.Height = height;
        transform.Translation = new Vector2(x, y);
    }
}
