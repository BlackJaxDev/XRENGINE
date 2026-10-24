using XREngine.Data.Core;

namespace System.Collections.Generic;

public partial class EventList<T>
{
    protected sealed override void OnLifetimeMutationBeginning(EObjectLifetimeMutation kind)
        => _mutationAuthority?.EnterLifetimeMutation(this);

    protected sealed override void OnLifetimeMutationEnded(EObjectLifetimeMutation kind)
        => _mutationAuthority?.ExitLifetimeMutation(this);
}
