using System.Numerics;
using System.Reflection;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    private const string NotAvailableText = "<Not Set>";

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateXRAssetEditor(Type assetType)
    {
        var selector = CreateObjectSelector(assetType);
        return (node, prop, objects) =>
        {
            EnsureVerticalLayout(node);

            var selectorHost = node.NewChild();
            selector?.Invoke(selectorHost, prop, objects);

            var assets = ResolvePropertyValues<XRAsset>(prop, objects);
            if (assets.Count == 0)
            {
                AddInfoLabel(CreateInspectorCard(node), "No asset assigned.");
                return;
            }

            var visited = new HashSet<XRAsset>(ReferenceEqualityComparer.Instance);
            foreach (var asset in assets)
            {
                if (asset is null || !visited.Add(asset))
                    continue;

                BuildAssetInspector(node, asset);
            }
        };
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateGlObjectEditor(Type propType)
    {
        var selector = CreateObjectSelector(propType);
        return (node, prop, objects) =>
        {
            EnsureVerticalLayout(node);

            var selectorHost = node.NewChild();
            selector?.Invoke(selectorHost, prop, objects);

            var glObjects = ResolvePropertyValues<OpenGLRenderer.GLObjectBase>(prop, objects);
            if (glObjects.Count == 0)
            {
                AddInfoLabel(CreateInspectorCard(node), "No OpenGL object assigned.");
                return;
            }

            var visited = new HashSet<OpenGLRenderer.GLObjectBase>(ReferenceEqualityComparer.Instance);
            foreach (var glObject in glObjects)
            {
                if (glObject is null || !visited.Add(glObject))
                    continue;

                BuildGlObjectInspector(node, glObject);
            }
        };
    }

    private static void BuildAssetInspector(SceneNode parent, XRAsset asset)
    {
        var card = CreateInspectorCard(parent);
        AddSectionHeader(card, GetAssetDescriptor(asset));
        AddInfoLabel(card, $"Type: {asset.GetType().Name}");
        AddInfoLabel(card, $"Dirty: {FormatBool(asset.IsDirty)}");
        AddInfoLabel(card, $"File Path: {FormatPath(asset.FilePath)}");
        AddInfoLabel(card, $"Origin File Path: {FormatPath(asset.OriginalPath)}");

        if (asset.SourceAsset is XRAsset source && !ReferenceEquals(source, asset))
            AddInfoLabel(card, $"Source Asset: {GetAssetDescriptor(source)}");

        BuildEmbeddedAssetsSection(card, asset);

        var buttonRow = card.NewChild();
        var buttonLayout = buttonRow.SetTransform<UIListTransform>();
        buttonLayout.DisplayHorizontal = true;
        buttonLayout.ItemSpacing = 6.0f;
        buttonLayout.ItemAlignment = EListAlignment.TopOrLeft;

        string buttonLabel = string.IsNullOrWhiteSpace(asset.FilePath) ? "Save As" : "Save";
        CreateInspectorButton(buttonRow, buttonLabel, () => SaveAsset(asset));
    }

    private static void BuildEmbeddedAssetsSection(SceneNode cardNode, XRAsset asset)
    {
        var embedded = asset.EmbeddedAssets?.Where(x => x is not null && !ReferenceEquals(x, asset)).ToList() ?? new List<XRAsset>();
        if (embedded.Count == 0)
        {
            AddInfoLabel(cardNode, "Embedded Assets: <None>");
            return;
        }

        AddInfoLabel(cardNode, $"Embedded Assets ({embedded.Count}):");

        var embeddedListNode = cardNode.NewChild();
        var embeddedList = embeddedListNode.SetTransform<UIListTransform>();
        embeddedList.DisplayHorizontal = false;
        embeddedList.ItemSpacing = 2.0f;
        embeddedList.Padding = new Vector4(18.0f, 0.0f, 4.0f, 2.0f);

        foreach (var embeddedAsset in embedded)
            AddInfoLabel(embeddedListNode, $"• {GetAssetDescriptor(embeddedAsset)}", new Vector4(2.0f, 0.0f, 2.0f, 0.0f));
    }

    private static void BuildGlObjectInspector(SceneNode parent, OpenGLRenderer.GLObjectBase glObject)
    {
        var card = CreateInspectorCard(parent);
        AddSectionHeader(card, glObject.GetDescribingName());

        string bindingIdText = glObject.TryGetBindingId(out uint bindingId) ? bindingId.ToString() : "<Ungenerated>";
        AddInfoLabel(card, $"Type: {glObject.Type}");
        AddInfoLabel(card, $"Binding ID: {bindingIdText}");
        AddInfoLabel(card, $"Generated: {FormatBool(glObject.IsGenerated)}");

        var rendererTitle = glObject.Renderer.XRWindow.Window?.Title ?? "Unknown Window";
        AddInfoLabel(card, $"Renderer: {rendererTitle}");

        var data = (glObject as IRenderAPIObject)?.Data;
        if (data is not null)
        {
            AddInfoLabel(card, $"Data: {GetAssetDescriptor(data)}");
            AddInfoLabel(card, $"Data In Use: {FormatBool(data.InUse)}");

            if (data is XRFrameBuffer fbo)
                BuildFrameBufferDetails(card, fbo);
        }
        else
        {
            AddInfoLabel(card, "Data: <null>");
        }
    }

    private static void BuildFrameBufferDetails(SceneNode cardNode, XRFrameBuffer fbo)
    {
        AddSectionHeader(cardNode, "Framebuffer Details");
        AddInfoLabel(cardNode, $"Dimensions: {fbo.Width} x {fbo.Height}");
        AddInfoLabel(cardNode, $"Targets: {fbo.Targets?.Length ?? 0}");
        AddInfoLabel(cardNode, $"Texture Types: {fbo.TextureTypes}");
        AddInfoLabel(cardNode, $"Draw Buffers: {FormatDrawBuffers(fbo.DrawBuffers)}");

        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
        {
            AddInfoLabel(cardNode, "Attachments: <None>");
        }
        else
        {
            AddInfoLabel(cardNode, "Attachments:");
            var attachmentsNode = cardNode.NewChild();
            var attachmentsList = attachmentsNode.SetTransform<UIListTransform>();
            attachmentsList.DisplayHorizontal = false;
            attachmentsList.ItemSpacing = 2.0f;
            attachmentsList.Padding = new Vector4(18.0f, 0.0f, 4.0f, 2.0f);

            foreach (var attachment in targets)
                AddInfoLabel(attachmentsNode, $"• {DescribeAttachmentTarget(attachment)}", new Vector4(2.0f, 0.0f, 2.0f, 0.0f));
        }

        if (TryGetPreviewTexture(fbo, out var previewTexture, out var attachmentLabel))
        {
            AddInfoLabel(cardNode, $"Preview ({attachmentLabel}):");
            AddFboPreview(cardNode, previewTexture);
        }
        else
        {
            AddInfoLabel(cardNode, "Preview: No color attachment texture available.");
        }
    }

    private static void AddFboPreview(SceneNode cardNode, XRTexture texture)
    {
        var previewNode = cardNode.NewChild<UIMaterialComponent>(out var previewMat);
        var material = XRMaterial.CreateUnlitTextureMaterialForward(texture);
        material.EnableTransparency();
        previewMat.Material = material;
        previewMat.FlipVerticalUVCoord = true;

        var previewTransform = previewNode.SetTransform<UIBoundableTransform>();
        previewTransform.Height = 200.0f;
        previewTransform.Width = null;
        previewTransform.Margins = new Vector4(6.0f, 4.0f, 6.0f, 8.0f);
    }

    private static bool TryGetPreviewTexture(XRFrameBuffer fbo, out XRTexture texture, out string attachmentLabel)
    {
        texture = null!;
        attachmentLabel = string.Empty;

        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
            return false;

        foreach (var target in targets)
        {
            if (!IsColorAttachment(target.Attachment))
                continue;

            if (target.Target is XRTexture xrTexture)
            {
                texture = xrTexture;
                attachmentLabel = target.Attachment.ToString();
                return true;
            }
        }

        return false;
    }

    private static bool IsColorAttachment(EFrameBufferAttachment attachment)
        => attachment >= EFrameBufferAttachment.ColorAttachment0
        && attachment <= EFrameBufferAttachment.ColorAttachment31;

    private static string DescribeAttachmentTarget((IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex) entry)
    {
        string targetName = entry.Target switch
        {
            XRTexture texture => $"{GetAssetDescriptor(texture)} ({texture.WidthHeightDepth.X}x{texture.WidthHeightDepth.Y})",
            _ => entry.Target.GetType().Name
        };

        string layerSuffix = entry.LayerIndex >= 0 ? $", Layer {entry.LayerIndex}" : string.Empty;
        string mipSuffix = entry.MipLevel > 0 ? $", Mip {entry.MipLevel}" : string.Empty;
        return $"{entry.Attachment}{layerSuffix}{mipSuffix} → {targetName}";
    }

    private static string FormatDrawBuffers(EDrawBuffersAttachment[]? drawBuffers)
    {
        if (drawBuffers is null || drawBuffers.Length == 0)
            return NotAvailableText;

        return string.Join(", ", drawBuffers.Select(db => db.ToString()));
    }

    private static void SaveAsset(XRAsset asset)
    {
        var manager = Engine.Assets;
        if (manager is null)
        {
            Debug.LogWarning("Cannot save asset because the asset manager is unavailable.");
            return;
        }

        if (string.IsNullOrWhiteSpace(asset.FilePath))
        {
            // Open a save file dialog
            PromptSaveAssetPathAsync(asset, path =>
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                asset.FilePath = path;
                Engine.InvokeOnMainThread(() => manager.Save(asset), executeNowIfAlreadyMainThread: true);
            });
            return;
        }

        Engine.InvokeOnMainThread(() => manager.Save(asset), executeNowIfAlreadyMainThread: true);
    }

    private static void PromptSaveAssetPathAsync(XRAsset asset, Action<string?> callback)
    {
        string suggestedName = BuildSuggestedFileName(asset);
        string? initialDir = DetermineInitialAssetDirectory(asset);
        
        string dialogId = $"SaveAsset_{asset.GetHashCode()}";
        ImGuiFileBrowser.SaveFile(
            dialogId,
            $"Save {asset.GetType().Name}",
            result =>
            {
                if (result.Success && !string.IsNullOrEmpty(result.SelectedPath))
                {
                    callback(EnsureAssetExtension(result.SelectedPath));
                }
                else
                {
                    callback(null);
                }
            },
            $"XR Assets (*.{AssetManager.AssetExtension})|*.{AssetManager.AssetExtension}|All Files (*.*)|*.*",
            initialDir,
            suggestedName
        );
    }

    private static string BuildSuggestedFileName(XRAsset asset)
    {
        string baseName = string.IsNullOrWhiteSpace(asset.Name) ? asset.GetType().Name : asset.Name!;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(baseName)
            ? $"{asset.GetType().Name}.{AssetManager.AssetExtension}"
            : $"{baseName}.{AssetManager.AssetExtension}";
    }

    private static string? DetermineInitialAssetDirectory(XRAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.FilePath))
            return Path.GetDirectoryName(asset.FilePath);

        return Engine.Assets?.GameAssetsPath;
    }

    private static string EnsureAssetExtension(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals($".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return path;

        return path.EndsWith('.') ? $"{path}{AssetManager.AssetExtension}" : $"{path}.{AssetManager.AssetExtension}";
    }

    private static string GetAssetDescriptor(XRAsset asset)
    {
        string suffix = string.IsNullOrWhiteSpace(asset.Name) ? string.Empty : $" '{asset.Name}'";
        return $"{asset.GetType().Name}{suffix}";
    }

    private static List<T> ResolvePropertyValues<T>(PropertyInfo prop, object?[]? objects) where T : class
    {
        List<T> values = new();
        if (objects is null)
            return values;

        foreach (var instance in objects)
        {
            if (instance is null)
                continue;

            try
            {
                if (prop.GetValue(instance) is T value && value is not null)
                    values.Add(value);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to read property '{prop.Name}' while building inspector.");
            }
        }

        return values;
    }

    private static void CreateInspectorButton(SceneNode parent, string text, Action onClick)
    {
        var buttonNode = parent.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
        EditorUI.Styles.UpdateButton(button);

        var mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Transparent);
        mat.EnableTransparency();
        background.Material = mat;

        var btnTransform = buttonNode.SetTransform<UIBoundableTransform>();
        btnTransform.Height = 28.0f;
        btnTransform.Width = null;
        btnTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

        buttonNode.NewChild<UITextComponent>(out var label);
        label.Text = text;
        label.FontSize = EditorUI.Styles.PropertyNameFontSize;
        label.Color = EditorUI.Styles.ButtonTextColor;
        label.HorizontalAlignment = EHorizontalAlignment.Center;
        label.VerticalAlignment = EVerticalAlignment.Center;
        label.BoundableTransform.Margins = new Vector4(6.0f, 0.0f, 6.0f, 0.0f);

        button.RegisterClickActions(_ =>
        {
            try
            {
                onClick();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Inspector action '{text}' failed.");
            }
        });
    }

    private static UIListTransform EnsureVerticalLayout(SceneNode node)
    {
        var list = node.SetTransform<UIListTransform>();
        list.DisplayHorizontal = false;
        list.ItemSpacing = 6.0f;
        list.ItemAlignment = EListAlignment.TopOrLeft;
        list.Padding = new Vector4(0.0f);
        return list;
    }

    private static SceneNode CreateInspectorCard(SceneNode parent)
    {
        var cardNode = parent.NewChild<UIMaterialComponent>(out var matComp);
        matComp.Material = EditorPanel.MakeBackgroundMaterial();
        var cardLayout = cardNode.SetTransform<UIListTransform>();
        cardLayout.DisplayHorizontal = false;
        cardLayout.ItemSpacing = 2.0f;
        cardLayout.ItemAlignment = EListAlignment.TopOrLeft;
        cardLayout.Padding = new Vector4(6.0f, 4.0f, 6.0f, 4.0f);
        cardLayout.Margins = new Vector4(0.0f, 4.0f, 0.0f, 4.0f);
        return cardNode;
    }

    private static void AddSectionHeader(SceneNode parent, string title)
    {
        var header = AddInfoLabel(parent, title, new Vector4(4.0f, 4.0f, 4.0f, 2.0f), EditorUI.Styles.PropertyNameTextColor);
        header.FontSize = (EditorUI.Styles.PropertyNameFontSize <= 0.0f ? 16.0f : EditorUI.Styles.PropertyNameFontSize) + 2.0f;
    }

    private static UITextComponent AddInfoLabel(SceneNode parent, string text, Vector4? margins = null, ColorF4? color = null)
    {
        parent.NewChild<UITextComponent>(out var label);
        label.Text = text;
        label.FontSize = EditorUI.Styles.PropertyInputFontSize ?? 14.0f;
        label.Color = color ?? EditorUI.Styles.PropertyInputTextColor;
        label.HorizontalAlignment = EHorizontalAlignment.Left;
        label.VerticalAlignment = EVerticalAlignment.Center;
        label.WrapMode = FontGlyphSet.EWrapMode.Word;
        label.BoundableTransform.Margins = margins ?? new Vector4(6.0f, 2.0f, 6.0f, 2.0f);
        return label;
    }

    private static string FormatBool(bool value) => value ? "Yes" : "No";

    private static string FormatPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return NotAvailableText;

        return path;
    }
}
