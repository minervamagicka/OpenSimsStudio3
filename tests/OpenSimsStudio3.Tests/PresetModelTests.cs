using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Tests;

/// <summary>Uses a preset written for this test in the shape OSS3 reads, not one taken from the game.</summary>
public class PresetModelTests
{
    const string Xml =
        "<preset><complate name=\"TestRgbMask\" reskey=\"\">" +
        "<value key=\"Multiplier\" value=\"key:00B2D882:00000000:0000000000000001\" />" +
        "<value key=\"Mask\" value=\"key:00B2D882:00000000:0000000000000002\" />" +
        "<value key=\"Logo\" value=\"key:00B2D882:00000000:0000000000000003\" />" +
        "<value key=\"Logo Enabled\" value=\"False\" />" +
        "<value key=\"Root Color\" value=\"0.1,0.2,0.3,1\" />" +
        "<value key=\"Pattern A\" value=\"solidColor_1\" />" +
        "<value key=\"Pattern A Enabled\" value=\"True\" />" +
        "<value key=\"Pattern A Tiling\" value=\"1,1\" />" +
        "<pattern name=\"solidColor_1\" variable=\"Pattern A\"><value key=\"Color\" value=\"1,0,0,1\" /></pattern>" +
        "</complate></preset>";

    [Fact]
    public void Unedited_preset_writes_back_exactly() => Assert.Equal(Xml, PresetModel.Parse(Xml).ToXml());

    [Fact]
    public void Text_before_the_root_is_kept()
    {
        var withDeclaration = "<?xml version=\"1.0\"?>\r\n" + Xml;
        Assert.Equal(withDeclaration, PresetModel.Parse(withDeclaration).ToXml());
    }

    [Fact]
    public void Reads_complate_textures_colours_and_channels()
    {
        var preset = PresetModel.Parse(Xml);
        Assert.Equal("TestRgbMask", preset.Complate);
        Assert.Equal(["Multiplier", "Mask", "Logo"], preset.Textures.Select(t => t.Name));
        Assert.Equal(new ResourceKey(0x00B2D882, 0, 2), preset.Textures[1].Key);
        Assert.Equal("Root Color", Assert.Single(preset.Colors).Name);
        var channel = Assert.Single(preset.Channels);
        Assert.True(channel.Enabled);
        Assert.Equal("solidColor_1", channel.PatternName);
        Assert.Equal(new[] { 1f, 0f, 0f, 1f }, channel.Color);
        Assert.Equal("1,1", channel.Tiling);
    }

    [Fact]
    public void Disabled_logo_is_unused_and_others_are_used()
    {
        var preset = PresetModel.Parse(Xml);
        Assert.False(preset.IsTextureUsed("Logo"));
        Assert.True(preset.IsTextureUsed("Multiplier"));
    }

    [Fact]
    public void Edits_change_only_their_attribute()
    {
        var preset = PresetModel.Parse(Xml);
        preset.SetChannelEnabled("Pattern A", false);
        Assert.True(preset.SetChannelColor("Pattern A", [0f, 0.5f, 2f, 1f]));
        preset.SetTexture("Mask", new ResourceKey(0x00B2D882, 0x10, 0xABCDEF));
        var expected = Xml.Replace("\"Pattern A Enabled\" value=\"True\"", "\"Pattern A Enabled\" value=\"False\"")
            .Replace("key=\"Color\" value=\"1,0,0,1\"", "key=\"Color\" value=\"0.0000,0.5000,1.0000,1.0000\"")
            .Replace("0000000000000002", "0000000000ABCDEF").Replace("key:00B2D882:00000000:0000000000ABCDEF", "key:00B2D882:00000010:0000000000ABCDEF");
        Assert.Equal(expected, preset.ToXml());
    }

    [Fact]
    public void Colour_on_a_channel_without_one_is_refused() =>
        Assert.False(PresetModel.Parse(Xml).SetChannelColor("Pattern B", [1f, 1f, 1f, 1f]));

    [Fact]
    public void Missing_value_throws() =>
        Assert.Throws<KeyNotFoundException>(() => PresetModel.Parse(Xml).SetColor("Tip Color", [1f, 1f, 1f, 1f]));

    [Fact]
    public void Preset_without_complate_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => PresetModel.Parse("<preset />"));
}
