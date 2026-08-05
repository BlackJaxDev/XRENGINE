using System.Globalization;
using XREngine.Data;

namespace XREngine.Components
{
    internal sealed class Audio2Face3DAnimation
    {
        private readonly float[] _timecodes;
        private readonly float[][] _blendshapeFrames;
        private readonly float[][]? _emotionFrames;

        private Audio2Face3DAnimation(string[] blendshapeNames, string[] emotionNames, float[] timecodes, float[][] blendshapeFrames, float[][]? emotionFrames)
        {
            BlendshapeNames = blendshapeNames;
            EmotionNames = emotionNames;
            _timecodes = timecodes;
            _blendshapeFrames = blendshapeFrames;
            _emotionFrames = emotionFrames;
        }

        public string[] BlendshapeNames { get; }
        public string[] EmotionNames { get; }
        public int FrameCount => _timecodes.Length;
        public int EmotionCount => EmotionNames.Length;
        public float Duration => FrameCount == 0 ? 0.0f : _timecodes[^1];

        public static Audio2Face3DAnimation Parse(string csvText)
        {
            if (!TryParse(csvText, out Audio2Face3DAnimation? animation, out string? error) || animation is null)
                throw new FormatException(error ?? "Invalid Audio2Face-3D CSV.");

            return animation;
        }

        public static bool TryParse(string csvText, out Audio2Face3DAnimation? animation, out string? error)
        {
            animation = null;
            error = null;

            if (string.IsNullOrWhiteSpace(csvText))
            {
                error = "CSV is empty.";
                return false;
            }

            string[] lines = csvText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
            {
                error = "CSV must contain a header and at least one frame.";
                return false;
            }

            string[] headerColumns = lines[0].Split(',', StringSplitOptions.TrimEntries);
            if (headerColumns.Length < 2)
            {
                error = "CSV header must contain a timecode column and at least one blendshape or emotion column.";
                return false;
            }

            if (!string.Equals(headerColumns[0], "timecode", StringComparison.OrdinalIgnoreCase))
            {
                error = "CSV header must start with 'timecode'.";
                return false;
            }

            var blendshapeNames = new List<string>(headerColumns.Length - 1);
            var blendshapeColumnIndices = new List<int>(headerColumns.Length - 1);
            bool[] activeEmotionColumns = new bool[Audio2Face3DRegistry.Count];
            int[] emotionColumnIndices = new int[headerColumns.Length];
            Array.Fill(emotionColumnIndices, -1);

            for (int columnIndex = 1; columnIndex < headerColumns.Length; columnIndex++)
            {
                string columnName = headerColumns[columnIndex];
                if (Audio2Face3DRegistry.TryGetIndex(columnName, out int emotionIndex))
                {
                    if (activeEmotionColumns[emotionIndex])
                    {
                        error = $"CSV emotion column '{columnName}' is duplicated.";
                        return false;
                    }

                    activeEmotionColumns[emotionIndex] = true;
                    emotionColumnIndices[columnIndex] = emotionIndex;
                }
                else
                {
                    blendshapeNames.Add(columnName);
                    blendshapeColumnIndices.Add(columnIndex);
                }
            }

            int emotionCount = 0;
            for (int i = 0; i < activeEmotionColumns.Length; i++)
            {
                if (activeEmotionColumns[i])
                    emotionCount++;
            }

            if (blendshapeNames.Count == 0 && emotionCount == 0)
            {
                error = "CSV header must contain at least one blendshape or emotion column.";
                return false;
            }

            var timecodes = new List<float>(lines.Length - 1);
            var blendshapeFrames = new List<float[]>(lines.Length - 1);
            List<float[]>? emotionFrames = emotionCount == 0 ? null : new List<float[]>(lines.Length - 1);
            float previousTime = float.NegativeInfinity;

            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                string[] values = lines[lineIndex].Split(',', StringSplitOptions.TrimEntries);
                if (values.Length != headerColumns.Length)
                {
                    error = $"Line {lineIndex + 1} expected {headerColumns.Length} columns but found {values.Length}.";
                    return false;
                }

                if (!float.TryParse(values[0], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float timecode))
                {
                    error = $"Line {lineIndex + 1} has an invalid timecode '{values[0]}'.";
                    return false;
                }

                if (timecode < previousTime)
                {
                    error = $"Line {lineIndex + 1} timecode {timecode} is earlier than the previous frame time {previousTime}.";
                    return false;
                }

                float[] blendshapeFrame = new float[blendshapeNames.Count];
                float[]? emotionFrame = emotionFrames is null ? null : new float[Audio2Face3DRegistry.Count];
                for (int columnIndex = 1; columnIndex < values.Length; columnIndex++)
                {
                    if (!float.TryParse(values[columnIndex], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float weight))
                    {
                        error = $"Line {lineIndex + 1} column '{headerColumns[columnIndex]}' has an invalid float '{values[columnIndex]}'.";
                        return false;
                    }

                    int emotionIndex = emotionColumnIndices[columnIndex];
                    if (emotionIndex >= 0)
                    {
                        emotionFrame![emotionIndex] = weight;
                    }
                    else
                    {
                        int blendshapeIndex = blendshapeColumnIndices.IndexOf(columnIndex);
                        blendshapeFrame[blendshapeIndex] = weight;
                    }
                }

                previousTime = timecode;
                timecodes.Add(timecode);
                blendshapeFrames.Add(blendshapeFrame);
                emotionFrames?.Add(emotionFrame!);
            }

            if (timecodes.Count == 0)
            {
                error = "CSV did not contain any animation frames.";
                return false;
            }

            string[] emotionNames = emotionCount == 0
                ? []
                : [.. Audio2Face3DRegistry.Names.Where((_, emotionIndex) => activeEmotionColumns[emotionIndex])];

            animation = new Audio2Face3DAnimation([.. blendshapeNames], emotionNames, [.. timecodes], [.. blendshapeFrames], emotionFrames is null ? null : [.. emotionFrames]);
            return true;
        }

        public void Sample(float timecode, float[] output)
        {
            if (output.Length != BlendshapeNames.Length)
                throw new ArgumentException("Output buffer length must match blendshape count.", nameof(output));

            if (FrameCount == 0)
            {
                Array.Clear(output, 0, output.Length);
                return;
            }

            if (FrameCount == 1 || timecode <= _timecodes[0])
            {
                CopyFrame(0, output);
                return;
            }

            if (timecode >= _timecodes[^1])
            {
                CopyFrame(FrameCount - 1, output);
                return;
            }

            int frameIndex = Array.BinarySearch(_timecodes, timecode);
            if (frameIndex >= 0)
            {
                CopyFrame(frameIndex, output);
                return;
            }

            int nextIndex = ~frameIndex;
            int previousIndex = Math.Max(0, nextIndex - 1);
            float startTime = _timecodes[previousIndex];
            float endTime = _timecodes[nextIndex];
            float factor = endTime <= startTime
                ? 0.0f
                : Math.Clamp((timecode - startTime) / (endTime - startTime), 0.0f, 1.0f);

            float[] previousFrame = _blendshapeFrames[previousIndex];
            float[] nextFrame = _blendshapeFrames[nextIndex];
            for (int i = 0; i < output.Length; i++)
                output[i] = Interp.Lerp(previousFrame[i], nextFrame[i], factor);
        }

        public void SampleEmotions(float timecode, float[] output)
        {
            if (output.Length != Audio2Face3DRegistry.Count)
                throw new ArgumentException("Emotion output buffer length must match the supported Audio2Emotion channel count.", nameof(output));

            if (_emotionFrames is null || FrameCount == 0)
            {
                Array.Clear(output, 0, output.Length);
                return;
            }

            if (FrameCount == 1 || timecode <= _timecodes[0])
            {
                CopyEmotionFrame(0, output);
                return;
            }

            if (timecode >= _timecodes[^1])
            {
                CopyEmotionFrame(FrameCount - 1, output);
                return;
            }

            int frameIndex = Array.BinarySearch(_timecodes, timecode);
            if (frameIndex >= 0)
            {
                CopyEmotionFrame(frameIndex, output);
                return;
            }

            int nextIndex = ~frameIndex;
            int previousIndex = Math.Max(0, nextIndex - 1);
            float startTime = _timecodes[previousIndex];
            float endTime = _timecodes[nextIndex];
            float factor = endTime <= startTime
                ? 0.0f
                : Math.Clamp((timecode - startTime) / (endTime - startTime), 0.0f, 1.0f);

            float[] previousFrame = _emotionFrames[previousIndex];
            float[] nextFrame = _emotionFrames[nextIndex];
            for (int i = 0; i < output.Length; i++)
                output[i] = Interp.Lerp(previousFrame[i], nextFrame[i], factor);
        }

        private void CopyFrame(int frameIndex, float[] output)
            => Array.Copy(_blendshapeFrames[frameIndex], output, output.Length);

        private void CopyEmotionFrame(int frameIndex, float[] output)
            => Array.Copy(_emotionFrames![frameIndex], output, output.Length);
    }
}
