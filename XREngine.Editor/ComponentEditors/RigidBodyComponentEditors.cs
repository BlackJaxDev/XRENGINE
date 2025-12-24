using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ImGuiNET;
using XREngine;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Tools;
using XREngine.Diagnostics;

namespace XREngine.Editor.ComponentEditors;

internal static class RigidBodyEditorShared
{
    private sealed class GenerationState
    {
        public bool InProgress;
        public string? LastMessage;
        public string? ActiveMessage;
        public PhysicsActorComponent.ConvexHullGenerationProgress? LastProgress;
        public readonly Stopwatch Stopwatch = new();
    }

    private sealed class HullPreviewState
    {
        public bool Enabled;
    }

    private static readonly ConditionalWeakTable<PhysicsActorComponent, GenerationState> _generationStates = new();
    private static readonly ConditionalWeakTable<PhysicsActorComponent, HullPreviewState> _previewStates = new();
    private static readonly string[] s_spinnerFrames = ["-", "\\", "|", "/"];

    public static void DrawConvexHullSection(PhysicsActorComponent component)
    {
        ImGui.SeparatorText("Convex Hull Utilities");

        var state = _generationStates.GetValue(component, _ => new GenerationState());
        bool hasModel = component.GetSiblingComponent<ModelComponent>() is not null;
        var cachedHulls = component.GetCachedConvexHulls();

        using (new ImGuiDisabledScope(!hasModel || state.InProgress))
        {
            string label = state.InProgress ? "Generating..." : "Generate Convex Hulls";
            if (ImGui.Button(label, new Vector2(-1f, 0f)) && hasModel && !state.InProgress)
                TriggerGeneration(component, state);
        }

        if (!hasModel)
            ImGui.TextDisabled("Requires a sibling ModelComponent.");

        if (hasModel)
            DrawWireframePreviewControls(component, cachedHulls);

        DrawGenerationStatus(state);
    }

    private static void TriggerGeneration(PhysicsActorComponent component, GenerationState state)
    {
        state.InProgress = true;
        state.LastMessage = null;
        state.ActiveMessage = "Preparing convex hull data...";
        state.LastProgress = null;
        state.Stopwatch.Restart();
        _ = RunGenerationAsync(component, state);
    }

    private static async Task RunGenerationAsync(PhysicsActorComponent component, GenerationState state)
    {
        try
        {
            var progress = new Progress<PhysicsActorComponent.ConvexHullGenerationProgress>(p =>
            {
                state.LastProgress = p;
                state.ActiveMessage = FormatProgressMessage(p);
            });

            await component.GenerateConvexHullsFromModelAsync(progress).ConfigureAwait(false);
            state.LastMessage = "Convex hulls cached successfully.";
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "Convex hull generation failed.");
            state.LastMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            state.InProgress = false;
            state.ActiveMessage = null;
            state.LastProgress = null;
            state.Stopwatch.Reset();
        }
    }

    private static string FormatProgressMessage(PhysicsActorComponent.ConvexHullGenerationProgress progress)
    {
        if (progress.UsedCache)
            return progress.Message;

        if (progress.TotalInputs > 0)
        {
            float percent = progress.Percentage * 100f;
            return $"{progress.Message} ({progress.CompletedInputs}/{progress.TotalInputs}, {percent:0}%)";
        }

        return progress.Message;
    }

    private static void DrawWireframePreviewControls(PhysicsActorComponent component, IReadOnlyList<CoACD.ConvexHullMesh>? cachedHulls)
    {
        bool hasHulls = cachedHulls is { Count: > 0 };
        var previewState = _previewStates.GetValue(component, _ => new HullPreviewState());

        if (!hasHulls && previewState.Enabled)
            previewState.Enabled = false;

        using (new ImGuiDisabledScope(!hasHulls))
        {
            bool previewEnabled = previewState.Enabled;
            if (ImGui.Checkbox("Preview hull wireframe", ref previewEnabled) && hasHulls)
                previewState.Enabled = previewEnabled;
        }

        if (previewState.Enabled && cachedHulls is not null)
        {
            RenderHullWireframe(component, cachedHulls);
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.9f, 1f), "Wireframe preview queued for rendering.");
        }
        else if (!hasHulls)
        {
            ImGui.TextDisabled("Generate convex hulls to enable preview.");
        }
    }

    private static void RenderHullWireframe(PhysicsActorComponent component, IReadOnlyList<CoACD.ConvexHullMesh> hulls)
    {
        var componentTransform = component.Transform;
        Matrix4x4 transform = componentTransform.RenderMatrix;
        var (offsetTranslation, offsetRotation) = GetShapeOffsetPose(component);
        bool hasTranslation = offsetTranslation != Vector3.Zero;
        bool hasRotation = !offsetRotation.Equals(Quaternion.Identity);
        if (hasRotation)
        {
            float lengthSquared = offsetRotation.LengthSquared();
            if (lengthSquared < 1e-6f)
            {
                hasRotation = false;
                offsetRotation = Quaternion.Identity;
            }
            else if (MathF.Abs(lengthSquared - 1f) > 1e-4f)
            {
                offsetRotation = Quaternion.Normalize(offsetRotation);
            }
        }
        bool hasOffset = hasTranslation || hasRotation;

        foreach (var hull in hulls)
        {
            var vertices = hull.Vertices;
            var indices = hull.Indices;
            if (vertices is null || vertices.Length == 0 || indices is null || indices.Length < 3)
                continue;

            for (int i = 0; i <= indices.Length - 3; i += 3)
            {
                if (!TryGetVertex(vertices, indices[i], out var a) ||
                    !TryGetVertex(vertices, indices[i + 1], out var b) ||
                    !TryGetVertex(vertices, indices[i + 2], out var c))
                {
                    continue;
                }

                var worldA = Vector3.Transform(hasOffset ? ApplyShapeOffset(a, offsetTranslation, offsetRotation, hasTranslation, hasRotation) : a, transform);
                var worldB = Vector3.Transform(hasOffset ? ApplyShapeOffset(b, offsetTranslation, offsetRotation, hasTranslation, hasRotation) : b, transform);
                var worldC = Vector3.Transform(hasOffset ? ApplyShapeOffset(c, offsetTranslation, offsetRotation, hasTranslation, hasRotation) : c, transform);

                Engine.Rendering.Debug.RenderTriangle(worldA, worldB, worldC, ColorF4.Cyan, solid: false);
            }
        }
    }

    private static Vector3 ApplyShapeOffset(
        Vector3 vertex,
        Vector3 translation,
        Quaternion rotation,
        bool hasTranslation,
        bool hasRotation)
    {
        if (hasRotation)
            vertex = Vector3.Transform(vertex, rotation);
        if (hasTranslation)
            vertex += translation;
        return vertex;
    }

    private static void DrawGenerationStatus(GenerationState state)
    {
        if (state.InProgress)
        {
            string spinner = s_spinnerFrames[(int)(ImGui.GetTime() * 8) % s_spinnerFrames.Length];
            string message = state.ActiveMessage ?? "Running convex decomposition...";
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.9f, 1f), $"{spinner} {message}");

            var progress = state.LastProgress;
            if (progress is { TotalInputs: > 0 })
            {
                float percent = progress.Value.Percentage;
                ImGui.ProgressBar(percent, new Vector2(-1f, 0f), $"{percent * 100f:0}%");
                ImGui.TextDisabled($"{progress.Value.CompletedInputs}/{progress.Value.TotalInputs} meshes • {state.Stopwatch.Elapsed:mm\\:ss}");
            }
            else if (state.Stopwatch.IsRunning)
            {
                ImGui.TextDisabled($"Elapsed {state.Stopwatch.Elapsed:mm\\:ss}");
            }
        }
        else if (!string.IsNullOrEmpty(state.LastMessage))
        {
            ImGui.TextWrapped(state.LastMessage);
        }
    }

    private static (Vector3 translation, Quaternion rotation) GetShapeOffsetPose(PhysicsActorComponent component)
    {
        return component switch
        {
            DynamicRigidBodyComponent dynamicComponent => (dynamicComponent.ShapeOffsetTranslation, dynamicComponent.ShapeOffsetRotation),
            StaticRigidBodyComponent staticComponent => (staticComponent.ShapeOffsetTranslation, staticComponent.ShapeOffsetRotation),
            _ => (Vector3.Zero, Quaternion.Identity)
        };
    }

    private static bool TryGetVertex(Vector3[] vertices, int index, out Vector3 vertex)
    {
        if ((uint)index >= vertices.Length)
        {
            vertex = default;
            return false;
        }

        vertex = vertices[index];
        return true;
    }

    public static PhysicsGroupsMask DrawGroupsMaskControls(PhysicsGroupsMask mask)
    {
        int word0 = unchecked((int)mask.Word0);
        int word1 = unchecked((int)mask.Word1);
        int word2 = unchecked((int)mask.Word2);
        int word3 = unchecked((int)mask.Word3);

        if (ImGui.InputInt("Mask Word 0", ref word0))
            mask.Word0 = unchecked((uint)Math.Max(0, word0));
        if (ImGui.InputInt("Mask Word 1", ref word1))
            mask.Word1 = unchecked((uint)Math.Max(0, word1));
        if (ImGui.InputInt("Mask Word 2", ref word2))
            mask.Word2 = unchecked((uint)Math.Max(0, word2));
        if (ImGui.InputInt("Mask Word 3", ref word3))
            mask.Word3 = unchecked((uint)Math.Max(0, word3));

        return mask;
    }

    public static string FormatVector(Vector3 vector)
        => $"{vector.X:F2}, {vector.Y:F2}, {vector.Z:F2}";

    public static Vector4 QuaternionToVector4(Quaternion rotation)
        => new(rotation.X, rotation.Y, rotation.Z, rotation.W);

    public static Quaternion Vector4ToQuaternion(Vector4 value)
    {
        var quat = new Quaternion(value.X, value.Y, value.Z, value.W);
        if (quat.LengthSquared() < float.Epsilon)
            return Quaternion.Identity;
        quat = Quaternion.Normalize(quat);
        return quat;
    }

    internal readonly struct ImGuiDisabledScope : IDisposable
    {
        private readonly bool _disabled;

        public ImGuiDisabledScope(bool disabled)
        {
            _disabled = disabled;
            if (disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_disabled)
                ImGui.EndDisabled();
        }
    }
}

public sealed class DynamicRigidBodyComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not DynamicRigidBodyComponent rigidBodyComponent)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(rigidBodyComponent, visited, "Dynamic Rigid Body Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(rigidBodyComponent.GetHashCode());
        DrawOverview(rigidBodyComponent);
        DrawCreationSettings(rigidBodyComponent);
        DrawActorSettings(rigidBodyComponent);
        DrawDynamicsSettings(rigidBodyComponent);
        RigidBodyEditorShared.DrawConvexHullSection(rigidBodyComponent);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawOverview(DynamicRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Overview", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var rigidBody = component.RigidBody;
        if (rigidBody is null)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Rigid body not created.");
        }
        else
        {
            ImGui.TextUnformatted($"Rigid body: {rigidBody.GetType().Name}");
            ImGui.TextUnformatted($"Sleeping: {rigidBody.IsSleeping}");
            ImGui.TextUnformatted($"Linear Velocity: {RigidBodyEditorShared.FormatVector(rigidBody.LinearVelocity)}");
            ImGui.TextUnformatted($"Angular Velocity: {RigidBodyEditorShared.FormatVector(rigidBody.AngularVelocity)}");
        }

        ImGui.TextUnformatted($"Auto create: {(component.AutoCreateRigidBody ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"Active in hierarchy: {component.IsActiveInHierarchy}");
    }

    private static void DrawCreationSettings(DynamicRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Creation Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool autoCreate = component.AutoCreateRigidBody;
        if (ImGui.Checkbox("Auto-create rigid body", ref autoCreate))
            component.AutoCreateRigidBody = autoCreate;

        float density = component.Density;
        if (ImGui.DragFloat("Density", ref density, 0.01f, 0.001f, 1000f, "%.3f"))
            component.Density = MathF.Max(0.0001f, density);

        Vector3 translation = component.ShapeOffsetTranslation;
        if (ImGui.DragFloat3("Shape Offset", ref translation, 0.01f))
            component.ShapeOffsetTranslation = translation;

        Vector4 rotation = RigidBodyEditorShared.QuaternionToVector4(component.ShapeOffsetRotation);
        if (ImGui.DragFloat4("Shape Rotation (xyzw)", ref rotation, 0.01f))
            component.ShapeOffsetRotation = RigidBodyEditorShared.Vector4ToQuaternion(rotation);
    }

    private static void DrawActorSettings(DynamicRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Actor Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool gravityEnabled = component.GravityEnabled;
        if (ImGui.Checkbox("Gravity Enabled", ref gravityEnabled))
            component.GravityEnabled = gravityEnabled;

        bool simulationEnabled = component.SimulationEnabled;
        if (ImGui.Checkbox("Simulation Enabled", ref simulationEnabled))
            component.SimulationEnabled = simulationEnabled;

        bool debugVisualization = component.DebugVisualization;
        if (ImGui.Checkbox("Debug Visualization", ref debugVisualization))
            component.DebugVisualization = debugVisualization;

        bool sendSleep = component.SendSleepNotifies;
        if (ImGui.Checkbox("Send Sleep Notifies", ref sendSleep))
            component.SendSleepNotifies = sendSleep;

        int collisionGroup = component.CollisionGroup;
        if (ImGui.InputInt("Collision Group", ref collisionGroup))
            component.CollisionGroup = (ushort)Math.Clamp(collisionGroup, 0, ushort.MaxValue);

        var mask = component.GroupsMask;
        mask = RigidBodyEditorShared.DrawGroupsMaskControls(mask);
        component.GroupsMask = mask;

        int dominanceGroup = component.DominanceGroup;
        if (ImGui.InputInt("Dominance Group", ref dominanceGroup))
            component.DominanceGroup = (byte)Math.Clamp(dominanceGroup, byte.MinValue, byte.MaxValue);

        int ownerClient = component.OwnerClient;
        if (ImGui.InputInt("Owner Client", ref ownerClient))
            component.OwnerClient = (byte)Math.Clamp(ownerClient, byte.MinValue, byte.MaxValue);

        string actorName = component.ActorName ?? string.Empty;
        if (ImGui.InputText("Actor Name", ref actorName, 128))
            component.ActorName = actorName;
    }

    private static void DrawDynamicsSettings(DynamicRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Dynamic Properties", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        PhysicsRigidBodyFlags bodyFlags = component.BodyFlags;
        if (DrawFlagCheckbox("Kinematic", PhysicsRigidBodyFlags.Kinematic, ref bodyFlags) |
            DrawFlagCheckbox("Use Kinematic Target For Queries", PhysicsRigidBodyFlags.UseKinematicTargetForQueries, ref bodyFlags) |
            DrawFlagCheckbox("Enable CCD", PhysicsRigidBodyFlags.EnableCcd, ref bodyFlags) |
            DrawFlagCheckbox("Speculative CCD", PhysicsRigidBodyFlags.EnableSpeculativeCcd, ref bodyFlags) |
            DrawFlagCheckbox("CCD Max Contact Impulse", PhysicsRigidBodyFlags.EnableCcdMaxContactImpulse, ref bodyFlags) |
            DrawFlagCheckbox("CCD Friction", PhysicsRigidBodyFlags.EnableCcdFriction, ref bodyFlags))
        {
            component.BodyFlags = bodyFlags;
        }

        PhysicsLockFlags lockFlags = component.LockFlags;
        if (DrawLockCheckbox("Lock Linear X", PhysicsLockFlags.LinearX, ref lockFlags) |
            DrawLockCheckbox("Lock Linear Y", PhysicsLockFlags.LinearY, ref lockFlags) |
            DrawLockCheckbox("Lock Linear Z", PhysicsLockFlags.LinearZ, ref lockFlags) |
            DrawLockCheckbox("Lock Angular X", PhysicsLockFlags.AngularX, ref lockFlags) |
            DrawLockCheckbox("Lock Angular Y", PhysicsLockFlags.AngularY, ref lockFlags) |
            DrawLockCheckbox("Lock Angular Z", PhysicsLockFlags.AngularZ, ref lockFlags))
        {
            component.LockFlags = lockFlags;
        }

        float linearDamping = component.LinearDamping;
        if (ImGui.DragFloat("Linear Damping", ref linearDamping, 0.01f, 0.0f, 100.0f))
            component.LinearDamping = MathF.Max(0.0f, linearDamping);

        float angularDamping = component.AngularDamping;
        if (ImGui.DragFloat("Angular Damping", ref angularDamping, 0.01f, 0.0f, 100.0f))
            component.AngularDamping = MathF.Max(0.0f, angularDamping);

        float maxLinearVelocity = component.MaxLinearVelocity;
        if (ImGui.DragFloat("Max Linear Velocity", ref maxLinearVelocity, 0.1f, 0.0f, 1000.0f))
            component.MaxLinearVelocity = MathF.Max(0.0f, maxLinearVelocity);

        float maxAngularVelocity = component.MaxAngularVelocity;
        if (ImGui.DragFloat("Max Angular Velocity", ref maxAngularVelocity, 0.1f, 0.0f, 1000.0f))
            component.MaxAngularVelocity = MathF.Max(0.0f, maxAngularVelocity);

        float mass = component.Mass;
        if (ImGui.DragFloat("Mass", ref mass, 0.01f, 0.0001f, 100000.0f, "%.4f"))
            component.Mass = MathF.Max(0.0001f, mass);

        Vector3 inertia = component.MassSpaceInertiaTensor;
        if (ImGui.DragFloat3("Mass Space Inertia Tensor", ref inertia, 0.01f))
            component.MassSpaceInertiaTensor = inertia;

        var massFrame = component.CenterOfMassLocalPose;
        Vector3 comTranslation = massFrame.Translation;
        bool massFrameChanged = false;
        if (ImGui.DragFloat3("Center Of Mass (Local)", ref comTranslation, 0.01f))
        {
            massFrame.Translation = comTranslation;
            massFrameChanged = true;
        }

        Vector4 comRotation = RigidBodyEditorShared.QuaternionToVector4(massFrame.Rotation);
        if (ImGui.DragFloat4("Center Of Mass Rotation", ref comRotation, 0.01f))
        {
            massFrame.Rotation = RigidBodyEditorShared.Vector4ToQuaternion(comRotation);
            massFrameChanged = true;
        }

        if (massFrameChanged)
            component.CenterOfMassLocalPose = massFrame;

        float minCcdAdvance = component.MinCcdAdvanceCoefficient;
        if (ImGui.DragFloat("Min CCD Advance Coefficient", ref minCcdAdvance, 0.001f, 0.0f, 1.0f))
            component.MinCcdAdvanceCoefficient = Math.Clamp(minCcdAdvance, 0.0f, 1.0f);

        float maxDepenetration = component.MaxDepenetrationVelocity;
        if (ImGui.DragFloat("Max Depenetration Velocity", ref maxDepenetration, 0.1f, 0.0f, 1000.0f))
            component.MaxDepenetrationVelocity = MathF.Max(0.0f, maxDepenetration);

        float maxContactImpulse = component.MaxContactImpulse;
        if (ImGui.DragFloat("Max Contact Impulse", ref maxContactImpulse, 0.1f, 0.0f, 100000.0f))
            component.MaxContactImpulse = MathF.Max(0.0f, maxContactImpulse);

        float contactSlop = component.ContactSlopCoefficient;
        if (ImGui.DragFloat("Contact Slop Coefficient", ref contactSlop, 0.001f, 0.0f, 1.0f))
            component.ContactSlopCoefficient = Math.Clamp(contactSlop, 0.0f, 1.0f);

        float stabilization = component.StabilizationThreshold;
        if (ImGui.DragFloat("Stabilization Threshold", ref stabilization, 0.01f, 0.0f, 100.0f))
            component.StabilizationThreshold = MathF.Max(0.0f, stabilization);

        float sleepThreshold = component.SleepThreshold;
        if (ImGui.DragFloat("Sleep Threshold", ref sleepThreshold, 0.001f, 0.0f, 10.0f))
            component.SleepThreshold = MathF.Max(0.0f, sleepThreshold);

        float contactReport = component.ContactReportThreshold;
        if (ImGui.DragFloat("Contact Report Threshold", ref contactReport, 0.1f, 0.0f, 100000.0f))
            component.ContactReportThreshold = MathF.Max(0.0f, contactReport);

        float wakeCounter = component.WakeCounter;
        if (ImGui.DragFloat("Wake Counter", ref wakeCounter, 0.01f, 0.0f, 10.0f))
            component.WakeCounter = MathF.Max(0.0f, wakeCounter);

        var iterations = component.SolverIterations;
        int positionIters = (int)iterations.MinPositionIterations;
        int velocityIters = (int)iterations.MinVelocityIterations;
        if (ImGui.InputInt("Min Position Iterations", ref positionIters))
        {
            iterations.MinPositionIterations = (uint)Math.Max(0, positionIters);
            component.SolverIterations = iterations;
        }
        if (ImGui.InputInt("Min Velocity Iterations", ref velocityIters))
        {
            iterations.MinVelocityIterations = (uint)Math.Max(0, velocityIters);
            component.SolverIterations = iterations;
        }

        Vector3 linearVelocity = component.LinearVelocity;
        if (ImGui.DragFloat3("Linear Velocity", ref linearVelocity, 0.01f))
            component.LinearVelocity = linearVelocity;

        Vector3 angularVelocity = component.AngularVelocity;
        if (ImGui.DragFloat3("Angular Velocity", ref angularVelocity, 0.01f))
            component.AngularVelocity = angularVelocity;

        if (component.KinematicTarget.HasValue)
        {
            ImGui.TextDisabled("Kinematic target active.");
            if (ImGui.Button("Clear Kinematic Target"))
                component.KinematicTarget = null;
        }
    }

    private static bool DrawFlagCheckbox(string label, PhysicsRigidBodyFlags flag, ref PhysicsRigidBodyFlags current)
    {
        bool enabled = current.HasFlag(flag);
        if (ImGui.Checkbox(label, ref enabled))
        {
            current = enabled ? current | flag : current & ~flag;
            return true;
        }
        return false;
    }

    private static bool DrawLockCheckbox(string label, PhysicsLockFlags flag, ref PhysicsLockFlags current)
    {
        bool enabled = current.HasFlag(flag);
        if (ImGui.Checkbox(label, ref enabled))
        {
            current = enabled ? current | flag : current & ~flag;
            return true;
        }
        return false;
    }
}

public sealed class StaticRigidBodyComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not StaticRigidBodyComponent rigidBodyComponent)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(rigidBodyComponent, visited, "Static Rigid Body Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(rigidBodyComponent.GetHashCode());
        DrawOverview(rigidBodyComponent);
        DrawCreationSettings(rigidBodyComponent);
        DrawActorSettings(rigidBodyComponent);
        RigidBodyEditorShared.DrawConvexHullSection(rigidBodyComponent);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawOverview(StaticRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Overview", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var rigidBody = component.RigidBody;
        if (rigidBody is null)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Rigid body not created.");
        }
        else
        {
            ImGui.TextUnformatted($"Rigid body: {rigidBody.GetType().Name}");
        }

        ImGui.TextUnformatted($"Auto create: {(component.AutoCreateRigidBody ? "Enabled" : "Disabled")}");
        ImGui.TextUnformatted($"Active in hierarchy: {component.IsActiveInHierarchy}");
    }

    private static void DrawCreationSettings(StaticRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Creation Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool autoCreate = component.AutoCreateRigidBody;
        if (ImGui.Checkbox("Auto-create rigid body", ref autoCreate))
            component.AutoCreateRigidBody = autoCreate;

        Vector3 translation = component.ShapeOffsetTranslation;
        if (ImGui.DragFloat3("Shape Offset", ref translation, 0.01f))
            component.ShapeOffsetTranslation = translation;

        Vector4 rotation = RigidBodyEditorShared.QuaternionToVector4(component.ShapeOffsetRotation);
        if (ImGui.DragFloat4("Shape Rotation (xyzw)", ref rotation, 0.01f))
            component.ShapeOffsetRotation = RigidBodyEditorShared.Vector4ToQuaternion(rotation);
    }

    private static void DrawActorSettings(StaticRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Actor Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool gravityEnabled = component.GravityEnabled;
        if (ImGui.Checkbox("Gravity Enabled", ref gravityEnabled))
            component.GravityEnabled = gravityEnabled;

        bool simulationEnabled = component.SimulationEnabled;
        if (ImGui.Checkbox("Simulation Enabled", ref simulationEnabled))
            component.SimulationEnabled = simulationEnabled;

        bool debugVisualization = component.DebugVisualization;
        if (ImGui.Checkbox("Debug Visualization", ref debugVisualization))
            component.DebugVisualization = debugVisualization;

        bool sendSleep = component.SendSleepNotifies;
        if (ImGui.Checkbox("Send Sleep Notifies", ref sendSleep))
            component.SendSleepNotifies = sendSleep;

        int collisionGroup = component.CollisionGroup;
        if (ImGui.InputInt("Collision Group", ref collisionGroup))
            component.CollisionGroup = (ushort)Math.Clamp(collisionGroup, 0, ushort.MaxValue);

        var mask = component.GroupsMask;
        mask = RigidBodyEditorShared.DrawGroupsMaskControls(mask);
        component.GroupsMask = mask;

        int dominanceGroup = component.DominanceGroup;
        if (ImGui.InputInt("Dominance Group", ref dominanceGroup))
            component.DominanceGroup = (byte)Math.Clamp(dominanceGroup, byte.MinValue, byte.MaxValue);

        int ownerClient = component.OwnerClient;
        if (ImGui.InputInt("Owner Client", ref ownerClient))
            component.OwnerClient = (byte)Math.Clamp(ownerClient, byte.MinValue, byte.MaxValue);

        string actorName = component.ActorName ?? string.Empty;
        if (ImGui.InputText("Actor Name", ref actorName, 128))
            component.ActorName = actorName;
    }
}
