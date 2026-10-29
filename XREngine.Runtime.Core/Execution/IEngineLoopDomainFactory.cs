namespace XREngine.Execution;

/// <summary>Creates the dedicated update, visibility, and fixed-update loop owner.</summary>
/// <remarks>
/// This optional capability belongs to the registered worker-domain factory.
/// The returned domain is idle. Its caller owns it before Start and supplies
/// the stop signals for the three callbacks, including after a failed Start.
/// </remarks>
public interface IEngineLoopDomainFactory
{
    IEngineLoopDomain CreateLoopDomain(Action update, Action collectVisible, Action fixedUpdate);
}
