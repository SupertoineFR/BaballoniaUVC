using Baballonia.SDK;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Baballonia.Contracts;

public interface IDeviceEnumerator
{
    protected ILogger Logger { get; set; }
    public Dictionary<string, string> Cameras { get; set; }
    public Dictionary<string, string> UpdateCameras();

    /// <summary>
    /// Lists the video modes supported by the given camera.
    /// </summary>
    /// <param name="cameraName">Friendly name (as returned by <see cref="UpdateCameras"/>) or device id</param>
    /// <returns>Supported modes, or an empty list if they can't be determined on this platform/device</returns>
    public IReadOnlyList<CaptureFormat> GetSupportedFormats(string cameraName) => [];
}
