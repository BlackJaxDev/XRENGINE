namespace XREngine.Rendering;

internal sealed class RenderObjectPublicationBatch
{
    private readonly HashSet<GenericRenderObject> _objectSet = new(ReferenceEqualityComparer.Instance);
    private readonly List<PublicationTransaction> _transactions = [];

    public List<GenericRenderObject> Objects { get; } = [];
    public bool IsAborted { get; set; }

    public void Enlist(GenericRenderObject value)
    {
        if (_objectSet.Add(value))
            Objects.Add(value);
    }

    public void EnlistTransaction(
        Action beforeCachePublication,
        Action rollbackOnFailure,
        Action afterPublication,
        Action? abortedBeforeApply = null)
        => _transactions.Add(new PublicationTransaction(
            beforeCachePublication,
            rollbackOnFailure,
            afterPublication,
            abortedBeforeApply));

    public void ApplyTransactions()
    {
        // Callbacks can synchronously open a nested publication scope (for example,
        // from a buffer-collection observer) and enlist another transaction in this
        // same root batch. Walk by index so those transactions join the current
        // fixed-point commit instead of invalidating a List enumerator.
        for (int index = 0; index < _transactions.Count; index++)
        {
            PublicationTransaction transaction = _transactions[index];
            if (transaction.Applied)
                continue;

            // Mark first so a callback that mutates state and then throws is still compensated.
            transaction.Applied = true;
            transaction.BeforeCachePublication();
        }
    }

    public void RollbackTransactions()
    {
        for (int index = _transactions.Count - 1; index >= 0; index--)
        {
            PublicationTransaction transaction = _transactions[index];
            if (transaction.Completed || transaction.RolledBack)
                continue;

            transaction.RolledBack = true;
            try
            {
                if (transaction.Applied)
                    transaction.RollbackOnFailure();
                else
                    transaction.AbortedBeforeApply?.Invoke();
            }
            catch (Exception ex)
            {
                RuntimeRenderingHostServices.Diagnostics.LogException(
                    ex,
                    "Render-object publication rollback failed.");
            }
        }
    }

    public void CompleteTransactions()
    {
        foreach (PublicationTransaction transaction in _transactions)
        {
            if (!transaction.Applied || transaction.RolledBack || transaction.Completed)
                continue;

            // Publication is already durable. Cleanup cannot turn it back into a failure.
            transaction.Completed = true;
            try
            {
                transaction.AfterPublication();
            }
            catch (Exception ex)
            {
                RuntimeRenderingHostServices.Diagnostics.LogException(
                    ex,
                    "Render-object post-publication cleanup failed.");
            }
        }
    }

    private sealed class PublicationTransaction(
        Action beforeCachePublication,
        Action rollbackOnFailure,
        Action afterPublication,
        Action? abortedBeforeApply)
    {
        public Action BeforeCachePublication { get; } = beforeCachePublication;
        public Action RollbackOnFailure { get; } = rollbackOnFailure;
        public Action AfterPublication { get; } = afterPublication;
        public Action? AbortedBeforeApply { get; } = abortedBeforeApply;
        public bool Applied { get; set; }
        public bool RolledBack { get; set; }
        public bool Completed { get; set; }
    }
}
