using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering;

/// <summary>
/// Performs conservative dead-code elimination over snippet regions in a fully
/// resolved GLSL source. Snippet regions are identified by the standard
/// <c>// ===== BEGIN SNIPPET: name =====</c> / <c>// ===== END SNIPPET: name =====</c>
/// markers emitted by <see cref="ShaderSourceResolver"/>.
///
/// Only top-level declarations (functions, function prototypes, uniforms,
/// uniform/buffer interface blocks, in/out/const variables, structs, and
/// <c>#define</c> directives) inside snippet regions are eligible for
/// removal. Anything outside snippet regions is preserved verbatim.
///
/// Liveness propagation seeds from identifiers referenced in the consumer
/// source (text outside any snippet region) plus all preprocessor directives
/// (which are always preserved). Declarations whose declared names appear in
/// the live set are kept and contribute their referenced identifiers back to
/// the live set, transitively.
///
/// The parser is intentionally conservative: when a chunk cannot be cleanly
/// classified, it is treated as live so it is preserved.
/// </summary>
internal static partial class GlslSnippetDeadCodeEliminator
{
    [GeneratedRegex(@"^[ \t]*//[ \t]*=====[ \t]*BEGIN[ \t]+(?:SNIPPET|INCLUDE):[ \t]*(?<name>[^=\r\n]+?)[ \t]*=+[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex BeginSnippetRegex();

    [GeneratedRegex(@"^[ \t]*//[ \t]*=====[ \t]*END[ \t]+(?:SNIPPET|INCLUDE):[ \t]*(?<name>[^=\r\n]+?)[ \t]*=+[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex EndSnippetRegex();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled)]
    private static partial Regex IdentifierRegex();

    /// <summary>
    /// GLSL keywords / built-in types and functions whose presence as identifiers
    /// must never count as a reference (they would never name a snippet decl).
    /// Excluding them keeps the live set tight and avoids spurious retention.
    /// </summary>
    private static readonly HashSet<string> GlslReserved = new(StringComparer.Ordinal)
    {
        // basic types
        "void","bool","int","uint","float","double",
        "vec2","vec3","vec4","ivec2","ivec3","ivec4","uvec2","uvec3","uvec4","bvec2","bvec3","bvec4",
        "dvec2","dvec3","dvec4",
        "mat2","mat3","mat4","mat2x2","mat2x3","mat2x4","mat3x2","mat3x3","mat3x4","mat4x2","mat4x3","mat4x4",
        "dmat2","dmat3","dmat4",
        // sampler types
        "sampler1D","sampler2D","sampler3D","samplerCube","sampler2DArray","samplerCubeArray",
        "sampler1DShadow","sampler2DShadow","samplerCubeShadow","sampler2DArrayShadow","samplerCubeArrayShadow",
        "sampler2DMS","sampler2DMSArray",
        "isampler1D","isampler2D","isampler3D","isamplerCube","isampler2DArray","isamplerCubeArray","isampler2DMS","isampler2DMSArray",
        "usampler1D","usampler2D","usampler3D","usamplerCube","usampler2DArray","usamplerCubeArray","usampler2DMS","usamplerCubeArray","usampler2DMSArray",
        "samplerBuffer","isamplerBuffer","usamplerBuffer",
        "image1D","image2D","image3D","imageCube","image2DArray","imageCubeArray","image2DMS","image2DMSArray","imageBuffer",
        "iimage1D","iimage2D","iimage3D","iimageCube","iimage2DArray","iimageCubeArray","iimageBuffer",
        "uimage1D","uimage2D","uimage3D","uimageCube","uimage2DArray","uimageCubeArray","uimageBuffer",
        // qualifiers / storage
        "in","out","inout","uniform","buffer","shared","const","volatile","restrict","readonly","writeonly",
        "centroid","sample","patch","smooth","flat","noperspective","invariant","precise",
        "layout","location","binding","offset","std140","std430","packed","shared","row_major","column_major",
        "highp","mediump","lowp","precision",
        "attribute","varying", // legacy
        "struct","return","if","else","for","while","do","switch","case","default","break","continue","discard","true","false",
        // common builtins
        "main","gl_Position","gl_FragCoord","gl_FragDepth","gl_PointSize","gl_VertexID","gl_InstanceID","gl_PrimitiveID",
        "gl_Layer","gl_ViewportIndex","gl_FrontFacing","gl_PointCoord","gl_SampleID","gl_SamplePosition","gl_SampleMaskIn","gl_SampleMask",
        "gl_ClipDistance","gl_CullDistance","gl_TessCoord","gl_TessLevelOuter","gl_TessLevelInner",
        "gl_in","gl_out","gl_PerVertex","gl_NumWorkGroups","gl_WorkGroupID","gl_LocalInvocationID","gl_GlobalInvocationID","gl_LocalInvocationIndex","gl_WorkGroupSize",
        // common builtin functions (subset; missing ones will just inflate live set harmlessly)
        "abs","acos","acosh","all","any","asin","asinh","atan","atanh","barrier","bitCount","bitfieldExtract","bitfieldInsert","bitfieldReverse",
        "ceil","clamp","cos","cosh","cross","degrees","determinant","dFdx","dFdy","dFdxFine","dFdyFine","dFdxCoarse","dFdyCoarse","fwidth",
        "distance","dot","equal","exp","exp2","faceforward","findLSB","findMSB","floatBitsToInt","floatBitsToUint","floor","fma","fract",
        "frexp","greaterThan","greaterThanEqual","groupMemoryBarrier","imageAtomicAdd","imageAtomicAnd","imageAtomicCompSwap","imageAtomicExchange",
        "imageAtomicMax","imageAtomicMin","imageAtomicOr","imageAtomicXor","imageLoad","imageSize","imageStore","imageSamples",
        "intBitsToFloat","interpolateAtCentroid","interpolateAtOffset","interpolateAtSample","inverse","inversesqrt","isinf","isnan","ldexp",
        "length","lessThan","lessThanEqual","log","log2","matrixCompMult","max","memoryBarrier","memoryBarrierAtomicCounter","memoryBarrierBuffer",
        "memoryBarrierImage","memoryBarrierShared","min","mix","mod","modf","noise","normalize","not","notEqual","outerProduct",
        "packDouble2x32","packHalf2x16","packSnorm2x16","packSnorm4x8","packUnorm2x16","packUnorm4x8","pow","radians","reflect","refract",
        "round","roundEven","sign","sin","sinh","smoothstep","sqrt","step","tan","tanh","texelFetch","texelFetchOffset",
        "texture","textureGather","textureGatherOffset","textureGatherOffsets","textureGrad","textureGradOffset","textureLod","textureLodOffset",
        "textureOffset","textureProj","textureProjGrad","textureProjGradOffset","textureProjLod","textureProjLodOffset","textureProjOffset",
        "textureQueryLevels","textureQueryLod","textureSamples","textureSize","transpose","trunc","uaddCarry","uintBitsToFloat","umulExtended","unpackDouble2x32",
        "unpackHalf2x16","unpackSnorm2x16","unpackSnorm4x8","unpackUnorm2x16","unpackUnorm4x8","usubBorrow",
        "atomicCounter","atomicCounterIncrement","atomicCounterDecrement","atomicAdd","atomicAnd","atomicOr","atomicXor","atomicMin","atomicMax","atomicExchange","atomicCompSwap",
        // engine-emitted markers
        "version","extension","define","undef","ifdef","ifndef","endif","pragma","include","line","error",
    };

    /// <summary>
    /// Trims unused declarations inside snippet regions of the given source.
    /// Returns the original source unchanged if no snippet regions are found
    /// or if parsing fails.
    /// </summary>
    public static string Trim(string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;

        try
        {
            return TrimInternal(source);
        }
        catch
        {
            // Conservative: never break shader compilation by aborting.
            return source;
        }
    }

    public static string StripRegionMarkers(string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;

        try
        {
            string stripped = BeginSnippetRegex().Replace(source, string.Empty);
            return EndSnippetRegex().Replace(stripped, string.Empty);
        }
        catch
        {
            return source;
        }
    }

    private static string TrimInternal(string source)
    {
        // 1) Locate snippet regions.
        List<SnippetRegion> regions = FindSnippetRegions(source);
        if (regions.Count == 0)
            return source;

        // 2) Build a comment/string-masked copy for parsing (positions preserved).
        string masked = MaskCommentsAndStrings(source);

        // 3) Parse each region into chunks.
        List<Chunk> allChunks = [];
        foreach (SnippetRegion region in regions)
            ParseChunks(source, masked, region.ContentStart, region.ContentEnd, region.Index, allChunks);

        if (allChunks.Count == 0)
            return source;

        // 4) Collect identifier seeds from non-snippet text and from preprocessor chunks.
        HashSet<string> live = new(StringComparer.Ordinal);

        // Walk through the source, skipping snippet content but including marker lines.
        // For non-snippet ranges we use the masked text to extract identifiers.
        int cursor = 0;
        foreach (SnippetRegion region in regions)
        {
            if (region.RegionStart > cursor)
                AddIdentifiers(masked, cursor, region.RegionStart, live);
            cursor = region.RegionEnd;
        }
        if (cursor < masked.Length)
            AddIdentifiers(masked, cursor, masked.Length, live);

        // Preprocessor chunks inside regions also seed liveness.
        foreach (Chunk chunk in allChunks)
        {
            if (chunk.IsPreprocessor)
                AddIdentifiers(masked, chunk.Start, chunk.End, live);
        }

        // 5) Index decl chunks by name.
        Dictionary<string, List<Chunk>> chunksByName = new(StringComparer.Ordinal);
        foreach (Chunk chunk in allChunks)
        {
            if (chunk.IsPreprocessor || chunk.Unparseable)
                continue;

            foreach (string name in chunk.DeclaredNames)
            {
                if (!chunksByName.TryGetValue(name, out List<Chunk>? list))
                {
                    list = [];
                    chunksByName[name] = list;
                }
                list.Add(chunk);
            }
        }

        // Unparseable chunks are always live (conservative).
        Queue<Chunk> propagate = new();
        foreach (Chunk chunk in allChunks)
        {
            if (chunk.Unparseable && !chunk.Live)
            {
                chunk.Live = true;
                propagate.Enqueue(chunk);
            }
        }

        // 6) Seed initial live chunks from name set.
        foreach (string name in live)
        {
            if (chunksByName.TryGetValue(name, out List<Chunk>? list))
            {
                foreach (Chunk c in list)
                {
                    if (!c.Live)
                    {
                        c.Live = true;
                        propagate.Enqueue(c);
                    }
                }
            }
        }

        // 7) Propagate liveness through references.
        while (propagate.Count > 0)
        {
            Chunk c = propagate.Dequeue();
            foreach (string referenced in c.ReferencedNames)
            {
                if (live.Add(referenced) && chunksByName.TryGetValue(referenced, out List<Chunk>? list))
                {
                    foreach (Chunk dep in list)
                    {
                        if (!dep.Live)
                        {
                            dep.Live = true;
                            propagate.Enqueue(dep);
                        }
                    }
                }
            }
        }

        // 8) Rewrite: keep non-snippet text as-is; inside snippet regions emit live + preprocessor + non-decl text.
        StringBuilder output = new(source.Length);
        cursor = 0;

        // Order chunks per region.
        Dictionary<int, List<Chunk>> chunksByRegion = [];
        foreach (Chunk chunk in allChunks)
        {
            if (!chunksByRegion.TryGetValue(chunk.RegionIndex, out List<Chunk>? list))
            {
                list = [];
                chunksByRegion[chunk.RegionIndex] = list;
            }
            list.Add(chunk);
        }

        for (int i = 0; i < regions.Count; i++)
        {
            SnippetRegion region = regions[i];
            output.Append(source, cursor, region.RegionStart - cursor);

            // Emit BEGIN marker line (region.RegionStart .. region.ContentStart).
            output.Append(source, region.RegionStart, region.ContentStart - region.RegionStart);

            // Walk content range emitting kept chunks and inter-chunk text.
            int contentCursor = region.ContentStart;
            if (chunksByRegion.TryGetValue(region.Index, out List<Chunk>? regionChunks))
            {
                foreach (Chunk chunk in regionChunks)
                {
                    if (chunk.Start > contentCursor)
                        output.Append(source, contentCursor, chunk.Start - contentCursor);

                    bool keep = chunk.IsPreprocessor || chunk.Unparseable || chunk.Live;
                    if (keep)
                        output.Append(source, chunk.Start, chunk.End - chunk.Start);
                    contentCursor = chunk.End;
                }
            }
            if (contentCursor < region.ContentEnd)
                output.Append(source, contentCursor, region.ContentEnd - contentCursor);

            // Emit END marker line.
            output.Append(source, region.ContentEnd, region.RegionEnd - region.ContentEnd);

            cursor = region.RegionEnd;
        }
        if (cursor < source.Length)
            output.Append(source, cursor, source.Length - cursor);

        return output.ToString();
    }

    // ---------------------------------------------------------------------
    // Snippet region discovery
    // ---------------------------------------------------------------------
    private sealed class SnippetRegion
    {
        public int Index;
        public string Name = string.Empty;
        public int RegionStart;     // start of BEGIN line
        public int ContentStart;    // first char after BEGIN line newline
        public int ContentEnd;      // start of END line
        public int RegionEnd;       // end of END line (incl. trailing newline if any)
    }

    private static List<SnippetRegion> FindSnippetRegions(string source)
    {
        List<SnippetRegion> regions = [];
        MatchCollection begins = BeginSnippetRegex().Matches(source);
        if (begins.Count == 0)
            return regions;

        MatchCollection ends = EndSnippetRegex().Matches(source);
        // Match begins to nearest forward end with same name; allow nesting.
        Stack<(string Name, int BeginIndex, int ContentStart, int RegionStart)> stack = new();
        int ei = 0;
        int idxCounter = 0;
        Match[] beginArr = begins.Cast<Match>().ToArray();
        Match[] endArr = ends.Cast<Match>().ToArray();
        int bi = 0;

        while (bi < beginArr.Length || ei < endArr.Length)
        {
            bool takeBegin = ei >= endArr.Length || (bi < beginArr.Length && beginArr[bi].Index < endArr[ei].Index);
            if (takeBegin)
            {
                Match m = beginArr[bi++];
                int regionStart = m.Index;
                int contentStart = m.Index + m.Length;
                if (contentStart < source.Length && source[contentStart] == '\r')
                    contentStart++;
                if (contentStart < source.Length && source[contentStart] == '\n')
                    contentStart++;
                stack.Push((m.Groups["name"].Value.Trim(), idxCounter++, contentStart, regionStart));
            }
            else
            {
                Match m = endArr[ei++];
                if (stack.Count == 0)
                    continue;
                var top = stack.Pop();
                int contentEnd = m.Index;
                int regionEnd = m.Index + m.Length;
                if (regionEnd < source.Length && source[regionEnd] == '\r')
                    regionEnd++;
                if (regionEnd < source.Length && source[regionEnd] == '\n')
                    regionEnd++;
                // We only DCE the OUTERMOST regions to avoid double-trimming nested.
                if (stack.Count == 0)
                {
                    regions.Add(new SnippetRegion
                    {
                        Index = top.BeginIndex,
                        Name = top.Name,
                        RegionStart = top.RegionStart,
                        ContentStart = top.ContentStart,
                        ContentEnd = contentEnd,
                        RegionEnd = regionEnd,
                    });
                }
            }
        }

        regions.Sort((a, b) => a.RegionStart.CompareTo(b.RegionStart));
        // Reindex sequentially.
        for (int i = 0; i < regions.Count; i++)
            regions[i].Index = i;
        return regions;
    }

    // ---------------------------------------------------------------------
    // Comment/string masking
    // ---------------------------------------------------------------------
    private static string MaskCommentsAndStrings(string source)
    {
        char[] buf = source.ToCharArray();
        int i = 0;
        while (i < buf.Length)
        {
            char c = buf[i];
            if (c == '/' && i + 1 < buf.Length)
            {
                if (buf[i + 1] == '/')
                {
                    // line comment to end of line
                    while (i < buf.Length && buf[i] != '\n')
                    {
                        if (buf[i] != '\r' && buf[i] != '\n')
                            buf[i] = ' ';
                        i++;
                    }
                    continue;
                }
                if (buf[i + 1] == '*')
                {
                    buf[i++] = ' ';
                    buf[i++] = ' ';
                    while (i < buf.Length)
                    {
                        if (buf[i] == '*' && i + 1 < buf.Length && buf[i + 1] == '/')
                        {
                            buf[i++] = ' ';
                            buf[i++] = ' ';
                            break;
                        }
                        if (buf[i] != '\r' && buf[i] != '\n')
                            buf[i] = ' ';
                        i++;
                    }
                    continue;
                }
            }
            if (c == '"')
            {
                buf[i++] = ' ';
                while (i < buf.Length && buf[i] != '"' && buf[i] != '\n')
                {
                    if (buf[i] == '\\' && i + 1 < buf.Length)
                    {
                        buf[i++] = ' ';
                        if (buf[i] != '\r' && buf[i] != '\n')
                            buf[i++] = ' ';
                        continue;
                    }
                    buf[i++] = ' ';
                }
                if (i < buf.Length && buf[i] == '"')
                    buf[i++] = ' ';
                continue;
            }
            i++;
        }
        return new string(buf);
    }

    // ---------------------------------------------------------------------
    // Chunk parsing
    // ---------------------------------------------------------------------
    private sealed class Chunk
    {
        public int RegionIndex;
        public int Start;
        public int End;
        public bool IsPreprocessor;
        public bool Unparseable;
        public bool Live;
        public List<string> DeclaredNames = [];
        public List<string> ReferencedNames = [];
    }

    private static void ParseChunks(string original, string masked, int start, int end, int regionIndex, List<Chunk> output)
    {
        int i = start;
        while (i < end)
        {
            // Skip whitespace.
            while (i < end && char.IsWhiteSpace(masked[i]))
                i++;
            if (i >= end)
                break;

            // Preprocessor line?
            if (masked[i] == '#')
            {
                int lineStart = i;
                while (i < end)
                {
                    // Read to end of line, honoring backslash-newline continuation.
                    while (i < end && masked[i] != '\n')
                        i++;
                    if (i >= end)
                        break;
                    // Check if previous non-whitespace char before \n is backslash.
                    int j = i - 1;
                    while (j >= lineStart && (masked[j] == '\r' || masked[j] == ' ' || masked[j] == '\t'))
                        j--;
                    if (j >= lineStart && masked[j] == '\\')
                    {
                        i++; // continue through newline
                        continue;
                    }
                    i++; // consume newline
                    break;
                }
                output.Add(new Chunk
                {
                    RegionIndex = regionIndex,
                    Start = lineStart,
                    End = i,
                    IsPreprocessor = true,
                });
                continue;
            }

            // Top-level declaration chunk.
            int chunkStart = i;
            int parenDepth = 0;
            int braceDepth = 0;
            bool sawOpenBrace = false;
            bool closed = false;
            while (i < end)
            {
                char c = masked[i];
                if (c == '(')
                    parenDepth++;
                else if (c == ')')
                    parenDepth = Math.Max(0, parenDepth - 1);
                else if (c == '{')
                {
                    sawOpenBrace = true;
                    braceDepth++;
                }
                else if (c == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0 && sawOpenBrace)
                    {
                        // After '}', see if a trailing instance + ';' closes the chunk
                        // (struct/uniform/buffer block). Otherwise '}' closes a function body.
                        int k = i + 1;
                        while (k < end && (masked[k] == ' ' || masked[k] == '\t' || masked[k] == '\r' || masked[k] == '\n'))
                            k++;
                        if (k < end && masked[k] == ';')
                        {
                            i = k + 1;
                            closed = true;
                            break;
                        }
                        // No semicolon: function definition ends here.
                        i++;
                        closed = true;
                        break;
                    }
                }
                else if (c == ';' && parenDepth == 0 && braceDepth == 0)
                {
                    i++;
                    closed = true;
                    break;
                }
                else if (c == '#' && parenDepth == 0 && braceDepth == 0 && IsAtLineStart(masked, i, chunkStart))
                {
                    // Preprocessor at top level interrupts; emit current text (if any) as
                    // unparseable preserved chunk, then re-loop.
                    break;
                }
                i++;
            }

            int chunkEnd = i;
            if (chunkEnd <= chunkStart)
                break;

            Chunk chunk = new()
            {
                RegionIndex = regionIndex,
                Start = chunkStart,
                End = chunkEnd,
            };

            if (!closed)
            {
                chunk.Unparseable = true;
            }
            else
            {
                ClassifyChunk(masked, chunkStart, chunkEnd, chunk);
            }
            output.Add(chunk);
        }
    }

    private static bool IsAtLineStart(string s, int i, int chunkStart)
    {
        for (int k = i - 1; k >= chunkStart; k--)
        {
            char c = s[k];
            if (c == '\n') return true;
            if (!char.IsWhiteSpace(c)) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------------
    // Chunk classification: extract declared names + referenced identifiers.
    // ---------------------------------------------------------------------
    private static readonly Regex StructHeaderRegex = new(@"\bstruct\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{", RegexOptions.Compiled);
    private static readonly Regex BlockHeaderRegex = new(@"\b(?:uniform|buffer)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{", RegexOptions.Compiled);
    private static readonly Regex FunctionHeaderRegex = new(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex LeadingLayoutQualifierRegex = new(@"^\s*layout\s*\([^)]*\)\s*", RegexOptions.Compiled);

    private static void ClassifyChunk(string masked, int start, int end, Chunk chunk)
    {
        string text = masked.Substring(start, end - start);

        // Always collect referenced identifiers (we'll subtract decl names later).
        HashSet<string> referenced = new(StringComparer.Ordinal);
        foreach (Match m in IdentifierRegex().Matches(text))
        {
            string id = m.Value;
            if (GlslReserved.Contains(id))
                continue;
            referenced.Add(id);
        }

        // Detect struct.
        Match structMatch = StructHeaderRegex.Match(text);
        if (structMatch.Success)
        {
            chunk.DeclaredNames.Add(structMatch.Groups["name"].Value);
            // Trailing instance name(s) after }.
            ExtractTrailingInstances(text, chunk.DeclaredNames);
            FinalizeReferences(chunk, referenced);
            return;
        }

        // Detect uniform/buffer block: must contain '{' (i.e., matched by BlockHeaderRegex).
        Match blockMatch = BlockHeaderRegex.Match(text);
        if (blockMatch.Success)
        {
            chunk.DeclaredNames.Add(blockMatch.Groups["name"].Value);
            ExtractTrailingInstances(text, chunk.DeclaredNames);
            // Also add member names so they remain referenceable when no instance is present.
            int braceOpen = text.IndexOf('{', blockMatch.Index);
            int braceClose = text.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                string members = text.Substring(braceOpen + 1, braceClose - braceOpen - 1);
                foreach (string memberName in ExtractMemberNames(members))
                    chunk.DeclaredNames.Add(memberName);
            }
            FinalizeReferences(chunk, referenced);
            return;
        }

        // Plain variable / uniform / in / out / const declaration. This runs before
        // function classification because GLSL declarations commonly contain
        // constructor calls in initializers, e.g. `mat4 Value = mat4(1.0);`.
        if (TryExtractPlainDeclarationName(text, out string declarationName))
        {
            chunk.DeclaredNames.Add(declarationName);
            FinalizeReferences(chunk, referenced);
            return;
        }

        // Function (definition or prototype): contains '(' at top level and a name immediately preceding it.
        // Skip if this is just an expression (rare at top level). We require pattern: ...IDENT(...)...
        Match funcMatch = FunctionHeaderRegex.Match(text);
        if (funcMatch.Success && !LooksLikeMacroOrCallStatement(text, funcMatch))
        {
            chunk.DeclaredNames.Add(funcMatch.Groups["name"].Value);
            FinalizeReferences(chunk, referenced);
            return;
        }

        chunk.Unparseable = true;
        FinalizeReferences(chunk, referenced);
    }

    private static bool TryExtractPlainDeclarationName(string text, out string name)
    {
        name = string.Empty;
        string flat = text.TrimEnd(';', '\r', '\n', ' ', '\t');
        if (flat.Length == 0)
            return false;

        int eq = FindTopLevelChar(flat, '=');
        if (eq >= 0)
            flat = flat[..eq];

        flat = StripLeadingLayoutQualifiers(flat);

        // Function prototypes have a signature on the left side; variable
        // declarations only have layout qualifiers, type/qualifier tokens, an
        // identifier, and optional array suffixes.
        if (FindTopLevelChar(flat, '(') >= 0)
            return false;

        int comma = FindLastTopLevelChar(flat, ',');
        if (comma >= 0)
            flat = flat[(comma + 1)..];

        flat = StripTrailingArray(flat);
        Match lastIdent = Regex.Match(flat, @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*$");
        if (!lastIdent.Success)
            return false;

        string candidate = lastIdent.Groups["name"].Value;
        if (GlslReserved.Contains(candidate))
            return false;

        name = candidate;
        return true;
    }

    private static string StripLeadingLayoutQualifiers(string text)
    {
        string previous;
        string current = text;
        do
        {
            previous = current;
            current = LeadingLayoutQualifierRegex.Replace(current, string.Empty, 1);
        }
        while (!ReferenceEquals(previous, current) && previous.Length != current.Length);

        return current;
    }

    private static int FindTopLevelChar(string text, char target)
    {
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
                parenDepth++;
            else if (c == ')')
                parenDepth = Math.Max(0, parenDepth - 1);
            else if (c == '[')
                bracketDepth++;
            else if (c == ']')
                bracketDepth = Math.Max(0, bracketDepth - 1);
            else if (c == '{')
                braceDepth++;
            else if (c == '}')
                braceDepth = Math.Max(0, braceDepth - 1);
            else if (c == target && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                return i;
        }

        return -1;
    }

    private static int FindLastTopLevelChar(string text, char target)
    {
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == ')')
                parenDepth++;
            else if (c == '(')
                parenDepth = Math.Max(0, parenDepth - 1);
            else if (c == ']')
                bracketDepth++;
            else if (c == '[')
                bracketDepth = Math.Max(0, bracketDepth - 1);
            else if (c == '}')
                braceDepth++;
            else if (c == '{')
                braceDepth = Math.Max(0, braceDepth - 1);
            else if (c == target && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                return i;
        }

        return -1;
    }

    private static void FinalizeReferences(Chunk chunk, HashSet<string> referenced)
    {
        foreach (string n in chunk.DeclaredNames)
            referenced.Remove(n);
        chunk.ReferencedNames = [.. referenced];
    }

    private static IEnumerable<string> ExtractMemberNames(string members)
    {
        // Each member: type name [array] ;   (qualifiers may precede type)
        foreach (string raw in members.Split(';'))
        {
            string s = raw.Trim();
            if (s.Length == 0) continue;
            string flat = StripTrailingArray(s);
            Match m = Regex.Match(flat, @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*$");
            if (m.Success)
                yield return m.Groups["name"].Value;
        }
    }

    private static string StripTrailingArray(string s)
    {
        s = s.TrimEnd();
        while (s.EndsWith(']'))
        {
            int open = s.LastIndexOf('[');
            if (open < 0) break;
            s = s[..open].TrimEnd();
        }
        return s;
    }

    private static void ExtractTrailingInstances(string text, List<string> names)
    {
        int closeBrace = text.LastIndexOf('}');
        if (closeBrace < 0)
            return;
        string tail = text[(closeBrace + 1)..];
        tail = tail.TrimEnd(';', '\r', '\n', ' ', '\t');
        if (tail.Length == 0)
            return;
        // tail can be like "u_camera" or "u_lights[4]" or "a, b". Split on commas.
        foreach (string part in tail.Split(','))
        {
            string p = StripTrailingArray(part.Trim());
            Match m = Regex.Match(p, @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*$");
            if (m.Success)
                names.Add(m.Groups["name"].Value);
        }
    }

    private static bool LooksLikeMacroOrCallStatement(string text, Match funcMatch)
    {
        // Must have a type/qualifier token before the name. If the only thing
        // before is whitespace (or nothing), it's not a function declaration.
        int idx = funcMatch.Index;
        int k = idx - 1;
        while (k >= 0 && char.IsWhiteSpace(text[k]))
            k--;
        return k < 0;
    }

    private static void AddIdentifiers(string masked, int start, int end, HashSet<string> live)
    {
        if (start >= end)
            return;
        string segment = masked.Substring(start, end - start);
        foreach (Match m in IdentifierRegex().Matches(segment))
        {
            string id = m.Value;
            if (GlslReserved.Contains(id))
                continue;
            live.Add(id);
        }
    }
}
