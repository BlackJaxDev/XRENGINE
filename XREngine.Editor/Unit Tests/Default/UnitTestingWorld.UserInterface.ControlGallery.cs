using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static partial class UserInterface
    {
        private static void CreateControlGallery(SceneNode canvasRoot)
        {
            if (Environment.GetEnvironmentVariable("XRE_UI_CONTROL_GALLERY") != "1")
                return;

            SceneNode panel = new(canvasRoot) { Name = "Native control gallery" };
            UIListTransform list = panel.SetTransform<UIListTransform>();
            panel.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.StaticChrome;
            list.MinAnchor = new Vector2(0, 1);
            list.MaxAnchor = new Vector2(0, 1);
            list.NormalizedPivot = new Vector2(0, 1);
            list.Translation = new Vector2(360, -20);
            list.Width = 280;
            list.Height = 380;
            list.ItemSpacing = 6;
            list.ItemAlignment = EListAlignment.TopOrLeft;
            list.BlocksInputBehind = true;
            AddGalleryBackground(panel, new ColorF4(0.12f, 0.12f, 0.12f, 0.8f));

            SceneNode buttonNode = NativeUIElements.CreateButton(panel, "Button", _ => { }, height: 26).Node;
            buttonNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;

            SceneNode toggleNode = MakeGalleryRow(panel, "Checkbox");
            var toggle = NativeUIElements.CreateCheckboxToggle(toggleNode).Toggle;
            toggle.Binding = new UIValueState<UIToggleComponent.ECurrentState>(UIToggleComponent.ECurrentState.True);
            AddGalleryLabel(toggleNode, "Checkbox");

            UIRadioGroupState group = new()
            {
                Binding = new UIValueState<ulong?>(1)
            };
            panel.Destroyed += _ => group.Dispose();
            for (ulong id = 1; id <= 2; id++)
            {
                SceneNode option = MakeGalleryRow(panel, $"Radio {id}");
                UIRadioButtonComponent radio = option.AddComponent<UIRadioButtonComponent>()!;
                radio.OptionId = id;
                radio.Group = group;
                radio.AccessibilityLabel = $"Radio {id}";
                AddGalleryBackground(option, new ColorF4(0.2f, 0.2f, 0.2f, 1));
                UITextComponent radioLabel = AddGalleryLabel(option, $"Radio {id}");
                ulong optionId = id;
                void RefreshRadio(UIValueChange<ulong?> _) => radioLabel.Text =
                    $"{(group.SelectedId == optionId ? "(o)" : "( )")} Radio {optionId}";
                group.SelectionChanged += RefreshRadio;
                RefreshRadio(default);
            }

            SceneNode numeric = MakeGalleryRow(panel, "Integer input");
            var numericInput = NativeUIElements.CreateTextInputField<UIIntInputComponent>(numeric).Input;
            numericInput.Binding = new UIValueState<string>("42");

            SceneNode sliderNode = MakeGalleryRow(panel, "Range 0–100");
            UIRangeSliderComponent slider = sliderNode.AddComponent<UIRangeSliderComponent>()!;
            slider.Maximum = 100;
            slider.Step = 1;
            slider.Binding = new UIValueState<double>(35);
            AddGalleryBackground(sliderNode, new ColorF4(0.2f, 0.35f, 0.5f, 1));
            SceneNode fillNode = new(sliderNode) { Name = "Range fill" };
            UIBoundableTransform fill = fillNode.SetTransform<UIBoundableTransform>();
            fill.MinAnchor = Vector2.Zero;
            fill.MaxAnchor = Vector2.Zero;
            fill.NormalizedPivot = Vector2.Zero;
            fill.Height = 28;
            AddGalleryBackground(fillNode, new ColorF4(0.15f, 0.75f, 0.9f, 1));
            void RefreshRange(UIValueChange<double> _) => fill.Width = (float)(270 * slider.NormalizedValue);
            slider.ValueChanged += RefreshRange;
            RefreshRange(default);
            AddGalleryLabel(sliderNode, "Range 0–100");

            SceneNode comboNode = MakeGalleryRow(panel, "Virtual combo (1,000 options)");
            UIComboBoxComponent combo = comboNode.AddComponent<UIComboBoxComponent>()!;
            combo.VirtualOptions = new GalleryOptions();
            combo.Popup = CreateGalleryPopup(canvasRoot, "Combo popup");
            combo.PopupPosition = new Vector2(660, 320);
            AddGalleryBackground(comboNode, new ColorF4(0.25f, 0.25f, 0.25f, 1));
            UITextComponent comboLabel = AddGalleryLabel(comboNode, "Virtual combo (1,000 options)");
            combo.SelectionChanged += _ => comboLabel.Text = combo.AccessibilityValue ?? "Choose option";

            SceneNode colorNode = MakeGalleryRow(panel, "Color palette");
            UIColorSelectorComponent color = colorNode.AddComponent<UIColorSelectorComponent>()!;
            color.Popup = CreateGalleryPopup(canvasRoot, "Color popup");
            color.PopupPosition = new Vector2(660, 320);
            color.Binding = new UIValueState<ColorF4>(ColorF4.White);
            AddGalleryBackground(colorNode, ColorF4.White);
            AddGalleryLabel(colorNode, "Color palette").Color = ColorF4.Black;

            UIImageAtlasComponent atlasOwner = panel.AddComponent<UIImageAtlasComponent>()!;
            atlasOwner.Initialize(new UIImageAtlas([
                new("Gallery cyan", 2, 2, new byte[] { 0, 180, 220, 255, 0, 230, 255, 255, 0, 230, 255, 255, 0, 180, 220, 255 }),
                new("Gallery amber", 2, 2, new byte[] { 255, 180, 0, 255, 255, 220, 0, 255, 255, 220, 0, 255, 255, 180, 0, 255 }),
            ], pageSize: 8));
            SceneNode imageRow = MakeGalleryRow(panel, "Shared image atlas", EUICompositionLayerKind.StaticChrome);
            NativeUIElements.CreateImage(imageRow, atlasOwner, "Gallery cyan");
            var amber = NativeUIElements.CreateImage(imageRow, atlasOwner, "Gallery amber");
            amber.Image.BoundableTransform.Translation = new Vector2(28, 0);
            AddGalleryLabel(imageRow, "Shared image atlas").BoundableTransform.Margins = new Vector4(60, 0, 4, 0);
        }

        private static SceneNode MakeGalleryRow(SceneNode parent, string label,
            EUICompositionLayerKind kind = EUICompositionLayerKind.Overlay)
        {
            SceneNode row = new(parent) { Name = label };
            UIBoundableTransform tfm = row.SetTransform<UIBoundableTransform>();
            row.AddComponent<UICompositionLayerComponent>()!.Kind = kind;
            tfm.Width = 270;
            tfm.Height = 28;
            tfm.BlocksInputBehind = true;
            return row;
        }

        private static UIContextMenuComponent CreateGalleryPopup(SceneNode canvasRoot, string name)
        {
            SceneNode node = new(canvasRoot) { Name = name };
            node.SetTransform<UIBoundableTransform>().Visibility = EVisibility.Collapsed;
            node.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
            return node.AddComponent<UIContextMenuComponent>()!;
        }

        private static void AddGalleryBackground(SceneNode node, ColorF4 color)
        {
            UIMaterialComponent background = node.AddComponent<UIMaterialComponent>()!;
            background.SetBatchSolidColor(color);
        }

        private static UITextComponent AddGalleryLabel(SceneNode node, string text)
        {
            node.NewChild<UITextComponent>(out UITextComponent label);
            label.Text = text;
            label.FontSize = 12;
            label.Color = ColorF4.White;
            label.VerticalAlignment = EVerticalAlignment.Center;
            label.BoundableTransform.Margins = new Vector4(30, 0, 4, 0);
            return label;
        }

        private sealed class GalleryOptions : IUIComboOptionSource,
            IUIComboPrefixSearch, IUIComboEnabledNavigation
        {
            private static readonly string[] Labels = CreateLabels();
            private readonly EditorComboPrefixIndex _prefixIndex = new(Labels);
            public int Count => Labels.Length;
            public ulong Revision => 1;
            public UIComboOption GetOption(int index) => new((ulong)index + 1, Labels[index]);
            public int IndexOfId(ulong id) => id >= 1 && id <= (ulong)Count ? (int)id - 1 : -1;
            public int FindPrefix(string prefix, int startIndex) => _prefixIndex.Find(prefix, startIndex);
            public int FindNextEnabled(int startIndex, int direction)
            {
                if (direction is not (1 or -1) || startIndex < -1 || startIndex >= Count)
                    return -1;
                return direction > 0
                    ? startIndex + 1 == Count ? 0 : startIndex + 1
                    : startIndex <= 0 ? Count - 1 : startIndex - 1;
            }

            private static string[] CreateLabels()
            {
                string[] labels = new string[1000];
                for (int i = 0; i < labels.Length; i++)
                    labels[i] = $"Option {i + 1}";
                return labels;
            }
        }
    }
}
