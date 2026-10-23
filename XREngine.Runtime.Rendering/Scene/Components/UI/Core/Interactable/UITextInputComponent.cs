using XREngine.Extensions;
using System.Numerics;
using System.Reflection;
using System.Globalization;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Input.Devices;
using XREngine.Input;
using XREngine.Timers;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Handles textual input from the user.
    /// </summary>
    [RequireComponents(typeof(UITextComponent))]
    public partial class UITextInputComponent : UIInspectorEditorComponent
    {
        public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.TextBox;
        public override bool TryConsumeFocusDirection(Vector2 direction)
        {
            if (!CanInteract || !float.IsFinite(direction.X) || !float.IsFinite(direction.Y))
                return false;
            if (MathF.Abs(direction.X) > MathF.Abs(direction.Y))
            {
                return IsComposing || TryMoveCaretByGrapheme(direction.X > 0 ? 1 : -1);
            }
            if (!SingleLineMode && direction.Y != 0)
            {
                return IsComposing || MoveCaretVertically(direction.Y > 0 ? 1 : -1);
            }
            return false;
        }
        public override string? AccessibilityValue => Text;
        private const int DefaultKeyRepeatDelayMs = 500;
        private const int DefaultKeyRepeatIntervalMs = 50;

        public UITextInputComponent() : base()
        {
            _keyRepeatTimer = new(this);
        }

        public UITextComponent TextComponent => GetSiblingComponent<UITextComponent>(true)!;

        private IUIValueBinding<string>? _binding;
        private bool _writingBinding;

        /// <summary>Typed text source. Its callback is active only while this control is active.</summary>
        public IUIValueBinding<string>? Binding
        {
            get => _binding;
            set
            {
                if (ReferenceEquals(_binding, value))
                    return;
                if (IsActiveInHierarchy && _binding is not null)
                    _binding.ValueChanged -= OnBindingChanged;
                SetField(ref _binding, value);
                if (IsActiveInHierarchy && _binding is not null)
                    _binding.ValueChanged += OnBindingChanged;
                if (_binding is not null)
                    OnBindingChanged(_binding.Value);
            }
        }

        private void OnBindingChanged(string value)
        {
            if (_writingBinding || IsComposing || IsFocused && CommitOnSubmit)
                return;
            Text = value ?? string.Empty;
            _editOriginalText = Text;
            IsInvalid = false;
        }

        private string _text = string.Empty;
        public string Text
        {
            get => _text;
            set => SetField(ref _text, value);
        }
        private string _preText = string.Empty;
        public string PreText
        {
            get => _preText;
            set => SetField(ref _preText, value);
        }
        private string _postText = string.Empty;
        public string PostText
        {
            get => _postText;
            set => SetField(ref _postText, value);
        }

        private bool _singleLineMode;
        private bool _commitOnSubmit;
        private string _editOriginalText = string.Empty;
        private bool _isComposing;
        private long _focusEpoch;
        /// <summary>Changes on every focus transition so delayed IME events cannot reach a later edit session.</summary>
        public long FocusEpoch => _focusEpoch;
        private string _compositionOriginalText = string.Empty;
        private int _compositionOriginalAnchor;
        private int _compositionOriginalCaret;
        private int _compositionStart;
        private int _compositionReplacedLength;
        private int _compositionLength;

        /// <summary>True while a platform text service owns a provisional edit.</summary>
        public bool IsComposing => _isComposing;
        public int CompositionStart => _isComposing ? _compositionStart : -1;
        public int CompositionLength => _isComposing ? _compositionLength : 0;

        public bool BeginComposition()
        {
            if (!CanInteract || IsReadOnly || !IsFocused)
                return false;
            if (_isComposing)
                CancelComposition();
            _compositionOriginalText = Text;
            _compositionOriginalAnchor = SelectionAnchor;
            _compositionOriginalCaret = CursorPosition;
            _compositionStart = SelectionStart;
            _compositionReplacedLength = SelectionEnd - SelectionStart;
            _compositionLength = 0;
            _isComposing = true;
            return true;
        }

        /// <summary>Displays preedit text without committing it to a binding or undo history.</summary>
        public bool UpdateComposition(string preedit, int caretOffset)
        {
            if (!_isComposing || !CanInteract || IsReadOnly || preedit is null ||
                caretOffset < 0 || caretOffset > preedit.Length)
                return false;
            if (!IsGraphemeBoundary(preedit, caretOffset))
            {
                int snapped = 0;
                foreach (int boundary in StringInfo.ParseCombiningCharacters(preedit))
                {
                    if (boundary > caretOffset)
                        break;
                    snapped = boundary;
                }
                caretOffset = snapped;
            }
            string candidate = _compositionOriginalText.Remove(_compositionStart, _compositionReplacedLength)
                .Insert(_compositionStart, preedit);
            if (MaxInputLength is { } maximum && candidate.Length > maximum)
                return false;
            Text = candidate;
            _compositionLength = preedit.Length;
            CursorPosition = _compositionStart + caretOffset;
            SelectionAnchor = CursorPosition;
            return true;
        }

        /// <summary>Commits one IME edit and records the original text as one undo step.</summary>
        public bool CommitComposition(string committedText)
        {
            if (!_isComposing || committedText is null || !PreValidateInput(committedText))
                return false;
            string result = _compositionOriginalText.Remove(_compositionStart, _compositionReplacedLength)
                .Insert(_compositionStart, committedText);
            bool valid = PostValidateInput(result);
            if (MaxInputLength is { } maximum && result.Length > maximum ||
                !valid && !AllowInvalidDraft)
                return false;
            if (result != _compositionOriginalText)
            {
                if (_undo.Count == MaximumUndoEntries)
                    _undo.RemoveAt(0);
                _undo.Add((_compositionOriginalText, _compositionOriginalAnchor, _compositionOriginalCaret));
                _redo.Clear();
            }
            _isComposing = false;
            Text = result;
            CursorPosition = _compositionStart + committedText.Length;
            SelectionAnchor = CursorPosition;
            IsInvalid = !valid;
            if (!CommitOnSubmit && valid)
                SetValue();
            return true;
        }

        public void CancelComposition()
        {
            if (!_isComposing)
                return;
            _isComposing = false;
            Text = _compositionOriginalText;
            SelectionAnchor = _compositionOriginalAnchor;
            CursorPosition = _compositionOriginalCaret;
            IsInvalid = !PostValidateInput(Text);
        }

        /// <summary>Keeps text changes local until Enter or focus loss commits a valid value.</summary>
        public bool CommitOnSubmit
        {
            get => _commitOnSubmit;
            set => SetField(ref _commitOnSubmit, value);
        }

        /// <summary>Allows a temporary invalid draft while the user edits a value.</summary>
        public bool AllowInvalidDraft { get; set; }
        /// <summary>
        /// When true, Enter submits the input (fires <see cref="Submitted"/>) instead of inserting a newline,
        /// and Escape cancels the input (fires <see cref="Cancelled"/>).
        /// </summary>
        public bool SingleLineMode
        {
            get => _singleLineMode;
            set => SetField(ref _singleLineMode, value);
        }

        /// <summary>
        /// Fired when <see cref="SingleLineMode"/> is true and the user presses Enter.
        /// </summary>
        public event Action<UITextInputComponent>? Submitted;

        /// <summary>
        /// Fired when <see cref="SingleLineMode"/> is true and the user presses Escape.
        /// </summary>
        public event Action<UITextInputComponent>? Cancelled;

        /// <summary>
        /// Called on immediately inputted text before it is added to the text.
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public virtual bool PreValidateInput(string input) => true;
        /// <summary>
        /// Called on the text after any input text has been added to it.
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public virtual bool PostValidateInput(string input) => true;

        protected override void OnGotFocus()
        {
            _focusEpoch++;
            _editOriginalText = Text;
            if (SceneNode is not null)
                TextComponent.Text = Text;
            base.OnGotFocus();
            StartEditOverlay();
        }
        protected override void OnLostFocus()
        {
            _focusEpoch++;
            StopEditOverlay();
            CancelComposition();
            if (CommitOnSubmit && Text != _editOriginalText && !CommitDraft())
                RestoreDraft();
            if (SceneNode is not null)
                TextComponent.Text = FormatText(Text);
            base.OnLostFocus();
        }
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (_binding is not null)
            {
                _binding.ValueChanged += OnBindingChanged;
                OnBindingChanged(_binding.Value);
            }
            // Restore the authored input after both components are attached. An empty
            // input leaves independently authored text on the sibling unchanged.
            if (!string.IsNullOrEmpty(Text) &&
                TryGetSiblingComponent<UITextComponent>(out var textComponent))
            {
                string displayText = IsFocused ? Text : FormatText(Text);
                string? previousText = textComponent!.Text;
                textComponent.Text = displayText;
                // A nested cooked restore can activate the node while property
                // notifications are suppressed by an ancestor's hydration scope.
                if (XRBase.ArePropertyNotificationsSuppressed &&
                    !string.Equals(previousText, displayText, StringComparison.Ordinal))
                    textComponent.RefreshTextLayoutAfterSuppressedChange();
            }

            if (InputPlatformServices.TryGetCapsLockState(out bool capsOn))
                _capsLock = capsOn;
        }
        protected override void OnComponentDeactivated()
        {
            StopEditOverlay();
            CancelComposition();
            if (_binding is not null)
                _binding.ValueChanged -= OnBindingChanged;
            _keyRepeatTimer.Cancel();
            _controlLeft = _controlRight = _shiftLeft = _shiftRight = false;
            base.OnComponentDeactivated();
        }

        public override void CancelForRecycle()
        {
            CancelComposition();
            _keyRepeatTimer.Cancel();
            _controlLeft = _controlRight = _shiftLeft = _shiftRight = false;
            if (CommitOnSubmit)
                RestoreDraft();
            Binding = null;
            _undo.Clear();
            _redo.Clear();
            SelectionAnchor = CursorPosition;
            base.CancelForRecycle();
        }
        public override void RegisterInput(IInputRegistration input)
        {
            base.RegisterInput(input);

            input.RegisterKeyCharacter(OnCharacterTyped);
            //input.RegisterKeyStateChange(EKey.A, A);
            //input.RegisterKeyStateChange(EKey.B, B);
            //input.RegisterKeyStateChange(EKey.C, C);
            //input.RegisterKeyStateChange(EKey.D, D);
            //input.RegisterKeyStateChange(EKey.E, E);
            //input.RegisterKeyStateChange(EKey.F, F);
            //input.RegisterKeyStateChange(EKey.G, G);
            //input.RegisterKeyStateChange(EKey.H, H);
            //input.RegisterKeyStateChange(EKey.I, I);
            //input.RegisterKeyStateChange(EKey.J, J);
            //input.RegisterKeyStateChange(EKey.K, K);
            //input.RegisterKeyStateChange(EKey.L, L);
            //input.RegisterKeyStateChange(EKey.M, M);
            //input.RegisterKeyStateChange(EKey.N, N);
            //input.RegisterKeyStateChange(EKey.O, O);
            //input.RegisterKeyStateChange(EKey.P, P);
            //input.RegisterKeyStateChange(EKey.Q, Q);
            //input.RegisterKeyStateChange(EKey.R, R);
            //input.RegisterKeyStateChange(EKey.S, S);
            //input.RegisterKeyStateChange(EKey.T, T);
            //input.RegisterKeyStateChange(EKey.U, U);
            //input.RegisterKeyStateChange(EKey.V, V);
            //input.RegisterKeyStateChange(EKey.W, W);
            //input.RegisterKeyStateChange(EKey.X, X);
            //input.RegisterKeyStateChange(EKey.Y, Y);
            //input.RegisterKeyStateChange(EKey.Z, Z);
            //input.RegisterKeyStateChange(EKey.Number0, Number0);
            //input.RegisterKeyStateChange(EKey.Number1, Number1);
            //input.RegisterKeyStateChange(EKey.Number2, Number2);
            //input.RegisterKeyStateChange(EKey.Number3, Number3);
            //input.RegisterKeyStateChange(EKey.Number4, Number4);
            //input.RegisterKeyStateChange(EKey.Number5, Number5);
            //input.RegisterKeyStateChange(EKey.Number6, Number6);
            //input.RegisterKeyStateChange(EKey.Number7, Number7);
            //input.RegisterKeyStateChange(EKey.Number8, Number8);
            //input.RegisterKeyStateChange(EKey.Number9, Number9);
            //input.RegisterKeyStateChange(EKey.Tilde, Tilde);
            //input.RegisterKeyStateChange(EKey.Grave, Grave);
            //input.RegisterKeyStateChange(EKey.Minus, Minus);
            //input.RegisterKeyStateChange(EKey.Equal, Equal);
            //input.RegisterKeyStateChange(EKey.BracketLeft, BracketLeft);
            //input.RegisterKeyStateChange(EKey.BracketRight, BracketRight);
            //input.RegisterKeyStateChange(EKey.Semicolon, Semicolon);
            //input.RegisterKeyStateChange(EKey.Apostrophe, Apostrophe);
            //input.RegisterKeyStateChange(EKey.Comma, Comma);
            //input.RegisterKeyStateChange(EKey.Period, Period);
            //input.RegisterKeyStateChange(EKey.Slash, Slash);
            //input.RegisterKeyStateChange(EKey.BackSlash, BackSlash);
            ////input.RegisterKeyStateChange(EKey.NonUSBackSlash, NonUSBackSlash);
            ///
            input.RegisterKeyStateChange(EKey.Space, Space);
            input.RegisterKeyStateChange(EKey.Backspace, Backspace);
            input.RegisterKeyStateChange(EKey.Delete, Delete);
            input.RegisterKeyStateChange(EKey.Left, Left);
            input.RegisterKeyStateChange(EKey.Right, Right);
            input.RegisterKeyStateChange(EKey.Up, Up);
            input.RegisterKeyStateChange(EKey.Down, Down);
            input.RegisterKeyStateChange(EKey.ShiftLeft, ShiftLeft);
            input.RegisterKeyStateChange(EKey.ShiftRight, ShiftRight);
            input.RegisterKeyStateChange(EKey.ControlLeft, ControlLeft);
            input.RegisterKeyStateChange(EKey.ControlRight, ControlRight);
            input.RegisterKeyStateChange(EKey.Z, UndoShortcut);
            input.RegisterKeyStateChange(EKey.Y, RedoShortcut);
            input.RegisterKeyStateChange(EKey.A, SelectAllShortcut);
            input.RegisterKeyStateChange(EKey.CapsLock, CapsLock);
            input.RegisterKeyStateChange(EKey.Enter, Enter);
            input.RegisterKeyStateChange(EKey.Escape, Escape);
        }

        private void OnCharacterTyped(char character)
        {
            if (AnyControl || IsComposing)
                return;
            AddChar(true, character.ToString());
        }

        private void Enter(bool pressed)
        {
            if (pressed)
            {
                if (IsComposing)
                    return;
                if (SingleLineMode)
                    UserSubmit();
                else
                    UserAddText(Environment.NewLine);
            }
        }

        private void Escape(bool pressed)
        {
            if (!pressed)
                return;
            if (IsComposing)
                CancelComposition();
            else if (SingleLineMode)
                UserCancel();
        }

        private void CapsLock(bool pressed)
        {
            if (pressed)
                return; //Only toggle on release, not on press

            if (InputPlatformServices.TryGetCapsLockState(out bool capsOn))
                _capsLock = capsOn;
            else
                _capsLock = !_capsLock;
        }

        private bool _shiftLeft = false;
        private bool _shiftRight = false;
        private bool _controlLeft;
        private bool _controlRight;
        private bool _capsLock = false;

        public bool AnyShift => _shiftLeft || _shiftRight;
        public bool AnyControl => _controlLeft || _controlRight;
        public bool CapsLocked => _capsLock;
        
        private void ShiftRight(bool pressed)
            => _shiftRight = pressed;
        private void ShiftLeft(bool pressed)
            => _shiftLeft = pressed;
        private void ControlLeft(bool pressed)
            => _controlLeft = pressed;
        private void ControlRight(bool pressed)
            => _controlRight = pressed;

        private void UndoShortcut(bool pressed)
        {
            if (pressed && AnyControl)
            {
                if (AnyShift)
                    Redo();
                else
                    Undo();
            }
        }

        private void RedoShortcut(bool pressed)
        {
            if (pressed && AnyControl)
                Redo();
        }

        private void SelectAllShortcut(bool pressed)
        {
            if (pressed && AnyControl)
            {
                SelectionAnchor = 0;
                CursorPosition = Text.Length;
            }
        }

        private readonly GameTimer _keyRepeatTimer;
        private string? _lastChar = string.Empty;
        private EKey? _lastMovementInput = null;

        private TimeSpan _keyRepeatDelay = TimeSpan.FromMilliseconds(DefaultKeyRepeatDelayMs);
        /// <summary>
        /// This is how long it takes a key to be held until it begins to repeatedly add the same key more than once at a set interval.
        /// </summary>
        public TimeSpan KeyRepeatDelay
        {
            get => _keyRepeatDelay;
            set => SetField(ref _keyRepeatDelay, value);
        }

        private TimeSpan _keyRepeatInterval = TimeSpan.FromMilliseconds(DefaultKeyRepeatIntervalMs);
        /// <summary>
        /// This is the time between repeated inputs when a key is held down.
        /// </summary>
        public TimeSpan KeyRepeatInterval
        {
            get => _keyRepeatInterval;
            set => SetField(ref _keyRepeatInterval, value);
        }

        private void A(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "A" : "a");
        private void B(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "B" : "b");
        private void C(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "C" : "c");
        private void D(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "D" : "d");
        private void E(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "E" : "e");
        private void F(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "F" : "f");
        private void G(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "G" : "g");
        private void H(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "H" : "h");
        private void I(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "I" : "i");
        private void J(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "J" : "j");
        private void K(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "K" : "k");
        private void L(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "L" : "l");
        private void M(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "M" : "m");
        private void N(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "N" : "n");
        private void O(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "O" : "o");
        private void P(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "P" : "p");
        private void Q(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "Q" : "q");
        private void R(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "R" : "r");
        private void S(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "S" : "s");
        private void T(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "T" : "t");
        private void U(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "U" : "u");
        private void V(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "V" : "v");
        private void W(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "W" : "w");
        private void X(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "X" : "x");
        private void Y(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "Y" : "y");
        private void Z(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "Z" : "z");
        private void Number0(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? ")" : "0");
        private void Number1(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "!" : "1");
        private void Number2(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "@" : "2");
        private void Number3(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "#" : "3");
        private void Number4(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "$" : "4");
        private void Number5(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "%" : "5");
        private void Number6(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "^" : "6");
        private void Number7(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "&" : "7");
        private void Number8(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "*" : "8");
        private void Number9(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "(" : "9");
        private void Tilde(bool pressed) => AddChar(pressed, "~");
        private void Grave(bool pressed) => AddChar(pressed, "`");
        private void Minus(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "_" : "-");
        private void Equal(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "+" : "=");
        private void BracketLeft(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "{" : "[");
        private void BracketRight(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "}" : "]");
        private void Semicolon(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? ":" : ";");
        private void Apostrophe(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "\"" : "'");
        private void Comma(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "<" : ",");
        private void Period(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? ">" : ".");
        private void Slash(bool pressed) => AddChar(pressed, AnyShift || CapsLocked ? "?" : "/");
        private void BackSlash(bool pressed) => AddChar(pressed, "\\");
        private void NonUSBackSlash(bool pressed) => AddChar(pressed, "|");
        private void Space(bool pressed) => AddChar(pressed, " ");
        private void Backspace(bool pressed)
        {
            if (pressed)
            {
                _lastChar = null;
                _lastMovementInput = EKey.Backspace;
                UserRemoveText(1, true);
                _keyRepeatTimer.StartMultiFire(AddLastChar, _keyRepeatInterval, -1, _keyRepeatDelay, ETickGroup.Normal, (int)ETickOrder.Input);
            }
            else
                _keyRepeatTimer.Cancel();
        }
        private void Delete(bool pressed)
        {
            if (pressed)
            {
                _lastChar = null;
                _lastMovementInput = EKey.Delete;
                UserRemoveText(1, false);
                _keyRepeatTimer.StartMultiFire(AddLastChar, _keyRepeatInterval, -1, _keyRepeatDelay, ETickGroup.Normal, (int)ETickOrder.Input);
            }
            else
                _keyRepeatTimer.Cancel();
        }
        private void Left(bool pressed)
        {
            if (pressed)
            {
                _lastChar = null;
                _lastMovementInput = EKey.Left;
                MoveCaretByGrapheme(-1, AnyShift);
                StartCharTimer();
            }
            else
                _keyRepeatTimer.Cancel();
        }

        private void Right(bool pressed)
        {
            if (pressed)
            {
                _lastChar = null;
                _lastMovementInput = EKey.Right;
                MoveCaretByGrapheme(1, AnyShift);
                _keyRepeatTimer.StartMultiFire(AddLastChar, _keyRepeatInterval, -1, _keyRepeatDelay, ETickGroup.Normal, (int)ETickOrder.Input);
            }
            else
                _keyRepeatTimer.Cancel();
        }

        private void AddChar(bool pressed, string c)
        {
            if (pressed)
            {
                _lastMovementInput = null;
                UserAddText(_lastChar = c);
                StartCharTimer();
            }
            else
                _keyRepeatTimer.Cancel();
        }

        private void StartCharTimer()
            => _keyRepeatTimer.StartMultiFire(AddLastChar, _keyRepeatInterval, -1, _keyRepeatDelay, ETickGroup.Normal, (int)ETickOrder.Input);

        private void AddLastChar(TimeSpan totalElapsed, int fireNumber)
        {
            if (_lastChar is null)
            {
                switch (_lastMovementInput)
                {
                    case EKey.Left:
                        MoveCaretByGrapheme(-1, AnyShift);
                        break;
                    case EKey.Right:
                        MoveCaretByGrapheme(1, AnyShift);
                        break;
                    case EKey.Backspace:
                        UserRemoveText(1, true);
                        break;
                    case EKey.Delete:
                        UserRemoveText(1, false);
                        break;
                }
            }
            else
                UserAddText(_lastChar);
        }

        public void UserAddText(string text)
        {
            if (!CanInteract || IsReadOnly || IsComposing)
                return;
            int start = SelectionStart;
            int length = SelectionEnd - start;
            if (MaxInputLength.HasValue && Text.Length - length + text.Length > MaxInputLength)
                return;

            if (!PreValidateInput(text))
                return;

            string newText = Text.Remove(start, length).Insert(start, text);
            bool valid = PostValidateInput(newText);
            if (!valid && !AllowInvalidDraft)
                return;
            SaveUndo();
            //Set the new text
            Text = newText;
            IsInvalid = !valid;
            ///Move the cursor to the end of the new text
            CursorPosition = start + text.Length;
            SelectionAnchor = CursorPosition;
            //Apply the new value to the property
            if (!CommitOnSubmit && valid)
                SetValue();
        }

        /// <summary>
        /// Applies one user edit and caret movement atomically. Native text services can
        /// supply a completed IME composition or selected-range replacement here.
        /// </summary>
        public bool UserReplaceText(string value, int selectionStart, int selectionEnd)
        {
            if (!CanInteract || IsReadOnly)
                return false;
            CancelComposition();
            ArgumentNullException.ThrowIfNull(value);
            if (selectionStart < 0 || selectionEnd < selectionStart || selectionEnd > value.Length)
                return false;
            if (!IsGraphemeBoundary(value, selectionStart) || !IsGraphemeBoundary(value, selectionEnd))
                return false;
            if (MaxInputLength is { } maximum && value.Length > maximum)
                return false;

            string previous = Text;
            if (value != previous)
            {
                int prefix = 0;
                int commonLength = Math.Min(previous.Length, value.Length);
                while (prefix < commonLength && previous[prefix] == value[prefix])
                    prefix++;

                int suffix = 0;
                while (suffix < commonLength - prefix &&
                       previous[previous.Length - 1 - suffix] == value[value.Length - 1 - suffix])
                    suffix++;

                int insertionLength = value.Length - prefix - suffix;
                if (insertionLength > 0 && !PreValidateInput(value.Substring(prefix, insertionLength)))
                    return false;
                bool valid = PostValidateInput(value);
                if (!valid && !AllowInvalidDraft)
                    return false;

                SaveUndo();
                Text = value;
                IsInvalid = !valid;
                if (!CommitOnSubmit && valid)
                    SetValue();
            }

            SelectionAnchor = selectionStart;
            CursorPosition = selectionEnd;
            return true;
        }

        /// <summary>Raises the same single-line submit action as the engine Enter key.</summary>
        public virtual void UserSubmit()
        {
            if (IsComposing)
                return;
            if (SingleLineMode && (!CommitOnSubmit || CommitDraft()))
                Submitted?.Invoke(this);
        }

        /// <summary>Raises the same single-line cancel action as the engine Escape key.</summary>
        public virtual void UserCancel()
        {
            if (IsComposing)
            {
                CancelComposition();
                return;
            }
            if (SingleLineMode)
            {
                if (CommitOnSubmit)
                    RestoreDraft();
                Cancelled?.Invoke(this);
            }
        }

        private bool CommitDraft()
        {
            if (!PostValidateInput(Text))
            {
                IsInvalid = true;
                return false;
            }
            IsInvalid = false;
            SetValue();
            _editOriginalText = Text;
            return true;
        }

        private void RestoreDraft()
        {
            Text = _binding?.Value ?? _editOriginalText;
            CursorPosition = Text.Length;
            SelectionAnchor = CursorPosition;
            IsInvalid = false;
            _undo.Clear();
            _redo.Clear();
        }

        public void UserRemoveText(int count, bool backward)
        {
            if (!CanInteract || IsReadOnly || IsComposing || count <= 0)
                return;
            if (SelectionStart != SelectionEnd)
            {
                int start = SelectionStart;
                string selectedText = Text.Remove(start, SelectionEnd - start);
                bool valid = PostValidateInput(selectedText);
                if (!valid && !AllowInvalidDraft)
                    return;
                SaveUndo();
                Text = selectedText;
                IsInvalid = !valid;
                CursorPosition = start;
                SelectionAnchor = start;
                if (!CommitOnSubmit && valid)
                    SetValue();
                return;
            }
            string newText;
            if (backward)
            {
                int boundary = CursorPosition;
                for (int i = 0; i < count && boundary > 0; i++)
                    boundary = PreviousGraphemeBoundary(boundary);
                count = CursorPosition - boundary;
                if (count <= 0)
                    return;

                newText = Text.Remove(CursorPosition - count, count);

                bool valid = PostValidateInput(newText);
                if (!valid && !AllowInvalidDraft)
                    return;

                SaveUndo();
                CursorPosition -= count;
            }
            else
            {
                int boundary = CursorPosition;
                for (int i = 0; i < count && boundary < Text.Length; i++)
                    boundary = NextGraphemeBoundary(boundary);
                count = boundary - CursorPosition;
                if (count <= 0)
                    return;

                newText = Text.Remove(CursorPosition, count);

                bool valid = PostValidateInput(newText);
                if (!valid && !AllowInvalidDraft)
                    return;

                SaveUndo();
            }

            //Set the new text
            Text = newText;
            IsInvalid = !PostValidateInput(newText);
            SelectionAnchor = CursorPosition;
            //Apply the new value to the property
            if (!CommitOnSubmit && !IsInvalid)
                SetValue();
        }

        private void SetValue()
        {
            if (_binding is not null)
            {
                _writingBinding = true;
                try { _binding.SetValue(Text); }
                finally { _writingBinding = false; }
                if (_binding.Value != Text)
                    Text = _binding.Value;
                return;
            }
            var prop = Property;
            var targets = Targets;
            if (prop is null || targets is null)
                return;

            ParseAndSet(prop, targets);
        }

        protected virtual void ParseAndSet(PropertyInfo prop, object?[] targets)
        {
            foreach (var target in targets)
            {
                var obj = Convert.ChangeType(Text, prop.PropertyType);
                prop.SetValue(target, obj);
            }
        }

        private int _cursorPosition = 0;
        public int CursorPosition
        {
            get => _cursorPosition;
            set
            {
                if (SetField(ref _cursorPosition, SnapGraphemeBoundary(value.Clamp(0, Text.Length))))
                {
                    _visualCaretLayout = null;
                    _visualCaretAffinity = UITextCaretAffinity.Downstream;
                    if (!_movingCaretVertically)
                    {
                        _preferredCaretX = null;
                        _verticalVisualLine = null;
                    }
                }
            }
        }

        private int _selectionAnchor;
        /// <summary>Fixed end of the current text selection, as a UTF-16 boundary.</summary>
        public int SelectionAnchor
        {
            get => _selectionAnchor;
            set => SetField(ref _selectionAnchor, SnapGraphemeBoundary(value.Clamp(0, Text.Length)));
        }

        public int SelectionStart => Math.Min(SelectionAnchor, CursorPosition);
        public int SelectionEnd => Math.Max(SelectionAnchor, CursorPosition);

        /// <summary>Moves the caret by one displayed text element.</summary>
        public void MoveCaretByGrapheme(int direction, bool extendSelection = false)
        {
            TryMoveCaretByGrapheme(direction, extendSelection);
        }

        private string? _graphemeText;
        private int[] _graphemeStarts = [];

        private int[] GraphemeBoundaries()
        {
            if (!ReferenceEquals(_graphemeText, Text))
            {
                _graphemeText = Text;
                _graphemeStarts = StringInfo.ParseCombiningCharacters(Text);
            }
            return _graphemeStarts;
        }

        private int PreviousGraphemeBoundary(int position)
        {
            int previous = 0;
            foreach (int boundary in GraphemeBoundaries())
            {
                if (boundary >= position)
                    break;
                previous = boundary;
            }
            return previous;
        }

        private int NextGraphemeBoundary(int position)
        {
            foreach (int boundary in GraphemeBoundaries())
                if (boundary > position)
                    return boundary;
            return Text.Length;
        }

        private int SnapGraphemeBoundary(int position)
            => position == Text.Length ? position : PreviousGraphemeBoundary(position + 1);

        private static bool IsGraphemeBoundary(string value, int position)
        {
            if (position == value.Length)
                return true;
            foreach (int boundary in StringInfo.ParseCombiningCharacters(value))
                if (boundary == position)
                    return true;
            return false;
        }

        private readonly List<(string Text, int Anchor, int Caret)> _undo = [];
        private readonly List<(string Text, int Anchor, int Caret)> _redo = [];
        private const int MaximumUndoEntries = 100;

        private void SaveUndo()
        {
            if (_undo.Count == MaximumUndoEntries)
                _undo.RemoveAt(0);
            _undo.Add((Text, SelectionAnchor, CursorPosition));
            _redo.Clear();
        }

        public bool Undo() => CanInteract && !IsReadOnly && !IsComposing && RestoreHistory(_undo, _redo);
        public bool Redo() => CanInteract && !IsReadOnly && !IsComposing && RestoreHistory(_redo, _undo);

        private bool RestoreHistory(List<(string Text, int Anchor, int Caret)> source,
            List<(string Text, int Anchor, int Caret)> destination)
        {
            if (source.Count == 0)
                return false;
            destination.Add((Text, SelectionAnchor, CursorPosition));
            var entry = source[^1];
            source.RemoveAt(source.Count - 1);
            Text = entry.Text;
            IsInvalid = !PostValidateInput(Text);
            SelectionAnchor = entry.Anchor;
            CursorPosition = entry.Caret;
            if (!CommitOnSubmit && !IsInvalid)
                SetValue();
            return true;
        }

        private int? _maxInputLength = null;
        public int? MaxInputLength
        {
            get => _maxInputLength;
            set => SetField(ref _maxInputLength, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            // The base implementation raises PropertyChanged, so advance the IME token
            // before the input host observes this transition.
            if (propName is nameof(IsReadOnly) or nameof(IsEnabled))
            {
                _focusEpoch++;
                if (IsReadOnly || !IsEnabled)
                    CancelComposition();
            }
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Text):
                    _preferredCaretX = null;
                    _verticalVisualLine = null;
                    //Re-validate cursor position
                    CursorPosition = _cursorPosition;
                    SelectionAnchor = _selectionAnchor;
                    // YAML assigns input properties before the owning node is attached.
                    if (SceneNode is not null)
                        TextComponent.Text = IsFocused ? Text : FormatText(Text);
                    break;
                case nameof(CursorPosition):
                case nameof(SelectionAnchor):
                    InvalidateEditOverlay();
                    break;
            }
        }

        public string FormatText(string text)
            => $"{PreText}{text}{PostText}";

        public void SetCursorPositionWithLocalPoint(Vector3 localPoint)
        {
            if (SceneNode is null || SceneNode.IsTransformNull ||
                !TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult layout))
                return;
            TrySetCursorPositionWithLayout(localPoint, layout);
        }

        /// <summary>Uses placed caret stops only when geometry matches the current text revision.</summary>
        public bool TrySetCursorPositionWithLayout(Vector3 localPoint, UITextLayoutResult layout)
        {
            if (SceneNode is null || SceneNode.IsTransformNull ||
                !TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult currentLayout) ||
                !ReferenceEquals(layout, currentLayout) || TextComponent.Text != Text ||
                !layout.HasCompleteGraphemeCaretStops)
                return false;
            ReadOnlySpan<UITextCaretStop> stops = layout.Carets.Span;
            if (stops.IsEmpty)
                return false;

            float nearest = float.PositiveInfinity;
            int nearestCaret = CursorPosition;
            int nearestIndex = -1;
            for (int i = 0; i < stops.Length; i++)
            {
                UITextCaretStop stop = stops[i];
                if (!IsCurrentGraphemeBoundary(stop.Utf16Offset))
                    continue;
                float bottom = stop.Position.Y - stop.LineHeight;
                float top = stop.Position.Y;
                float vertical = localPoint.Y < bottom ? bottom - localPoint.Y :
                    localPoint.Y > top ? localPoint.Y - top : 0;
                float horizontal = localPoint.X - stop.Position.X;
                float distance = horizontal * horizontal + vertical * vertical;
                if (distance < nearest)
                {
                    nearest = distance;
                    nearestCaret = stop.Utf16Offset;
                    nearestIndex = i;
                }
            }
            if (!float.IsFinite(nearest))
                return false;
            CursorPosition = nearestCaret;
            _visualCaretLayout = layout.UsesShapedGlyphs ? layout : null;
            _visualCaretIndex = nearestIndex;
            _visualCaretAffinity = stops[nearestIndex].Affinity;
            SelectionAnchor = CursorPosition;
            return true;
        }

        private bool IsCurrentGraphemeBoundary(int position)
            => position == Text.Length || Array.BinarySearch(GraphemeBoundaries(), position) >= 0;

        /// <summary>Gets the current caret bounds from a matching placed text result.</summary>
        public bool TryGetCaretLocalBounds(UITextLayoutResult layout, out Vector4 bounds)
        {
            bounds = default;
            if (SceneNode is null || SceneNode.IsTransformNull ||
                !TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult currentLayout) ||
                !ReferenceEquals(layout, currentLayout) || TextComponent.Text != Text ||
                !layout.HasCompleteGraphemeCaretStops)
                return false;
            if (!TryFindCaretStop(layout.Carets.Span, out UITextCaretStop stop))
                return false;
            bounds = new(stop.Position.X, stop.Position.Y - stop.LineHeight, 1f, stop.LineHeight);
            return true;
        }
    }
}
