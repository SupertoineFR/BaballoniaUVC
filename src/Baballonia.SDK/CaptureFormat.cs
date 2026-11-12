using System.Globalization;

namespace Baballonia.SDK;

/// <summary>
/// Describes a video mode (resolution, framerate and optionally pixel format) that a camera supports
/// or that a user has requested a capture source to use.
/// </summary>
/// <param name="Width">Frame width in pixels</param>
/// <param name="Height">Frame height in pixels</param>
/// <param name="Fps">Frames per second</param>
/// <param name="PixelFormat">FourCC / subtype name (e.g. "MJPG", "YUY2"), or empty when unknown</param>
public sealed record CaptureFormat(int Width, int Height, double Fps, string PixelFormat = "")
{
    public override string ToString()
    {
        var fps = Fps.ToString(Math.Abs(Fps - Math.Round(Fps)) < 0.01 ? "0" : "0.##", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(PixelFormat)
            ? $"{Width} x {Height} @ {fps} fps"
            : $"{Width} x {Height} @ {fps} fps ({PixelFormat})";
    }

    /// <summary>
    /// Returns the FourCC code of <see cref="PixelFormat"/> if it is a 4 character code, otherwise null.
    /// </summary>
    public int? GetFourCc()
    {
        if (PixelFormat is not { Length: 4 }) return null;
        return PixelFormat[0] | (PixelFormat[1] << 8) | (PixelFormat[2] << 16) | (PixelFormat[3] << 24);
    }
}
