namespace OpenSimsStudio3.Formats.Install;

/// <summary>
/// The Sims 3 products by "major": 1 is the base game, then every expansion (EP) and stuff pack (SP) in release order.
/// The numbers are the order the retail installer lays the packs out in and what OSS3 stores as a part's product;
/// codes (EP1, SP1 ...) are the usual community shorthand, and icon files are the Game\Bin\Sims3EPnn.ico /
/// Sims3SPnn.ico each retail pack folder ships. Names are the retail store names.
/// </summary>
public static class GameProducts
{
    /// <summary>Highest known major (Into the Future).</summary>
    public const int MaxMajor = 21;

    // Index = major. Kind is "EP"/"SP"; Number is the pack's own counter (EP11 = Into the Future); base game is ("", 0).
    static readonly (string Kind, int Number, string Name)[] Table =
    [
        ("", -1, ""),
        ("", 0, "The Sims 3"),
        ("EP", 1, "World Adventures"),
        ("SP", 1, "High-End Loft Stuff"),
        ("EP", 2, "Ambitions"),
        ("SP", 2, "Fast Lane Stuff"),
        ("EP", 3, "Late Night"),
        ("SP", 3, "Outdoor Living Stuff"),
        ("EP", 4, "Generations"),
        ("SP", 4, "Town Life Stuff"),
        ("EP", 5, "Pets"),
        ("SP", 5, "Master Suite Stuff"),
        ("EP", 6, "Showtime"),
        ("SP", 6, "Katy Perry's Sweet Treats"),
        ("SP", 7, "Diesel Stuff"),
        ("EP", 7, "Supernatural"),
        ("EP", 8, "Seasons"),
        ("SP", 8, "70s, 80s & 90s Stuff"),
        ("EP", 9, "University Life"),
        ("EP", 10, "Island Paradise"),
        ("SP", 9, "Movie Stuff"),
        ("EP", 11, "Into the Future"),
    ];

    public static bool IsKnown(int major) => major >= 1 && major <= MaxMajor;

    /// <summary>"EP3", "SP1" ...; "BASE" for the base game; "unknown" when the major is not a known product.</summary>
    public static string Code(int major) =>
        !IsKnown(major) ? "unknown" : Table[major].Number == 0 ? "BASE" : $"{Table[major].Kind}{Table[major].Number}";

    public static string? Name(int major) => IsKnown(major) ? Table[major].Name : null;

    public static bool IsExpansion(int major) => Code(major).StartsWith("EP", StringComparison.Ordinal);

    /// <summary>The icon file in the product's Game\Bin: Sims3.ico for the base game, Sims3EP03.ico style for packs.</summary>
    public static string? IconFileName(int major) =>
        !IsKnown(major) ? null : Table[major].Number == 0 ? "Sims3.ico" : $"Sims3{Table[major].Kind}{Table[major].Number:00}.ico";

    /// <summary>The major of a pack from its code parts (EP, 3), or 0 when there is no such pack.</summary>
    internal static int FromKind(string kind, int number)
    {
        for (var major = 2; major <= MaxMajor; major++)
            if (Table[major].Kind == kind && Table[major].Number == number) return major;
        return 0;
    }
}
