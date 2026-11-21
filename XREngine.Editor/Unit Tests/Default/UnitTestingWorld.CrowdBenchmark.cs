using System.Diagnostics;
using System.Numerics;
using XREngine.Rendering.Models;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static partial class Models
    {
        /// <summary>One avatar of the crowd benchmark.</summary>
        /// <param name="Root">The imported or cloned hierarchy root of the avatar.</param>
        /// <param name="AvatarRoot">The node that gets the humanoid and animation setup.</param>
        private readonly record struct CrowdAvatar(SceneNode Root, SceneNode AvatarRoot);

        private static int _crowdBenchmarkClaimed;

        /// <summary>
        /// Returns true once, for the first animated model import, when the crowd benchmark is on.
        /// Later animated imports keep one copy.
        /// </summary>
        private static bool TryClaimCrowdBenchmark(Settings.ModelImportSettings model)
            => Toggles.CrowdBenchmark.Enabled &&
               model.Kind is UnitTestModelImportKind.Animated &&
               Interlocked.Exchange(ref _crowdBenchmarkClaimed, 1) == 0;

        /// <summary>
        /// Builds the crowd from a freshly imported avatar before its humanoid setup. It clones
        /// <paramref name="template"/> once per added avatar and puts every avatar under its own
        /// slot node, which stands on a benchmark ring and faces the ring center. With
        /// <paramref name="templateTakesFirstSlot"/>, the template moves under the first slot and
        /// counts as one crowd avatar; otherwise (the player avatar) it stays where it is and the
        /// crowd gets <see cref="UnitTestingCrowdBenchmarkSettings.AvatarCount"/> clones.
        /// </summary>
        /// <param name="template">The imported hierarchy root.</param>
        /// <param name="templateAvatarRoot">The avatar root inside <paramref name="template"/>.</param>
        /// <param name="slotParent">
        /// The node that gets the slot nodes, or null to return the slots as new roots in
        /// <paramref name="slotRoots"/> (an import scene that is not published yet).
        /// </param>
        /// <param name="templateTakesFirstSlot">True when the template is not the player avatar.</param>
        /// <param name="slotRoots">Receives the slot nodes when <paramref name="slotParent"/> is null.</param>
        private static List<CrowdAvatar> CreateCrowd(
            SceneNode template,
            SceneNode templateAvatarRoot,
            SceneNode? slotParent,
            bool templateTakesFirstSlot,
            List<SceneNode> slotRoots)
        {
            UnitTestingCrowdBenchmarkSettings crowd = Toggles.CrowdBenchmark;
            int avatarCount = Math.Clamp(crowd.AvatarCount, 1, UnitTestingCrowdBenchmarkSettings.MaxAvatarCount);
            List<CrowdAvatar> avatars = new(avatarCount);
            if (!TryResolveChildPath(template, templateAvatarRoot, out int[] avatarPath))
            {
                LogCrowdWarning($"[CrowdBenchmark] The avatar root '{templateAvatarRoot.Name}' is not under '{template.Name}'. The crowd is not created.");
                return avatars;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int index = 0; index < avatarCount; index++)
            {
                crowd.ResolvePlacement(index, avatarCount, out Vector3 position, out float yaw);
                SceneNode slot = slotParent is null
                    ? new SceneNode($"Crowd Avatar {index + 1}")
                    : new SceneNode(slotParent, $"Crowd Avatar {index + 1}");
                Transform slotTransform = slot.GetTransformAs<Transform>(true)!;
                slotTransform.Translation = position;
                slotTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
                if (slotParent is null)
                    slotRoots.Add(slot);

                SceneNode root;
                if (index == 0 && templateTakesFirstSlot)
                {
                    root = template;
                    root.Parent = slot;
                }
                else
                {
                    root = slotParent is null
                        ? SceneNodePrefabUtility.CloneHierarchy(template)
                        : SceneNodePrefabUtility.CloneHierarchyAttached(template, slot);
                    root.Name = $"{template.Name} Crowd {index + 1}";
                    if (slotParent is null)
                        root.Parent = slot;
                }

                if (!TryFollowChildPath(root, avatarPath, out SceneNode avatarRoot))
                {
                    LogCrowdWarning($"[CrowdBenchmark] Crowd avatar {index + 1} lost its avatar root after cloning.");
                    continue;
                }
                avatars.Add(new CrowdAvatar(root, avatarRoot));
            }

            stopwatch.Stop();
            // The auxiliary log also writes in Release builds, where the category logs are compiled out.
            Debug.WriteAuxiliaryLog(
                CrowdBenchmarkLogFileName,
                $"[CrowdBenchmark] Created {avatars.Count} avatars of '{template.Name}' on {Math.Max(1, crowd.RingRadii?.Count ?? 0)} rings " +
                $"in {stopwatch.Elapsed.TotalMilliseconds:F1} ms (mode {crowd.Mode}).");
            return avatars;
        }

        /// <summary>
        /// Builds the crowd on a long-running worker thread and then calls <paramref name="onAppThread"/>
        /// on the app thread with the avatars and their detached slot nodes. Each clone is a full
        /// serializer copy of the avatar and can take more than a minute for a large avatar. On the app
        /// thread, twelve clones stop scene updates for many minutes and the desktop frame stops presenting.
        /// The template and the clones are not attached to a scene or world while the worker runs, so no
        /// other thread reads or changes them. The callback attaches the slot nodes.
        /// </summary>
        /// <param name="template">The detached imported hierarchy root.</param>
        /// <param name="templateAvatarRoot">The avatar root inside <paramref name="template"/>.</param>
        /// <param name="templateTakesFirstSlot">True when the template is not the player avatar.</param>
        /// <param name="appThreadReason">The reason text for the app-thread invoke.</param>
        /// <param name="onAppThread">
        /// Receives the created avatars and the slot nodes. Both lists are empty when the clone failed;
        /// the template is then detached again and keeps no slot parent.
        /// </param>
        private static void CreateCrowdOffAppThread(
            SceneNode template,
            SceneNode templateAvatarRoot,
            bool templateTakesFirstSlot,
            string appThreadReason,
            Action<List<CrowdAvatar>, List<SceneNode>> onAppThread)
        {
            _ = Task.Factory.StartNew(
                () =>
                {
                    List<SceneNode> slotRoots = [];
                    List<CrowdAvatar> avatars;
                    try
                    {
                        avatars = CreateCrowd(template, templateAvatarRoot, slotParent: null, templateTakesFirstSlot, slotRoots);
                    }
                    catch (Exception ex)
                    {
                        LogCrowdWarning($"[CrowdBenchmark] Cloning '{template.Name}' failed. The crowd is not created. {ex.GetType().Name}: {ex.Message}");
                        if (template.Parent is { } slot && slotRoots.Contains(slot))
                            template.Parent = null;
                        avatars = [];
                        slotRoots = [];
                    }

                    _ = Engine.InvokeOnAppThread(() => onAppThread(avatars, slotRoots), appThreadReason, executeNowIfAlreadyAppThread: true);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>The auxiliary log file that gets the crowd benchmark lines.</summary>
        private const string CrowdBenchmarkLogFileName = "crowd-benchmark.log";

        /// <summary>
        /// Writes a crowd benchmark warning to the Meshes log and to the auxiliary crowd log, so
        /// that a Release build also records it.
        /// </summary>
        private static void LogCrowdWarning(string message)
        {
            Debug.MeshesWarning(message);
            Debug.WriteAuxiliaryLog(CrowdBenchmarkLogFileName, message);
        }

        /// <summary>
        /// Reads the child indices from <paramref name="root"/> down to <paramref name="target"/>.
        /// A clone has the same child order, so the path finds the matching node in it.
        /// </summary>
        private static bool TryResolveChildPath(SceneNode root, SceneNode target, out int[] path)
        {
            List<int> reversed = [];
            SceneNode? node = target;
            while (node is not null && !ReferenceEquals(node, root))
            {
                SceneNode? parent = node.Parent;
                if (parent is null)
                {
                    path = [];
                    return false;
                }
                int childIndex = parent.Transform.Children.IndexOf(node.Transform);
                if (childIndex < 0)
                {
                    path = [];
                    return false;
                }
                reversed.Add(childIndex);
                node = parent;
            }

            reversed.Reverse();
            path = [.. reversed];
            return node is not null;
        }

        /// <summary>Follows the child indices of <see cref="TryResolveChildPath"/> from a root.</summary>
        private static bool TryFollowChildPath(SceneNode root, int[] path, out SceneNode node)
        {
            node = root;
            foreach (int childIndex in path)
            {
                if ((uint)childIndex >= (uint)node.Transform.Children.Count ||
                    node.Transform.Children[childIndex].SceneNode is not SceneNode child)
                    return false;
                node = child;
            }
            return true;
        }
    }
}
