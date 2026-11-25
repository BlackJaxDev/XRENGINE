using System.Runtime.InteropServices;

namespace XREngine.Rendering.UI;

/// <summary>Windows DirectWrite script, bidi, and line-break analysis.</summary>
public sealed unsafe class WindowsDirectWriteParagraphAnalyzer : IUITextParagraphAnalyzer
{
    private static readonly Guid FactoryId = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    private const int EInvalidArg = unchecked((int)0x80070057);

    public UITextParagraphAnalysis Analyze(string text, string language, UITextDirection direction)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("UI.DirectWriteParagraphUnavailable: DirectWrite requires Windows.");
        if (text.Length == 0)
            return new UITextParagraphAnalysis([], []);

        nint factory = 0, analyzer = 0, sourcePointer = 0, sinkPointer = 0;
        using Source source = new(text, language, direction);
        Sink sink = new(text.Length);
        try
        {
            Guid iid = FactoryId;
            Check(DWriteCreateFactory(1, ref iid, out factory), "Factory");
            // Windows SDK dwrite.h: IDWriteFactory::CreateTextAnalyzer, after IUnknown.
            void** factoryMethods = *(void***)factory;
            var createAnalyzer = (delegate* unmanaged[Stdcall]<nint, nint*, int>)factoryMethods[21];
            Check(createAnalyzer(factory, &analyzer), "Analyzer");
            sourcePointer = Marshal.GetComInterfaceForObject(source, typeof(IDWriteTextAnalysisSourceNative));
            sinkPointer = Marshal.GetComInterfaceForObject(sink, typeof(IDWriteTextAnalysisSinkNative));
            // IDWriteTextAnalyzer: AnalyzeScript, AnalyzeBidi, Number, LineBreakpoints.
            void** methods = *(void***)analyzer;
            var script = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, nint, int>)methods[3];
            var bidi = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, nint, int>)methods[4];
            var breaks = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, nint, int>)methods[6];
            uint length = (uint)text.Length;
            Check(script(analyzer, sourcePointer, 0, length, sinkPointer), "Script");
            Check(bidi(analyzer, sourcePointer, 0, length, sinkPointer), "Bidi");
            Check(breaks(analyzer, sourcePointer, 0, length, sinkPointer), "LineBreakpoints");
            return sink.Finish();
        }
        finally
        {
            if (sinkPointer != 0) Marshal.Release(sinkPointer);
            if (sourcePointer != 0) Marshal.Release(sourcePointer);
            if (analyzer != 0) Marshal.Release(analyzer);
            if (factory != 0) Marshal.Release(factory);
            GC.KeepAlive(sink);
            GC.KeepAlive(source);
        }
    }

    private static void Check(int hr, string stage)
    {
        if (hr < 0)
            throw new InvalidOperationException($"UI.DirectWriteParagraph{stage}Failed: HRESULT 0x{hr:X8}.",
                Marshal.GetExceptionForHR(hr));
    }

    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid, out nint factory);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Source : IDWriteTextAnalysisSourceNative, IDisposable
    {
        private readonly string _text;
        private readonly GCHandle _textPin;
        private readonly GCHandle _localePin;
        private readonly UITextDirection _direction;

        internal Source(string text, string language, UITextDirection direction)
        {
            _text = text;
            _direction = direction;
            _textPin = GCHandle.Alloc(text, GCHandleType.Pinned);
            _localePin = GCHandle.Alloc(language, GCHandleType.Pinned);
        }

        public int GetTextAtPosition(uint position, out nint text, out uint length)
        {
            text = 0;
            length = 0;
            if (position >= _text.Length) return 0;
            text = _textPin.AddrOfPinnedObject() + checked((int)position * sizeof(char));
            length = (uint)_text.Length - position;
            return 0;
        }

        public int GetTextBeforePosition(uint position, out nint text, out uint length)
        {
            text = 0;
            length = 0;
            if (position == 0 || position > _text.Length) return 0;
            text = _textPin.AddrOfPinnedObject();
            length = position;
            return 0;
        }

        public int GetParagraphReadingDirection() => (int)_direction;

        public int GetLocaleName(uint position, out uint length, out nint locale)
        {
            length = 0;
            locale = 0;
            if (position >= _text.Length) return EInvalidArg;
            length = (uint)_text.Length - position;
            locale = _localePin.AddrOfPinnedObject();
            return 0;
        }

        public int GetNumberSubstitution(uint position, out uint length, out nint substitution)
        {
            length = 0;
            substitution = 0;
            if (position >= _text.Length) return EInvalidArg;
            length = (uint)_text.Length - position;
            return 0;
        }

        public void Dispose()
        {
            if (_localePin.IsAllocated) _localePin.Free();
            if (_textPin.IsAllocated) _textPin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ScriptAnalysis
    {
        internal readonly ushort Script;
        internal readonly int Shapes;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Sink(int length) : IDWriteTextAnalysisSinkNative
    {
        private readonly ushort[] _scripts = new ushort[length];
        private readonly int[] _shapes = new int[length];
        private readonly byte[] _explicitLevels = new byte[length];
        private readonly byte[] _resolvedLevels = new byte[length];
        private readonly byte[] _breakpoints = new byte[length];
        private readonly byte[] _coverage = new byte[length];

        private bool Valid(uint position, uint count) => position <= _coverage.Length &&
            count <= _coverage.Length - position;

        public int SetScriptAnalysis(uint position, uint length, nint analysis)
        {
            if (!Valid(position, length) || analysis == 0) return EInvalidArg;
            ScriptAnalysis value = Marshal.PtrToStructure<ScriptAnalysis>(analysis);
            for (int i = (int)position; i < position + length; i++)
            {
                _scripts[i] = value.Script;
                _shapes[i] = value.Shapes;
                _coverage[i] |= 1;
            }
            return 0;
        }

        public int SetLineBreakpoints(uint position, uint length, nint breakpoints)
        {
            if (!Valid(position, length) || breakpoints == 0) return EInvalidArg;
            Marshal.Copy(breakpoints, _breakpoints, (int)position, (int)length);
            for (int i = (int)position; i < position + length; i++) _coverage[i] |= 4;
            return 0;
        }

        public int SetBidiLevel(uint position, uint length, byte explicitLevel, byte resolvedLevel)
        {
            if (!Valid(position, length)) return EInvalidArg;
            for (int i = (int)position; i < position + length; i++)
            {
                _explicitLevels[i] = explicitLevel;
                _resolvedLevels[i] = resolvedLevel;
                _coverage[i] |= 2;
            }
            return 0;
        }

        public int SetNumberSubstitution(uint position, uint length, nint substitution) =>
            Valid(position, length) ? 0 : EInvalidArg;

        internal UITextParagraphAnalysis Finish()
        {
            List<UITextParagraphRun> runs = [];
            for (int start = 0; start < _coverage.Length;)
            {
                if (_coverage[start] != 7)
                    throw new InvalidOperationException("UI.DirectWriteParagraphIncomplete: analysis omitted a UTF-16 unit.");
                int end = start + 1;
                while (end < _coverage.Length && _coverage[end] == 7 &&
                    _scripts[end] == _scripts[start] && _shapes[end] == _shapes[start] &&
                    _explicitLevels[end] == _explicitLevels[start] &&
                    _resolvedLevels[end] == _resolvedLevels[start])
                    end++;
                runs.Add(new UITextParagraphRun(start, end - start, _scripts[start], _shapes[start],
                    _explicitLevels[start], _resolvedLevels[start]));
                start = end;
            }
            UITextLineBreakpoint[] breakpoints = new UITextLineBreakpoint[_breakpoints.Length];
            for (int i = 0; i < breakpoints.Length; i++)
            {
                byte value = _breakpoints[i];
                breakpoints[i] = new UITextLineBreakpoint((UITextBreakCondition)(value & 3),
                    (UITextBreakCondition)((value >> 2) & 3), (value & 16) != 0, (value & 32) != 0);
            }
            return new UITextParagraphAnalysis([.. runs], breakpoints);
        }
    }
}
