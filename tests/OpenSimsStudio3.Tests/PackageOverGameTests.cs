using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Tests;

public class PackageOverGameTests
{
    static readonly ResourceKey Shared = new(1, 0, 1), GameOnly = new(1, 0, 2), PackageOnly = new(1, 0, 3);

    [Fact]
    public void Package_wins_over_game_and_falls_back_to_it()
    {
        using var temp = new TempFolder();
        TestPackages.Write(temp["cc.package"], (Shared, [9]), (PackageOnly, [8]));
        var game = new FakeGame(new() { [Shared] = [1], [GameOnly] = [2] });
        using var source = new PackageOverGame(temp["cc.package"], game);
        Assert.Equal(new byte[] { 9 }, source.Read(Shared));
        Assert.Equal(new byte[] { 2 }, source.Read(GameOnly));
        Assert.Equal(new byte[] { 8 }, source.Read(PackageOnly));
        Assert.Null(source.Read(new ResourceKey(7, 7, 7)));
        Assert.Equal([Shared, PackageOnly], source.PackageKeys.OrderBy(k => k.Instance));
        Assert.True(source.Contains(GameOnly));
    }

    [Fact]
    public void Resource_key_prints_as_type_group_instance() =>
        Assert.Equal("034AEECB:00000001:00000000000000FF", new ResourceKey(0x034AEECB, 1, 0xFF).ToString());
}
