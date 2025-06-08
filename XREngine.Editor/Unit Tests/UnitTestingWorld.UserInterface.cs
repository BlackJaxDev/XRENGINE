﻿using Silk.NET.Input;
using System.Numerics;
using XREngine.Actors.Types;
using XREngine.Components;
using XREngine.Editor.UI.Components;
using XREngine.Editor.UI.Toolbar;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Components.Scripting;
using XREngine.Scene.Transforms;
using XREngine.Scene.Components.UI;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static class UserInterface
    {
        private const bool DockFPSTopLeft = false;

        private static readonly Queue<float> _fpsAvg = new();
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
            textTransform.Width = null;
            textTransform.Height = null;
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
            var canvasTfm = rootCanvasNode.GetTransformAs<UICanvasTransform>(true)!;
            canvasTfm.DrawSpace = ECanvasDrawSpace.Screen;
            canvasTfm.Width = 1920.0f;
            canvasTfm.Height = 1080.0f;
            canvasTfm.CameraDrawSpaceDistance = 10.0f;
            canvasTfm.Padding = new Vector4(0.0f);

            if (Toggles.VisualizeQuadtree)
                rootCanvasNode.AddComponent<DebugVisualizeQuadtreeComponent>();

            if (screenSpaceCamera is not null)
                screenSpaceCamera.UserInterface = canvas;

            AddFPSText(null, rootCanvasNode);

            if (Toggles.RiveUI)
            {
                var riveUI = rootCanvasNode.AddComponent<RiveUIComponent>()!;
                riveUI.SetSource("RiveAssets/ball.riv");
            }

            if (Toggles.AddEditorUI)
            {
                //Add input handler
                var input = rootCanvasNode.AddComponent<UIInputComponent>()!;
                input.OwningPawn = pawnForInput;

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

            return canvas;
        }

        private static UIEditorComponent? _editorComponent = null;

        public static void GameCSProjLoader_OnAssemblyUnloaded(string obj) => RemakeMenu();
        public static void GameCSProjLoader_OnAssemblyLoaded(string arg1, GameCSProjLoader.AssemblyData arg2) => RemakeMenu();

        public static void RemakeMenu()
        {
            if (_editorComponent is not null)
                _editorComponent.RootMenuOptions = GenerateRootMenu();
        }

        //Signals the camera to take a picture of the current view.
        public static void TakeScreenshot(UIInteractableComponent comp)
        {
            //Debug.Out("Take Screenshot clicked");

            var camera = Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One).ControlledPawn as EditorFlyingCameraPawnComponent;
            camera?.TakeScreenshot();
        }
        //Loads a project from the file system.
        public static void LoadProject(UIInteractableComponent comp)
        {
            //Debug.Out("Load Project clicked");
        }
        //Saves all modified assets in the project.
        public static async void SaveAll(UIInteractableComponent comp)
        {
            await Engine.Assets.SaveAllAsync();
        }

        //Generates the root menu for the editor UI.
        //TODO: allow scripts to add menu options with attributes
        public static List<ToolbarItemBase> GenerateRootMenu()
        {
            List<ToolbarItemBase> buttons = [
                new ToolbarButton("File", [Key.ControlLeft, Key.F],
            [
                new ToolbarButton("Save All", SaveAll),
                new ToolbarButton("Open", [
                    new ToolbarButton("Project", LoadProject),
                    ])
            ]),
            new ToolbarButton("Edit"),
            new ToolbarButton("Assets"),
            new ToolbarButton("Tools", [Key.ControlLeft, Key.T],
            [
                new ToolbarButton("Take Screenshot", TakeScreenshot),
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
                TransformTool3D.GetInstance(x.Transform);
                x.Activated -= Edit;
            }

            if (node.IsActiveInHierarchy && node.World is not null)
                TransformTool3D.GetInstance(node.Transform);
            else
                node.Activated += Edit;
        }
    }
}