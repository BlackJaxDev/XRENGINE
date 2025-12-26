#if WINDOWS
using Extensions;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using XREngine.Rendering.UI;

namespace XREngine.Networking
{
    public class VirtualizedConsoleUIComponent : UITextComponent
    {
        public class ConsoleWriter(VirtualizedConsoleUIComponent comp) : TextWriter
        {
            public VirtualizedConsoleUIComponent Component { get; } = comp;

            public override Encoding Encoding { get; } = Encoding.UTF8;
            public override void Write(char value)
            {
                bool newLine = value == '\n';
                if (newLine)
                    Component.AddItem("");
                else
                    Component.AddToLastItem(value.ToString());
            }
        }
        public class TraceWriter(VirtualizedConsoleUIComponent comp) : System.Diagnostics.TraceListener
        {
            public VirtualizedConsoleUIComponent Component { get; } = comp;

            public override void Write(string? message)
            {
                var lines = message?.Split('\n');
                if (lines is null || lines.Length == 0)
                    return;

                Component.AddToLastItem(lines[0]);
                for (int i = 1; i < lines.Length; i++)
                    Component.AddItem(lines[i]);
            }

            public override void WriteLine(string? message)
                => Write($"{message}\n");
        }

        private readonly List<string> _items = [];
        public List<string> Items => _items;

        private readonly List<int> _visibleItems = [];
        public List<int> VisibleItems => _visibleItems;

        private float _topOffset = 0.0f;
        public float TopOffset
        {
            get => _topOffset;
            set => SetField(ref _topOffset, value);
        }

        private int _visibleItemMinIndex;
        public int VisibleItemMinIndex
        {
            get => _visibleItemMinIndex;
            private set => SetField(ref _visibleItemMinIndex, value);
        }

        private bool _autoScroll = true;
        public bool AutoScroll
        {
            get => _autoScroll;
            set => SetField(ref _autoScroll, value);
        }

        private float _fontHeight = 20.0f;
        public float FontHeight => _fontHeight;

        public int MaxVisibleItemCount
            => (int)MathF.Ceiling(BoundableTransform.ActualHeight / FontHeight);

        public VirtualizedConsoleUIComponent() { }

        //protected override void OnTransformChanged()
        //{
        //    base.OnTransformChanged();  
        //    BoundableTransform.NormalizedPivot = new Vector2(0, 0);
        //}

        /// <summary>
        /// Returns whether the item at the given index is visible.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public bool IsVisible(int index)
        {
            //Use indices instead of contains for performance, and because visible items may not be updated yet.
            int max = Math.Min(_items.Count - _visibleItemMinIndex, MaxVisibleItemCount);
            return index >= _visibleItemMinIndex && index < _visibleItemMinIndex + max;
        }

        public void AddItem(string? message)
        {
            bool anyItemsOffscreenLower = AutoScroll && AnyItemsOffscreenLower();
            _items.Add(message ?? "");
            if (IsVisible(_items.Count - 1))
                UpdateVisibleText();
            if (AutoScroll && !anyItemsOffscreenLower && AnyItemsOffscreenLower())
                Scroll(FontHeight);
        }

        public void AddToLastItem(string? message)
        {
            if (_items.Count > 0)
                _items[^1] += message ?? "";
            else
                AddItem(message);
            if (IsVisible(_items.Count - 1))
                UpdateVisibleText();
        }

        public void Scroll(float amount)
        {
            TopOffset += amount;
            if (TopOffset < 0)
                TopOffset = 0;
            else if (TopOffset > BoundableTransform.ActualHeight)
                TopOffset = BoundableTransform.ActualHeight;
            float fontHeight = FontHeight;
            int hiddenItemCount = ((int)MathF.Floor(TopOffset / fontHeight)).ClampMax(_items.Count);
            float hiddenOffset = hiddenItemCount * fontHeight;
            float topDelta = TopOffset - hiddenOffset;
            float yOffset = -topDelta;
            BoundableTransform.LocalPivotTranslation = new Vector2(0, yOffset);
            VisibleItemMinIndex = hiddenItemCount;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Font):
                case nameof(FontSize):
                    _fontHeight = Font?.MeasureString("A", FontSize ?? 0.0f).Y ?? 0;
                    UpdateVisibleText();
                    break;
                case nameof(VisibleItemMinIndex):
                    UpdateVisibleText();
                    break;
            }
        }

        private bool _allowUpdate = true;
        protected override void UpdateText(bool forceRemake, bool invalidateLayout = true)
        {
            if (!_allowUpdate)
                return;
            UpdateVisibleText();
            base.UpdateText(forceRemake, invalidateLayout);
        }

        private (int min, int count) _visibleItemRange = (0, 0);
        public (int min, int count) VisibleItemRange => _visibleItemRange;

        private void UpdateVisibleText()
        {
            var lastRange = _visibleItemRange;
            int min = _visibleItemMinIndex;
            int count = Math.Min(_items.Count - _visibleItemMinIndex, MaxVisibleItemCount);
            _visibleItemRange = (min, count);
            if (lastRange == _visibleItemRange)
                return;

            string text = "";
            _visibleItems.Clear();
            for (int i = 0; i < count; i++)
            {
                int index = _visibleItemMinIndex + i;
                _visibleItems.Add(index);
                text += _items[index];
                if (i < count - 1)
                    text += "\n";
            }
            _allowUpdate = false;
            Text = text;
            _allowUpdate = true;
        }

        /// <summary>
        /// Returns whether any items are offscreen above the visible area.
        /// "Upper" refers to the start of the list, because the list is displayed from top to bottom.
        /// </summary>
        /// <returns></returns>
        public bool AnyItemsOffscreenUpper()
            => _visibleItemMinIndex > 0;

        /// <summary>
        /// Returns whether any items are offscreen below the visible area.
        /// "Lower" refers to the end of the list, because the list is displayed from top to bottom.
        /// </summary>
        /// <returns></returns>
        public bool AnyItemsOffscreenLower()
            => (_items.Count - _visibleItemMinIndex) > MaxVisibleItemCount;

        /// <summary>
        /// Helper method to tell the console to output to this component.
        /// Note that this will overwrite the current console output.
        /// </summary>
        public void SetAsConsoleOut()
            => Console.SetOut(new ConsoleWriter(this));
        public void RemoveAsConsoleOut()
        {
            var writer = Console.Out as ConsoleWriter;
            if (writer?.Component == this)
                Console.SetOut(TextWriter.Null);
        }
        /// <summary>
        /// Helper method to tell trace to output to this component.
        /// Adds as a listener to the trace listeners.
        /// </summary>
        public void AddAsTraceOut()
            => Trace.Listeners.Add(new TraceWriter(this));
        public void RemoveAsTraceOut()
        {
            var match = Trace.Listeners.OfType<TraceWriter>().FirstOrDefault(x => x.Component == this);
            if (match is not null)
                Trace.Listeners.Remove(match);
        }
    }
}
#endif
