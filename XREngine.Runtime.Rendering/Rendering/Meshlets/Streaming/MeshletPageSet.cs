namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The result of the page cook of one cluster hierarchy mesh: the resident tables and the decoded
/// GPU image of each page. The page file stores the images compressed, at 4 KiB-aligned offsets.
/// </summary>
/// <param name="Table">The resident metadata of the mesh.</param>
/// <param name="PageImages">
/// The GPU image of each page, in page order. Each image holds only the used bytes of its page,
/// at most <see cref="MeshletPageFormat.PageBytes"/>. A pool slot is larger, and the bytes after
/// the image are not read.
/// </param>
/// <param name="Stats">The totals of the page cook, for the cook log.</param>
public sealed record MeshletPageSet(MeshletPageTable Table, byte[][] PageImages, MeshletPageSetStats Stats);
