namespace XREngine;

public static partial class RenderFrameViewBatchPlanner
{
    private ref struct RenderFrameViewBatchPlanBuilder
    {
        private int _count;
        private RenderFrameViewBatch _batch0;
        private RenderFrameViewBatch _batch1;
        private RenderFrameViewBatch _batch2;
        private RenderFrameViewBatch _batch3;
        private RenderFrameViewBatch _batch4;
        private RenderFrameViewBatch _batch5;
        private RenderFrameViewBatch _batch6;
        private RenderFrameViewBatch _batch7;

        public void Append(in RenderFrameViewBatch batch)
        {
            switch (_count)
            {
                case 0: _batch0 = batch; break;
                case 1: _batch1 = batch; break;
                case 2: _batch2 = batch; break;
                case 3: _batch3 = batch; break;
                case 4: _batch4 = batch; break;
                case 5: _batch5 = batch; break;
                case 6: _batch6 = batch; break;
                case 7: _batch7 = batch; break;
                default: throw new InvalidOperationException("Frame view batch plan exceeded its maximum batch count.");
            }

            _count++;
        }

        public readonly RenderFrameViewBatchPlan ToPlan()
        {
            if (_count == 0)
                throw new InvalidOperationException("Frame view batch plan must contain at least one batch.");

            return new(
                _count,
                _batch0,
                _batch1,
                _batch2,
                _batch3,
                _batch4,
                _batch5,
                _batch6,
                _batch7);
        }
    }
}
