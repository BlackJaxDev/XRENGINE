using System;
using Silk.NET.Input;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Actors.Types;
using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Components.Scripting;
using XREngine.Core.Files;
using XREngine.Editor.UI;
using XREngine.Editor.UI.Components;
using XREngine.Editor.UI.Toolbar;
using XREngine.Editor.UI.Tools;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Components.UI;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private static readonly bool DockFPSTopLeft = false;
        private static readonly Queue<float> _fpsAvg = new();

        private static UIEditorComponent? _editorComponent = null;

        private static void TickFPS(UITextComponent t)
        {
            _fpsAvg.Enqueue(1.0f / Engine.Time.Timer.Render.Delta);
            if (_fpsAvg.Count > 60)
                _fpsAvg.Dequeue();
            string str = $"{MathF.Round(_fpsAvg.Sum() / _fpsAvg.Count)}hz";
            var net = Engine.Networking;
            if (net is not null)
            {
                str += $"\n{net.AverageRoundTripTimeMs}ms";
                str += $"\n{net.DataPerSecondString}";
                str += $"\n{net.PacketsPerSecond}p/s";
            }
            t.Text = str;
        }

        //Simple FPS counter in the bottom right for debugging.
        public static UITextComponent AddFPSText(FontGlyphSet? font, SceneNode parentNode)
        {
            SceneNode textNode = new(parentNode) { Name = "TestTextNode" };
            UITextComponent text = textNode.AddComponent<UITextComponent>()!;
            text.Font = font;
            text.FontSize = 22;
            text.WrapMode = FontGlyphSet.EWrapMode.None;
            text.RegisterAnimationTick<UITextComponent>(TickFPS);
            var textTransform = textNode.GetTransformAs<UIBoundableTransform>(true)!;
            if (DockFPSTopLeft)
            {
                textTransform.MinAnchor = new Vector2(0.0f, 1.0f);
                textTransform.MaxAnchor = new Vector2(0.0f, 1.0f);
                textTransform.NormalizedPivot = new Vector2(0.0f, 1.0f);
            }
            else
            {
                textTransform.MinAnchor = new Vector2(1.0f, 0.0f);
                textTransform.MaxAnchor = new Vector2(1.0f, 0.0f);
                textTransform.NormalizedPivot = new Vector2(1.0f, 0.0f);
            }
            textTransform.Margins = new Vector4(10.0f, 10.0f, 10.0f, 10.0f);
            textTransform.Scale = new Vector3(1.0f);
            return text;
        }

        public static void ShowMenu(UICanvasComponent canvas, bool screenSpace, TransformBase? parent)
        {
            var canvasTfm = canvas.CanvasTransform;
            canvasTfm.Parent = parent;
            canvasTfm.DrawSpace = screenSpace ? ECanvasDrawSpace.Screen : ECanvasDrawSpace.World;
            canvas.IsActive = !canvas.IsActive;
        }

        //The full editor UI - includes a toolbar, inspector, viewport and scene hierarchy.
        public static UICanvasComponent CreateEditorUI(SceneNode parent, CameraComponent? screenSpaceCamera, PawnComponent? pawnForInput = null)
        {
            var rootCanvasNode = new SceneNode(parent) { Name = "TestUINode" };
            var canvas = rootCanvasNode.AddComponent<UICanvasComponent>()!;
            var canvasTfm = canvas.CanvasTransform;
            canvasTfm.DrawSpace = ECanvasDrawSpace.Screen;
            canvasTfm.Width = 1920.0f;
            canvasTfm.Height = 1080.0f;
            canvasTfm.Padding = new Vector4(0.0f);

            if (Toggles.RiveUI || Toggles.DearImGuiUI || Toggles.DearImGuiProfiler || Toggles.AddEditorUI)
            {
                var inputComponent = rootCanvasNode.AddComponent<UICanvasInputComponent>();
                if (inputComponent is not null)
                {
                    inputComponent.OwningPawn = pawnForInput;
                    EditorDragDropUtility.Initialize(inputComponent);
                }
            }

            if (Toggles.VisualizeQuadtree)
                rootCanvasNode.AddComponent<DebugVisualizeQuadtreeComponent>();

            screenSpaceCamera?.UserInterface = canvas;

            if (UnitTestingWorld.Toggles.RiveUI)
            {
                bool disableRiveUi = false;
                SceneNode riveNode = new(rootCanvasNode) { Name = "RIVE Node" };
                var tfm = riveNode.SetTransform<UIBoundableTransform>();
                tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);

                try
                {
                    var riveComponent = riveNode.AddComponent<RiveUIComponent>();
                    if (riveComponent is null)
                        disableRiveUi = true;
                    else
                        riveComponent.SetSource("RiveAssets/switcher.riv");
                }
                catch (DllNotFoundException ex)
                {
                    Debug.LogWarning($"Rive native library missing: {ex.Message}. Disabling Rive UI.");
                    disableRiveUi = true;
                }
                catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException dllEx)
                {
                    Debug.LogWarning($"Rive native library failed to load: {dllEx.Message}. Disabling Rive UI.");
                    disableRiveUi = true;
                }

                if (disableRiveUi)
                {
                    Toggles.RiveUI = false;
                    riveNode.Parent = null;
                }
            }

            bool addDearImGui = UnitTestingWorld.Toggles.DearImGuiUI || UnitTestingWorld.Toggles.DearImGuiProfiler;
            if (addDearImGui)
            {
                SceneNode dearImGuiNode = new(rootCanvasNode) { Name = "Dear ImGui Node" };
                var tfm = dearImGuiNode.SetTransform<UIBoundableTransform>();
                tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
                tfm.Width = null;
                tfm.Height = null;

                var dearImGuiComponent = dearImGuiNode.AddComponent<DearImGuiComponent>();
                if (dearImGuiComponent is null)
                {
                    Toggles.DearImGuiUI = false;
                    Toggles.DearImGuiProfiler = false;
                    dearImGuiNode.Parent = null;
                }
                else
                {
                    dearImGuiComponent.Draw += DrawDearImGuiTest;
                }
            }
            
            if (Toggles.AddEditorUI)
            {
                //This will take care of editor UI arrangement operations for us
                var mainUINode = rootCanvasNode.NewChild<UIEditorComponent>(out UIEditorComponent? editorComp);
                if (editorComp.UITransform is UIBoundableTransform tfm)
                {
                    tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                    tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                    tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
                    tfm.Translation = new Vector2(0.0f, 0.0f);
                    tfm.Width = null;
                    tfm.Height = null;
                }
                _editorComponent = editorComp;
                RemakeMenu();

                GameCSProjLoader.OnAssemblyLoaded += GameCSProjLoader_OnAssemblyLoaded;
                GameCSProjLoader.OnAssemblyUnloaded += GameCSProjLoader_OnAssemblyUnloaded;
            }

            AddFPSText(null, rootCanvasNode);

            return canvas;
        }

        public static void GameCSProjLoader_OnAssemblyUnloaded(string obj)
        {
            RemakeMenu();
            InvalidateTypeDescriptorCache();
        }

        public static void GameCSProjLoader_OnAssemblyLoaded(string arg1, GameCSProjLoader.AssemblyData arg2)
        {
            RemakeMenu();
            InvalidateTypeDescriptorCache();
        }

        public static void RemakeMenu()
        {
            if (_editorComponent != null)
                _editorComponent.RootMenuOptions = GenerateRootMenu();
        }

        //Signals the camera to take a picture of the current view.
        public static void TakeScreenshot(UIInteractableComponent comp)
        {
            //Debug.Out("Take Screenshot clicked");

            var camera = Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One).ControlledPawn as EditorFlyingCameraPawnComponent;
            camera?.TakeScreenshot();
        }

        //Opens a dialog to select and load a project file.
        public static void OpenProjectDialog(UIInteractableComponent comp)
        {
            XREngine.Editor.UI.ImGuiFileBrowser.OpenFile(
                "OpenProjectDialog",
                "Open Project",
                result =>
                {
                    if (result.Success && !string.IsNullOrEmpty(result.SelectedPath))
                    {
                        Engine.LoadProject(result.SelectedPath);
                    }
                },
                $"XREngine Projects (*.{XRProject.ProjectExtension})|*.{XRProject.ProjectExtension}|All Files (*.*)|*.*"
            );
        }

        private static bool _showNewProjectDialog = false;
        private static byte[] _newProjectNameBuffer = new byte[256];
        private static byte[] _newProjectPathBuffer = new byte[512];

        //Shows the new project dialog.
        public static void ShowNewProjectDialog()
        {
            _showNewProjectDialog = true;
            Array.Clear(_newProjectNameBuffer);
            Array.Clear(_newProjectPathBuffer);
            
            // Set default path to user's Documents folder
            string defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            System.Text.Encoding.UTF8.GetBytes(defaultPath, 0, Math.Min(defaultPath.Length, _newProjectPathBuffer.Length - 1), _newProjectPathBuffer, 0);
        }

        //Opens a folder browser to select project location
        private static void BrowseForProjectLocation()
        {
            ImGuiFileBrowser.SelectFolder(
                "SelectProjectLocation",
                "Select Project Location",
                result =>
                {
                    if (result.Success && !string.IsNullOrEmpty(result.SelectedPath))
                    {
                        Array.Clear(_newProjectPathBuffer);
                        System.Text.Encoding.UTF8.GetBytes(result.SelectedPath, 0, Math.Min(result.SelectedPath.Length, _newProjectPathBuffer.Length - 1), _newProjectPathBuffer, 0);
                    }
                }
            );
        }

        //Draws the new project dialog if visible.
        private static void DrawNewProjectDialog()
        {
            // Draw any active file browser dialogs
            ImGuiFileBrowser.DrawDialogs();

            if (!_showNewProjectDialog)
                return;

            ImGuiNET.ImGui.OpenPopup("New Project");

            var viewport = ImGuiNET.ImGui.GetMainViewport();
            ImGuiNET.ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiNET.ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
            ImGuiNET.ImGui.SetNextWindowSize(new System.Numerics.Vector2(500, 180));

            if (ImGuiNET.ImGui.BeginPopupModal("New Project", ref _showNewProjectDialog, ImGuiNET.ImGuiWindowFlags.NoResize))
            {
                ImGuiNET.ImGui.Text("Project Name:");
                ImGuiNET.ImGui.InputText("##ProjectName", _newProjectNameBuffer, (uint)_newProjectNameBuffer.Length);

                ImGuiNET.ImGui.Text("Project Location:");
                ImGuiNET.ImGui.InputText("##ProjectPath", _newProjectPathBuffer, (uint)_newProjectPathBuffer.Length);
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.Button("Browse..."))
                {
                    BrowseForProjectLocation();
                }

                ImGuiNET.ImGui.Separator();

                if (ImGuiNET.ImGui.Button("Create", new System.Numerics.Vector2(120, 0)))
                {
                    string projectName = ExtractString(_newProjectNameBuffer);
                    string projectPath = ExtractString(_newProjectPathBuffer);

                    if (!string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(projectPath))
                    {
                        string fullPath = System.IO.Path.Combine(projectPath, projectName);
                        if (Engine.CreateAndLoadProject(fullPath, projectName))
                        {
                            _showNewProjectDialog = false;
                        }
                        else
                        {
                        Debug.LogWarning($"Failed to create project: {fullPath}");
                        }
                    }
                }
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.Button("Cancel", new System.Numerics.Vector2(120, 0)))
                {
                    _showNewProjectDialog = false;
                }

                ImGuiNET.ImGui.EndPopup();
            }
        }

        private static string ExtractString(byte[] buffer)
        {
            int nullIndex = Array.IndexOf(buffer, (byte)0);
            int length = nullIndex >= 0 ? nullIndex : buffer.Length;
            return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        }

        //Saves all modified assets in the project.
        public static async void SaveAll(UIInteractableComponent? comp)
        {
            await Engine.Assets.SaveAllAsync();
            RefreshSaveMenu();
        }

        private static ToolbarButton? _saveMenu;
        private static bool _saveMenuHooksInitialized;

        private static void EnsureSaveMenuHooks()
        {
            if (_saveMenuHooksInitialized)
                return;

            if (Engine.Assets is not null)
            {
                Engine.Assets.AssetMarkedDirty += OnAssetMarkedDirty;
                Engine.Assets.AssetSaved += OnAssetSaved;
            }
            _saveMenuHooksInitialized = true;
            RefreshSaveMenu();
        }

        private static void OnAssetMarkedDirty(XRAsset asset)
        {
            RefreshSaveMenu();
        }

        private static void OnAssetSaved(XRAsset asset)
        {
            RefreshSaveMenu();
        }

        private static void RefreshSaveMenu()
        {
            if (_saveMenu is null)
                return;

            _saveMenu.ChildOptions.Clear();

            var assets = Engine.Assets;
            if (assets is null)
            {
                _saveMenu.ChildOptions.Add(new ToolbarButton("No asset manager available"));
                return;
            }

            var dirtyAssets = assets.DirtyAssets.ToArray();
            if (dirtyAssets.Length == 0)
            {
                _saveMenu.ChildOptions.Add(new ToolbarButton("No modified assets"));
                return;
            }

            foreach (var asset in dirtyAssets)
            {
                string displayName = GetAssetDisplayName(asset.Value);
                var capturedAsset = asset;
                _saveMenu.ChildOptions.Add(new ToolbarButton(displayName, _ => SaveSingleAsset(capturedAsset.Value)));
            }
        }

        private static string GetAssetDisplayName(XRAsset asset)
        {
            if (!string.IsNullOrWhiteSpace(asset.Name))
                return asset.Name;
            if (!string.IsNullOrWhiteSpace(asset.FilePath))
                return Path.GetFileNameWithoutExtension(asset.FilePath);
            return $"{asset.GetType().Name} ({asset.ID.ToString()[..8]})";
        }

        private static async void SaveSingleAsset(XRAsset asset)
        {
            var assets = Engine.Assets;
            if (assets is null)
                return;

            await assets.SaveAsync(asset);
            RefreshSaveMenu();
        }

        //Generates the root menu for the editor UI.
        //TODO: allow scripts to add menu options with attributes
        public static List<ToolbarItemBase> GenerateRootMenu()
        {
            EnsureUndoMenuHooks();
            EnsureSaveMenuHooks();
            _saveMenu ??= new ToolbarButton("Save");
            RefreshSaveMenu();

            List<ToolbarItemBase> buttons = [
                new ToolbarButton("File", [Key.ControlLeft, Key.F],
            [
                _saveMenu,
                new ToolbarButton("Save All", SaveAll, [Key.ControlLeft, Key.ShiftLeft, Key.S]),
                new ToolbarButton("Save Settings", [
                    new ToolbarButton("Save Engine Settings", _ => Engine.SaveProjectEngineSettings()),
                    new ToolbarButton("Save User Settings", _ => Engine.SaveProjectUserSettings()),
                    new ToolbarButton("Save All Settings", _ => Engine.SaveProjectSettings()),
                ]),
                new ToolbarButton("Open", [
                    new ToolbarButton("Project", OpenProjectDialog),
                ]),
                new ToolbarButton("New Project", _ => ShowNewProjectDialog()),
            ]),
            CreateEditMenu(),
            new ToolbarButton("Assets"),
            new ToolbarButton("Tools", [Key.ControlLeft, Key.T],
            [
                new ToolbarButton("Take Screenshot", TakeScreenshot),
                new ToolbarButton("Shader Locking Tool", _ => ShaderLockingWindow.Instance.Open()),
                new ToolbarButton("Shader Analyzer Tool", _ => ShaderAnalyzerWindow.Instance.Open()),
            ]),
            new ToolbarButton("View"),
            new ToolbarButton("Window"),
            new ToolbarButton("Help"),
        ];

            //Add dynamically loaded menu options
            foreach (GameCSProjLoader.AssemblyData assembly in GameCSProjLoader.LoadedAssemblies.Values)
            {
                foreach (Type menuItem in assembly.MenuItems)
                {
                    if (!menuItem.IsSubclassOf(typeof(ToolbarItemBase)))
                        continue;

                    buttons.Add((ToolbarItemBase)Activator.CreateInstance(menuItem)!);
                }
            }

            return buttons;
        }

        public static void EnableTransformToolForNode(SceneNode? node)
        {
            if (node is null)
                return;

            //we have to wait for the scene node to be activated in the instance of the world before we can attach the transform tool
            void Edit(SceneNode x)
            {
                var tool = TransformTool3D.GetInstance(x.Transform);
                TransformToolUndoAdapter.Attach(tool);
                x.Activated -= Edit;
            }

            if (node.IsActiveInHierarchy && node.World is not null)
            {
                var tool = TransformTool3D.GetInstance(node.Transform);
                TransformToolUndoAdapter.Attach(tool);
            }
            else
                node.Activated += Edit;
        }

        private static ToolbarButton? _undoHistoryMenu;
        private static bool _undoHooksInitialized;

        private static void EnsureUndoMenuHooks()
        {
            if (_undoHooksInitialized)
                return;

            Undo.HistoryChanged += RefreshUndoHistoryMenu;
            _undoHooksInitialized = true;
            RefreshUndoHistoryMenu();
        }

        private static ToolbarButton CreateEditMenu()
        {
            var undoButton = new ToolbarButton("Undo", OnToolbarUndo, [Key.ControlLeft, Key.Z]);
            var redoButton = new ToolbarButton("Redo", OnToolbarRedo, [Key.ControlLeft, Key.Y]);
            _undoHistoryMenu ??= new ToolbarButton("Undo History");
            RefreshUndoHistoryMenu();

            return new ToolbarButton("Edit", undoButton, redoButton, _undoHistoryMenu);
        }

        private static void OnToolbarUndo(UIInteractableComponent _)
        {
            Undo.TryUndo();
        }

        private static void OnToolbarRedo(UIInteractableComponent _)
        {
            Undo.TryRedo();
        }

        private static void RefreshUndoHistoryMenu()
        {
            if (_undoHistoryMenu is null)
                return;

            _undoHistoryMenu.ChildOptions.Clear();

            var history = Undo.PendingUndo;
            if (history.Count == 0)
            {
                _undoHistoryMenu.ChildOptions.Add(new ToolbarButton("No undo steps available"));
                return;
            }

            int index = 0;
            foreach (var entry in history)
            {
                int targetIndex = index;
                string label = $"{targetIndex + 1}. {entry.Description}";
                _undoHistoryMenu.ChildOptions.Add(new ToolbarButton(label, _ => UndoMultiple(targetIndex)));
                index++;
                if (index >= 15)
                    break;
            }
        }

        private static void UndoMultiple(int targetIndex)
        {
            for (int i = 0; i <= targetIndex; i++)
            {
                if (!Undo.TryUndo())
                    break;
            }
        }

        private static void RedoMultiple(int targetIndex)
        {
            for (int i = 0; i <= targetIndex; i++)
            {
                if (!Undo.TryRedo())
                    break;
            }
        }

        private static void InvalidateTypeDescriptorCache()
        {
            // Placeholder until the editor exposes type-descriptor caching again.
        }
    }
}