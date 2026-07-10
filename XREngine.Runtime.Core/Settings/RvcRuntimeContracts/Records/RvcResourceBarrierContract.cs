namespace XREngine;

public readonly record struct RvcResourceBarrierContract(
    ERvcResourceBarrierBackend Backend,
    ERvcFrameGraphUsage BeforeUsage,
    ERvcFrameGraphUsage AfterUsage,
    string ResourceName,
    string DiagnosticName);
