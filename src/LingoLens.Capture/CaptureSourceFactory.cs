using Microsoft.Extensions.Logging;
using LingoLens.Core.Capture;
using Windows.Graphics.Capture;

namespace LingoLens.Capture;

/// <summary>
/// Picks the best concrete capture source for a target:
/// <list type="bullet">
/// <item><description><see cref="CaptureMode.Window"/> → <see cref="WgcCaptureSource"/>.</description></item>
/// <item><description><see cref="CaptureMode.Monitor"/> → <see cref="WgcCaptureSource"/> when WGC is
/// available, otherwise <see cref="DxgiDuplicationCaptureSource"/>.</description></item>
/// <item><description><see cref="CaptureMode.Region"/> → <see cref="RegionCaptureSource"/>.</description></item>
/// </list>
/// </summary>
public sealed class CaptureSourceFactory : ICaptureSourceFactory
{
    private readonly DeviceResources _resources;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates the factory.</summary>
    public CaptureSourceFactory(DeviceResources resources, ILoggerFactory loggerFactory)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public IScreenCaptureSource Create(CaptureTarget target)
    {
        return target.Mode switch
        {
            CaptureMode.Window => CreateWgc(),
            CaptureMode.Monitor => CreateMonitor(),
            CaptureMode.Region => new RegionCaptureSource(
                _resources,
                _loggerFactory,
                _loggerFactory.CreateLogger<RegionCaptureSource>()),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Mode, "Unsupported capture mode."),
        };
    }

    private WgcCaptureSource CreateWgc() =>
        new(_resources, _loggerFactory.CreateLogger<WgcCaptureSource>());

    private IScreenCaptureSource CreateMonitor()
    {
        if (IsWgcSupported())
            return CreateWgc();

        return new DxgiDuplicationCaptureSource(
            _resources,
            _loggerFactory.CreateLogger<DxgiDuplicationCaptureSource>());
    }

    private static bool IsWgcSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }
}
