using Baballonia.Contracts;
using Baballonia.SDK;
using Baballonia.Services.Inference.Platforms;
using Baballonia.Services.Inference.VideoSources;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Baballonia.Services.Inference;

public class SingleCameraSourceFactory
{
    private readonly ILogger<SingleCameraSourceFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDeviceEnumerator _deviceEnumerator;
    private readonly IPlatformConnector _platformConnector;

    // Video mode requested by the user, keyed by the camera address as shown in the UI (friendly name or raw address)
    private readonly ConcurrentDictionary<string, CaptureFormat> _requestedFormats = new();

    public SingleCameraSourceFactory(ILogger<SingleCameraSourceFactory> logger, ILoggerFactory loggerFactory, IDeviceEnumerator deviceEnumerator, IPlatformConnector platformConnector)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _deviceEnumerator = deviceEnumerator;
        _platformConnector = platformConnector;
    }

    /// <summary>
    /// Sets the video mode to request the next time a capture for <paramref name="address"/> is started.
    /// Pass null to let the camera use its default mode.
    /// </summary>
    public void SetRequestedFormat(string address, CaptureFormat? format)
    {
        if (string.IsNullOrEmpty(address)) return;
        if (format == null)
            _requestedFormats.TryRemove(address, out _);
        else
            _requestedFormats[address] = format;
    }

    public CaptureFormat? GetRequestedFormat(string address) =>
        !string.IsNullOrEmpty(address) && _requestedFormats.TryGetValue(address, out var format) ? format : null;

    public SingleCameraSource? Create(string address, string providerName, CaptureFormat? requestedFormat = null)
    {
        ICaptureFactory? provider;
        if (!string.IsNullOrEmpty(providerName))
        {
            provider = _platformConnector.GetCaptureFactories()
                .FirstOrDefault(factory => factory.GetProviderName() == providerName && factory.CanConnect(address));
            if(provider == null)
                throw new ArgumentNullException($"No provider \"{provider}\" is not compatible with \"{address}\"");

        }
        else
        {
            provider = _platformConnector.GetCaptureFactories().First(factory => factory.CanConnect(address));
            if(provider == null)
                throw new ArgumentNullException($"No suitable provider for {address} found");
        }

        var capture = provider.Create(address);
        capture.RequestedFormat = requestedFormat;

        return new SingleCameraSource(_loggerFactory.CreateLogger<SingleCameraSource>(), capture, address);
    }

    public Task<SingleCameraSource?> CreateStart(string address)
    {
        var camera = address;
        _deviceEnumerator.Cameras ??= _deviceEnumerator.UpdateCameras();
        if (_deviceEnumerator.Cameras.TryGetValue(camera, out var mappedAddress))
        {
            camera = mappedAddress;
        }

        var provider = _platformConnector.GetCaptureFactories().FirstOrDefault(factory => factory.CanConnect(camera));
        if (provider == null)
            throw new ArgumentNullException($"No provider for {address} not found");

        return CreateStart(address, provider.GetProviderName());
    }

    public Task<SingleCameraSource?> CreateStart(string address, string providerName)
    {
        var camera = address;
        var requestedFormat = GetRequestedFormat(address);

        _deviceEnumerator.Cameras ??= _deviceEnumerator.UpdateCameras();
        if (_deviceEnumerator.Cameras.TryGetValue(camera, out var mappedAddress))
        {
            camera = mappedAddress;
        }

        return Task.Run<SingleCameraSource?>(() =>
        {
            var cameraSource = Create(camera, providerName, requestedFormat);
            if (cameraSource == null)
                return null;

            if (!cameraSource.Start())
            {
                _logger.LogError("Could not initialize {}", address);
                return null;
            }

            Stopwatch sw = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(13);
            while (sw.Elapsed < timeout)
            {
                var testFrame = cameraSource.GetFrame();
                if (testFrame != null)
                    return cameraSource;
            }

            _logger.LogError("No data was received from {}, with {}, closing... Maybe the camera is opened somewhere else?", address, providerName);
            cameraSource.Dispose();
            return null;
        });
    }
}
