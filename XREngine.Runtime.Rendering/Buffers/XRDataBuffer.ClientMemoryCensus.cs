using System.Text;
using XREngine.Data;

namespace XREngine.Rendering
{
    public partial class XRDataBuffer
    {
        /// <summary>
        /// Sums the client-side (CPU) bytes held by every live data buffer, split into
        /// mesh-owned and other buffers and grouped by attribute name. A cold
        /// diagnostic for memory investigations (MCP <c>invoke_method</c>); it walks
        /// the render-object cache under its lock and allocates its result.
        /// </summary>
        /// <param name="top">Number of attribute-name groups to list, largest first.</param>
        public static string DescribeClientMemory(int top = 20)
        {
            Dictionary<string, (long bytes, int count)> byName = new(StringComparer.Ordinal);
            long meshBytes = 0L, otherBytes = 0L, spilledBytes = 0L;
            int meshCount = 0, otherCount = 0, releasedCount = 0;
            lock (RenderObjectCache)
            {
                foreach (KeyValuePair<Type, List<GenericRenderObject>> entry in RenderObjectCache)
                {
                    if (!typeof(XRDataBuffer).IsAssignableFrom(entry.Key))
                        continue;

                    foreach (GenericRenderObject renderObject in entry.Value)
                    {
                        if (renderObject is not XRDataBuffer buffer)
                            continue;

                        DataSource? source = buffer.ClientSideSource;
                        if (source is null || source.Address == IntPtr.Zero)
                        {
                            releasedCount++;
                            continue;
                        }

                        if (source is XRBufferSpilledDataSource)
                            spilledBytes += source.Length;
                        long bytes = source.External ? 0L : source.Length;
                        if (buffer.IsMeshOwnedBuffer)
                        {
                            meshBytes += bytes;
                            meshCount++;
                        }
                        else
                        {
                            otherBytes += bytes;
                            otherCount++;
                        }

                        string name = buffer.AttributeName ?? "<unnamed>";
                        byName.TryGetValue(name, out (long bytes, int count) total);
                        byName[name] = (total.bytes + bytes, total.count + 1);
                    }
                }
            }

            StringBuilder text = new();
            text.Append($"meshMB={meshBytes / 1048576.0:F1} meshBuffers={meshCount} otherMB={otherBytes / 1048576.0:F1} otherBuffers={otherCount} " +
                        $"spilledMB={spilledBytes / 1048576.0:F1} withoutClientBytes={releasedCount}");
            foreach (KeyValuePair<string, (long bytes, int count)> group in byName.OrderByDescending(pair => pair.Value.bytes).Take(top))
                text.Append($"; {group.Key}={group.Value.bytes / 1048576.0:F1}MB/{group.Value.count}");
            return text.ToString();
        }
    }
}
