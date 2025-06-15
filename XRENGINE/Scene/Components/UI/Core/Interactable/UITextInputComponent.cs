using Extensions;
using NAudio.SoundFont;
using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Input.Devices;
using XREngine.Native;
using XREngine.Timers;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Handles textual input from the user.
    /// </summary>
    [RequireComponents(typeof(UITextComponent))]
    public class UITextInputComponent : UIInteractableComponent
    {
        private const int DefaultKeyRepeatDelayMs = 500;
        private const int DefaultKeyRepeatIntervalMs = 50;

        public UITextInputComponent() : base()
        {
            _keyRepeatTimer = new(this);
        }

        public UITextComponent TextComponent => GetSiblingComponent<UITextComponent>(true)!;

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

        private PropertyInfo? _property;
        public PropertyInfo? Property
        {
            get => _property;
            set => SetField(ref _property, value);
        }

        private object?[]? _targets;
        public object?[]? Targets
        {
            get => _targets;
            set => SetField(ref _targets, value);
        }

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
            TextComponent.Text = Text;
            base.OnGotFocus();
        }
        protected override void OnLostFocus()
        {
            TextComponent.Text = FormatText(Text);
            base.OnLostFocus();
        }
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (NativeMethods.TryDetermineSystemCapsLockState(out bool capsOn))
                _capsLock = capsOn;
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
        }
        public override void RegisterInput(InputInterface input)
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
            input.RegisterKeyStateChange(EKey.ShiftLeft, ShiftLeft);
            input.RegisterKeyStateChange(EKey.ShiftRight, ShiftRight);
            input.RegisterKeyStateChange(EKey.CapsLock, CapsLock);
            input.RegisterKeyStateChange(EKey.Enter, Enter);
        }

        private void OnCharacterTyped(char character)
        {
            AddChar(true, character.ToString());
        }

        private void Enter(bool pressed)
        {
            if (pressed)
                UserAddText(Environment.NewLine);
        }

        private void CapsLock(bool pressed)
        {
            if (pressed)
                return; //Only toggle on release, not on press

            if (NativeMethods.TryDetermineSystemCapsLockState(out bool capsOn))
                _capsLock = capsOn;
            else
                _capsLock = !_capsLock;
        }

        private bool _shiftLeft = false;
        private bool _shiftRight = false;
        private bool _capsLock = false;

        public bool AnyShift => _shiftLeft || _shiftRight;
        public bool CapsLocked => _capsLock;
        
        private void ShiftRight(bool pressed)
            => _shiftRight = pressed;
        private void ShiftLeft(bool pressed)
            => _shiftLeft = pressed;

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
                CursorPosition--;
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
                CursorPosition++;
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
                        CursorPosition--;
                        break;
                    case EKey.Right:
                        CursorPosition++;
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
            if (MaxInputLength.HasValue && Text.Length + text.Length > MaxInputLength)
                return;

            if (!PreValidateInput(text))
                return;

            string newText = Text.Insert(CursorPosition, text);
            if (!PostValidateInput(newText))
                return;
            
            //Set the new text
            Text = newText;
            ///Move the cursor to the end of the new text
            CursorPosition += text.Length;
            //Apply the new value to the property
            SetValue();
        }

        public void UserRemoveText(int count, bool backward)
        {
            string newText;
            if (backward)
            {
                if (CursorPosition - count < 0)
                    count = CursorPosition;
                if (count <= 0)
                    return;

                newText = Text.Remove(CursorPosition - count, count);

                if (!PostValidateInput(newText))
                    return;

                //Move the cursor back
                CursorPosition -= count;
            }
            else
            {
                if (CursorPosition + count > Text.Length)
                    count = Text.Length - CursorPosition;
                if (count <= 0)
                    return;

                newText = Text.Remove(CursorPosition, count);

                if (!PostValidateInput(newText))
                    return;

                //Cursor stays in the same position
            }

            //Set the new text
            Text = newText;
            //Apply the new value to the property
            SetValue();
        }

        private void SetValue()
        {
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
            set => SetField(ref _cursorPosition, value.Clamp(0, Text.Length));
        }

        private int? _maxInputLength = null;
        public int? MaxInputLength
        {
            get => _maxInputLength;
            set => SetField(ref _maxInputLength, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Text):
                    //Re-validate cursor position
                    CursorPosition = _cursorPosition;
                    //Display updated text to the user
                    TextComponent.Text = FormatText(Text);
                    break;
            }
        }

        public string FormatText(string text)
            => $"{PreText}{text}{PostText}";

        public void SetCursorPositionWithLocalPoint(Vector3 localPoint)
        {
            var text = TextComponent.Text;
            if (string.IsNullOrEmpty(text))
            {
                CursorPosition = 0;
                return;
            }
            var font = TextComponent.Font;
            if (font is null)
            {
                CursorPosition = 0;
                return;
            }

            //TODO

            //var glyphs = TextComponent.Glyphs;
            //if (glyphs is null)
            //{
            //    CursorPosition = 0;
            //    return;
            //}
            //var cursorX = localPoint.X;
            //var cursorY = localPoint.Y;
            //var cursorIndex = 0;
            //var cursorDistance = float.MaxValue;
            //for (int i = 0; i < glyphs.Count; i++)
            //{
            //    var glyph = glyphs[i];
            //    var glyphX = glyph.transform.X;
            //    var glyphY = glyph.transform.Y;
            //    var glyphWidth = glyph.transform.Z;
            //    var glyphHeight = glyph.transform.W;
            //    if (cursorY >= glyphY && cursorY <= glyphY + glyphHeight)
            //    {
            //        var distance = Math.Abs(cursorX - glyphX);
            //        if (distance < cursorDistance)
            //        {
            //            cursorDistance = distance;
            //            cursorIndex = i;
            //        }
            //    }
            //}
            //CursorPosition = cursorIndex;
        }
    }
}
