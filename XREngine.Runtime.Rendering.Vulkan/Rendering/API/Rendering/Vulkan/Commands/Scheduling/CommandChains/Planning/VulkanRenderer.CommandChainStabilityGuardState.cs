using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal struct CommandChainStabilityGuardState
{
    public ulong ResourcePlanRevision;
    public int StableObservations;
    public int ScheduledAttemptsForRevision;
    public int ConsecutiveRecordedWithoutReuse;
    public int ConsecutiveBypasses;
}

