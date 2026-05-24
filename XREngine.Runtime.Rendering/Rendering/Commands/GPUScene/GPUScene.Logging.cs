// =====================================================================================
// GPUScene.Logging.cs - Budget-limited debug logging helpers.
// Part of the GPUScene partial class. See GPUScene.cs for the canonical class summary.
// =====================================================================================

using XREngine.Extensions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands
{
    public partial class GPUScene
    {

        // -------------------------------------------------------------------------
        // Debug Logging: Budget-limited logging to prevent log spam during heavy operations.
        // -------------------------------------------------------------------------

        /// <summary>Remaining log entries for material assignment debug output.</summary>
        private int _materialDebugLogBudget = 16;

        /// <summary>Remaining log entries for command build debug output.</summary>
        private int _commandBuildLogBudget = 12;

        /// <summary>Remaining log entries for command roundtrip verification output.</summary>
        private int _commandRoundtripLogBudget = 8;

        /// <summary>Remaining log entries for command roundtrip mismatch warnings.</summary>
        private int _commandRoundtripMismatchLogBudget = 4;

        /// <summary>Remaining log entries for command update warnings/errors.</summary>
        private int _commandUpdateErrorLogBudget = 24;

        /// <summary>Remaining log entries for runtime meshlet payload repair.</summary>
        private int _runtimeMeshletRepairLogBudget = 16;

        /// <summary>Checks if GPU scene logging is enabled in settings.</summary>
        private static bool IsGpuSceneLoggingEnabled()
            => RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging;

        /// <summary>Logs a message if GPU scene logging is enabled.</summary>
        private static void SceneLog(string message, params object[] args)
        {
            if (!IsGpuSceneLoggingEnabled())
                return;

            Debug.Meshes(message, args);
        }

        /// <summary>Logs a formatted message if GPU scene logging is enabled.</summary>
        private static void SceneLog(FormattableString message)
        {
            if (!IsGpuSceneLoggingEnabled())
                return;

            Debug.Meshes(message.ToString());
        }

    }
}
