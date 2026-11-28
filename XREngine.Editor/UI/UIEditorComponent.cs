using System;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Editor.UI.Toolbar;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.UI.Components;

/// <summary>
/// The root component for the desktop editor.
/// </summary>
[RequiresTransform(typeof(UIBoundableTransform))]
public partial class UIEditorComponent : UIComponent
{
    private UIToolbarComponent? _toolbar;
    private SceneNode? _editorRoot;
    private bool _editorTerminal;
    private bool _remakingEditor;
    private bool _retiringOwnedRoot;
    private bool _deactivatingEditor;
    private bool _activationPending;
    private bool _deferredRemakePending;
    private long _editorLifecycleIntent;
    private long _deferredLifecycleIntent;
    private ObjectConstructionIdentity _deferredComponentIdentity;
    private ObjectConstructionIdentity _deferredNodeIdentity;
    private ObjectConstructionIdentity _deferredTransformIdentity;
    private SceneNode? _deferredNode;
    private TransformBase? _deferredTransform;
    private IRuntimeWorldContext? _deferredWorld;
    public UIToolbarComponent? Toolbar => _toolbar;

    private List<ToolbarItemBase> _rootMenuOptions = [];
    public List<ToolbarItemBase> RootMenuOptions
    {
        get => _rootMenuOptions;
        set => SetField(ref _rootMenuOptions, value);
    }

    /// <summary>Height of the header row under the menu bar.</summary>
    private const float HeaderRowHeight = 34.0f;

    private float _menuHeight = 34.0f;
    public float MenuHeight
    {
        get => _menuHeight;
        set => SetField(ref _menuHeight, value);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(MenuHeight):
                if (_toolbar is not null)
                    _toolbar.SubmenuItemHeight = MenuHeight;
                break;
            case nameof(RootMenuOptions):
                if (_toolbar is not null)
                    _toolbar.RootMenuOptions = RootMenuOptions;
                break;
        }
    }

    protected override void OnComponentActivated()
    {
        if (_editorTerminal) return;
        if (_deactivatingEditor)
        {
            _activationPending = true;
            return;
        }
        _editorLifecycleIntent++;
        base.OnComponentActivated();
        if (!_editorTerminal && !IsDestroyed && !IsDestroyQueued && IsActiveInHierarchy)
            RemakeChildren();
    }

    protected override void OnComponentDeactivated()
    {
        if (_deactivatingEditor) return;
        _editorLifecycleIntent++;
        CancelDeferredRemake();
        _deactivatingEditor = true;
        try { RetireOwnedRoot(); }
        finally
        {
            try { base.OnComponentDeactivated(); }
            finally
            {
                _deactivatingEditor = false;
                bool replay = _activationPending;
                _activationPending = false;
                if (replay && !_editorTerminal && IsActiveInHierarchy)
                    OnComponentActivated();
            }
        }
    }

    protected override void OnDestroying()
    {
        _editorLifecycleIntent++;
        CancelDeferredRemake();
        _editorTerminal = true;
        _activationPending = false;
        try { RetireOwnedRoot(); }
        finally { base.OnDestroying(); }
    }

    /// <summary>
    /// Deactivates the exact editor-owned subtree before removing it from the tree.
    /// <see cref="TransformBase.Clear"/> only detaches child transforms — it does NOT
    /// call <see cref="XRComponent.OnComponentDeactivated"/> on their components.
    /// Without this, streaming pipelines, audio sources, and other lifecycle-tied
    /// resources survive as orphans and continue playing into the mixer.
    /// </summary>
    private void RetireOwnedRoot()
    {
        if (_retiringOwnedRoot) return;
        SceneNode? owned = _editorRoot;
        if (owned is null) return;
        _retiringOwnedRoot = true;
        try
        {
            if (owned.IsDestroyed || !ReferenceEquals(owned.Parent, SceneNode))
            {
                _editorRoot = null;
                _toolbar = null;
                return;
            }
            if (owned.IsActiveSelf)
                owned.IsActiveSelf = false;
            if (owned.IsDestroyed || !ReferenceEquals(owned.Parent, SceneNode))
            {
                _editorRoot = null;
                _toolbar = null;
                return;
            }
            owned.Destroy(true);
            if (!owned.IsDestroyed)
                throw new InvalidOperationException("The editor-owned UI root refused teardown.");
            _editorRoot = null;
            _toolbar = null;
        }
        finally { _retiringOwnedRoot = false; }
    }

    private bool StopTerminatedBuild(SceneNode candidate)
    {
        if (!_editorTerminal && !_deactivatingEditor && IsActive && !IsDestroyed && !IsDestroyQueued &&
            ReferenceEquals(_editorRoot, candidate) && ReferenceEquals(candidate.Parent, SceneNode))
            return false;
        if (ReferenceEquals(_editorRoot, candidate))
            RetireOwnedRoot();
        return true;
    }

    public void RemakeChildren()
    {
        if (_editorTerminal || _deactivatingEditor || !IsActive || _remakingEditor) return;
        if (PhysicsChainWorld.IsWorldMutationActive)
        {
            ScheduleDeferredRemake();
            return;
        }
        CancelDeferredRemake();
        _remakingEditor = true;
        try
        {
            using var sample = Engine.Profiler.Start("UIEditorComponent.RemakeChildren");
            RetireOwnedRoot();
            if (_editorTerminal) return;

            var splitChild = SceneNode.NewChildWithTransform(out UIBoundableTransform rootTfm, "EditorRoot");
            _editorRoot = splitChild;
            if (StopTerminatedBuild(splitChild)) return;
            UIMaterialComponent background = splitChild.AddComponent<UIMaterialComponent>()!;
            if (StopTerminatedBuild(splitChild)) return;
            // When the scene fills the window, the root stays clear so the scene shows between and through
            // the frosted panel cards. When the scene is docked in a panel, a soft gradient fills the window.
            EditorGlassTheme.SceneBehindPanels = !SceneViewPanel.IsSceneDockedInPanel;
            if (EditorGlassTheme.SceneBehindPanels)
                background.SetBatchSolidColor(ColorF4.Transparent);
            else
                EditorGlassTheme.ApplyStatic(background, EditorGlassTheme.WindowBackdrop);
            if (StopTerminatedBuild(splitChild)) return;
            background.RenderPass = (int)EDefaultRenderPass.Background;
            if (StopTerminatedBuild(splitChild)) return;
            rootTfm.MinAnchor = Vector2.Zero;
            rootTfm.MaxAnchor = Vector2.One;

            // The content (the dock, or a test view) fills the window under the top rows. It comes before the top
            // in the tree, so in painter order the menu bar and its drop-down menus draw over the panels.
            SceneNode content = splitChild.NewChildWithTransform(out UIBoundableTransform contentTfm, "EditorContent");
            if (StopTerminatedBuild(splitChild)) return;
            contentTfm.MinAnchor = Vector2.Zero;
            contentTfm.MaxAnchor = Vector2.One;
            contentTfm.Margins = new Vector4(0.0f, 0.0f, 0.0f, MenuHeight + HeaderRowHeight);

            // The top holds the main menu bar and, under it, the header row with the transform and play controls,
            // like the ImGui menu bar and toolbar.
            SceneNode top = splitChild.NewChildWithTransform(out UIDualSplitTransform topSplit, "EditorTop");
            if (StopTerminatedBuild(splitChild)) return;
            topSplit.MinAnchor = new Vector2(0.0f, 1.0f);
            topSplit.MaxAnchor = new Vector2(1.0f, 1.0f);
            topSplit.NormalizedPivot = new Vector2(0.0f, 1.0f);
            topSplit.Height = MenuHeight + HeaderRowHeight;
            topSplit.VerticalSplit = true;
            topSplit.FirstFixedSize = true;
            topSplit.FixedSize = MenuHeight;
            topSplit.SplitterSize = 0.0f;
            top.NewChild(out UIToolbarComponent toolbarComp, "Menu");
            if (StopTerminatedBuild(splitChild)) return;
            top.NewChild(out EditorHeaderToolbarComponent _, "Header");
            if (StopTerminatedBuild(splitChild)) return;
            toolbarComp.RootMenuOptions = RootMenuOptions;
            if (StopTerminatedBuild(splitChild)) return;
            toolbarComp.SubmenuItemHeight = MenuHeight;
            if (StopTerminatedBuild(splitChild)) return;
            _toolbar = toolbarComp;

            if (EditorUnitTests.Toggles.UltralightWebView)
            {
                SceneNode webRoot = content.NewChildWithTransform<UIBoundableTransform>(out _, "WebViewRoot");
                if (StopTerminatedBuild(splitChild)) return;
                var webComp = webRoot.AddComponent<UIWebViewComponent>()!;
                if (StopTerminatedBuild(splitChild)) return;
                webRoot.AddComponent<UIWebViewInputComponent>();
                if (StopTerminatedBuild(splitChild)) return;
                RendererBackendId backendId = AbstractRenderer.Current?.BackendId
                    ?? throw new InvalidOperationException(
                        "An active renderer is required before creating the accelerated web view.");
                webComp.Backend = WebRendererBackendRegistry.CreateRequiredAccelerated(backendId);
                webComp.TransparentBackground = false;
                webComp.Url = string.IsNullOrWhiteSpace(EditorUnitTests.Toggles.UltralightWebViewUrl)
                    ? "https://blackjaxvr.com"
                    : EditorUnitTests.Toggles.UltralightWebViewUrl;
                if (StopTerminatedBuild(splitChild)) return;
                Debug.UI($"[UIEditor] Ultralight web view enabled. Url='{webComp.Url}'");

                return;
            }

            if (EditorUnitTests.Toggles.VideoStreaming)
            {
                SceneNode videoRoot = content.NewChildWithTransform<UIBoundableTransform>(out _, "VideoRoot");
                if (StopTerminatedBuild(splitChild)) return;

                // Configure video components before first activation so we don't start the
                // streaming pipeline with the default URL and then immediately restart with
                // an override (which can leave transient black output if the override fails).
                bool wasActive = videoRoot.IsActiveSelf;
                videoRoot.IsActiveSelf = false;

                var videoComp = videoRoot.AddComponent<UIVideoComponent>()!;
                if (StopTerminatedBuild(splitChild)) return;
                if (!string.IsNullOrWhiteSpace(EditorUnitTests.Toggles.VideoStreamingUrl))
                    videoComp.StreamUrl = EditorUnitTests.Toggles.VideoStreamingUrl;
                if (StopTerminatedBuild(splitChild)) return;
                if (EditorUnitTests.Toggles.VideoStreamingAudio)
                {
                    var audioSource = videoRoot.AddComponent<AudioSourceComponent>()!;
                    if (StopTerminatedBuild(splitChild)) return;
                    audioSource.RelativeToListener = true;
                    audioSource.SteamAudioNonSpatialStereo = true;
                    if (StopTerminatedBuild(splitChild)) return;
                }

                videoRoot.IsActiveSelf = wasActive;
                if (StopTerminatedBuild(splitChild)) return;

                return;
            }

            SceneNode dockNode = content.NewChildWithTransform<UIBoundableTransform>(out UIBoundableTransform dockBounds, "DockableRoot");
            // Half a gutter here plus half a gutter around each card gives one full gutter at the window edge.
            dockBounds.Margins = new Vector4(EditorGlassTheme.PanelGutter * 0.5f);
            if (StopTerminatedBuild(splitChild)) return;
            if (EditorUnitTests.Toggles.BackgroundShader)
            {
                XRShader bgShader = ShaderHelper.LoadEngineShader("UI/Backgrounds/Surf2.fs", EShaderType.Fragment);
                var mat = new XRMaterial(bgShader)
                {
                    RenderPass = (int)EDefaultRenderPass.OnTopForward,
                    RenderOptions = new RenderingParameters
                    {
                        CullMode = ECullMode.None,
                        DepthTest = new DepthTest()
                        {
                            Enabled = ERenderParamUsage.Enabled,
                            Function = EComparison.Always,
                            UpdateDepth = false
                        },
                        RequiredEngineUniforms = EUniformRequirements.RenderTime | EUniformRequirements.ViewportDimensions,
                    }
                };
                var comp = dockNode.AddComponent<UIMaterialComponent>()!;
                if (StopTerminatedBuild(splitChild)) return;
                comp.Material = mat;
            }
            if (StopTerminatedBuild(splitChild)) return;
            NativeDockTreeHost.Build(dockNode, WorldAs<RuntimeWorld>());

            ////Create the dockable windows transform for panels
            //var dockableNode = splitChild.NewChild<UIDockingRootComponent>(out var root);
            //var dock = root.DockingTransform;

            //var leftNode = dock.Left?.SceneNode;
            //if (leftNode is not null)
            //{
            //    leftNode.Transform.Clear();
            //    leftNode.NewChild<HierarchyPanel>(out var hierarchy);

            //    hierarchy.RootNodes.Clear();
            //    if (World is not null)
            //        hierarchy.RootNodes.AddRange(World.RootNodes);
            //}

            //var rightNode = dock.Right?.SceneNode;
            //if (rightNode is not null)
            //{
            //    rightNode.Transform.Clear();
            //    rightNode.NewChild<InspectorPanel>(out var inspector);
            //}

            //var bottomNode = dock.Bottom?.SceneNode;
            //if (bottomNode is not null)
            //{
            //    bottomNode.Transform.Clear();
            //    bottomNode.NewChild<ConsolePanel>(out var console);
            //}
        }
        finally { _remakingEditor = false; }
    }

    private void ScheduleDeferredRemake()
    {
        if (_deferredRemakePending || SceneNode is not { } node ||
            node.Transform is not { } transform || World is not { } world)
            return;
        _deferredRemakePending = true;
        _deferredLifecycleIntent = _editorLifecycleIntent;
        _deferredComponentIdentity = ObjectConstructionIdentity.Capture(this);
        _deferredNodeIdentity = ObjectConstructionIdentity.Capture(node);
        _deferredTransformIdentity = ObjectConstructionIdentity.Capture(transform);
        _deferredNode = node;
        _deferredTransform = transform;
        _deferredWorld = world;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, CompleteDeferredRemake);
    }

    private void CompleteDeferredRemake()
    {
        if (!_deferredRemakePending) return;
        if (_editorTerminal || _deactivatingEditor || IsDestroyed || IsDestroyQueued ||
            !IsActiveInHierarchy || _deferredNode?.IsDestroyed == true ||
            _deferredNode?.IsDestroyQueued == true ||
            _deferredWorld is RuntimeWorld { IsDisposing: true } ||
            _deferredLifecycleIntent != _editorLifecycleIntent ||
            !_deferredComponentIdentity.IsCurrent || !_deferredNodeIdentity.IsCurrent ||
            !_deferredTransformIdentity.IsCurrent ||
            !ReferenceEquals(SceneNode, _deferredNode) ||
            !ReferenceEquals(SceneNode.Transform, _deferredTransform) ||
            !ReferenceEquals(World, _deferredWorld) ||
            !ReferenceEquals(SceneNode.World, _deferredWorld) ||
            !ReferenceEquals(_deferredTransform?.World, _deferredWorld))
        {
            CancelDeferredRemake();
            return;
        }
        if (PhysicsChainWorld.IsWorldMutationActive) return;
        CancelDeferredRemake();
        RemakeChildren();
    }

    private void CancelDeferredRemake()
    {
        if (!_deferredRemakePending) return;
        _deferredRemakePending = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompleteDeferredRemake);
        _deferredComponentIdentity = default;
        _deferredNodeIdentity = default;
        _deferredTransformIdentity = default;
        _deferredNode = null;
        _deferredTransform = null;
        _deferredWorld = null;
    }

    /// <summary>
    /// The scene node that contains the menu options.
    /// </summary>
    public SceneNode ToolbarNode
    {
        get
        {
            var first = _editorRoot;
            if (first is null || first.IsDestroyed)
                RemakeChildren();
            first = _editorRoot;
            if (first is null || first.Transform.Children.Count < 2)
                RemakeChildren();
            first = _editorRoot ?? throw new InvalidOperationException("The editor UI root is unavailable.");
            // The content node is first and the top rows (menu bar and header) are last.
            return first!.LastChild!;
        }
    }
    /// <summary>
    /// The scene node that contains the dockable windows.
    /// </summary>
    public SceneNode DockableNode
    {
        get
        {
            var first = _editorRoot;
            if (first is null || first.IsDestroyed)
                RemakeChildren();
            first = _editorRoot;
            if (first is null || first.Transform.Children.Count < 2)
                RemakeChildren();
            first = _editorRoot ?? throw new InvalidOperationException("The editor UI root is unavailable.");
            return first.LastChild!;
        }
    }
}
