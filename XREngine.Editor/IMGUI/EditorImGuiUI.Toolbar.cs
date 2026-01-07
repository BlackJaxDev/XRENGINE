using ImGuiNET;
using System;
using System.Numerics;
using XREngine;
using XREngine.Scene.Components.Editing;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    // Toolbar state
    private static bool _snapEnabled;
    private static float _snapTranslationValue = 1.0f;
    private static float _snapRotationValue = 15.0f;
    private static float _snapScaleValue = 0.1f;
    
    private const float ToolbarButtonSize = 26f;
    private const float ToolbarHeight = 34f;
    private const float ToolbarSpacing = 4f;
    private const float ToolbarSectionSpacing = 16f;

    /// <summary>
    /// Draws the main editor toolbar below the menu bar.
    /// Contains transform tools, transform space selector, snapping controls, and play mode controls.
    /// </summary>
    private static void DrawToolbar()
    {
        var viewport = ImGui.GetMainViewport();
        float menuBarHeight = ImGui.GetFrameHeight();
        
        // Position toolbar below menu bar
        ImGui.SetNextWindowPos(new Vector2(viewport.Pos.X, viewport.Pos.Y + menuBarHeight));
        ImGui.SetNextWindowSize(new Vector2(viewport.Size.X, ToolbarHeight));
        
        ImGuiWindowFlags toolbarFlags = 
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNavFocus;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        
        if (ImGui.Begin("##EditorToolbar", toolbarFlags))
        {
            // Left section: Transform tools
            DrawTransformModeButtons();
            ImGui.SameLine(0f, ToolbarSectionSpacing);
            DrawTransformSpaceButtons();
            ImGui.SameLine(0f, ToolbarSectionSpacing);
            DrawSnapControls();
            
            // Center section: Play mode buttons
            float playButtonsWidth = GetPlayControlsEstimatedWidth();
            float currentX = ImGui.GetCursorPosX();

            float contentMinX = ImGui.GetWindowContentRegionMin().X;
            float contentMaxX = ImGui.GetWindowContentRegionMax().X;
            float contentWidth = MathF.Max(0f, contentMaxX - contentMinX);
            float desiredX = contentMinX + (contentWidth - playButtonsWidth) * 0.5f;

            float minX = currentX + ToolbarSectionSpacing;
            float targetX = MathF.Max(minX, desiredX);

            ImGui.SameLine(0f, 0f);
            ImGui.SetCursorPosX(targetX);
            DrawPlayModeButtons();
        }
        ImGui.End();
        
        ImGui.PopStyleVar(3);
    }

    /// <summary>
    /// Draws transform mode buttons (Translate, Rotate, Scale).
    /// </summary>
    private static void DrawTransformModeButtons()
    {
        var currentMode = TransformTool3D.TransformMode;
        
        ImGui.BeginGroup();
        DrawToolbarSectionLabel("Transform:");
        
        // Translate button
        bool isTranslate = currentMode == ETransformMode.Translate;
        if (DrawToolbarToggleButton("W", "Translate (W)", isTranslate, GetTransformModeColor(ETransformMode.Translate, isTranslate), SvgEditorIcons.IconTranslate))
            TransformTool3D.TransformMode = ETransformMode.Translate;
        
        ImGui.SameLine(0f, 2f);
        
        // Rotate button
        bool isRotate = currentMode == ETransformMode.Rotate;
        if (DrawToolbarToggleButton("E", "Rotate (E)", isRotate, GetTransformModeColor(ETransformMode.Rotate, isRotate), SvgEditorIcons.IconRotate))
            TransformTool3D.TransformMode = ETransformMode.Rotate;
        
        ImGui.SameLine(0f, 2f);
        
        // Scale button
        bool isScale = currentMode == ETransformMode.Scale;
        if (DrawToolbarToggleButton("R", "Scale (R)", isScale, GetTransformModeColor(ETransformMode.Scale, isScale), SvgEditorIcons.IconScale))
            TransformTool3D.TransformMode = ETransformMode.Scale;
        
        ImGui.EndGroup();
    }

    /// <summary>
    /// Draws transform space selector buttons (World, Local, Parent, Screen).
    /// </summary>
    private static void DrawTransformSpaceButtons()
    {
        var currentSpace = TransformTool3D.TransformSpace;
        
        ImGui.BeginGroup();
        DrawToolbarSectionLabel("Space:");
        
        // World
        bool isWorld = currentSpace == ETransformSpace.World;
        if (DrawToolbarToggleButton("Wld", "World Space", isWorld, GetTransformSpaceColor(isWorld), SvgEditorIcons.IconWorld))
            TransformTool3D.TransformSpace = ETransformSpace.World;
        
        ImGui.SameLine(0f, 2f);
        
        // Local
        bool isLocal = currentSpace == ETransformSpace.Local;
        if (DrawToolbarToggleButton("Loc", "Local Space", isLocal, GetTransformSpaceColor(isLocal), SvgEditorIcons.IconLocal))
            TransformTool3D.TransformSpace = ETransformSpace.Local;
        
        ImGui.SameLine(0f, 2f);
        
        // Parent
        bool isParent = currentSpace == ETransformSpace.Parent;
        if (DrawToolbarToggleButton("Par", "Parent Space", isParent, GetTransformSpaceColor(isParent), SvgEditorIcons.IconParent))
            TransformTool3D.TransformSpace = ETransformSpace.Parent;
        
        ImGui.SameLine(0f, 2f);
        
        // Screen
        bool isScreen = currentSpace == ETransformSpace.Screen;
        if (DrawToolbarToggleButton("Scr", "Screen Space", isScreen, GetTransformSpaceColor(isScreen), SvgEditorIcons.IconScreen))
            TransformTool3D.TransformSpace = ETransformSpace.Screen;
        
        ImGui.EndGroup();
    }

    /// <summary>
    /// Draws snapping controls.
    /// </summary>
    private static void DrawSnapControls()
    {
        ImGui.BeginGroup();
        ImGui.AlignTextToFramePadding();
        
        // Snap toggle button
        Vector4 snapColor = _snapEnabled 
            ? new Vector4(0.15f, 0.55f, 0.95f, 1.0f) 
            : new Vector4(0.4f, 0.4f, 0.4f, 1.0f);
        
        if (DrawToolbarToggleButton("Snap", "Toggle Snapping", _snapEnabled, snapColor, SvgEditorIcons.IconSnap))
            _snapEnabled = !_snapEnabled;
        
        ImGui.SameLine(0f, ToolbarSpacing);
        
        // Snap value inputs (only show when snapping is enabled)
        if (_snapEnabled)
        {
            ImGui.SetNextItemWidth(60f);
            if (ImGui.DragFloat("##SnapTrans", ref _snapTranslationValue, 0.1f, 0.01f, 100f, "T: %.2f"))
                _snapTranslationValue = MathF.Max(0.01f, _snapTranslationValue);
            
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Translation Snap Value");
            
            ImGui.SameLine(0f, 4f);
            
            ImGui.SetNextItemWidth(50f);
            if (ImGui.DragFloat("##SnapRot", ref _snapRotationValue, 1f, 1f, 180f, "R: %.0f°"))
                _snapRotationValue = MathF.Max(1f, _snapRotationValue);
            
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Rotation Snap Value (degrees)");
            
            ImGui.SameLine(0f, 4f);
            
            ImGui.SetNextItemWidth(50f);
            if (ImGui.DragFloat("##SnapScale", ref _snapScaleValue, 0.01f, 0.01f, 10f, "S: %.2f"))
                _snapScaleValue = MathF.Max(0.01f, _snapScaleValue);
            
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Scale Snap Value");
        }
        
        ImGui.EndGroup();
    }

    /// <summary>
    /// Draws play mode control buttons (Play/Pause, Stop, Step Frame).
    /// </summary>
    private static void DrawPlayModeButtons()
    {
        bool isPlaying = EditorState.InPlayMode;
        bool isPaused = EditorState.IsPaused;
        bool isEditing = EditorState.InEditMode;
        bool isTransitioning = EditorState.IsTransitioning;
        
        ImGui.BeginGroup();
        
        ImGui.BeginDisabled(isTransitioning);
        
        // Play/Pause button
        if (isEditing)
        {
            // Show Play button
            Vector4 playColor = new(0.3f, 0.8f, 0.3f, 1.0f);
            if (DrawToolbarActionButton("▶", "Play (F5)", playColor, SvgEditorIcons.IconPlay))
                EditorState.EnterPlayMode();
        }
        else if (isPlaying)
        {
            // Show Pause button
            Vector4 pauseColor = new(0.9f, 0.7f, 0.2f, 1.0f);
            if (DrawToolbarActionButton("⏸", "Pause", pauseColor, SvgEditorIcons.IconPause))
                EditorState.Pause();
        }
        else if (isPaused)
        {
            // Show Resume button
            Vector4 resumeColor = new(0.3f, 0.8f, 0.3f, 1.0f);
            if (DrawToolbarActionButton("▶", "Resume", resumeColor, SvgEditorIcons.IconPlay))
                EditorState.Resume();
        }
        
        ImGui.SameLine(0f, 2f);
        
        // Stop button (only available when not editing)
        ImGui.BeginDisabled(isEditing);
        {
            Vector4 stopColor = isEditing 
                ? new Vector4(0.4f, 0.4f, 0.4f, 1.0f) 
                : new Vector4(0.9f, 0.3f, 0.3f, 1.0f);
            
            if (DrawToolbarActionButton("⏹", "Stop (Shift+F5)", stopColor, SvgEditorIcons.IconStop))
                EditorState.ExitPlayMode();
        }
        ImGui.EndDisabled();
        
        ImGui.SameLine(0f, 2f);
        
        // Step Frame button (only available when paused)
        ImGui.BeginDisabled(!isPaused);
        {
            Vector4 stepColor = isPaused 
                ? new Vector4(0.5f, 0.7f, 0.9f, 1.0f) 
                : new Vector4(0.4f, 0.4f, 0.4f, 1.0f);
            
            if (DrawToolbarActionButton("⏭", "Step Frame (F6)", stepColor, SvgEditorIcons.IconStepFrame))
                EditorState.StepFrame();
        }
        ImGui.EndDisabled();
        
        ImGui.EndDisabled(); // isTransitioning
        
        // Show current state indicator (hide "Edit")
        ImGui.SameLine(0f, ToolbarSpacing);
        ImGui.AlignTextToFramePadding();
        
        Vector4 stateColor;
        string stateText;
        
        if (isTransitioning)
        {
            stateColor = new Vector4(0.8f, 0.8f, 0.2f, 1.0f);
            stateText = Engine.PlayMode.State == EPlayModeState.EnteringPlay ? "Starting..." : "Stopping...";
        }
        else if (isPlaying)
        {
            stateColor = new Vector4(0.3f, 1.0f, 0.3f, 1.0f);
            stateText = "Playing";
        }
        else if (isPaused)
        {
            stateColor = new Vector4(1.0f, 0.8f, 0.2f, 1.0f);
            stateText = "Paused";
        }
        else
        {
            // In edit mode we don't show a status label to keep the toolbar clean.
            stateText = string.Empty;
            stateColor = default;
        }

        if (!string.IsNullOrEmpty(stateText))
            ImGui.TextColored(stateColor, stateText);
        
        ImGui.EndGroup();
    }

    private static float GetPlayControlsEstimatedWidth()
    {
        // Buttons: Play/Pause, Stop, Step
        float buttonRow = (ToolbarButtonSize * 3f) + (2f * 2f);

        // Add status text width if we'll render it.
        bool isTransitioning = Engine.PlayMode.IsTransitioning;
        bool isPlaying = Engine.PlayMode.IsPlaying;
        bool isPaused = Engine.PlayMode.IsPaused;

        string stateText = string.Empty;
        if (isTransitioning)
            stateText = Engine.PlayMode.State == EPlayModeState.EnteringPlay ? "Starting..." : "Stopping...";
        else if (isPlaying)
            stateText = "Playing";
        else if (isPaused)
            stateText = "Paused";

        if (string.IsNullOrEmpty(stateText))
            return buttonRow;

        float textWidth = ImGui.CalcTextSize(stateText).X;
        return buttonRow + ToolbarSpacing + textWidth;
    }

    private static void DrawToolbarSectionLabel(string label)
    {
        // Draw centered text without moving the button row vertically.
        Vector2 start = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.CalcTextSize(label);
        float y = start.Y + (ToolbarButtonSize - size.Y) * 0.5f;

        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        ImGui.GetWindowDrawList().AddText(new Vector2(start.X, y), color, label);

        // Reserve space for the label so following items layout correctly.
        ImGui.Dummy(new Vector2(size.X, ToolbarButtonSize));
        ImGui.SameLine(0f, ToolbarSpacing);
    }

    /// <summary>
    /// Draws a toggleable toolbar button with text.
    /// </summary>
    private static bool DrawToolbarToggleButton(string label, string tooltip, bool isActive, Vector4 activeColor, string? iconName = null)
    {
        var style = ImGui.GetStyle();
        Vector4 bgColor;
        Vector4 hoverColor;
        Vector4 textColor;
        
        if (isActive)
        {
            bgColor = activeColor;
            hoverColor = new Vector4(
                MathF.Min(1f, activeColor.X + 0.1f),
                MathF.Min(1f, activeColor.Y + 0.1f),
                MathF.Min(1f, activeColor.Z + 0.1f),
                activeColor.W);
            textColor = new Vector4(1f, 1f, 1f, 1f);
        }
        else
        {
            bgColor = style.Colors[(int)ImGuiCol.Button];
            hoverColor = style.Colors[(int)ImGuiCol.ButtonHovered];
            textColor = style.Colors[(int)ImGuiCol.Text];
        }
        
        ImGui.PushStyleColor(ImGuiCol.Button, bgColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoverColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, activeColor);
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        
        bool clicked;
        if (iconName != null && TryGetIconHandle(iconName, out nint handle))
            clicked = DrawToolbarIconButton(label, handle);
        else
            clicked = ImGui.Button(label, new Vector2(ToolbarButtonSize, ToolbarButtonSize));
        
        ImGui.PopStyleColor(4);
        
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        
        return clicked;
    }

    /// <summary>
    /// Draws an action toolbar button (non-toggle).
    /// </summary>
    private static bool DrawToolbarActionButton(string label, string tooltip, Vector4 color, string? iconName = null)
    {
        var style = ImGui.GetStyle();
        Vector4 hoverColor = new(
            MathF.Min(1f, color.X + 0.15f),
            MathF.Min(1f, color.Y + 0.15f),
            MathF.Min(1f, color.Z + 0.15f),
            color.W);
        
        Vector4 activeColor = new(
            MathF.Max(0f, color.X - 0.1f),
            MathF.Max(0f, color.Y - 0.1f),
            MathF.Max(0f, color.Z - 0.1f),
            color.W);
        
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoverColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, activeColor);
        
        bool clicked;
        if (iconName != null && TryGetIconHandle(iconName, out nint handle))
            clicked = DrawToolbarIconButton(label, handle);
        else
            clicked = ImGui.Button(label, new Vector2(ToolbarButtonSize, ToolbarButtonSize));
        
        ImGui.PopStyleColor(3);
        
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        
        return clicked;
    }

    private static bool DrawToolbarIconButton(string id, nint handle)
    {
        var style = ImGui.GetStyle();
        var buttonSize = new Vector2(ToolbarButtonSize, ToolbarButtonSize);

        // Use a normal button for consistent sizing/alignment, then draw the icon ourselves.
        // This avoids ImageButton shrinking the icon due to frame padding.
        Vector2 buttonMin = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Button($"##{id}", buttonSize);

        Vector2 buttonMax = buttonMin + buttonSize;

        // Keep a small border so the icon reads well.
        float padding = MathF.Min(3f, style.FramePadding.X);
        Vector2 iconMin = buttonMin + new Vector2(padding, padding);
        Vector2 iconMax = buttonMax - new Vector2(padding, padding);

        // Flip V so SVG textures match other UI usage.
        Vector2 uv0 = new(0.0f, 1.0f);
        Vector2 uv1 = new(1.0f, 0.0f);
        uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
        ImGui.GetWindowDrawList().AddImage((nint)handle, iconMin, iconMax, uv0, uv1, tint);

        return clicked;
    }

    /// <summary>
    /// Gets the color for a transform mode button.
    /// </summary>
    private static Vector4 GetTransformModeColor(ETransformMode mode, bool isActive)
    {
        if (!isActive)
            return new Vector4(0.3f, 0.3f, 0.3f, 1.0f);
        
        return mode switch
        {
            ETransformMode.Translate => new Vector4(0.2f, 0.6f, 0.9f, 1.0f), // Blue
            ETransformMode.Rotate => new Vector4(0.2f, 0.8f, 0.4f, 1.0f),    // Green
            ETransformMode.Scale => new Vector4(0.9f, 0.6f, 0.2f, 1.0f),     // Orange
            _ => new Vector4(0.5f, 0.5f, 0.5f, 1.0f)
        };
    }

    /// <summary>
    /// Gets the color for a transform space button.
    /// </summary>
    private static Vector4 GetTransformSpaceColor(bool isActive)
    {
        return isActive 
            ? new Vector4(0.15f, 0.55f, 0.95f, 1.0f)  // Active blue
            : new Vector4(0.3f, 0.3f, 0.3f, 1.0f);    // Inactive gray
    }

    /// <summary>
    /// Gets the toolbar height for dock space offset calculations.
    /// </summary>
    public static float GetToolbarReservedHeight()
    {
        return ToolbarHeight;
    }

    /// <summary>
    /// Gets snap values for transform operations.
    /// </summary>
    public static bool GetSnapSettings(out float translationSnap, out float rotationSnap, out float scaleSnap)
    {
        translationSnap = _snapTranslationValue;
        rotationSnap = _snapRotationValue;
        scaleSnap = _snapScaleValue;
        return _snapEnabled;
    }

    /// <summary>
    /// Sets snap enabled state.
    /// </summary>
    public static void SetSnapEnabled(bool enabled) => _snapEnabled = enabled;
    
    /// <summary>
    /// Gets whether snapping is currently enabled.
    /// </summary>
    public static bool IsSnapEnabled => _snapEnabled;
}
