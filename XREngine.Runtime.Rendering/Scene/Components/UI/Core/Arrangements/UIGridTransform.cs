using XREngine.Extensions;
using System.ComponentModel;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    [XRTransformEditor("XREngine.Editor.TransformEditors.UIGridTransformEditor")]
    public partial class UIGridTransform : UIBoundableTransform
    {
        private EventList<UISizingDefinition> _rows;
        private EventList<UISizingDefinition> _columns;
        private List<int>[,]? _indices = null;
        private bool _invertY;
        private readonly HashSet<UISizingDefinition> _observedDefinitions = [];
        private readonly HashSet<UISizingValue> _observedValues = [];
        private readonly HashSet<UIGridChildPlacementInfo> _observedPlacements = [];
        private bool _terminal;

        protected override void OnDestroying()
        {
            _terminal = true;
            _rows.CollectionChanged -= CollectionChanged;
            _columns.CollectionChanged -= CollectionChanged;
            foreach (UISizingDefinition definition in _observedDefinitions)
                definition.PropertyChanged -= SizingDefinitionChanged;
            foreach (UISizingValue value in _observedValues)
                value.PropertyChanged -= SizingValueChanged;
            foreach (UIGridChildPlacementInfo placement in _observedPlacements)
            {
                placement.PropertyChanging -= Info_PropertyChanging;
                placement.PropertyChanged -= Info_PropertyChanged;
            }
            _observedDefinitions.Clear();
            _observedValues.Clear();
            _observedPlacements.Clear();
            base.OnDestroying();
        }

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
            set => SetField(ref _indices, value); 
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
                if (_terminal) return;
                if (SetField(ref _rows, value,
                    old => old.CollectionChanged -= CollectionChanged,
                    current => { if (!_terminal && ReferenceEquals(_rows, current)) current.CollectionChanged += CollectionChanged; }))
                    OnChildrenChanged();
            }
        }
        public EventList<UISizingDefinition> Columns
        {
            get => _columns;
            set
            {
                if (_terminal) return;
                if (SetField(ref _columns, value,
                    old => old.CollectionChanged -= CollectionChanged,
                    current => { if (!_terminal && ReferenceEquals(_columns, current)) current.CollectionChanged += CollectionChanged; }))
                    OnChildrenChanged();
            }
        }

        private void CollectionChanged(object sender, TCollectionChangedEventArgs<UISizingDefinition> e)
        {
            if (!_terminal) OnChildrenChanged();
        }

        private void OnChildrenChanged()
        {
            if (_terminal) return;
            RefreshSizingSubscriptions();
            _indices = null;
            InvalidateLayout();
        }

        private void RefreshSizingSubscriptions()
        {
            foreach (UISizingDefinition definition in _observedDefinitions)
                definition.PropertyChanged -= SizingDefinitionChanged;
            foreach (UISizingValue value in _observedValues)
                value.PropertyChanged -= SizingValueChanged;
            _observedDefinitions.Clear();
            _observedValues.Clear();
            ObserveDefinitions(Rows);
            ObserveDefinitions(Columns);
        }

        private void ObserveDefinitions(EventList<UISizingDefinition> definitions)
        {
            foreach (UISizingDefinition definition in definitions)
            {
                if (definition is null || !_observedDefinitions.Add(definition))
                    continue;
                definition.PropertyChanged += SizingDefinitionChanged;
                ObserveValue(definition.Value);
                ObserveValue(definition.Min);
                ObserveValue(definition.Max);
            }
        }

        private void ObserveValue(UISizingValue? value)
        {
            if (value is not null && _observedValues.Add(value))
                value.PropertyChanged += SizingValueChanged;
        }

        private void SizingDefinitionChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (!_terminal) OnChildrenChanged();
        }

        private void SizingValueChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (!_terminal) OnChildrenChanged();
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
                    {
                        if (info.Row >= 0 && info.Column >= 0 && info.Row < Rows.Count && info.Column < Columns.Count)
                            _indices[info.Row, info.Column].Add(i);
                    }
            //}
        }

        protected override void ArrangeChildren(BoundingRectangleF childRegion)
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

            float remainingRowHeight = childRegion.Height;
            float remainingColWidth = childRegion.Width;

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

            for (int r = 0; r < Rows.Count; r++)
                if (Rows[r].Value.Mode == ESizingMode.Proportional)
                    Rows[r].CalculatedValue = rowPropDenom <= 0 ? 0 : Rows[r].Value.Value / rowPropDenom * remainingRowHeight;
            for (int c = 0; c < Columns.Count; c++)
                if (Columns[c].Value.Mode == ESizingMode.Proportional)
                    Columns[c].CalculatedValue = colPropDenom <= 0 ? 0 : Columns[c].Value.Value / colPropDenom * remainingColWidth;

            //Post-pass: actually size each row and col, and resize each component
            float heightOffset = 0.0f;
            for (int r = 0; r < Rows.Count; ++r)
            {
                var row = Rows[r];

                //Calculate the proportional value now that the fixed and auto values have been processed
                float height = row.CalculatedValue;

                float widthOffset = 0.0f;
                for (int c = 0; c < Columns.Count; ++c)
                {
                    var col = Columns[c];

                    //Calculate the proportional value now that the fixed and auto values have been processed
                    float width = col.CalculatedValue;

                    List<int> indices = Indices[r, c];
                    if (indices is null)
                        Indices[r, c] = indices = [];
                    foreach (var index in indices)
                    {
                        TransformBase? childTfm = null;
                        childTfm = Children[index];
                        if (childTfm is not UIBoundableTransform uiBoundable)
                            continue;

                        if (uiBoundable.PlacementInfo is not UIGridChildPlacementInfo placement)
                            continue;
                        float allocatedWidth = width;
                        for (int span = 1; span < placement.ColumnSpan && c + span < Columns.Count; span++)
                            allocatedWidth += Columns[c + span].CalculatedValue;
                        float allocatedHeight = height;
                        for (int span = 1; span < placement.RowSpan && r + span < Rows.Count; span++)
                            allocatedHeight += Rows[r + span].CalculatedValue;

                        float x = childRegion.X;
                        float y = childRegion.Y;
                        y += InvertY ? heightOffset : childRegion.Height - heightOffset - allocatedHeight;
                        x += widthOffset;
                        placement.Offset = new System.Numerics.Vector2(x - childRegion.X, y - childRegion.Y);
                        UILayoutSystem.FitLayout(uiBoundable, new BoundingRectangleF(x, y, allocatedWidth, allocatedHeight));
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
        public override void VerifyPlacementInfo(TransformBase childTransform, ref ITransformChildPlacementInfo? placementInfo)
        {
            if (childTransform is UITransform uiChildTransform && placementInfo is not UIGridChildPlacementInfo)
                placementInfo = new UIGridChildPlacementInfo(uiChildTransform);
        }
        protected override void OnChildAdded(TransformBase item)
        {
            if (item is UITransform uic)
            {
                //Add placement info
                if (uic.PlacementInfo is not UIGridChildPlacementInfo info)
                    uic.PlacementInfo = info = new UIGridChildPlacementInfo(uic);

                //Register events
                if (!_terminal && _observedPlacements.Add(info))
                {
                    info.PropertyChanging += Info_PropertyChanging;
                    info.PropertyChanged += Info_PropertyChanged;
                }

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
                _observedPlacements.Remove(info);

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
            if (_terminal) return;
            if (sender is not UIGridChildPlacementInfo info)
                return;

            switch (e.PropertyName)
            {
                case nameof(UIGridChildPlacementInfo.RowSpan):
                case nameof(UIGridChildPlacementInfo.Row):
                    RemoveControlFromRows(info);
                    OnChildrenChanged();
                    break;

                case nameof(UIGridChildPlacementInfo.ColumnSpan):
                case nameof(UIGridChildPlacementInfo.Column):
                    RemoveControlFromColumns(info);
                    OnChildrenChanged();
                    break;
            }
        }

        private void Info_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (_terminal) return;
            if (sender is not UIGridChildPlacementInfo info)
                return;

            switch (e.PropertyName)
            {
                case nameof(UIGridChildPlacementInfo.RowSpan):
                case nameof(UIGridChildPlacementInfo.Row):
                    AddControlToRows(info);
                    OnChildrenChanged();
                    break;

                case nameof(UIGridChildPlacementInfo.ColumnSpan):
                case nameof(UIGridChildPlacementInfo.Column):
                    AddControlToColumns(info);
                    OnChildrenChanged();
                    break;
            }
        }

        private void AddControlToRows(UIGridChildPlacementInfo info)
        {
            int startIndex = info.Row;
            for (int rowIndex = startIndex; rowIndex < Rows.Count && rowIndex - startIndex < info.RowSpan; ++rowIndex)
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
            for (int colIndex = startIndex; colIndex < Columns.Count && colIndex - startIndex < info.ColumnSpan; ++colIndex)
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
            for (int rowIndex = startIndex; rowIndex < Rows.Count && rowIndex - startIndex < info.RowSpan; ++rowIndex)
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
            for (int colIndex = startIndex; colIndex < Columns.Count && colIndex - startIndex < info.ColumnSpan; ++colIndex)
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
