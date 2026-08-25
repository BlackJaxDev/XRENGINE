namespace XREngine.Core.Files
{
    public class EventRaisingStreamWriter(Stream s) : StreamWriter(s)
    {
        public event EventHandler<EventArgs<string>>? StringWritten;

        private void OnStringWritten(string? str)
        {
            if (str is not null)
                StringWritten?.Invoke(this, new EventArgs<string>(str));
        }

        public override void Write(string? value)
        {
            base.Write(value);
            OnStringWritten(value);
        }
        public override void Write(bool value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void Write(char value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void Write(char[]? buffer)
        {
            base.Write(buffer);
            OnStringWritten(new string(buffer));
        }
        public override void Write(char[] buffer, int index, int count)
        {
            base.Write(buffer, index, count);
            OnStringWritten(new string(buffer, index, count));
        }
        public override void Write(decimal value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void Write(double value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void Write(float value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void Write(int value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void Write(long value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void Write(object? value)
        {
            base.Write(value);
            OnStringWritten(value?.ToString());
        }
        public override void Write(string format, object? arg0)
        {
            base.Write(format, arg0);
            OnStringWritten(string.Format(format, arg0));
        }
        public override void Write(string format, object? arg0, object? arg1)
        {
            base.Write(format, arg0, arg1);
            OnStringWritten(string.Format(format, arg0, arg1));
        }
        public override void Write(string format, object? arg0, object? arg1, object? arg2)
        {
            base.Write(format, arg0, arg1, arg2);
            OnStringWritten(string.Format(format, arg0, arg1, arg2));
        }
        public override void Write(string format, params object?[] arg)
        {
            base.Write(format, arg);
            OnStringWritten(string.Format(format, arg));
        }
        public override void Write(uint value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void Write(ulong value)
        {
            base.Write(value);
            OnStringWritten(value.ToString());
        }
        public override void WriteLine()
        {
            base.WriteLine();
            OnStringWritten(NewLine);
        }
        public override void WriteLine(bool value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override void WriteLine(char value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override void WriteLine(char[]? buffer)
        {
            base.WriteLine(buffer);
            OnStringWritten(new string(buffer) + NewLine);
        }
        public override void WriteLine(char[] buffer, int index, int count)
        {
            base.WriteLine(buffer, index, count);
            OnStringWritten(new string(buffer, index, count) + NewLine);
        }
        public override void WriteLine(decimal value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override void WriteLine(double value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override void WriteLine(float value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override void WriteLine(int value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override void WriteLine(long value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override void WriteLine(object? value)
        {
            base.WriteLine(value);
            OnStringWritten(value?.ToString() + NewLine);
        }
        public override void WriteLine(string format, object? arg0)
        {
            base.WriteLine(format, arg0);
            OnStringWritten(string.Format(format, arg0));
        }
        public override void WriteLine(string format, object? arg0, object? arg1)
        {
            base.WriteLine(format, arg0, arg1);
            OnStringWritten(string.Format(format, arg0, arg1));
        }
        public override void WriteLine(string format, object? arg0, object? arg1, object? arg2)
        {
            base.WriteLine(format, arg0, arg1, arg2);
            OnStringWritten(string.Format(format, arg0, arg1, arg2));
        }
        public override void WriteLine(string format, params object?[] arg)
        {
            base.WriteLine(format, arg);
            OnStringWritten(string.Format(format, arg));
        }
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            OnStringWritten(value + NewLine);
        }
        public override void WriteLine(uint value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override void WriteLine(ulong value)
        {
            base.WriteLine(value);
            OnStringWritten(value.ToString() + NewLine);
        }
        public override Task WriteAsync(char value) => base.WriteAsync(value);
        public override Task WriteAsync(char[] buffer, int index, int count) => base.WriteAsync(buffer, index, count);
        public override Task WriteAsync(string? value) => base.WriteAsync(value);
        public override Task WriteLineAsync() => base.WriteLineAsync();
        public override Task WriteLineAsync(char value) => base.WriteLineAsync(value);
        public override Task WriteLineAsync(char[] buffer, int index, int count) => base.WriteLineAsync(buffer, index, count);
        public override Task WriteLineAsync(string? value) => base.WriteLineAsync(value);
    }
}
