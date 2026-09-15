namespace XREngine.ControlPlane.Service;

/// <summary>Launch side effects are isolated from registry policy and HTTP handlers.</summary>
internal interface IWorkerProcessLauncher
{
    void Start(WorkerExecution execution);
}
