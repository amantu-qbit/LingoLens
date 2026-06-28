using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LingoLens.Core;
using LingoLens.Core.Overlay;
using LingoLens.Overlay.Interop;
using LingoLens.Overlay.Layout;
using LingoLens.Overlay.Smoothing;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DXGI.AlphaMode;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using RectI = LingoLens.Core.RectI;

namespace LingoLens.Overlay.Rendering;

/// <summary>
/// The transparent, click-through, top-most overlay surface backed by DirectComposition + Direct2D/DirectWrite.
/// </summary>
/// <remarks>
/// <para>
/// The renderer owns a dedicated UI thread that creates a layered, click-through <c>WS_POPUP</c> window
/// (<c>WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP</c>),
/// runs its own message pump, and hosts the DirectComposition tree. All public members marshal their work onto
/// that thread, so callers may invoke from any lane (the inference/layout lane typically calls
/// <see cref="Present"/>). The compositor is never blocked by inference.
/// </para>
/// <para>GPU/COM objects are disposed deterministically on the UI thread during <see cref="DisposeAsync"/>.</para>
/// </remarks>
public sealed class DirectCompositionOverlay : IOverlayRenderer
{
    // Minimum padding inside a backplate, in DIPs.
    private const double MinPaddingDip = 2.0;

    private readonly ILogger<DirectCompositionOverlay> _logger;
    private readonly object _gate = new();

    // UI-thread machinery.
    private Thread? _uiThread;
    private uint _uiThreadId;
    private readonly ConcurrentQueue<Action> _uiActions = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _running;
    private volatile bool _disposed;
    private Exception? _startupError;

    // Window.
    private IntPtr _hwnd;
    private string? _className;
    private bool _classRegistered;
    private NativeMethods.WndProc? _wndProcDelegate; // keep alive to avoid GC of the thunk

    // Target mapping.
    private RectI _screenBounds = RectI.Empty; // virtual-desktop pixels
    private double _dpi = 96.0;

    // Graphics device chain.
    private ID3D11Device? _d3dDevice;
    private IDXGIDevice? _dxgiDevice;
    private IDXGIFactory2? _dxgiFactory;
    private IDXGISwapChain1? _swapChain;
    private IDCompositionDevice? _compDevice;
    private IDCompositionTarget? _compTarget;
    private IDCompositionVisual? _rootVisual;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ID2D1Bitmap1? _targetBitmap;
    private IDWriteFactory? _dwriteFactory;
    private TextLayoutEngine? _layout;

    private int _swapWidth;
    private int _swapHeight;
    private bool _mapDiagLogged; // emit the coordinate-mapping diagnostic once per target

    // Latest frame to draw (latest-wins; the render thread reads this).
    private OverlayFrame _pendingFrame = OverlayFrame.Empty;
    private bool _visible;
    private OverlayStyle _style;

    /// <summary>Creates the overlay renderer. The UI thread/window is created lazily on first use.</summary>
    public DirectCompositionOverlay(OverlayStyle? style = null, ILogger<DirectCompositionOverlay>? logger = null)
    {
        _style = style ?? new OverlayStyle();
        _logger = logger ?? NullLogger<DirectCompositionOverlay>.Instance;
    }

    /// <inheritdoc />
    public bool IsVisible
    {
        get { lock (_gate) return _visible; }
    }

    /// <inheritdoc />
    public OverlayStyle Style
    {
        get { lock (_gate) return _style; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _style = value;
            // Re-render with the new style if we have a frame up.
            EnsureStarted();
            Post(RenderCurrent);
        }
    }

    /// <inheritdoc />
    public void Show()
    {
        EnsureStarted();
        lock (_gate) _visible = true;
        Post(() =>
        {
            if (_hwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            RenderCurrent();
        });
    }

    /// <inheritdoc />
    public void Hide()
    {
        lock (_gate) _visible = false;
        if (!_running) return;
        Post(() =>
        {
            if (_hwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        });
    }

    /// <inheritdoc />
    public void SetTargetBounds(RectI screenBounds, double dpi)
    {
        EnsureStarted();
        lock (_gate)
        {
            _screenBounds = screenBounds;
            _dpi = dpi <= 0 ? 96.0 : dpi;
        }
        Post(() => ApplyTargetBounds(screenBounds, dpi <= 0 ? 96.0 : dpi));
    }

    /// <inheritdoc />
    public void Present(OverlayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed) return;
        EnsureStarted();
        lock (_gate) _pendingFrame = frame;
        Post(RenderCurrent);
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate) _pendingFrame = OverlayFrame.Empty;
        if (!_running) return;
        Post(RenderCurrent);
    }

    // ----------------------------------------------------------------------------------------------------
    // UI-thread lifecycle.
    // ----------------------------------------------------------------------------------------------------

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Fast path: a worker thread has already been started (it may still be initializing). Gate on the
        // thread having been created — not on _running, which only flips true *after* the worker finishes
        // building the GPU/COM device chain. Two concurrent callers must not each spin up a UI thread.
        if (_uiThread is not null) return;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_uiThread is not null) return;

            var uiThread = new Thread(UiThreadMain)
            {
                Name = "LingoLens.Overlay.UI",
                IsBackground = true,
            };
            // STA is friendliest for windowing/COM apartment behavior.
            uiThread.SetApartmentState(ApartmentState.STA);
            // Publish the field before Start() so a racing caller observes it and falls through to _ready.Wait().
            _uiThread = uiThread;
            uiThread.Start();
        }

        // Block until the window + device chain are up (or we have a startup error).
        _ready.Wait();
        if (_startupError is not null)
            throw new InvalidOperationException("Overlay UI thread failed to start.", _startupError);
    }

    private void UiThreadMain()
    {
        try
        {
            _uiThreadId = NativeMethods.GetCurrentThreadId();
            EnsureDpiAwareness();
            CreateWindow();
            CreateDevice();
            _running = true;
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _logger.LogError(ex, "Failed to initialize the overlay window/compositor.");
            _ready.Set();
            return;
        }

        _ready.Set();

        // Message pump: drain queued UI actions on every iteration. We use GetMessage (blocking) plus a
        // posted WM_NULL-style wake via PostMessage from Post() to stay responsive without busy-waiting.
        while (_running)
        {
            // Process all pending cross-thread actions first.
            DrainActions();

            if (!NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
                break; // WM_QUIT

            if (msg.message == NativeMethods.WM_ST_SHUTDOWN)
            {
                _running = false;
                break;
            }

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        // Final drain (e.g. disposal action posted just before shutdown).
        DrainActions();
        TeardownGraphics();
        DestroyWindowSafe();
    }

    private void DrainActions()
    {
        while (_uiActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { _logger.LogError(ex, "Overlay UI action threw."); }
        }
    }

    private void Post(Action action)
    {
        if (_disposed && !_running) return;
        _uiActions.Enqueue(action);
        // Wake the pump so the action runs even if no other messages arrive.
        if (_hwnd != IntPtr.Zero)
            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_APP, IntPtr.Zero, IntPtr.Zero);
        else if (_uiThreadId != 0)
            NativeMethods.PostThreadWake(_uiThreadId);
    }

    private static void EnsureDpiAwareness()
    {
        // Per-monitor-v2; guard if a prior call/manifest already set awareness (returns false ⇒ ignore).
        try { NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* not fatal: older OS or already set via manifest */ }

        // Critically, also set the context for THIS UI thread before the window is created. Without it, on a
        // scaled display (e.g. 150%) the layered overlay window and SetWindowPos get DPI-virtualized: the
        // physical-pixel swapchain is placed by DWM at a scaled screen position, which lands the translated
        // text well below the source (the monitor-mode vertical offset). Per-thread v2 stops that.
        try { NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* older OS without the per-thread API; the process-level call above still helps */ }
    }

    private void CreateWindow()
    {
        IntPtr hInstance = NativeMethods.GetModuleHandleW(null);
        _className = "LingoLensOverlay_" + Guid.NewGuid().ToString("N");

        _wndProcDelegate = WndProc;
        var wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);

        IntPtr classNamePtr = Marshal.StringToHGlobalUni(_className);
        try
        {
            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                style = NativeMethods.CS_HREDRAW | NativeMethods.CS_VREDRAW,
                lpfnWndProc = wndProcPtr,
                hInstance = hInstance,
                hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, (IntPtr)NativeMethods.IDC_ARROW),
                hbrBackground = IntPtr.Zero, // no GDI background — DComp owns all pixels
                lpszClassName = classNamePtr,
            };

            ushort atom = NativeMethods.RegisterClassExW(ref wc);
            if (atom == 0)
                throw new InvalidOperationException(
                    $"RegisterClassExW failed (0x{Marshal.GetLastWin32Error():X8}).");

            // RegisterClassExW copies the class name into its own storage; the HGLOBAL is no longer needed.
            _classRegistered = true;
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }

        uint exStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT |
                       NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE |
                       NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOREDIRECTIONBITMAP;

        // Start hidden and zero-sized; SetTargetBounds will place it.
        _hwnd = NativeMethods.CreateWindowExW(
            exStyle,
            _className,
            "LingoLens Overlay",
            NativeMethods.WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateWindowExW failed (0x{Marshal.GetLastWin32Error():X8}).");

        // Extend the (empty) frame so the whole window is composited per-pixel; with
        // WS_EX_NOREDIRECTIONBITMAP + DComp this gives a fully transparent click-through surface.
        var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        int dwmHr = NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        if (dwmHr < 0)
            _logger.LogWarning("DwmExtendFrameIntoClientArea returned 0x{Hr:X8}.", dwmHr);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                // Belt-and-suspenders: even without WS_EX_TRANSPARENT the window never grabs the mouse.
                return (IntPtr)NativeMethods.HTTRANSPARENT;

            case NativeMethods.WM_APP:
                // Wake-only message used by Post(); actions are drained in the pump loop.
                return IntPtr.Zero;

            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_DPICHANGED:
                // Reapply bounds; the host typically also calls SetTargetBounds, but be self-healing.
                Post(() => ApplyTargetBounds(_screenBounds, _dpi));
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Device / swapchain.
    // ----------------------------------------------------------------------------------------------------

    private void CreateDevice()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        };

        // BGRA support is required for Direct2D interop; DComp requires it too.
        var flags = DeviceCreationFlags.BgraSupport;

        // TODO(verify-on-hardware): confirm the default adapter chosen here matches the compute device the
        // pipeline picked (IComputeDeviceManager). For the overlay the choice is cosmetic, but cross-adapter
        // present can be slower; if needed, plumb the selected DXGI adapter LUID in via SetTargetBounds.
        var hr = D3D11.D3D11CreateDevice(
            adapter: null!,
            DriverType.Hardware,
            flags,
            featureLevels,
            out var device);

        if (hr.Failure || device is null)
        {
            // Fall back to WARP (software) so the overlay still works on machines without a usable GPU.
            _logger.LogWarning("Hardware D3D11 device creation failed ({Hr}); falling back to WARP.", hr);
            hr = D3D11.D3D11CreateDevice(null!, DriverType.Warp, flags, featureLevels, out device);
            hr.CheckError();
        }

        _d3dDevice = device!;
        _dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();

        // DirectComposition device bound to the DXGI device.
        _compDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _compDevice.CreateTargetForHwnd(_hwnd, topmost: true, out _compTarget).CheckError();
        _compDevice.CreateVisual(out _rootVisual).CheckError();
        _compTarget!.SetRoot(_rootVisual).CheckError();

        // Direct2D device + context over the same DXGI device.
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(_dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        // DXGI factory for the composition swapchain.
        var adapter = _dxgiDevice.GetAdapter();
        _dxgiFactory = adapter.GetParent<IDXGIFactory2>();
        adapter.Dispose();

        _dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);
        _layout = new TextLayoutEngine(_dwriteFactory);
    }

    private void EnsureSwapChain(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_swapChain is not null && _swapWidth == width && _swapHeight == height)
            return;

        if (_swapChain is null)
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied, // per-pixel alpha against the desktop
                Flags = SwapChainFlags.None,
            };

            _swapChain = _dxgiFactory!.CreateSwapChainForComposition(_dxgiDevice!, desc, null);
            _rootVisual!.SetContent(_swapChain).CheckError();
            _compDevice!.Commit().CheckError();
        }
        else
        {
            // Release the bitmap that targets the old backbuffer before resizing.
            _d2dContext!.Target = null;
            _targetBitmap?.Dispose();
            _targetBitmap = null;
            _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None)
                      .CheckError();
        }

        _swapWidth = width;
        _swapHeight = height;
        BindBackbuffer();
    }

    private void BindBackbuffer()
    {
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            96f, 96f,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _targetBitmap = _d2dContext!.CreateBitmapFromDxgiSurface(surface, props);
        _d2dContext.Target = _targetBitmap;
    }

    private void ApplyTargetBounds(RectI bounds, double dpi)
    {
        if (_hwnd == IntPtr.Zero) return;

        int reqW = Math.Max(1, bounds.Width);
        int reqH = Math.Max(1, bounds.Height);
        int w = reqW, h = reqH;
        _mapDiagLogged = false; // re-log the coordinate mapping for this (new) target

        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST,
            bounds.X, bounds.Y, w, h,
            NativeMethods.SWP_NOACTIVATE);

        // Size the swapchain to the window's ACTUAL realized client size, not the requested size. With the
        // overlay thread set to per-monitor-v2 these match exactly; reading back keeps the swapchain pixel
        // grid aligned to the on-screen pixel grid even if any residual DPI-virtualization remains, so the
        // mapped boxes land on top of the source text instead of being scaled/shifted.
        bool gotRect = NativeMethods.GetWindowRect(_hwnd, out var wr);
        if (gotRect)
        {
            w = Math.Max(1, wr.Right - wr.Left);
            h = Math.Max(1, wr.Bottom - wr.Top);
        }

        // Unconditional placement diagnostic (once per target): shows whether the overlay window landed
        // where it was asked to. A realized rect whose top/left differs from the requested bounds, or whose
        // size differs from the request, is the smoking gun for a monitor-mode offset — and unlike the
        // per-item "Overlay map:" line, this is logged even when no text is drawn.
        uint winDpi = NativeMethods.GetDpiForWindow(_hwnd);
        _logger.LogInformation(
            "Overlay placement: requested=({BX},{BY} {BW}x{BH}) realized={Realized} dpi={Dpi}.",
            bounds.X, bounds.Y, reqW, reqH,
            gotRect ? $"({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom})" : "(n/a)", winDpi);

        EnsureSwapChain(w, h);
        RenderCurrent();
    }

    // ----------------------------------------------------------------------------------------------------
    // Drawing.
    // ----------------------------------------------------------------------------------------------------

    private void RenderCurrent()
    {
        if (!_running || _d2dContext is null || _swapChain is null) return;

        OverlayFrame frame;
        OverlayStyle style;
        bool visible;
        RectI bounds;
        lock (_gate)
        {
            frame = _pendingFrame;
            style = _style;
            visible = _visible;
            bounds = _screenBounds;
        }

        if (!visible)
            return;

        EnsureSwapChain(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));

        try
        {
            DrawFrame(frame, style, bounds);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            _logger.LogError(ex, "Direct2D draw failed; attempting device recovery.");
            RecoverDevice();
        }
    }

    private void DrawFrame(OverlayFrame frame, OverlayStyle style, RectI bounds)
    {
        var ctx = _d2dContext!;
        // We render in device pixels (the swapchain is sized in pixels); geometry is mapped directly from
        // source-pixel space into window-local pixel space, so no extra DPI scale factor is applied here.
        ctx.BeginDraw();
        ctx.Transform = Matrix3x2.Identity;
        ctx.Clear(new Color4(0f, 0f, 0f, 0f)); // fully transparent

        if (frame.Items.Count > 0 && !frame.SourceBounds.IsEmpty)
        {
            // Map source-pixel space → window-local pixel space.
            float sx = bounds.Width / (float)Math.Max(1, frame.SourceBounds.Width);
            float sy = bounds.Height / (float)Math.Max(1, frame.SourceBounds.Height);
            float ox = -frame.SourceBounds.X * sx;
            float oy = -frame.SourceBounds.Y * sy;

            // Coordinate diagnostics: pins down a "translations land in the wrong place" report by showing
            // the requested bounds, the actual on-screen window rect + DPI, and where item 0 maps to. Logged
            // ONCE per target (at Information) so it appears in a normal log without per-frame spam — exactly
            // what's needed to diagnose a placement offset from a user's log.
            if (!_mapDiagLogged)
            {
                _mapDiagLogged = true;
                var fb0 = frame.Items[0].SourceBox.Bounds;
                uint dpi = _hwnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(_hwnd) : 0;
                string winRect = (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var wr))
                    ? $"({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom})" : "(n/a)";
                _logger.LogInformation(
                    "Overlay map: bounds=({BX},{BY} {BW}x{BH}) win={Win} dpi={Dpi} swap={SwW}x{SwH} src=({CX},{CY} {CW}x{CH}) scale=({Sx:F3},{Sy:F3}); item0 box=({IX},{IY}) → plate=({PX},{PY}).",
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, winRect, dpi, _swapWidth, _swapHeight,
                    frame.SourceBounds.X, frame.SourceBounds.Y, frame.SourceBounds.Width, frame.SourceBounds.Height,
                    sx, sy, (int)fb0.X, (int)fb0.Y, (int)((float)fb0.X * sx + ox), (int)((float)fb0.Y * sy + oy));
            }

            foreach (var item in frame.Items)
                DrawItem(ctx, item, style, sx, sy, ox, oy);
        }

        var hr = ctx.EndDraw();
        if (hr.Failure)
        {
            _logger.LogWarning("EndDraw reported {Hr}.", hr);
            if (hr.Code == unchecked((int)0x887A0005)) // DXGI_ERROR_DEVICE_REMOVED
            {
                RecoverDevice();
                return;
            }
        }

        // Vsync-locked present (syncInterval = 1). Never blocks the inference lane (we're on the UI thread).
        _swapChain!.Present(1, PresentFlags.None);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Overlay drew {Count} item(s) into {W}x{H} (first opacity {Op:F2}).",
                frame.Items.Count, _swapWidth, _swapHeight,
                frame.Items.Count > 0 ? frame.Items[0].Opacity : 0.0);
    }

    private void DrawItem(ID2D1DeviceContext ctx, OverlayItem item, OverlayStyle style,
        float sx, float sy, float ox, float oy)
    {
        // Map the source quad's AABB into window-local pixels.
        var b = item.SourceBox.Bounds;
        float left = (float)b.X * sx + ox;
        float top = (float)b.Y * sy + oy;
        float width = (float)b.Width * sx;
        float height = (float)b.Height * sy;
        if (width <= 1f || height <= 1f) return;

        float opacity = (float)Math.Clamp(item.Opacity, 0.0, 1.0);
        if (opacity <= 0.001f) return;

        // Choose backplate + text colors.
        var (plate, textColor) = ResolveColors(item, style);
        float plateAlpha = (float)Math.Clamp(style.BackplateOpacity, 0.0, 1.0) * opacity;

        float radius = (float)Math.Max(0, style.CornerRadius);

        // Readable font size from the source line height (English at ~the original line height), floored so
        // it is never tiny. We deliberately do NOT shrink text to fit the narrow source box — English is
        // longer than the Chinese it replaces, so the backplate grows to fit instead of squashing the glyphs.
        float fontSize = MathF.Min(28f, MathF.Max(TextLayoutEngine.MinReadableFontDip, height * 0.82f));
        float pad = MathF.Max((float)MinPaddingDip, fontSize * 0.28f);

        // Keep the box anchored to the source line: allow it to grow to at most ~2.2x the source width
        // (or a readable minimum), wrapping to more lines beyond that, and never past the window edge. This
        // stops a long translation from sprawling across the screen and overlapping its neighbours.
        float maxTextWidth = MathF.Min(_swapWidth - left - 2 * pad, MathF.Max(width * 2.2f, fontSize * 12f));
        maxTextWidth = MathF.Max(1f, maxTextWidth);
        using var layout = _layout!.CreateReadableLayout(
            item.Text ?? string.Empty, fontSize, maxTextWidth, _swapHeight, style.FontFamily, out var tr);

        // Grow the backplate to the measured text (at least the original box), clamped to the window.
        float plateW = MathF.Max(1f, MathF.Min(_swapWidth - left, MathF.Max(width, tr.Width + 2 * pad)));
        float plateH = MathF.Max(1f, MathF.Max(height, tr.Height + 2 * pad));
        // Safety: never let one backplate dominate the screen if a line decodes unexpectedly long.
        plateH = MathF.Min(plateH, MathF.Max(height * 2.2f, fontSize * 5f));

        // 1) Rounded translucent backplate, sized to the readable text.
        var plateRect = new System.Drawing.RectangleF(left, top, plateW, plateH);
        using var plateBrush = ctx.CreateSolidColorBrush(new Color4(plate.R, plate.G, plate.B, plateAlpha));
        ctx.FillRoundedRectangle(new RoundedRectangle(plateRect, radius, radius), plateBrush);

        var origin = new Vector2(left + pad, top + pad);

        // 3) Soft shadow + thin outline for legibility on any background.
        // Draw a slightly offset, semi-transparent dark copy as a shadow, then the foreground text.
        float shadowAlpha = 0.55f * opacity;
        // Pick the halo from the text's perceived brightness (luminance), not its alpha: light text needs a
        // dark shadow, dark text needs a light halo. Alpha is effectively always 1 here, so keying on it
        // meant dark text never received the legibility halo it needs.
        float textLuminance = 0.2126f * textColor.R + 0.7152f * textColor.G + 0.0722f * textColor.B;
        var shadowColor = textLuminance > 0.5f
            ? new Color4(0f, 0f, 0f, shadowAlpha)   // light text ⇒ dark shadow
            : new Color4(1f, 1f, 1f, shadowAlpha);  // dark text ⇒ light halo
        using var shadowBrush = ctx.CreateSolidColorBrush(shadowColor);

        // 1px halo in the four cardinal directions approximates a thin outline cheaply.
        ctx.DrawTextLayout(new Vector2(origin.X + 1, origin.Y + 1), layout, shadowBrush, DrawTextOptions.Clip);
        ctx.DrawTextLayout(new Vector2(origin.X - 1, origin.Y + 1), layout, shadowBrush, DrawTextOptions.Clip);
        ctx.DrawTextLayout(new Vector2(origin.X + 1, origin.Y - 1), layout, shadowBrush, DrawTextOptions.Clip);
        ctx.DrawTextLayout(new Vector2(origin.X - 1, origin.Y - 1), layout, shadowBrush, DrawTextOptions.Clip);

        using var textBrush = ctx.CreateSolidColorBrush(new Color4(textColor.R, textColor.G, textColor.B, opacity));
        ctx.DrawTextLayout(origin, layout, textBrush, DrawTextOptions.Clip);
    }

    private static (Color4 plate, Color4 text) ResolveColors(OverlayItem item, OverlayStyle style)
    {
        // Explicit per-item colors win.
        if (item.ForegroundArgb is uint fg && item.BackgroundArgb is uint bg)
            return (FromArgb(bg), FromArgb(fg));

        if (!style.AutoContrast)
        {
            // Neutral defaults: dark plate, white text.
            return (new Color4(0.06f, 0.06f, 0.08f, 1f), new Color4(1f, 1f, 1f, 1f));
        }

        // Auto-contrast: pick a plate from the item's background hint (or neutral) and a text color that
        // maximizes contrast against it.
        Color4 plate = item.BackgroundArgb is uint b
            ? FromArgb(b)
            : new Color4(0.06f, 0.06f, 0.08f, 1f);

        float luminance = 0.2126f * plate.R + 0.7152f * plate.G + 0.0722f * plate.B;
        Color4 text = luminance < 0.5f
            ? new Color4(0.98f, 0.98f, 1f, 1f)   // dark plate ⇒ light text
            : new Color4(0.05f, 0.05f, 0.07f, 1f); // light plate ⇒ dark text
        return (plate, text);
    }

    private static Color4 FromArgb(uint argb)
    {
        float a = ((argb >> 24) & 0xFF) / 255f;
        float r = ((argb >> 16) & 0xFF) / 255f;
        float g = ((argb >> 8) & 0xFF) / 255f;
        float bch = (argb & 0xFF) / 255f;
        return new Color4(r, g, bch, a);
    }

    private void RecoverDevice()
    {
        // Tear down and rebuild the full graphics chain after a device-removed/reset.
        try { TeardownGraphics(); } catch (Exception ex) { _logger.LogError(ex, "Teardown during recovery failed."); }
        try
        {
            CreateDevice();
            RectI bounds; double dpi;
            lock (_gate) { bounds = _screenBounds; dpi = _dpi; }
            if (!bounds.IsEmpty) ApplyTargetBounds(bounds, dpi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Overlay device recovery failed.");
        }
    }

    private void TeardownGraphics()
    {
        if (_d2dContext is not null) _d2dContext.Target = null;
        _targetBitmap?.Dispose(); _targetBitmap = null;
        _layout?.Dispose(); _layout = null;
        _dwriteFactory?.Dispose(); _dwriteFactory = null;
        _swapChain?.Dispose(); _swapChain = null;
        _rootVisual?.Dispose(); _rootVisual = null;
        _compTarget?.Dispose(); _compTarget = null;
        _compDevice?.Dispose(); _compDevice = null;
        _d2dContext?.Dispose(); _d2dContext = null;
        _d2dDevice?.Dispose(); _d2dDevice = null;
        _d2dFactory?.Dispose(); _d2dFactory = null;
        _dxgiFactory?.Dispose(); _dxgiFactory = null;
        _dxgiDevice?.Dispose(); _dxgiDevice = null;
        _d3dDevice?.Dispose(); _d3dDevice = null;
        _swapWidth = _swapHeight = 0;
    }

    private void DestroyWindowSafe()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        // The window must be gone before the class can be unregistered. Both happen on the UI thread that
        // owns the class, so registration is balanced and no atom is leaked across overlay lifetimes.
        if (_classRegistered && _className is not null)
        {
            if (!NativeMethods.UnregisterClassW(_className, NativeMethods.GetModuleHandleW(null)))
                _logger.LogWarning("UnregisterClassW failed (0x{Err:X8}).", Marshal.GetLastWin32Error());
            _classRegistered = false;
        }

        _wndProcDelegate = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var thread = _uiThread;
        if (thread is null || !_running)
        {
            _ready.Dispose();
            return;
        }

        // Ask the pump to stop, then join off the calling thread so we never block a hot path.
        Post(() => _running = false);
        if (_uiThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_uiThreadId, NativeMethods.WM_ST_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.PostThreadMessageW(_uiThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        await Task.Run(() =>
        {
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                _logger.LogWarning("Overlay UI thread did not exit within the timeout.");
        }).ConfigureAwait(false);

        _ready.Dispose();
    }
}
