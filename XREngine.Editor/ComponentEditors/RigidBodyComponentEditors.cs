using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    private sealed class HullPreviewState
    {
        public HashSet<int> EnabledHullIndices { get; } = [];
    }

    private readonly record struct HullMetrics(
        int VertexCount,
        int TriangleCount,
        Vector3 Min,
        Vector3 Max)
    {
        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;
    }

    private static readonly ConditionalWeakTable<PhysicsActorComponent, HullPreviewState> _previewStates = new();
    private static readonly string[] s_spinnerFrames = ["-", "\\", "|", "/"];

    public static void DrawConvexHullSection(PhysicsActorComponent component)
    {
        ImGui.SeparatorText("Convex Hull Utilities");

        var status = component.GetConvexHullGenerationStatus();
        bool hasModel = component.GetSiblingComponent<ModelComponent>() is not null;
        var cachedHulls = component.GetCachedConvexHulls();

        using (new ImGuiDisabledScope(!hasModel || status.InProgress))
        {
            string label = status.InProgress ? "Generating..." : "Generate Convex Hulls";
            if (ImGui.Button(label, new Vector2(-1f, 0f)) && hasModel && !status.InProgress)
                _ = component.GenerateConvexHullsFromModelAsync();
        }

        if (!hasModel)
            ImGui.TextDisabled("Requires a sibling ModelComponent.");

        if (hasModel || cachedHulls is { Count: > 0 })
            DrawWireframePreviewControls(component, cachedHulls);

        DrawGenerationStatus(status);
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

        if (!hasHulls)
        {
            previewState.EnabledHullIndices.Clear();
            ImGui.TextDisabled("Generate convex hulls to enable preview.");
            return;
        }

        TrimInvalidHullSelections(previewState, cachedHulls.Count);

        ImGui.SeparatorText("Generated Hulls");
        DrawHullSummary(cachedHulls);
        DrawHullPreviewToolbar(previewState, cachedHulls.Count);
        DrawHullPreviewTable(previewState, cachedHulls);

        int selectedHullCount = previewState.EnabledHullIndices.Count;
        if (selectedHullCount > 0)
        {
            RenderHullWireframes(component, cachedHulls, previewState.EnabledHullIndices);
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.9f, 1f), $"Queued {selectedHullCount} of {cachedHulls.Count} hull wireframes for rendering.");
        }
        else
        {
            ImGui.TextDisabled("Enable one or more hull previews to render wireframes.");
        }
    }

    private static void DrawHullSummary(IReadOnlyList<CoACD.ConvexHullMesh> hulls)
    {
        int totalVertices = 0;
        int totalTriangles = 0;

        for (int i = 0; i < hulls.Count; i++)
        {
            HullMetrics metrics = CalculateHullMetrics(hulls[i]);
            totalVertices += metrics.VertexCount;
            totalTriangles += metrics.TriangleCount;
        }

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("ConvexHullSummary", 3, tableFlags))
            return;

        ImGui.TableSetupColumn("Hulls", ImGuiTableColumnFlags.WidthStretch, 0.33f);
        ImGui.TableSetupColumn("Vertices", ImGuiTableColumnFlags.WidthStretch, 0.33f);
        ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(hulls.Count.ToString());
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(totalVertices.ToString());
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(totalTriangles.ToString());

        ImGui.EndTable();
    }

    private static void DrawHullPreviewToolbar(HullPreviewState previewState, int hullCount)
    {
        if (ImGui.SmallButton("Show All Hulls"))
        {
            previewState.EnabledHullIndices.Clear();
            for (int i = 0; i < hullCount; i++)
                previewState.EnabledHullIndices.Add(i);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Hide All Hulls"))
            previewState.EnabledHullIndices.Clear();

        ImGui.SameLine();
        ImGui.TextDisabled($"Previewing {previewState.EnabledHullIndices.Count}/{hullCount}");
    }

    private static void DrawHullPreviewTable(HullPreviewState previewState, IReadOnlyList<CoACD.ConvexHullMesh> hulls)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.BordersOuter
            | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.NoSavedSettings;

        if (!ImGui.BeginTable("ConvexHullDetails", 6, tableFlags))
            return;

        ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthFixed, 70.0f);
        ImGui.TableSetupColumn("Hull", ImGuiTableColumnFlags.WidthFixed, 55.0f);
        ImGui.TableSetupColumn("Vertices", ImGuiTableColumnFlags.WidthFixed, 70.0f);
        ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.WidthFixed, 75.0f);
        ImGui.TableSetupColumn("Bounds Size", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn("Center", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableHeadersRow();

        for (int hullIndex = 0; hullIndex < hulls.Count; hullIndex++)
        {
            HullMetrics metrics = CalculateHullMetrics(hulls[hullIndex]);
            bool previewEnabled = previewState.EnabledHullIndices.Contains(hullIndex);

            ImGui.PushID(hullIndex);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            if (ImGui.Checkbox("##PreviewHull", ref previewEnabled))
            {
                if (previewEnabled)
                    previewState.EnabledHullIndices.Add(hullIndex);
                else
                    previewState.EnabledHullIndices.Remove(hullIndex);
            }

            ImGui.TableSetColumnIndex(1);
            if (previewEnabled)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.9f, 0.9f, 1.0f));
            ImGui.TextUnformatted($"#{hullIndex + 1}");
            if (previewEnabled)
                ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(metrics.VertexCount.ToString());

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(metrics.TriangleCount.ToString());

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(FormatVector(metrics.Size));

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(FormatVector(metrics.Center));

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Min: {FormatVector(metrics.Min)}\nMax: {FormatVector(metrics.Max)}");

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void RenderHullWireframes(
        PhysicsActorComponent component,
        IReadOnlyList<CoACD.ConvexHullMesh> hulls,
        HashSet<int> enabledHullIndices)
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

        for (int hullIndex = 0; hullIndex < hulls.Count; hullIndex++)
        {
            if (!enabledHullIndices.Contains(hullIndex))
                continue;

            var hull = hulls[hullIndex];
            var vertices = hull.Vertices;
            var indices = hull.Indices;
            if (vertices is null || vertices.Length == 0 || indices is null || indices.Length < 3)
                continue;

            ColorF4 color = GetHullPreviewColor(hullIndex);

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

                Engine.Rendering.Debug.RenderTriangle(worldA, worldB, worldC, color, solid: false);
            }
        }
    }

    private static void TrimInvalidHullSelections(HullPreviewState previewState, int hullCount)
        => previewState.EnabledHullIndices.RemoveWhere(index => index < 0 || index >= hullCount);

    private static HullMetrics CalculateHullMetrics(CoACD.ConvexHullMesh hull)
    {
        Vector3[]? vertices = hull.Vertices;
        int vertexCount = vertices?.Length ?? 0;
        int triangleCount = (hull.Indices?.Length ?? 0) / 3;
        if (vertexCount == 0)
            return new HullMetrics(vertexCount, triangleCount, Vector3.Zero, Vector3.Zero);

        Vector3 min = vertices![0];
        Vector3 max = vertices[0];
        for (int i = 1; i < vertices.Length; i++)
        {
            min = Vector3.Min(min, vertices[i]);
            max = Vector3.Max(max, vertices[i]);
        }

        return new HullMetrics(vertexCount, triangleCount, min, max);
    }

    private static ColorF4 GetHullPreviewColor(int hullIndex)
    {
        ColorF4[] palette =
        [
            new ColorF4(0.25f, 0.85f, 1.0f, 1.0f),
            new ColorF4(1.0f, 0.72f, 0.24f, 1.0f),
            new ColorF4(0.52f, 1.0f, 0.48f, 1.0f),
            new ColorF4(1.0f, 0.48f, 0.68f, 1.0f),
            new ColorF4(0.72f, 0.62f, 1.0f, 1.0f),
            new ColorF4(1.0f, 0.88f, 0.42f, 1.0f)
        ];

        return palette[hullIndex % palette.Length];
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

    private static void DrawGenerationStatus(PhysicsActorComponent.ConvexHullGenerationStatus status)
    {
        if (status.InProgress)
        {
            string spinner = s_spinnerFrames[(int)(ImGui.GetTime() * 8) % s_spinnerFrames.Length];
            string message = status.Progress is { } progressState
                ? FormatProgressMessage(progressState)
                : status.ActiveMessage ?? "Running convex decomposition...";
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.9f, 1f), $"{spinner} {message}");

            var progress = status.Progress;
            if (progress is { TotalInputs: > 0 })
            {
                float percent = progress.Value.Percentage;
                ImGui.ProgressBar(percent, new Vector2(-1f, 0f), $"{percent * 100f:0}%");
                if (status.StartedAtUtc is DateTimeOffset startedAtUtc)
                    ImGui.TextDisabled($"{progress.Value.CompletedInputs}/{progress.Value.TotalInputs} meshes • {(DateTimeOffset.UtcNow - startedAtUtc):mm\\:ss}");
                else
                    ImGui.TextDisabled($"{progress.Value.CompletedInputs}/{progress.Value.TotalInputs} meshes");
            }
            else if (status.StartedAtUtc is DateTimeOffset startedAtUtc)
            {
                ImGui.TextDisabled($"Elapsed {(DateTimeOffset.UtcNow - startedAtUtc):mm\\:ss}");
            }
        }
        else if (!string.IsNullOrEmpty(status.LastMessage))
        {
            ImGui.TextWrapped(status.LastMessage);
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
        {
            using var _ = Undo.TrackChange("Auto-create Rigid Body", component);
            component.AutoCreateRigidBody = autoCreate;
        }

        float density = component.Density;
        if (ImGui.DragFloat("Density", ref density, 0.01f, 0.001f, 1000f, "%.3f"))
            component.Density = MathF.Max(0.0001f, density);
        ImGuiUndoHelper.TrackDragUndo("Density", component);

        Vector3 translation = component.ShapeOffsetTranslation;
        if (ImGui.DragFloat3("Shape Offset", ref translation, 0.01f))
            component.ShapeOffsetTranslation = translation;
        ImGuiUndoHelper.TrackDragUndo("Shape Offset", component);

        Vector4 rotation = RigidBodyEditorShared.QuaternionToVector4(component.ShapeOffsetRotation);
        if (ImGui.DragFloat4("Shape Rotation (xyzw)", ref rotation, 0.01f))
            component.ShapeOffsetRotation = RigidBodyEditorShared.Vector4ToQuaternion(rotation);
        ImGuiUndoHelper.TrackDragUndo("Shape Rotation", component);
    }

    private static void DrawActorSettings(DynamicRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Actor Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool gravityEnabled = component.GravityEnabled;
        if (ImGui.Checkbox("Gravity Enabled", ref gravityEnabled))
        {
            using var _ = Undo.TrackChange("Gravity Enabled", component);
            component.GravityEnabled = gravityEnabled;
        }

        bool simulationEnabled = component.SimulationEnabled;
        if (ImGui.Checkbox("Simulation Enabled", ref simulationEnabled))
        {
            using var _ = Undo.TrackChange("Simulation Enabled", component);
            component.SimulationEnabled = simulationEnabled;
        }

        bool debugVisualization = component.DebugVisualization;
        if (ImGui.Checkbox("Debug Visualization", ref debugVisualization))
        {
            using var _ = Undo.TrackChange("Debug Visualization", component);
            component.DebugVisualization = debugVisualization;
        }

        bool sendSleep = component.SendSleepNotifies;
        if (ImGui.Checkbox("Send Sleep Notifies", ref sendSleep))
        {
            using var _ = Undo.TrackChange("Send Sleep Notifies", component);
            component.SendSleepNotifies = sendSleep;
        }

        int collisionGroup = component.CollisionGroup;
        if (ImGui.InputInt("Collision Group", ref collisionGroup))
            component.CollisionGroup = (ushort)Math.Clamp(collisionGroup, 0, ushort.MaxValue);
        ImGuiUndoHelper.TrackDragUndo("Collision Group", component);

        var mask = component.GroupsMask;
        mask = RigidBodyEditorShared.DrawGroupsMaskControls(mask);
        component.GroupsMask = mask;

        int dominanceGroup = component.DominanceGroup;
        if (ImGui.InputInt("Dominance Group", ref dominanceGroup))
            component.DominanceGroup = (byte)Math.Clamp(dominanceGroup, byte.MinValue, byte.MaxValue);
        ImGuiUndoHelper.TrackDragUndo("Dominance Group", component);

        int ownerClient = component.OwnerClient;
        if (ImGui.InputInt("Owner Client", ref ownerClient))
            component.OwnerClient = (byte)Math.Clamp(ownerClient, byte.MinValue, byte.MaxValue);
        ImGuiUndoHelper.TrackDragUndo("Owner Client", component);

        string actorName = component.ActorName ?? string.Empty;
        if (ImGui.InputText("Actor Name", ref actorName, 128))
            component.ActorName = actorName;
        ImGuiUndoHelper.TrackDragUndo("Actor Name", component);
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
            using var _ = Undo.TrackChange("Body Flags", component);
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
            using var _ = Undo.TrackChange("Lock Flags", component);
            component.LockFlags = lockFlags;
        }

        float linearDamping = component.LinearDamping;
        if (ImGui.DragFloat("Linear Damping", ref linearDamping, 0.01f, 0.0f, 100.0f))
            component.LinearDamping = MathF.Max(0.0f, linearDamping);
        ImGuiUndoHelper.TrackDragUndo("Linear Damping", component);

        float angularDamping = component.AngularDamping;
        if (ImGui.DragFloat("Angular Damping", ref angularDamping, 0.01f, 0.0f, 100.0f))
            component.AngularDamping = MathF.Max(0.0f, angularDamping);
        ImGuiUndoHelper.TrackDragUndo("Angular Damping", component);

        float maxLinearVelocity = component.MaxLinearVelocity;
        if (ImGui.DragFloat("Max Linear Velocity", ref maxLinearVelocity, 0.1f, 0.0f, 1000.0f))
            component.MaxLinearVelocity = MathF.Max(0.0f, maxLinearVelocity);
        ImGuiUndoHelper.TrackDragUndo("Max Linear Velocity", component);

        float maxAngularVelocity = component.MaxAngularVelocity;
        if (ImGui.DragFloat("Max Angular Velocity", ref maxAngularVelocity, 0.1f, 0.0f, 1000.0f))
            component.MaxAngularVelocity = MathF.Max(0.0f, maxAngularVelocity);
        ImGuiUndoHelper.TrackDragUndo("Max Angular Velocity", component);

        float mass = component.Mass;
        if (ImGui.DragFloat("Mass", ref mass, 0.01f, 0.0001f, 100000.0f, "%.4f"))
            component.Mass = MathF.Max(0.0001f, mass);
        ImGuiUndoHelper.TrackDragUndo("Mass", component);

        Vector3 inertia = component.MassSpaceInertiaTensor;
        if (ImGui.DragFloat3("Mass Space Inertia Tensor", ref inertia, 0.01f))
            component.MassSpaceInertiaTensor = inertia;
        ImGuiUndoHelper.TrackDragUndo("Inertia Tensor", component);

        var massFrame = component.CenterOfMassLocalPose;
        Vector3 comTranslation = massFrame.Translation;
        bool massFrameChanged = false;
        if (ImGui.DragFloat3("Center Of Mass (Local)", ref comTranslation, 0.01f))
        {
            massFrame.Translation = comTranslation;
            massFrameChanged = true;
        }
        ImGuiUndoHelper.TrackDragUndo("Center Of Mass", component);

        Vector4 comRotation = RigidBodyEditorShared.QuaternionToVector4(massFrame.Rotation);
        if (ImGui.DragFloat4("Center Of Mass Rotation", ref comRotation, 0.01f))
        {
            massFrame.Rotation = RigidBodyEditorShared.Vector4ToQuaternion(comRotation);
            massFrameChanged = true;
        }
        ImGuiUndoHelper.TrackDragUndo("Center Of Mass Rotation", component);

        if (massFrameChanged)
            component.CenterOfMassLocalPose = massFrame;

        float minCcdAdvance = component.MinCcdAdvanceCoefficient;
        if (ImGui.DragFloat("Min CCD Advance Coefficient", ref minCcdAdvance, 0.001f, 0.0f, 1.0f))
            component.MinCcdAdvanceCoefficient = Math.Clamp(minCcdAdvance, 0.0f, 1.0f);
        ImGuiUndoHelper.TrackDragUndo("Min CCD Advance", component);

        float maxDepenetration = component.MaxDepenetrationVelocity;
        if (ImGui.DragFloat("Max Depenetration Velocity", ref maxDepenetration, 0.1f, 0.0f, 1000.0f))
            component.MaxDepenetrationVelocity = MathF.Max(0.0f, maxDepenetration);
        ImGuiUndoHelper.TrackDragUndo("Max Depenetration", component);

        float maxContactImpulse = component.MaxContactImpulse;
        if (ImGui.DragFloat("Max Contact Impulse", ref maxContactImpulse, 0.1f, 0.0f, 100000.0f))
            component.MaxContactImpulse = MathF.Max(0.0f, maxContactImpulse);
        ImGuiUndoHelper.TrackDragUndo("Max Contact Impulse", component);

        float contactSlop = component.ContactSlopCoefficient;
        if (ImGui.DragFloat("Contact Slop Coefficient", ref contactSlop, 0.001f, 0.0f, 1.0f))
            component.ContactSlopCoefficient = Math.Clamp(contactSlop, 0.0f, 1.0f);
        ImGuiUndoHelper.TrackDragUndo("Contact Slop", component);

        float stabilization = component.StabilizationThreshold;
        if (ImGui.DragFloat("Stabilization Threshold", ref stabilization, 0.01f, 0.0f, 100.0f))
            component.StabilizationThreshold = MathF.Max(0.0f, stabilization);
        ImGuiUndoHelper.TrackDragUndo("Stabilization", component);

        float sleepThreshold = component.SleepThreshold;
        if (ImGui.DragFloat("Sleep Threshold", ref sleepThreshold, 0.001f, 0.0f, 10.0f))
            component.SleepThreshold = MathF.Max(0.0f, sleepThreshold);
        ImGuiUndoHelper.TrackDragUndo("Sleep Threshold", component);

        float contactReport = component.ContactReportThreshold;
        if (ImGui.DragFloat("Contact Report Threshold", ref contactReport, 0.1f, 0.0f, 100000.0f))
            component.ContactReportThreshold = MathF.Max(0.0f, contactReport);
        ImGuiUndoHelper.TrackDragUndo("Contact Report", component);

        float wakeCounter = component.WakeCounter;
        if (ImGui.DragFloat("Wake Counter", ref wakeCounter, 0.01f, 0.0f, 10.0f))
            component.WakeCounter = MathF.Max(0.0f, wakeCounter);
        ImGuiUndoHelper.TrackDragUndo("Wake Counter", component);

        var iterations = component.SolverIterations;
        int positionIters = (int)iterations.MinPositionIterations;
        int velocityIters = (int)iterations.MinVelocityIterations;
        if (ImGui.InputInt("Min Position Iterations", ref positionIters))
        {
            iterations.MinPositionIterations = (uint)Math.Max(0, positionIters);
            component.SolverIterations = iterations;
        }
        ImGuiUndoHelper.TrackDragUndo("Position Iterations", component);
        if (ImGui.InputInt("Min Velocity Iterations", ref velocityIters))
        {
            iterations.MinVelocityIterations = (uint)Math.Max(0, velocityIters);
            component.SolverIterations = iterations;
        }
        ImGuiUndoHelper.TrackDragUndo("Velocity Iterations", component);

        Vector3 linearVelocity = component.LinearVelocity;
        if (ImGui.DragFloat3("Linear Velocity", ref linearVelocity, 0.01f))
            component.LinearVelocity = linearVelocity;
        ImGuiUndoHelper.TrackDragUndo("Linear Velocity", component);

        Vector3 angularVelocity = component.AngularVelocity;
        if (ImGui.DragFloat3("Angular Velocity", ref angularVelocity, 0.01f))
            component.AngularVelocity = angularVelocity;
        ImGuiUndoHelper.TrackDragUndo("Angular Velocity", component);

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
        {
            using var _ = Undo.TrackChange("Auto-create Rigid Body", component);
            component.AutoCreateRigidBody = autoCreate;
        }

        Vector3 translation = component.ShapeOffsetTranslation;
        if (ImGui.DragFloat3("Shape Offset", ref translation, 0.01f))
            component.ShapeOffsetTranslation = translation;
        ImGuiUndoHelper.TrackDragUndo("Shape Offset", component);

        Vector4 rotation = RigidBodyEditorShared.QuaternionToVector4(component.ShapeOffsetRotation);
        if (ImGui.DragFloat4("Shape Rotation (xyzw)", ref rotation, 0.01f))
            component.ShapeOffsetRotation = RigidBodyEditorShared.Vector4ToQuaternion(rotation);
        ImGuiUndoHelper.TrackDragUndo("Shape Rotation", component);
    }

    private static void DrawActorSettings(StaticRigidBodyComponent component)
    {
        if (!ImGui.CollapsingHeader("Actor Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool gravityEnabled = component.GravityEnabled;
        if (ImGui.Checkbox("Gravity Enabled", ref gravityEnabled))
        {
            using var _ = Undo.TrackChange("Gravity Enabled", component);
            component.GravityEnabled = gravityEnabled;
        }

        bool simulationEnabled = component.SimulationEnabled;
        if (ImGui.Checkbox("Simulation Enabled", ref simulationEnabled))
        {
            using var _ = Undo.TrackChange("Simulation Enabled", component);
            component.SimulationEnabled = simulationEnabled;
        }

        bool debugVisualization = component.DebugVisualization;
        if (ImGui.Checkbox("Debug Visualization", ref debugVisualization))
        {
            using var _ = Undo.TrackChange("Debug Visualization", component);
            component.DebugVisualization = debugVisualization;
        }

        bool sendSleep = component.SendSleepNotifies;
        if (ImGui.Checkbox("Send Sleep Notifies", ref sendSleep))
        {
            using var _ = Undo.TrackChange("Send Sleep Notifies", component);
            component.SendSleepNotifies = sendSleep;
        }

        int collisionGroup = component.CollisionGroup;
        if (ImGui.InputInt("Collision Group", ref collisionGroup))
            component.CollisionGroup = (ushort)Math.Clamp(collisionGroup, 0, ushort.MaxValue);
        ImGuiUndoHelper.TrackDragUndo("Collision Group", component);

        var mask = component.GroupsMask;
        mask = RigidBodyEditorShared.DrawGroupsMaskControls(mask);
        component.GroupsMask = mask;

        int dominanceGroup = component.DominanceGroup;
        if (ImGui.InputInt("Dominance Group", ref dominanceGroup))
            component.DominanceGroup = (byte)Math.Clamp(dominanceGroup, byte.MinValue, byte.MaxValue);
        ImGuiUndoHelper.TrackDragUndo("Dominance Group", component);

        int ownerClient = component.OwnerClient;
        if (ImGui.InputInt("Owner Client", ref ownerClient))
            component.OwnerClient = (byte)Math.Clamp(ownerClient, byte.MinValue, byte.MaxValue);
        ImGuiUndoHelper.TrackDragUndo("Owner Client", component);

        string actorName = component.ActorName ?? string.Empty;
        if (ImGui.InputText("Actor Name", ref actorName, 128))
            component.ActorName = actorName;
        ImGuiUndoHelper.TrackDragUndo("Actor Name", component);
    }
}
