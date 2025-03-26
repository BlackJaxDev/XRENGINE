using Extensions;
using System.ComponentModel;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    public partial class UIGridTransform : UIBoundableTransform
    {
        private EventList<UISizingDefinition> _rows;
        private EventList<UISizingDefinition> _columns;
        private List<int>[,]? _indices = null;
        private bool _invertY;

        public UIGridTransform()
        {
            _rows = [];
            _rows.CollectionChanged += CollectionChanged;

            _columns = [];
            _columns.CollectionChanged += CollectionChanged;
        }

        //Jagged array indexed by (row,col) of int lists.
        //Each int list contains indices of child UI components that reside in the cell specified by (row,col).
        [Browsable(false)]
        public List<int>[,] Indices 
        {
            get
            {
                if (_indices is null)
                    RegenerateIndices();
                return _indices!;
            }
            set => _indices = value; 
        }

        public bool InvertY
        {
            get => _invertY;
            set
            {
                if (SetField(ref _invertY, value))
                    InvalidateLayout();
            }
        }

        public EventList<UISizingDefinition> Rows
        {
            get => _rows;
            set
            {
                if (SetField(ref _rows, value,
                    x => _rows.CollectionChanged -= CollectionChanged,
                    x => _rows.CollectionChanged += CollectionChanged))
                    OnChildrenChanged();
            }
        }
        public EventList<UISizingDefinition> Columns
        {
            get => _columns;
            set
            {
                if (SetField(ref _columns, value,
                    x => _columns.CollectionChanged -= CollectionChanged,
                    x => _columns.CollectionChanged += CollectionChanged))
                    OnChildrenChanged();
            }
        }

        private void CollectionChanged(object sender, TCollectionChangedEventArgs<UISizingDefinition> e)
            => OnChildrenChanged();

        private void OnChildrenChanged()
        {
            _indices = null;
            InvalidateLayout();
        }

        public List<UITransform> GetComponentsInRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= Rows.Count)
                return [];

            List<UITransform> list = [];
            for (int i = 0; i < Columns.Count; ++i)
                CollectQuadrant(list, Indices[rowIndex, i]);
            
            return list;
        }

        public List<UITransform> GetComponentsInColumn(int colIndex)
        {
            if (colIndex < 0 || colIndex >= Columns.Count)
                return [];

            List<UITransform> list = [];
            for (int i = 0; i < Rows.Count; ++i)
                CollectQuadrant(list, Indices[i, colIndex]);
            
            return list;
        }

        private void CollectQuadrant(List<UITransform> list, List<int> quadrant)
        {
            //lock (Children)
            //{
                foreach (var index in quadrant)
                    if (Children.TryGet(index, out var value) && value is UITransform uiTfm)
                        list.Add(uiTfm);
            //}
        }

        public static float GetRowAutoHeight(IEnumerable<UITransform> comps)
        {
            float height = 0.0f;
            foreach (var comp in comps)
                if (comp is UIBoundableTransform bc)
                    height = Math.Max(bc.GetMaxChildHeight(), height);
            return height;
        }

        public static float GetColAutoWidth(IEnumerable<UITransform> comps)
        {
            float width = 0.0f;
            foreach (var comp in comps)
                if (comp is UIBoundableTransform bc)
                    width = Math.Max(bc.GetMaxChildWidth(), width);
            return width;
        }

        private void RegenerateIndices()
        {
            _indices = new List<int>[Rows.Count, Columns.Count];
            for (int r = 0; r < Rows.Count; ++r)
                for (int c = 0; c < Columns.Count; ++c)
                    _indices[r, c] = [];

            //lock (Children)
            //{
                for (int i = 0; i < Children.Count; ++i)
                    if (Children[i] is UITransform uic && uic.PlacementInfo is UIGridChildPlacementInfo info)
                        _indices[info.Row, info.Column].Add(i);
            //}
        }

        protected override void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            //Set to fixed values or initialize to zero for auto calculation
            float rowPropDenom = 0.0f;
            float colPropDenom = 0.0f;

            List<UITransform> autoComps = [];
            
            //Pre-pass: 
            //calculate initial values,
            //grab any components affected by auto sizing,
            //add proportional values for use later
            foreach (var row in Rows)
            {
                switch (row.Value?.Mode ?? ESizingMode.Fixed)
                {
                    case ESizingMode.Auto:
                        row.CalculatedValue = 0.0f;
                        foreach (UITransform comp in row.AttachedControls)
                            if (!autoComps.Contains(comp))
                                autoComps.Add(comp);
                        break;
                    case ESizingMode.Fixed:
                        row.CalculatedValue = row.Value?.Value ?? 0.0f;
                        break;
                    case ESizingMode.Proportional:
                        row.CalculatedValue = 0.0f;
                        rowPropDenom += row.Value?.Value ?? 0.0f;
                        break;
                }
            }
            foreach (var col in Columns)
            {
                switch (col.Value?.Mode ?? ESizingMode.Fixed)
                {
                    case ESizingMode.Auto:
                        col.CalculatedValue = 0.0f;
                        foreach (UITransform comp in col.AttachedControls)
                            if (!autoComps.Contains(comp))
                                autoComps.Add(comp);
                        break;
                    case ESizingMode.Fixed:
                        col.CalculatedValue = col.Value?.Value ?? 0.0f;
                        break;
                    case ESizingMode.Proportional:
                        col.CalculatedValue = 0.0f;
                        colPropDenom += col.Value?.Value ?? 0.0f;
                        break;
                }
            }
            //Auto sizing pass, only calculate auto size for components that are affected
            foreach (UITransform tfm in autoComps)
            {
                if (tfm?.PlacementInfo is not UIGridChildPlacementInfo info)
                    continue;

                bool hasCalcAutoHeight = false;
                bool hasCalcAutoWidth = false;
                float autoHeight = 0.0f;
                float autoWidth = 0.0f;

                //Calc height through one or more rows
                foreach (int rowIndex in info.AssociatedRowIndices)
                {
                    var row = Rows[rowIndex];
                    switch (row.Value?.Mode ?? ESizingMode.Fixed)
                    {
                        case ESizingMode.Auto:
                            if (!hasCalcAutoHeight)
                            {
                                hasCalcAutoHeight = true;
                                autoHeight = tfm?.GetMaxChildHeight() ?? 0.0f;
                            }
                            row.CalculatedValue = Math.Max(row.CalculatedValue, autoHeight);
                            break;
                    }
                }

                //Calc width through one or more cols
                foreach (int colIndex in info.AssociatedColumnIndices)
                {
                    var col = Columns[colIndex];
                    switch (col.Value?.Mode ?? ESizingMode.Fixed)
                    {
                        case ESizingMode.Auto:
                            if (!hasCalcAutoWidth)
                            {
                                hasCalcAutoWidth = true;
                                autoWidth = tfm?.GetMaxChildWidth() ?? 0.0f;
                            }
                            col.CalculatedValue = Math.Max(col.CalculatedValue, autoWidth);
                            break;
                    }
                }
            }

            float remainingRowHeight = parentRegion.Height;
            float remainingColWidth = parentRegion.Width;

            foreach (var row in Rows)
            {
                if (row.Value.Mode != ESizingMode.Proportional)
                    remainingRowHeight -= row.CalculatedValue;
            }

            foreach (var col in Columns)
            {
                if (col.Value.Mode != ESizingMode.Proportional)
                    remainingColWidth -= col.CalculatedValue;
            }

            //Clamp remaining to zero
            if (remainingRowHeight < 0.0f)
                remainingRowHeight = 0.0f;
            if (remainingColWidth < 0.0f)
                remainingColWidth = 0.0f;

            //Post-pass: actually size each row and col, and resize each component
            float heightOffset = 0.0f;
            for (int r = 0; r < Rows.Count; ++r)
            {
                var row = Rows[r];

                //Calculate the proportional value now that the fixed and auto values have been processed
                if (row.Value.Mode == ESizingMode.Proportional)
                    row.CalculatedValue = rowPropDenom <= 0.0f ? 0.0f : row.Value.Value / rowPropDenom * remainingRowHeight;

                float height = row.CalculatedValue;

                float widthOffset = 0.0f;
                for (int c = 0; c < Columns.Count; ++c)
                {
                    var col = Columns[c];

                    //Calculate the proportional value now that the fixed and auto values have been processed
                    if (col.Value.Mode == ESizingMode.Proportional)
                        col.CalculatedValue = colPropDenom <= 0.0f ? 0.0f : col.Value.Value / colPropDenom * remainingColWidth;

                    float width = col.CalculatedValue;

                    List<int> indices = Indices[r, c];
                    if (indices is null)
                        Indices[r, c] = indices = [];
                    foreach (var index in indices)
                    {
                        TransformBase? childTfm = null;
                        //lock (Children)
                        //{
                            childTfm = Children[index];
                        //}
                        if (childTfm is not UITransform uiComp)
                            continue;

                        float x = parentRegion.X;
                        float y = parentRegion.Y;
                        y += parentRegion.Height - heightOffset - height;
                        x += widthOffset;

                        if (uiComp is UIBoundableTransform uiBoundable)
                        {
                            AdjustByMargin(
                                uiBoundable,
                                ref widthOffset, ref heightOffset,
                                ref x, ref y,
                                ref width, ref height);
                        }

                        uiComp.FitLayout(new BoundingRectangleF(x, y, width, height));
                    }

                    widthOffset += width;
                }

                heightOffset += height;
            }
        }

        public static void AdjustByMargin(
            UIBoundableTransform uibComp,
            ref float widthOffset,
            ref float heightOffset,
            ref float x,
            ref float y,
            ref float width,
            ref float height)
        {
            var margins = uibComp.Margins;
            float left = margins.X;
            float bottom = margins.Y;
            float right = margins.Z;
            float top = margins.W;

            float temp = bottom + top;
            width -= left + right;
            height -= temp;
            heightOffset += temp;

            y += bottom;
            x += left;

            temp = left + right;
            height -= bottom + top;
            width -= temp;
            widthOffset += temp;

            x += left;
            y += bottom;
        }
        public override void VerifyPlacementInfo(UITransform childTransform, ref UIChildPlacementInfo? placementInfo)
        {
            if (placementInfo is not UIGridChildPlacementInfo)
                placementInfo = new UIGridChildPlacementInfo(childTransform);
        }
        protected override void OnChildAdded(TransformBase item)
        {
            if (item is UITransform uic)
            {
                //Add placement info
                if (uic.PlacementInfo is not UIGridChildPlacementInfo info)
                    uic.PlacementInfo = info = new UIGridChildPlacementInfo(uic);

                //Register events
                info.PropertyChanging += Info_PropertyChanging;
                info.PropertyChanged += Info_PropertyChanged;

                //Add row/col associations
                AddControlToRows(info);
                AddControlToColumns(info);
            }

            base.OnChildAdded(item);

            //Regenerate row/col indices and invalidate layout for next render
            OnChildrenChanged();
        }

        protected override void OnChildRemoved(TransformBase item)
        {
            if (item is UITransform uic && uic.PlacementInfo is UIGridChildPlacementInfo info)
            {
                //Unregister events
                info.PropertyChanging -= Info_PropertyChanging;
                info.PropertyChanged -= Info_PropertyChanged;

                //Remove row/col associations
                RemoveControlFromRows(info);
                RemoveControlFromColumns(info);
            }

            base.OnChildRemoved(item);

            //Regenerate row/col indices and invalidate layout for next render
            OnChildrenChanged();
        }

        private void Info_PropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            if (sender is not UIGridChildPlacementInfo info)
                return;

            switch (e.PropertyName)
            {
                case nameof(UIGridChildPlacementInfo.RowSpan):
                case nameof(UIGridChildPlacementInfo.Row):
                    RemoveControlFromRows(info);
                    break;

                case nameof(UIGridChildPlacementInfo.ColumnSpan):
                case nameof(UIGridChildPlacementInfo.Column):
                    RemoveControlFromColumns(info);
                    break;
            }

            //TODO: don't fully regenerate every time,
            //Just update the indices of this element
            OnChildrenChanged();
        }

        private void Info_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (sender is not UIGridChildPlacementInfo info)
                return;

            switch (e.PropertyName)
            {
                case nameof(UIGridChildPlacementInfo.RowSpan):
                case nameof(UIGridChildPlacementInfo.Row):
                    AddControlToRows(info);
                    break;

                case nameof(UIGridChildPlacementInfo.ColumnSpan):
                case nameof(UIGridChildPlacementInfo.Column):
                    AddControlToColumns(info);
                    break;
            }

            //TODO: don't fully regenerate every time,
            //Just update the indices of this element
            OnChildrenChanged();
        }

        private void AddControlToRows(UIGridChildPlacementInfo info)
        {
            int startIndex = info.Row;
            for (int rowIndex = startIndex; rowIndex < startIndex + info.RowSpan; ++rowIndex)
            {
                if (rowIndex < 0 || rowIndex >= Rows.Count)
                    continue;

                var list = Rows[rowIndex]?.AttachedControls;
                if (list != null && !list.Contains(info.Owner))
                {
                    list.Add(info.Owner);
                    info.AssociatedRowIndices.Add(rowIndex);
                }
            }
        }
        private void AddControlToColumns(UIGridChildPlacementInfo info)
        {
            int startIndex = info.Column;
            for (int colIndex = startIndex; colIndex < startIndex + info.ColumnSpan; ++colIndex)
            {
                if (colIndex < 0 || colIndex >= Columns.Count)
                    continue;

                var list = Columns[colIndex]?.AttachedControls;
                if (list != null && !list.Contains(info.Owner))
                {
                    list.Add(info.Owner);
                    info.AssociatedColumnIndices.Add(colIndex);
                }
            }
        }
        private void RemoveControlFromRows(UIGridChildPlacementInfo info)
        {
            int startIndex = info.Row;
            for (int rowIndex = startIndex; rowIndex < startIndex + info.RowSpan; ++rowIndex)
            {
                if (rowIndex < 0 || rowIndex >= Rows.Count)
                    continue;

                var list = Rows[rowIndex]?.AttachedControls;
                if (list != null && list.Contains(info.Owner))
                {
                    list.Remove(info.Owner);
                    info.AssociatedRowIndices.Remove(rowIndex);
                }
            }
        }
        private void RemoveControlFromColumns(UIGridChildPlacementInfo info)
        {
            int startIndex = info.Column;
            for (int colIndex = startIndex; colIndex < startIndex + info.ColumnSpan; ++colIndex)
            {
                if (colIndex < 0 || colIndex >= Columns.Count)
                    continue;

                var list = Columns[colIndex]?.AttachedControls;
                if (list != null && list.Contains(info.Owner))
                {
                    list.Remove(info.Owner);
                    info.AssociatedColumnIndices.Remove(colIndex);
                }
            }
        }
    }
}