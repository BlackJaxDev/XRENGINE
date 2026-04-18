using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Extensions
{
    public static partial class StringExtensions
    {
        public static bool EndsWithAny(this string s, string[] values, StringComparison comparisonType)
        {
            foreach (string value in values)
                if (s.EndsWith(value, comparisonType))
                    return true;
            return false;
        }
        public static string GetExtensionLowercase(this string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string ext = Path.GetExtension(path);
            if (ext.StartsWith("."))
                ext = ext[1..];

            return ext.ToLowerInvariant();
        }
        public static bool IsAbsolutePath(this string path)
            => path.IsValidPath()
                && Path.IsPathRooted(path)
                && (!Path.GetPathRoot(path)?.Equals(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ?? false);
        public static bool IsValidPath(this string path)
            => !string.IsNullOrWhiteSpace(path) && path.IndexOfAny([.. Path.GetInvalidPathChars()]) == -1;
        public static bool StartsWithDirectorySeparator(this string str)
            => !string.IsNullOrEmpty(str) && str[0] == Path.DirectorySeparatorChar;
        public static bool EndsWithDirectorySeparator(this string str)
            => !string.IsNullOrEmpty(str) && str[^1] == Path.DirectorySeparatorChar;
        public static string SplitCamelCase(this string str)
            => MyRegex().Replace(MyRegex1().Replace(str, "$1 $2"), "$1 $2");
        public static bool IsValidExistingPath(this string path)
            => path.IsExistingDirectoryPath() != null;
        /// <summary>
        /// Determines the type of this path.
        /// <see langword="true"/> is a directory,
        /// <see langword="false"/> is a file,
        /// and <see langword="null"/> means the path is not valid.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static bool? IsExistingDirectoryPath(this string path)
        {
            //if (string.IsNullOrWhiteSpace(path))
            //    return null;
            //char[] invalid = { '<', '>', /*':',*/ '"', /*'\\', '/',*/ '|', '?', '*' };
            //if (path.IndexOfAny(invalid) >= 0)
            //    return null;
            //if (path.EndsWith(".") || path.EndsWith(" "))
            //    return null;
            if (Directory.Exists(path)) return true; //Is a folder 
            if (File.Exists(path)) return false; //Is a file
            return null; //Path is invalid
        }
        public static bool Equals(this string str, string other, bool ignoreCase)
            => string.Equals(str, other, ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture);
        public static decimal ParseInvariantDecimal(this string str)
            => decimal.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static float ParseInvariantFloat(this string str)
            => float.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static double ParseInvariantDouble(this string str)
            => double.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static sbyte ParseInvariantSByte(this string str)
            => sbyte.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static byte ParseInvariantByte(this string str)
            => byte.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static short ParseInvariantShort(this string str)
            => short.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static ushort ParseInvariantUShort(this string str)
            => ushort.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static int ParseInvariantInt(this string str)
            => int.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static uint ParseInvariantUInt(this string str)
            => uint.Parse(str, CultureInfo.InvariantCulture.NumberFormat);
        public static bool EqualsOrdinalIgnoreCase(this string str, string other)
            => str.Equals(other, StringComparison.OrdinalIgnoreCase);
        public static bool EqualsOrdinal(this string str, string other)
            => str.Equals(other, StringComparison.Ordinal);
        public static bool EqualsInvariantIgnoreCase(this string str, string other)
            => str.Equals(other, StringComparison.InvariantCultureIgnoreCase);
        public static bool EqualsInvariant(this string str, string other)
            => str.Equals(other, StringComparison.InvariantCulture);
        //public static bool IsNullOrEmpty(this string str)
        //{
        //    return string.IsNullOrEmpty(str);
        //}
        public static string MakeAbsolutePathRelativeTo(this string mainPath, string otherPath)
        {
            if (mainPath.StartsWith("file:///"))
                mainPath = mainPath.Remove(0, 8);
            if (otherPath.StartsWith("file:///"))
                otherPath = otherPath.Remove(0, 8);
            string[] mainParts = Path.GetFullPath(mainPath).Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string[] otherParts = Path.GetFullPath(otherPath).Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            
            int mainLen = mainParts.Length;
            string fileName = mainParts[^1];
            if (fileName.Contains('.'))
                --mainLen;
            else
                fileName = "";

            //Find the first folder that does not match between the two paths
            int bias;
            for (bias = 0; bias < Math.Min(mainLen, otherParts.Length); ++bias)
                if (!mainParts[bias].Equals(otherParts[bias], StringComparison.InvariantCulture))
                    break;

            string newDir = string.Empty;
            for (int i = bias; i < otherParts.Length; ++i)
                newDir += Path.DirectorySeparatorChar + "..";
            for (int i = bias; i < mainLen; ++i)
                newDir += Path.DirectorySeparatorChar + mainParts[i];

            if (!string.IsNullOrEmpty(newDir))
                return newDir + Path.DirectorySeparatorChar.ToString() + fileName;
            else
                return fileName;
        }

        /// <summary>
        /// Parses the given string as an enum of the given type.
        /// </summary>
        public static T AsEnum<T>(this string s) where T : struct
            => Enum.Parse<T>(s);

        public static string ReplaceWhitespace(this string input, string replacement)
            => WhitespaceMatcher().Replace(input, replacement);

        public static unsafe nint Write(this string s, nint addr, bool nullTerminate)
        {
            sbyte* dPtr = (sbyte*)addr;
            foreach (char c in s)
                *dPtr++ = (sbyte)c;
            if (nullTerminate)
                *dPtr++ = 0;
            return (nint)dPtr;
        }
        public static unsafe void Write(this string s, ref nint addr, bool nullTerminate)
        {
            sbyte* dPtr = (sbyte*)addr;
            foreach (char c in s)
                *dPtr++ = (sbyte)c;
            if (nullTerminate)
                *dPtr++ = 0;
            addr = (nint)dPtr;
        }
        public static unsafe void Write(this string s, ref sbyte* addr, bool nullTerminate)
        {
            foreach (char c in s)
                *addr++ = (sbyte)c;
            if (nullTerminate)
                *addr++ = 0;
        }
        public static unsafe void Write(this string s, sbyte* addr, bool nullTerminate)
        {
            foreach (char c in s)
                *addr++ = (sbyte)c;
            if (nullTerminate)
                *addr++ = 0;
        }
        public static unsafe void Write(this string s, ref sbyte* addr, int maxLength, bool nullTerminate)
        {
            for (int i = 0; i < Math.Max(s.Length, maxLength); ++i)
                *addr++ = (sbyte)s[i];
            if (nullTerminate)
                *addr++ = 0;
        }
        public static unsafe void Write(this string s, sbyte* addr, int maxLength, bool nullTerminate)
        {
            for (int i = 0; i < Math.Max(s.Length, maxLength); ++i)
                *addr++ = (sbyte)s[i];
            if (nullTerminate)
                *addr++ = 0;
        }
        public static unsafe void Write(this string s, ref byte* addr, Encoding encoding)
        {
            byte[] bytes = encoding.GetBytes(s);
            foreach (byte b in bytes)
                *addr++ = b;
        }
        public static unsafe int Write(this string s, byte* addr, Encoding encoding)
        {
            byte[] bytes = encoding.GetBytes(s);
            foreach (byte b in bytes)
                *addr++ = b;
            return bytes.Length;
        }
        public static unsafe void Write(this string s, ref nint addr, Encoding encoding)
        {
            byte* bAddr = (byte*)addr;
            byte[] bytes = encoding.GetBytes(s);
            foreach (byte b in bytes)
                *bAddr++ = b;
        }
        public static unsafe int Write(this string s, nint addr, Encoding encoding)
        {
            byte* bAddr = (byte*)addr;
            byte[] bytes = encoding.GetBytes(s);
            foreach (byte b in bytes)
                *bAddr++ = b;
            return bytes.Length;
        }
        /// <summary>
        /// Finds the first instance that is not the character passed.
        /// </summary>
        public static int FindFirstNot(this string str, int begin, char chr)
        {
            for (int i = begin; i < str.Length; ++i)
                if (str[i] != chr)
                    return i;
            return -1;
        }
        /// <summary>
        /// Finds the first instance that is the character passed.
        /// </summary>
        public static int FindFirst(this string str, int begin, char chr)
        {
            for (int i = begin; i < str.Length; ++i)
                if (str[i] == chr)
                    return i;
            return -1;
        }
        /// <summary>
        /// Finds the first instance that is the character passed.
        /// </summary>
        public static int FindFirst(this string str, int begin, Predicate<char> matchPredicate)
        {
            for (int i = begin; i < str.Length; ++i)
                if (matchPredicate(str[i]))
                    return i;
            return -1;
        }
        /// <summary>
        /// Finds the first instance that is the string passed, searching backward in the string.
        /// </summary>
        public static int FindFirst(this string str, int begin, string searchStr)
        {
            int firstIndex = 0;
            for (int i = begin; i < str.Length; ++i)
            {
                bool found = true;
                firstIndex = i;
                for (int x = 0; x < searchStr.Length && i < str.Length; ++x, ++i)
                {
                    if (str[i] != searchStr[x])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                    return firstIndex;
            }
            return -1;
        }

        #region Find Occurrence
        /// <summary>
        /// Finds a specific instance that is the character passed.
        /// </summary>
        public static int FindOccurence(this string str, int begin, int occurrenceIndex, char chr)
        {
            int occurrence = 0;
            for (int i = begin; i < str.Length; ++i)
                if (str[i] == chr)
                {
                    if (occurrenceIndex == occurrence)
                        return i;
                    ++occurrence;
                }
            return -1;
        }
        /// <summary>
        /// Finds a specific instance that is the character passed.
        /// </summary>
        public static int FindOccurenceNot(this string str, int begin, int occurrenceIndex, char chr)
        {
            int occurrence = 0;
            for (int i = begin; i < str.Length; ++i)
                if (str[i] != chr)
                {
                    if (occurrenceIndex == occurrence)
                        return i;
                    ++occurrence;
                }
            return -1;
        }
        /// <summary>
        /// Finds the first instance that is the string passed, searching forward in the string.
        /// </summary>
        public static int FindOccurrence(this string str, int begin, int occurrenceIndex, string searchStr)
        {
            int occurrence = 0;
            int offset = 0;
            for (int i = begin; i < str.Length; ++i)
            {
                bool found = true;
                offset = i;
                for (int x = 0; x < searchStr.Length && i < str.Length; ++x, ++i)
                {
                    if (str[i] != searchStr[x])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    if (occurrenceIndex == occurrence)
                        return offset;
                    ++occurrence;
                }
            }
            return -1;
        }
        /// <summary>
        /// Finds all indices of the chars passed.
        /// </summary>
        public static int[] FindAllOccurrences(this string str, int firstIndex, int lastIndex, bool parallelSearch, params char[] searchChars)
        {
            if (parallelSearch)
            {
                ConcurrentBag<int> bag = new();
                Parallel.For(firstIndex, lastIndex + 1, i =>
                {
                    if (searchChars.Any(x => x == str[i]))
                        bag.Add(i);
                });
                int[] array = bag.ToArray();
                Array.Sort(array);
                return array;
            }
            else
            {
                List<int> o = new();
                for (int i = firstIndex; i <= lastIndex; ++i)
                    if (searchChars.Any(x => x == str[i]))
                        o.Add(i);
                return o.ToArray();
            }
        }
        /// <summary>
        /// Finds the first instance that is the string passed, searching forward in the string.
        /// </summary>
        public static int[] FindAllOccurrences(this string str, int begin, string searchStr)
        {
            List<int> o = new();
            int firstIndex = 0;
            bool found;
            for (int i = begin; i < str.Length; ++i)
            {
                found = true;
                firstIndex = i;
                for (int x = 0; x < searchStr.Length && i < str.Length; ++x, ++i)
                {
                    if (str[i] != searchStr[x])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                    o.Add(firstIndex);
            }
            return o.ToArray();
        }
        /// <summary>
        /// Finds the first instance that is the string passed, searching backward in the string.
        /// </summary>
        public static int FindOccurrenceReverse(this string str, int begin, int occurrenceIndex, string searchStr)
        {
            int occurrence = 0;
            for (int i = begin; i >= 0; --i)
            {
                bool found = true;
                for (int x = searchStr.Length - 1; x >= 0 && i >= 0; --x, --i)
                {
                    if (str[i] != searchStr[x])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    if (occurrenceIndex == occurrence)
                        return i + 1;
                    ++occurrence;
                }
            }
            return -1;
        }
        #endregion

        #region Find First Reverse
        /// <summary>
        /// Finds the first instance that is the character passed, searching backward in the string.
        /// </summary>
        public static int FindFirstReverse(this string str, char chr)
        {
            return str.FindFirstReverse(str.Length - 1, chr);
        }
        /// <summary>
        /// Finds the first instance that is the string passed, searching backward in the string.
        /// </summary>
        public static int FindFirstReverse(this string str, string searchStr)
        {
            return str.FindFirstReverse(str.Length - 1, searchStr);
        }
        /// <summary>
        /// Finds the first instance that is the character passed, searching backward in the string.
        /// </summary>
        public static int FindFirstReverse(this string str, int begin, char chr)
        {
            for (int i = begin; i >= 0; --i)
                if (str[i] == chr)
                    return i;
            return -1;
        }
        /// <summary>
        /// Finds the first instance that is the string passed, searching backward in the string.
        /// </summary>
        public static int FindFirstReverse(this string str, int begin, string searchStr)
        {
            for (int i = begin; i >= 0; --i)
            {
                bool found = true;
                for (int x = searchStr.Length - 1; x >= 0 && i >= 0; --x, --i)
                {
                    if (str[i] != searchStr[x])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                    return i + 1;
            }
            return -1;
        }
        /// <summary>
        /// Finds the first instance that is not the character passed, searching backward in the string.
        /// </summary>
        public static int FindFirstNotReverse(this string str, int begin, char chr)
        {
            for (int i = begin; i >= 0; --i)
                if (str[i] != chr)
                    return i;
            return -1;
        }
        #endregion

        /// <summary>
        /// Prints this string to the engine's output logs and moves to the next line.
        /// </summary>
        /// <param name="str">The string to be printed.</param>
        /// <param name="args">Arguments for string.Format().</param>
        public static void PrintLine(this string str, params object[] args)
            => Debug.Print(args.Length == 0 ? str : string.Format(str, args));

        [GeneratedRegex("(\\p{Ll})(\\P{Ll})")]
        private static partial Regex MyRegex();
        [GeneratedRegex("(\\P{Ll})(\\P{Ll}\\p{Ll})")]
        private static partial Regex MyRegex1();
        [GeneratedRegex("\\s+")]
        private static partial Regex WhitespaceMatcher();
    }
}
