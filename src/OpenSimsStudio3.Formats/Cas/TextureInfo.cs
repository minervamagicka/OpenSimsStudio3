namespace OpenSimsStudio3.Formats.Cas;

/// <summary>Size and format of a TS3 texture (_IMG, 0x00B2D882), from its DDS header. Null when the bytes are not a DDS.</summary>
public sealed record TextureInfo(int Width, int Height, string Format, int Mipmaps)
{
    public static TextureInfo? Read(byte[]? bytes)
    {
        // DDS: "DDS " magic, then DDS_HEADER (height at +12, width at +16, mip count at +28, pixel format FourCC at +84).
        if (bytes == null || bytes.Length < 128 || bytes[0] != 'D' || bytes[1] != 'D' || bytes[2] != 'S' || bytes[3] != ' ') return null;
        var height = BitConverter.ToInt32(bytes, 12);
        var width = BitConverter.ToInt32(bytes, 16);
        var mips = BitConverter.ToInt32(bytes, 28);
        var fourcc = System.Text.Encoding.ASCII.GetString(bytes, 84, 4).TrimEnd('\0');
        var format = fourcc.Length > 0 && fourcc.All(c => c >= ' ' && c < 127) ? fourcc : $"{BitConverter.ToInt32(bytes, 88)}-bit";
        return new TextureInfo(width, height, format, Math.Max(1, mips));
    }
}
