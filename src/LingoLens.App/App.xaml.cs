using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using LingoLens.App.Services;
using LingoLens.App.ViewModels;
using LingoLens.App.Views;
using LingoLens.Capture;
using LingoLens.Compute;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;
using LingoLens.Core.Hosting;
using LingoLens.Ocr;
using LingoLens.Overlay;
using LingoLens.Pipeline;
using LingoLens.Translation;

namespace LingoLens.App;

public partial class App : Application
{
    private IHost? _host;

    /// <summary>Where logs land: %LOCALAPPDATA%\LingoLens\logs</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingoLens", "logs");

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services ?? throw new InvalidOperationException("Host not started.");

    public App()
    {
        // Catch failures that happen even before/around OnStartup.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InitializeLogging();
        LogEnvironment();

        try
        {
            Log.Information("Building host…");
            _host = Host.CreateDefaultBuilder()
                .UseSerilog() // route every ILogger<T> to the same Serilog file
                .ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.SetBasePath(AppContext.BaseDirectory);
                    cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .ConfigureServices((context, services) =>
                {
                    IConfiguration config = context.Configuration;

                    services
                        .AddLingoLensCore(options =>
                            config.GetSection(LingoLensOptions.SectionName).Bind(options))
                        .AddLingoLensCompute()
                        .AddLingoLensCapture()
                        .AddLingoLensOcr()
                        .AddLingoLensTranslation()
                        .AddLingoLensOverlay()
                        .AddLingoLensPipeline();

                    services.AddSingleton<TargetEnumerator>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            Log.Information("Starting host…");
            await _host.StartAsync();

            LogComputeDiagnostics();

            Log.Information("Resolving main window…");
            var main = _host.Services.GetRequiredService<MainWindow>();

            Log.Information("Showing main window…");
            main.Show();
            Log.Information("Startup complete.");
        }
        catch (Exception ex)
        {
            Fatal("Startup failed", ex);
            Shutdown(-1);
        }
    }

    private static void InitializeLogging()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Debug()
                .WriteTo.File(
                    Path.Combine(LogDirectory, "lingolens-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch
        {
            // If logging setup itself fails, the app should still try to run / show an error.
        }
    }

    private static void LogEnvironment()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly().GetName();
            Log.Information("==== LingoLens starting ====");
            Log.Information("Version {Version}", asm.Version);
            Log.Information("OS {OS} ({Arch}); {Framework}",
                RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture,
                RuntimeInformation.FrameworkDescription);
            Log.Information("64-bit process: {Is64}", Environment.Is64BitProcess);
            Log.Information("Base dir: {Base}", AppContext.BaseDirectory);
            Log.Information("Log dir: {Log}", LogDirectory);
        }
        catch { /* never let diagnostics throw */ }
    }

    private void LogComputeDiagnostics()
    {
        try
        {
            var devices = _host!.Services.GetRequiredService<IComputeDeviceManager>();
            Log.Information("Compute devices ({Count}):", devices.AvailableDevices.Count);
            foreach (var d in devices.AvailableDevices)
                Log.Information("  - {Name} [{Provider}] vram={Vram}MB maxTier={Tier}",
                    d.Name, d.Provider, d.VramBytes / (1024 * 1024), d.MaxTier);
            Log.Information("Selected: {Name} ({Provider}); recommended tier {Tier}",
                devices.Selected.Name, devices.Selected.Provider, devices.RecommendedTier);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not log compute diagnostics");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Fatal("Unhandled UI-thread exception", e.Exception);
        e.Handled = true; // error surfaced; don't hard-crash the process
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Fatal("Unhandled exception (AppDomain)", e.ExceptionObject as Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    /// <summary>Logs a fatal error and surfaces it to the user with the log location.</summary>
    private static void Fatal(string context, Exception? ex)
    {
        try { Log.Fatal(ex, "{Context}", context); Log.CloseAndFlush(); } catch { }
        try
        {
            MessageBox.Show(
                $"{context}.\n\n{ex?.GetType().Name}: {ex?.Message}\n\n" +
                $"A detailed log was written to:\n{LogDirectory}\n\n" +
                "Please share that log file so the issue can be fixed.",
                "LingoLens — error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* headless / no desktop session */ }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutting down…");
        if (_host is not null)
        {
            try
            {
                var pipeline = _host.Services.GetService<Core.Pipeline.ITranslationPipeline>();
                if (pipeline is not null) await pipeline.StopAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Error stopping pipeline on exit"); }

            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(3));
                // Singletons (pipeline/translator/capture/ocr/overlay) are IAsyncDisposable-only.
                await ((IAsyncDisposable)_host).DisposeAsync();
            }
            catch (Exception ex) { Log.Warning(ex, "Error disposing host on exit"); }
        }
        Log.Information("==== LingoLens exited ====");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
