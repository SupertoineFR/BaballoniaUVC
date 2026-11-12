using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Baballonia.SDK;
using Capture = Baballonia.SDK.Capture;

namespace Baballonia.OpenCVCapture;

/// <summary>
/// Wrapper class for OpenCV
/// </summary>
public sealed class OpenCvCapture(string source, ILogger<OpenCvCapture> logger) : Capture(source, logger)
{
    private VideoCapture? _videoCapture;
    private static readonly VideoCaptureAPIs PreferredBackend;

    private Task? _updateTask;
    private readonly CancellationTokenSource _updateTaskCts = new();

    static OpenCvCapture()
    {
        // Choose the most appropriate backend based on the detected OS
        // This is needed to handle concurrent camera access
        if (OperatingSystem.IsWindows())
        {
            PreferredBackend = VideoCaptureAPIs.DSHOW;
        }
        else if (OperatingSystem.IsLinux())
        {
            PreferredBackend = VideoCaptureAPIs.GSTREAMER;
        }
        else if (OperatingSystem.IsMacOS())
        {
            PreferredBackend = VideoCaptureAPIs.AVFOUNDATION;
        }
        else
        {
            // Fallback to ANY which lets OpenCV choose
            PreferredBackend = VideoCaptureAPIs.ANY;
        }
    }

    public override async Task<bool> StartCapture()
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            try
            {
                if (int.TryParse(Source, out var index))
                    _videoCapture = await Task.Run(() => VideoCapture.FromCamera(index, PreferredBackend), cts.Token);
                else
                    _videoCapture = await Task.Run(() => new VideoCapture(Source), cts.Token);
            }
            catch (Exception e)
            {
                logger.LogError("Error: {}", e);
                IsReady = false;
                return false;
            }
        }

        // Handle edge case cameras like the Varjo Aero that send frames in YUV
        // This won't activate the IR illuminators, but it's a good idea to standardize inputs
        _videoCapture.ConvertRgb = true;
        IsReady = _videoCapture.IsOpened();

        if (IsReady && RequestedFormat is { } format)
            ApplyRequestedFormat(_videoCapture, format);

        CancellationToken token = _updateTaskCts.Token;
        _updateTask = Task.Run(() => VideoCapture_UpdateLoop(_videoCapture, token));

        return IsReady;
    }

    private void ApplyRequestedFormat(VideoCapture capture, CaptureFormat format)
    {
        try
        {
            // Order matters for the DirectShow backend (verified empirically, other orders silently
            // fall back to an uncompressed format such as YUY2 at a lower framerate):
            //  1. FPS first. It is remembered and reused by every later re-negotiation.
            //  2. Width, then height. The new resolution is negotiated once both are known.
            //  3. FourCC last. It re-negotiates the media type at the resolution now in use.
            if (format.Fps > 0)
                capture.Set(VideoCaptureProperties.Fps, format.Fps);

            capture.Set(VideoCaptureProperties.FrameWidth, format.Width);
            capture.Set(VideoCaptureProperties.FrameHeight, format.Height);

            if (format.GetFourCc() is { } fourCc)
                capture.Set(VideoCaptureProperties.FourCC, fourCc);

            var actualFourCc = (int)capture.Get(VideoCaptureProperties.FourCC);
            var actualFourCcStr = actualFourCc > 0
                ? new string([(char)(actualFourCc & 0xFF), (char)((actualFourCc >> 8) & 0xFF), (char)((actualFourCc >> 16) & 0xFF), (char)((actualFourCc >> 24) & 0xFF)])
                : "?";
            logger.LogInformation(
                "Requested camera mode {Requested}; camera reports {Width}x{Height} @ {Fps} fps ({FourCC})",
                format, capture.FrameWidth, capture.FrameHeight, capture.Fps, actualFourCcStr);
        }
        catch (Exception e)
        {
            logger.LogWarning("Could not apply camera mode {Format}: {Error}", format, e.Message);
        }
    }

    private Task VideoCapture_UpdateLoop(VideoCapture capture, CancellationToken ct)
    {
        var frame = new Mat();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IsReady = capture.Read(frame);
                // no need to push empty Mat
                if (IsReady)
                    SetRawMat(frame);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        return Task.CompletedTask;
    }

    public override Task<bool> StopCapture()
    {
        if (_videoCapture is null)
            return Task.FromResult(false);

        if (_updateTask != null) {
            _updateTaskCts.Cancel();
            _updateTask.Wait();
        }

        IsReady = false;
        if (_videoCapture != null)
        {
            _videoCapture.Release();
            _videoCapture.Dispose();
            _videoCapture = null;
        }
        return Task.FromResult(true);
    }
}
