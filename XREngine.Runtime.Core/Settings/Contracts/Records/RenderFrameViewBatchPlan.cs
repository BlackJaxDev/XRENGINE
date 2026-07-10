namespace XREngine;

public readonly record struct RenderFrameViewBatchPlan
{
    private readonly RenderFrameViewBatch _batch0;
    private readonly RenderFrameViewBatch _batch1;
    private readonly RenderFrameViewBatch _batch2;
    private readonly RenderFrameViewBatch _batch3;
    private readonly RenderFrameViewBatch _batch4;
    private readonly RenderFrameViewBatch _batch5;
    private readonly RenderFrameViewBatch _batch6;
    private readonly RenderFrameViewBatch _batch7;

    internal RenderFrameViewBatchPlan(
        int batchCount,
        RenderFrameViewBatch batch0,
        RenderFrameViewBatch batch1,
        RenderFrameViewBatch batch2,
        RenderFrameViewBatch batch3,
        RenderFrameViewBatch batch4,
        RenderFrameViewBatch batch5,
        RenderFrameViewBatch batch6,
        RenderFrameViewBatch batch7)
    {
        BatchCount = batchCount;
        _batch0 = batch0;
        _batch1 = batch1;
        _batch2 = batch2;
        _batch3 = batch3;
        _batch4 = batch4;
        _batch5 = batch5;
        _batch6 = batch6;
        _batch7 = batch7;
    }

    public int BatchCount { get; }

    public RenderFrameViewBatch GetBatch(int index)
        => index switch
        {
            0 when BatchCount > 0 => _batch0,
            1 when BatchCount > 1 => _batch1,
            2 when BatchCount > 2 => _batch2,
            3 when BatchCount > 3 => _batch3,
            4 when BatchCount > 4 => _batch4,
            5 when BatchCount > 5 => _batch5,
            6 when BatchCount > 6 => _batch6,
            7 when BatchCount > 7 => _batch7,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    public static RenderFrameViewBatchPlan Create(ReadOnlySpan<RenderFrameViewBatch> batches)
    {
        if (batches.Length < 1 || batches.Length > RenderFrameViewSet.MaxViewCount)
            throw new ArgumentOutOfRangeException(nameof(batches));

        RenderFrameViewBatch batch0 = default;
        RenderFrameViewBatch batch1 = default;
        RenderFrameViewBatch batch2 = default;
        RenderFrameViewBatch batch3 = default;
        RenderFrameViewBatch batch4 = default;
        RenderFrameViewBatch batch5 = default;
        RenderFrameViewBatch batch6 = default;
        RenderFrameViewBatch batch7 = default;

        for (int i = 0; i < batches.Length; i++)
        {
            switch (i)
            {
                case 0: batch0 = batches[i]; break;
                case 1: batch1 = batches[i]; break;
                case 2: batch2 = batches[i]; break;
                case 3: batch3 = batches[i]; break;
                case 4: batch4 = batches[i]; break;
                case 5: batch5 = batches[i]; break;
                case 6: batch6 = batches[i]; break;
                case 7: batch7 = batches[i]; break;
            }
        }

        return new(batches.Length, batch0, batch1, batch2, batch3, batch4, batch5, batch6, batch7);
    }
}
