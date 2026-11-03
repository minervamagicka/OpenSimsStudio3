using OpenSimsStudio3.Formats.Sims3Pack;

/// <summary>
/// sims3pack &lt;file.sims3pack&gt;: prints the header version, the manifest and each packaged file's size and CRC check,
/// to compare OSS3's output with a .sims3pack the launcher accepts. Read-only; prints no file contents.
/// </summary>
static class Sims3PackDump
{
    public static int Run(string path)
    {
        var pack = Sims3PackFormat.Read(path);
        Console.WriteLine($"header version 0x{pack.Version:X4}, {pack.Entries.Count} packaged file(s)");
        Console.WriteLine(pack.Manifest.ToString());
        var bad = 0;
        foreach (var e in pack.Entries)
        {
            var crc = Sims3PackFormat.Crc(e.Data);
            var match = string.Equals(crc, e.Crc, StringComparison.OrdinalIgnoreCase);
            if (!match) bad++;
            Console.WriteLine($"{e.Name}: offset {e.Offset}, {e.Length} bytes, CRC {(match ? "matches" : $"differs (stored {e.Crc}, s3pi {crc})")}");
        }
        return bad == 0 ? 0 : 1;
    }
}
