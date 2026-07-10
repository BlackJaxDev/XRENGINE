namespace XREngine;

public readonly record struct RvcResourceAliasContract(
    string FirstResourceName,
    string SecondResourceName,
    ERvcResourceAliasRule Rules)
{
    public bool CanAlias(bool sameView, bool hasHistory, bool isDebugOrMirror, bool isExternalSwapchain)
    {
        if ((Rules & ERvcResourceAliasRule.SameViewOnly) != 0 && !sameView)
            return false;
        if ((Rules & ERvcResourceAliasRule.NoHistoryAlias) != 0 && hasHistory)
            return false;
        if ((Rules & ERvcResourceAliasRule.DebugResourcesNeverAlias) != 0 && isDebugOrMirror)
            return false;
        if ((Rules & ERvcResourceAliasRule.ExternalSwapchainNeverAlias) != 0 && isExternalSwapchain)
            return false;

        return true;
    }
}
