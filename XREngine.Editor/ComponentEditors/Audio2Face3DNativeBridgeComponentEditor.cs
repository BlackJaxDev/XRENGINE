using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using XREngine.Components;
using XREngine.Editor.UI;

namespace XREngine.Editor.ComponentEditors;

public sealed class Audio2Face3DNativeBridgeComponentEditor : IXRComponentEditor
{
    private const string ModelDialogFilter = "Model files (*.json;*.onnx;*.engine)|*.json;*.onnx;*.engine|All files (*.*)|*.*";

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not Audio2Face3DNativeBridgeComponent bridge)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(bridge, visited, "Audio2Face Native Bridge"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(bridge.GetHashCode());
        DrawStatusSection(bridge);
        DrawSdkSection(bridge);
        DrawMicrophoneSection(bridge);
        DrawActionsSection(bridge);
        DrawAdvancedSection(bridge, visited);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawStatusSection(Audio2Face3DNativeBridgeComponent bridge)
    {
        if (!ImGui.CollapsingHeader("Status", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool nativeBridgeAvailable = Audio2Face3DNativeBridge.IsAvailable(out string? availabilityError);

        ImGui.TextDisabled($"Adapter Registered: {Audio2Face3DLiveClientRegistry.HasAdapter}");
        ImGui.TextDisabled($"Native DLL: {(nativeBridgeAvailable ? "Available" : "Unavailable")}");
        ImGui.TextDisabled($"Live Component Connected: {bridge.Audio2Face.IsLiveConnected}");
        ImGui.TextDisabled($"Microphone Capturing: {bridge.Microphone.IsCapturing}");
        ImGui.TextDisabled($"Bridge Input Rate: {bridge.InputSampleRate} Hz");

        if (!string.IsNullOrWhiteSpace(availabilityError))
            ImGui.TextWrapped($"Native Bridge: {availabilityError}");
        if (!string.IsNullOrWhiteSpace(bridge.LastBridgeError))
            ImGui.TextWrapped($"Bridge Error: {bridge.LastBridgeError}");
        if (!string.IsNullOrWhiteSpace(bridge.Audio2Face.LastLiveError))
            ImGui.TextWrapped($"Live Error: {bridge.Audio2Face.LastLiveError}");
    }

    private static void DrawSdkSection(Audio2Face3DNativeBridgeComponent bridge)
    {
        if (!ImGui.CollapsingHeader("SDK", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("Use Tools/Dependencies/Get-Audio2Face3DSdk.ps1 or the Install-Audio2Face3D-SDK task to clone the official NVIDIA SDK.");
        ImGui.TextDisabled("The checked-in native shim is only a stub until you wire it to the upstream Audio2Face / Audio2Emotion executors.");

        int inputSampleRate = bridge.InputSampleRate;
        if (ImGui.InputInt("Input Sample Rate", ref inputSampleRate))
        {
            using var _ = Undo.TrackChange("Set Audio2Face Bridge Sample Rate", bridge);
            bridge.InputSampleRate = inputSampleRate;
        }

        bool enableEmotion = bridge.EnableEmotion;
        if (ImGui.Checkbox("Enable Emotion", ref enableEmotion))
        {
            using var _ = Undo.TrackChange("Toggle Audio2Face Bridge Emotion", bridge);
            bridge.EnableEmotion = enableEmotion;
        }

        DrawPathInput("Face Model Path", bridge.FaceModelPath, bridge, static (target, value) => target.FaceModelPath = value, "Set Audio2Face Face Model Path", $"A2F_Bridge_Face_{bridge.GetHashCode()}");
        DrawPathInput("Emotion Model Path", bridge.EmotionModelPath, bridge, static (target, value) => target.EmotionModelPath = value, "Set Audio2Face Emotion Model Path", $"A2F_Bridge_Emotion_{bridge.GetHashCode()}");
    }

    private static void DrawMicrophoneSection(Audio2Face3DNativeBridgeComponent bridge)
    {
        if (!ImGui.CollapsingHeader("Microphone", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Current Capture Format: {bridge.Microphone.SampleRate} Hz / {bridge.Microphone.BitsPerSampleValue}-bit mono");

        bool driveFromMicrophone = bridge.DriveFromMicrophone;
        if (ImGui.Checkbox("Drive From Microphone", ref driveFromMicrophone))
        {
            using var _ = Undo.TrackChange("Toggle Audio2Face Bridge Microphone Drive", bridge);
            bridge.DriveFromMicrophone = driveFromMicrophone;
        }

        bool autoConfigure = bridge.AutoConfigureMicrophoneFormat;
        if (ImGui.Checkbox("Auto Configure Microphone Format", ref autoConfigure))
        {
            using var _ = Undo.TrackChange("Toggle Audio2Face Bridge Auto Configure Mic", bridge);
            bridge.AutoConfigureMicrophoneFormat = autoConfigure;
        }

        bool autoStart = bridge.AutoStartMicrophone;
        if (ImGui.Checkbox("Auto Start Microphone", ref autoStart))
        {
            using var _ = Undo.TrackChange("Toggle Audio2Face Bridge Auto Start Mic", bridge);
            bridge.AutoStartMicrophone = autoStart;
        }

        bool autoConnect = bridge.AutoConnectLiveClient;
        if (ImGui.Checkbox("Auto Connect Live Client", ref autoConnect))
        {
            using var _ = Undo.TrackChange("Toggle Audio2Face Bridge Auto Connect", bridge);
            bridge.AutoConnectLiveClient = autoConnect;
        }

        bool autoRegister = bridge.AutoRegisterAdapter;
        if (ImGui.Checkbox("Auto Register Adapter", ref autoRegister))
        {
            using var _ = Undo.TrackChange("Toggle Audio2Face Bridge Auto Register", bridge);
            bridge.AutoRegisterAdapter = autoRegister;
        }
    }

    private static void DrawActionsSection(Audio2Face3DNativeBridgeComponent bridge)
    {
        if (!ImGui.CollapsingHeader("Actions", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.Button("Connect Live Client"))
            bridge.Audio2Face.TryConnectLiveClient();

        ImGui.SameLine();
        if (ImGui.Button("Disconnect Live Client"))
            bridge.Audio2Face.DisconnectLiveClient();

        if (ImGui.Button("Start Microphone"))
            bridge.Microphone.StartCapture();

        ImGui.SameLine();
        if (ImGui.Button("Stop Microphone"))
            bridge.Microphone.StopCapture();

        ImGui.TextDisabled("Live mode expects Audio2Face3DComponent.SourceMode = LiveStream on the same scene node.");
    }

    private static void DrawAdvancedSection(Audio2Face3DNativeBridgeComponent bridge, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Advanced"))
            return;

        EditorImGuiUI.DrawDefaultComponentInspector(bridge, visited);
    }

    private static void DrawPathInput(
        string label,
        string currentValue,
        Audio2Face3DNativeBridgeComponent bridge,
        Action<Audio2Face3DNativeBridgeComponent, string> setter,
        string undoLabel,
        string dialogId)
    {
        string value = currentValue;
        if (ImGui.InputText(label, ref value, 1024))
        {
            using var _ = Undo.TrackChange(undoLabel, bridge);
            setter(bridge, value);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Browse##{label}"))
        {
            ImGuiFileBrowser.OpenFile(
                dialogId,
                $"Choose {label}",
                result =>
                {
                    if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                        return;

                    using var _ = Undo.TrackChange(undoLabel, bridge);
                    setter(bridge, Path.GetFullPath(result.SelectedPath));
                },
                ModelDialogFilter,
                ResolveInitialDirectory(currentValue));
        }
    }

    private static string ResolveInitialDirectory(string currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            string fullPath = Path.GetFullPath(currentPath);
            if (File.Exists(fullPath))
                return Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            if (Directory.Exists(fullPath))
                return fullPath;
        }

        string sdkDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Build", "Dependencies", "Audio2Face-3D-SDK");
        return Directory.Exists(sdkDirectory) ? sdkDirectory : Directory.GetCurrentDirectory();
    }
}