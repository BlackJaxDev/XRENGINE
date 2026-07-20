namespace XREngine;

internal sealed class EngineRuntimeMaintenanceServices : IRuntimeMaintenanceServices
{
    public EngineMaintenanceGcResult RequestGarbageCollection(EngineMaintenanceGcRequest request)
        => Engine.RequestMaintenanceGarbageCollection(request);
}
