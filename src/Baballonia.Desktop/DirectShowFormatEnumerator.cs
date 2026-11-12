#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Baballonia.SDK;
using DirectShowLib;
using Microsoft.Extensions.Logging;

namespace Baballonia.Desktop;

/// <summary>
/// Queries the video modes (resolution / framerate / pixel format) a DirectShow capture device exposes on its capture pin.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DirectShowFormatEnumerator
{
    // FourCC based media subtypes all share this GUID layout: {XXXXXXXX-0000-0010-8000-00AA00389B71}
    private static readonly byte[] FourCcGuidTail = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    private static readonly Dictionary<Guid, string> KnownSubtypes = new()
    {
        { MediaSubType.RGB24, "RGB24" },
        { MediaSubType.RGB32, "RGB32" },
        { MediaSubType.RGB565, "RGB565" },
        { MediaSubType.RGB555, "RGB555" },
        { MediaSubType.ARGB32, "ARGB32" },
    };

    public static IReadOnlyList<CaptureFormat> Enumerate(DsDevice device, ILogger logger)
    {
        var result = new List<CaptureFormat>();

        object? filterObj = null;
        IPin? pin = null;
        try
        {
            var iid = typeof(IBaseFilter).GUID;
            device.Mon.BindToObject(null!, null!, ref iid, out filterObj);
            if (filterObj is not IBaseFilter filter)
                return result;

            pin = DsFindPin.ByCategory(filter, PinCategory.Capture, 0)
                  ?? DsFindPin.ByDirection(filter, PinDirection.Output, 0);
            if (pin is not IAMStreamConfig streamConfig)
                return result;

            var videoControl = filter as IAMVideoControl;

            var hr = streamConfig.GetNumberOfCapabilities(out var count, out var capsSize);
            DsError.ThrowExceptionForHR(hr);

            var capsPtr = Marshal.AllocCoTaskMem(Math.Max(capsSize, Marshal.SizeOf<VideoStreamConfigCaps>()));
            try
            {
                for (var i = 0; i < count; i++)
                {
                    AMMediaType? mediaType = null;
                    try
                    {
                        hr = streamConfig.GetStreamCaps(i, out mediaType, capsPtr);
                        if (hr < 0 || mediaType == null || mediaType.majorType != MediaType.Video)
                            continue;

                        int width, height;
                        long avgTimePerFrame;
                        if (mediaType.formatType == FormatType.VideoInfo && mediaType.formatPtr != IntPtr.Zero)
                        {
                            var vih = Marshal.PtrToStructure<VideoInfoHeader>(mediaType.formatPtr)!;
                            width = vih.BmiHeader.Width;
                            height = Math.Abs(vih.BmiHeader.Height);
                            avgTimePerFrame = vih.AvgTimePerFrame;
                        }
                        else if (mediaType.formatType == FormatType.VideoInfo2 && mediaType.formatPtr != IntPtr.Zero)
                        {
                            var vih2 = Marshal.PtrToStructure<VideoInfoHeader2>(mediaType.formatPtr)!;
                            width = vih2.BmiHeader.Width;
                            height = Math.Abs(vih2.BmiHeader.Height);
                            avgTimePerFrame = vih2.AvgTimePerFrame;
                        }
                        else
                        {
                            continue;
                        }

                        if (width <= 0 || height <= 0)
                            continue;

                        var pixelFormat = GetSubtypeName(mediaType.subType);
                        var caps = Marshal.PtrToStructure<VideoStreamConfigCaps>(capsPtr)!;

                        foreach (var fps in GetFrameRates(videoControl, pin, i, width, height, avgTimePerFrame, caps))
                            result.Add(new CaptureFormat(width, height, fps, pixelFormat));
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("Skipping stream caps {Index} of {Device}: {Error}", i, device.Name, ex.Message);
                    }
                    finally
                    {
                        if (mediaType != null)
                            DsUtils.FreeAMMediaType(mediaType);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(capsPtr);
            }
        }
        finally
        {
            if (pin != null) Marshal.ReleaseComObject(pin);
            if (filterObj != null && Marshal.IsComObject(filterObj)) Marshal.ReleaseComObject(filterObj);
        }

        return result
            .Distinct()
            .OrderByDescending(f => f.Width * f.Height)
            .ThenByDescending(f => f.Width)
            .ThenByDescending(f => f.Fps)
            .ThenBy(f => f.PixelFormat, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<double> GetFrameRates(IAMVideoControl? videoControl, IPin pin, int capsIndex,
        int width, int height, long avgTimePerFrame, VideoStreamConfigCaps caps)
    {
        var rates = new SortedSet<double>();

        // UVC devices (via ksproxy) report the discrete framerate list here
        if (videoControl != null)
        {
            try
            {
                var hr = videoControl.GetFrameRateList(pin, capsIndex, new System.Drawing.Size(width, height),
                    out var listSize, out var listPtr);
                if (hr >= 0 && listPtr != IntPtr.Zero)
                {
                    try
                    {
                        for (var j = 0; j < listSize; j++)
                            AddInterval(rates, Marshal.ReadInt64(listPtr, j * sizeof(long)));
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(listPtr);
                    }
                }
            }
            catch
            {
                // Not supported by this driver, fall back below
            }
        }

        if (rates.Count == 0)
        {
            // Fall back to the min/max frame interval range and the default rate
            AddInterval(rates, caps.MinFrameInterval);
            AddInterval(rates, caps.MaxFrameInterval);
            AddInterval(rates, avgTimePerFrame);
        }

        return rates.Reverse();
    }

    private static void AddInterval(SortedSet<double> rates, long interval100Ns)
    {
        if (interval100Ns <= 0) return;
        var fps = Math.Round(10_000_000.0 / interval100Ns, 2);
        if (fps is > 0 and < 10_000)
            rates.Add(fps);
    }

    private static string GetSubtypeName(Guid subtype)
    {
        if (KnownSubtypes.TryGetValue(subtype, out var name))
            return name;

        var bytes = subtype.ToByteArray();
        if (bytes.AsSpan(4).SequenceEqual(FourCcGuidTail))
        {
            var chars = bytes.Take(4).Select(b => (char)b).ToArray();
            if (chars.All(c => c is >= ' ' and <= '~'))
                return new string(chars).Trim();
        }

        return "";
    }
}
#endif
