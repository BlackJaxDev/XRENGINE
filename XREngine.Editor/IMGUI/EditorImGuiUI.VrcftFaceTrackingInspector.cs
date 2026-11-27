using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Components;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly ConditionalWeakTable<SceneNode, VrcftInspectorNodeState> VrcftInspectorStates = [];

    private static readonly string[] FaceTrackingControlModeLabels =
    [
        "Auto (Dynamic Switch)",
        "Force Face Tracking",
        "Force Procedural",
    ];

    /// <summary>
    /// Stores the most recent animator template attachment result for <paramref name="avatarRoot"/>
    /// so the ImGui inspector displays live blendshape coverage and diagnostic details.
    /// </summary>
    internal static void RecordVrcftTemplateAttachResult(SceneNode avatarRoot, VrcftTemplateAttachResult result)
    {
        ArgumentNullException.ThrowIfNull(avatarRoot);
        ArgumentNullException.ThrowIfNull(result);

        VrcftInspectorNodeState state = VrcftInspectorStates.GetOrCreateValue(avatarRoot);
        state.LastResult = result;
        state.LastError = null;
    }

    private static void DrawVrcftTemplateInspector(AnimStateMachineComponent stateMachineComponent)
    {
        if (stateMachineComponent.SceneNode is not SceneNode avatarRoot)
            return;

        if (!ImGui.CollapsingHeader("Unity Animator Package Attachment"))
            return;

        VrcftInspectorNodeState state = VrcftInspectorStates.GetOrCreateValue(avatarRoot);
        ImGui.PushID("VrcftTemplateAttach");

        ImGui.InputText("Package Source", ref state.PackageSource, 512);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enter a VPM repository URL, local .zip archive, package folder, or Unity project root.");

        ImGui.InputText("Package ID (optional)", ref state.PackageId, 128);
        ImGui.InputText("Package Version (optional)", ref state.PackageVersion, 64);
        ImGui.InputText("Source Mesh Path", ref state.SourceFaceMeshPath, 128);

        DrawVrcftFaceMeshPicker(avatarRoot, state);

        if (ImGui.Button("Discover Controllers"))
        {
            try
            {
                var discoverOptions = new VrcftTemplateAttachOptions
                {
                    PackageSource = state.PackageSource.Trim(),
                    PackageId = string.IsNullOrWhiteSpace(state.PackageId) ? null : state.PackageId.Trim(),
                    PackageVersion = string.IsNullOrWhiteSpace(state.PackageVersion) ? null : state.PackageVersion.Trim(),
                };

                state.DiscoveredControllers = VrcftTemplateAttachmentService.DiscoverControllers(discoverOptions);
                state.SelectedControllers.Clear();
                state.LastError = null;
            }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                Debug.AnimationWarning($"[VrcftTemplate] Controller discovery failed: {ex.Message}");
            }
        }

        if (state.DiscoveredControllers.Count > 0)
        {
            ImGui.SeparatorText($"Package Controllers ({state.DiscoveredControllers.Count})");
            for (int i = 0; i < state.DiscoveredControllers.Count; i++)
            {
                string controllerPath = state.DiscoveredControllers[i];
                bool selected = state.SelectedControllers.Contains(controllerPath);
                if (ImGui.Checkbox($"{controllerPath}##ctrl_{i}", ref selected))
                {
                    if (selected)
                        state.SelectedControllers.Add(controllerPath);
                    else
                        state.SelectedControllers.Remove(controllerPath);
                }
            }
        }
        else
        {
            ImGui.InputText("Controller Path (optional)", ref state.ManualControllerPath, 512);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Package-relative or absolute .controller path, or click Discover Controllers above.");
        }

        if (ImGui.Button("Attach Selected Controllers"))
        {
            try
            {
                List<string> selectedPaths = [];
                for (int i = 0; i < state.DiscoveredControllers.Count; i++)
                {
                    if (state.SelectedControllers.Contains(state.DiscoveredControllers[i]))
                        selectedPaths.Add(state.DiscoveredControllers[i]);
                }

                if (selectedPaths.Count == 0 && !string.IsNullOrWhiteSpace(state.ManualControllerPath))
                    selectedPaths.Add(state.ManualControllerPath.Trim());

                var options = new VrcftTemplateAttachOptions
                {
                    PackageSource = state.PackageSource.Trim(),
                    PackageId = string.IsNullOrWhiteSpace(state.PackageId) ? null : state.PackageId.Trim(),
                    PackageVersion = string.IsNullOrWhiteSpace(state.PackageVersion) ? null : state.PackageVersion.Trim(),
                    ControllerPaths = selectedPaths,
                    FaceMeshPathOverride = string.IsNullOrWhiteSpace(state.FaceMeshOverride)
                        ? null
                        : state.FaceMeshOverride.Trim(),
                    SourceFaceMeshPath = string.IsNullOrWhiteSpace(state.SourceFaceMeshPath)
                        ? "Body"
                        : state.SourceFaceMeshPath.Trim(),
                    ApplyLocalDefaults = true,
                    AllowMissingImportedBindings = true,
                };

                state.LastResult = VrcftTemplateAttachmentService.Attach(avatarRoot, options, stateMachineComponent);
                state.LastError = null;

                foreach (AnimStateMachineOscReceiverComponent receiver in avatarRoot.GetComponents<AnimStateMachineOscReceiverComponent>())
                {
                    if (ReferenceEquals(receiver.GetAnimStateMachine(), stateMachineComponent))
                    {
                        receiver.SetOscQueryParameterTypeOverrides(state.LastResult.OscParameterTypeOverrides);
                        receiver.RefreshParameterBindings();
                    }
                }
            }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                Debug.AnimationWarning($"[VrcftTemplate] Inspector attachment failed: {ex.Message}");
            }
        }

        if (avatarRoot.GetComponent<AnimStateMachineOscReceiverComponent>() is null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Add OSC Receiver + OSCQuery"))
            {
                OscSenderComponent? sender = avatarRoot.GetComponent<OscSenderComponent>()
                    ?? avatarRoot.AddComponent(() => new OscSenderComponent
                    {
                        Port = 9001,
                        ParameterPrefix = AnimStateMachineOscReceiverComponent.DefaultAvatarParameterPrefix,
                    }, "Face Tracking OSC Sender");

                _ = avatarRoot.AddComponent(() =>
                {
                    var receiver = new AnimStateMachineOscReceiverComponent
                    {
                        AnimStateMachine = stateMachineComponent,
                        OutboundSender = sender,
                        EnableDynamicParameterBinding = true,
                        EnableOscQuery = true,
                        AvatarId = AnimStateMachineOscReceiverComponent.CreateDeterministicAvatarId(avatarRoot.Name),
                        AvatarName = avatarRoot.Name ?? "XRENGINE Avatar",
                    };
                    if (state.LastResult is { OscParameterTypeOverrides.Count: > 0 })
                        receiver.SetOscQueryParameterTypeOverrides(state.LastResult.OscParameterTypeOverrides);
                    return receiver;
                }, "Face Tracking OSC Receiver");
            }
        }

        if (!string.IsNullOrWhiteSpace(state.LastError))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.45f, 0.45f, 1.0f));
            ImGui.TextWrapped($"Error: {state.LastError}");
            ImGui.PopStyleColor();
        }

        if (state.LastResult is VrcftTemplateAttachResult result)
            DrawVrcftAttachmentSummary(result);

        ImGui.PopID();
    }

    private static void DrawOscReceiverDiagnostics(AnimStateMachineOscReceiverComponent receiver)
    {
        if (!ImGui.CollapsingHeader("OSC & OSCQuery Diagnostics"))
            return;

        ImGui.PushID("OscReceiverDiagnostics");
        ImGui.TextUnformatted(receiver.ActivePort > 0 && receiver.ActivePort != receiver.Port
            ? $"UDP Receive Port: {receiver.ActivePort} (requested {receiver.Port} was busy)"
            : $"UDP Receive Port: {(receiver.ActivePort > 0 ? receiver.ActivePort : receiver.Port)}");
        if (!string.IsNullOrWhiteSpace(receiver.LastServerError))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.45f, 0.45f, 1.0f));
            ImGui.TextWrapped($"UDP Server Error: {receiver.LastServerError}");
            ImGui.PopStyleColor();
        }
        ImGui.TextUnformatted($"Dynamic Binding: {(receiver.EnableDynamicParameterBinding ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"Avatar ID: {receiver.AvatarId}");
        ImGui.TextUnformatted($"Bound Parameters: {receiver.BoundParameterCount}");
        ImGui.TextUnformatted($"Received Packets: {receiver.ReceivedParameterPacketCount}");
        ImGui.TextUnformatted($"Outbound /avatar/change Sent: {receiver.AvatarChangeSendCount}");

        ImGui.SeparatorText("Face Tracking / Procedural Switching");
        string activeDriver = receiver.IsFaceTrackingDrivingAvatar
            ? "OSC Face Tracking (Live)"
            : receiver.IsRelaxingToNeutral
                ? "Relaxing to Neutral"
                : receiver.IsProceduralFallbackActive
                    ? "Procedural Eye Gaze & Blinking"
                    : "Idle";
        Vector4 statusColor = receiver.IsFaceTrackingDrivingAvatar
            ? new Vector4(0.4f, 0.9f, 0.4f, 1.0f)
            : receiver.IsRelaxingToNeutral
                ? new Vector4(0.95f, 0.8f, 0.35f, 1.0f)
                : new Vector4(0.6f, 0.85f, 1.0f, 1.0f);
        ImGui.TextColored(statusColor, $"Active Face Driver: {activeDriver}");

        float secondsSincePacket = receiver.SecondsSinceLastTrackingPacket;
        ImGui.TextUnformatted(secondsSincePacket >= 0.0f
            ? $"Last Tracking Signal: {secondsSincePacket:F2}s ago ({(receiver.IsTrackingSignalLive ? "Live" : "Lost")})"
            : "Last Tracking Signal: None received yet");

        int modeIndex = (int)receiver.ControlMode;
        if (ImGui.Combo("Face Control Mode", ref modeIndex, FaceTrackingControlModeLabels, FaceTrackingControlModeLabels.Length))
            receiver.ControlMode = (EFaceTrackingControlMode)modeIndex;

        float signalLossTimeout = receiver.SignalLossTimeoutSeconds;
        if (ImGui.SliderFloat("Signal Loss Timeout (s)", ref signalLossTimeout, 0.25f, 15.0f, "%.2f"))
            receiver.SignalLossTimeoutSeconds = signalLossTimeout;

        float neutralResetDuration = receiver.NeutralResetDurationSeconds;
        if (ImGui.SliderFloat("Neutral Reset Fade (s)", ref neutralResetDuration, 0.0f, 2.0f, "%.2f"))
            receiver.NeutralResetDurationSeconds = neutralResetDuration;

        AnimStateMachineComponent? animator = receiver.GetAnimStateMachine();
        if (animator is not null)
            ImGui.TextUnformatted($"State Machine Schema Version: {animator.StateMachine.ParameterSchemaVersion}");
        else
            ImGui.TextDisabled("Target AnimStateMachineComponent: None");

        ImGui.SeparatorText("OSCQuery Discovery");
        if (receiver.IsOscQueryRunning)
        {
            ImGui.TextColored(
                new Vector4(0.4f, 0.9f, 0.4f, 1.0f),
                $"Running on http://127.0.0.1:{receiver.ActiveOscQueryHttpPort}/?HOST_INFO");
        }
        else
        {
            ImGui.TextDisabled("OSCQuery HTTP Server: Stopped");
        }

        ImGui.TextUnformatted($"Service Name: {receiver.OscQueryServiceName}");
        ImGui.TextUnformatted($"mDNS Responder: {(receiver.IsOscQueryMdnsActive ? "Active (_oscjson._tcp.local., _osc._udp.local.)" : "Inactive")}");
        ImGui.TextUnformatted($"Discovered OSC Peers: {receiver.DiscoveredOscQueryPeerCount}");
        if (!string.IsNullOrWhiteSpace(receiver.LastOscQueryError))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.65f, 0.35f, 1.0f));
            ImGui.TextWrapped($"Last OSCQuery Error: {receiver.LastOscQueryError}");
            ImGui.PopStyleColor();
        }

        if (ImGui.Button("Refresh Parameter Bindings"))
            receiver.RefreshParameterBindings();
        ImGui.SameLine();
        if (ImGui.Button("Send /avatar/change"))
            receiver.NotifyAvatarChanged();

        if (receiver.SceneNode is SceneNode ownerNode
            && VrcftInspectorStates.TryGetValue(ownerNode, out VrcftInspectorNodeState? state)
            && state.LastResult is VrcftTemplateAttachResult attachResult)
        {
            ImGui.SeparatorText("Attached Animator Controllers");
            DrawVrcftAttachmentSummary(attachResult);
        }

        ImGui.PopID();
    }

    private static void DrawVrcftFaceMeshPicker(SceneNode avatarRoot, VrcftInspectorNodeState state)
    {
        ImGui.InputText("Face Mesh Override", ref state.FaceMeshOverride, 256);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Relative node path from the avatar root (leave blank for automatic resolution).");

        var meshChoices = new List<(string Path, int BlendshapeCount)>();
        CollectAvatarFaceMeshChoices(avatarRoot, string.Empty, meshChoices);

        string preview = string.IsNullOrWhiteSpace(state.FaceMeshOverride)
            ? "<Auto>"
            : state.FaceMeshOverride;

        if (ImGui.BeginCombo("Detected Face Meshes", preview))
        {
            bool autoSelected = string.IsNullOrWhiteSpace(state.FaceMeshOverride);
            if (ImGui.Selectable("<Auto>", autoSelected))
                state.FaceMeshOverride = string.Empty;

            for (int i = 0; i < meshChoices.Count; i++)
            {
                (string path, int count) = meshChoices[i];
                string displayPath = path.Length == 0 ? "<avatar root>" : path;
                string label = $"{displayPath} ({count} blendshapes)##{i}";
                bool isSelected = string.Equals(state.FaceMeshOverride, path, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(label, isSelected))
                    state.FaceMeshOverride = path;
            }

            ImGui.EndCombo();
        }
    }

    private static void CollectAvatarFaceMeshChoices(
        SceneNode node,
        string relativePath,
        List<(string Path, int BlendshapeCount)> results)
    {
        if (node.GetComponent<ModelComponent>() is ModelComponent model)
        {
            HashSet<string> names = VrcftFaceMeshResolver.CollectModelBlendshapeNames(model);
            results.Add((relativePath, names.Count));
        }

        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is not SceneNode childNode)
                continue;
            string name = childNode.Name ?? string.Empty;
            string childPath = relativePath.Length == 0 ? name : $"{relativePath}/{name}";
            CollectAvatarFaceMeshChoices(childNode, childPath, results);
        }
    }

    private static void DrawVrcftAttachmentSummary(VrcftTemplateAttachResult result)
    {
        string targetPath = result.FaceMeshResolution.RelativeNodePath is null
            ? "<none>"
            : result.FaceMeshResolution.RelativeNodePath.Length == 0
                ? "<avatar root>"
                : result.FaceMeshResolution.RelativeNodePath;
        int totalReferenced = result.MatchedBlendshapeCount + result.UnmatchedBlendshapeCount;
        int totalLayers = 0;
        int totalBlendTrees = 0;
        int totalParams = 0;
        for (int i = 0; i < result.CompileResults.Count; i++)
        {
            totalLayers += result.CompileResults[i].CompiledLayerCount;
            totalBlendTrees += result.CompileResults[i].CompiledBlendTreeCount;
            totalParams += result.CompileResults[i].CompiledParameterCount;
        }

        ImGui.TextUnformatted($"Controllers Attached: {result.ControllerPaths.Count}");
        ImGui.TextUnformatted($"Face Mesh: {targetPath} ({result.FaceMeshResolution.Source})");
        ImGui.TextUnformatted($"Blendshape Coverage: {result.MatchedBlendshapeCount}/{totalReferenced} matched, {result.UnmatchedBlendshapeCount} unmatched");
        ImGui.TextUnformatted($"Compiled Graph: {totalLayers} layers, {totalBlendTrees} blend trees, {totalParams} parameters");

        if (result.UnmatchedBlendshapes.Count > 0 && ImGui.TreeNode($"Missing Blendshapes ({result.UnmatchedBlendshapes.Count})"))
        {
            for (int i = 0; i < result.UnmatchedBlendshapes.Count; i++)
                ImGui.BulletText(result.UnmatchedBlendshapes[i]);
            ImGui.TreePop();
        }
    }

    private sealed class VrcftInspectorNodeState
    {
        public string PackageSource = string.Empty;
        public string PackageId = string.Empty;
        public string PackageVersion = string.Empty;
        public string SourceFaceMeshPath = "Body";
        public string FaceMeshOverride = string.Empty;
        public string ManualControllerPath = string.Empty;
        public IReadOnlyList<string> DiscoveredControllers = [];
        public HashSet<string> SelectedControllers = new(StringComparer.OrdinalIgnoreCase);
        public VrcftTemplateAttachResult? LastResult;
        public string? LastError;
    }
}
