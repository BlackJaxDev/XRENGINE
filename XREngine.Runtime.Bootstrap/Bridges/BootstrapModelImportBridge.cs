using System;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapModelImportBridge
{
    public static IBootstrapModelImportBridge? Current { get; set; }
}
