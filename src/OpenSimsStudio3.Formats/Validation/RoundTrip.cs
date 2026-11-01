using System.Reflection;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Formats.Validation;

/// <summary>
/// Byte-exact round trips: parse a resource, write it back from the parsed fields, compare with the original.
/// s3pi normally hands back the original bytes for an unedited resource, so this forces a real write: the resource's
/// own UnParse for the container, and each RCOL chunk's UnParse for the chunks inside it.
/// </summary>
public static class RoundTrip
{
    public sealed record Result(bool Passed, string Detail)
    {
        public static Result Pass() => new(true, "");
        public static Result Fail(string detail) => new(false, detail);
    }

    static readonly MethodInfo ResourceUnParse =
        typeof(AResource).GetMethod("UnParse", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("AResource.UnParse not found; s3pi changed?");

    /// <summary>Round-trips a plain resource (e.g. CASP) through its type's parser and writer.</summary>
    public static Result Resource(byte[] original, Func<Stream, AResource> parse)
    {
        AResource parsed;
        try { parsed = parse(new MemoryStream(original, writable: false)); }
        catch (Exception e) { return Result.Fail($"parse threw {e.GetType().Name}: {e.Message}"); }
        return Compare(original, Write(parsed), "resource");
    }

    /// <summary>
    /// Round-trips an RCOL resource (e.g. GEOM): every chunk is rewritten from its fields and compared with its
    /// original bytes, then the container is rewritten and compared with the whole original resource.
    /// </summary>
    public static Result Rcol(byte[] original)
    {
        GenericRCOLResource rcol;
        try { rcol = new GenericRCOLResource(0, new MemoryStream(original, writable: false)); }
        catch (Exception e) { return Result.Fail($"parse threw {e.GetType().Name}: {e.Message}"); }

        var ranges = ChunkRanges(original);
        if (ranges == null || ranges.Count != rcol.ChunkEntries.Count) return Result.Fail($"could not read the chunk index [{DescribeRcolHeader(original)}]");
        for (var i = 0; i < rcol.ChunkEntries.Count; i++)
        {
            var block = rcol.ChunkEntries[i].RCOLBlock;
            if (block is DefaultRCOL) return Result.Fail($"chunk {i} ({block.Tag}) has no s3pi wrapper, so it was not checked");
            // The chunk's original bytes, sliced from the file. (block.AsBytes is not safe here: some wrappers mark
            // themselves edited while parsing, and then AsBytes returns freshly written bytes.)
            var stored = original.AsSpan((int)ranges[i].Start, (int)ranges[i].Length).ToArray();
            byte[] written;
            try
            {
                using var s = block.UnParse();
                written = ((MemoryStream)s).ToArray();
            }
            catch (Exception e) { return Result.Fail($"chunk {i} ({block.Tag}) write threw {e.GetType().Name}: {e.Message}"); }
            var chunk = Compare(stored, written, $"chunk {i} ({block.Tag})");
            if (!chunk.Passed) return chunk;
        }
        var container = Compare(original, Write(rcol), "container");
        return container.Passed ? container : Result.Fail($"{container.Detail} [{DescribeRcolHeader(original)}]");
    }

    /// <summary>
    /// Where each chunk sits in an RCOL resource. Mirrors s3pi's reading rule: with a single chunk the stored index
    /// is ignored and the chunk runs from just after the header to the end of the resource.
    /// </summary>
    static List<(long Start, long Length)>? ChunkRanges(byte[] data)
    {
        if (data.Length < 20) return null;
        var r = new BinaryReader(new MemoryStream(data));
        r.ReadUInt32(); r.ReadInt32(); r.ReadUInt32();
        var (externals, chunks) = (r.ReadInt32(), r.ReadInt32());
        var indexAt = 20 + 16L * chunks + 16L * externals;
        if (chunks < 1 || externals < 0 || indexAt + 8L * chunks > data.Length) return null;
        if (chunks == 1)
        {
            var start = 0x2CL + 16L * externals;
            return start <= data.Length ? [(start, data.Length - start)] : null;
        }
        r.BaseStream.Position = indexAt;
        var ranges = new List<(long, long)>();
        for (var i = 0; i < chunks; i++)
        {
            var (start, length) = ((long)r.ReadUInt32(), (long)r.ReadInt32());
            if (start + length > data.Length || length < 0) return null;
            ranges.Add((start, length));
        }
        return ranges;
    }

    /// <summary>The stored RCOL header fields, read directly from the bytes (for failure reports).</summary>
    static string DescribeRcolHeader(byte[] data)
    {
        if (data.Length < 20) return "header too short";
        var r = new BinaryReader(new MemoryStream(data));
        var (version, publicChunks, _, externals, chunks) = (r.ReadUInt32(), r.ReadInt32(), r.ReadUInt32(), r.ReadInt32(), r.ReadInt32());
        var indexAt = 20 + 16L * chunks + 16L * externals;
        if (chunks < 0 || externals < 0 || indexAt + 8L * chunks > data.Length) return $"v{version} chunks={chunks} externals={externals} (index out of range)";
        r.BaseStream.Position = indexAt;
        var entries = Enumerable.Range(0, chunks).Select(_ => $"@0x{r.ReadUInt32():X}+{r.ReadInt32()}");
        return $"v{version} public={publicChunks} chunks={chunks} externals={externals} index={string.Join(",", entries)} expected_first_chunk_at=0x{indexAt + 8 * chunks:X}";
    }

    static byte[] Write(AResource resource)
    {
        using var s = (Stream)ResourceUnParse.Invoke(resource, null)!;
        s.Position = 0;
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    static Result Compare(byte[] expected, byte[] actual, string what)
    {
        var length = Math.Min(expected.Length, actual.Length);
        for (var i = 0; i < length; i++)
            if (expected[i] != actual[i])
                return Result.Fail($"{what}: first difference at byte {i} (0x{i:X}): expected {expected[i]:X2}, wrote {actual[i]:X2}; sizes {expected.Length} vs {actual.Length}");
        return expected.Length == actual.Length
            ? Result.Pass()
            : Result.Fail($"{what}: sizes differ, expected {expected.Length} bytes, wrote {actual.Length}");
    }
}
