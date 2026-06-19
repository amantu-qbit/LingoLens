using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LingoLens.App.Services;
using LingoLens.App.ViewModels;
using LingoLens.App.Views;
using LingoLens.Capture;
using LingoLens.Compute;
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

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services ?? throw new InvalidOperationException("Host not started.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
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
            .ConfigureLogging(l => l.AddDebug())
            .Build();

        await _host.StartAsync();

        var main = _host.Services.GetRequiredService<MainWindow>();
        main.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                var pipeline = _host.Services.GetService<Core.Pipeline.ITranslationPipeline>();
                if (pipeline is not null) await pipeline.StopAsync();
            }
            catch { /* best-effort shutdown */ }

            await _host.StopAsync(TimeSpan.FromSeconds(3));

            // The host's singletons (pipeline/translator/capture/ocr/overlay) are
            // IAsyncDisposable-only; synchronous Dispose() would throw, so dispose async.
            await ((IAsyncDisposable)_host).DisposeAsync();
        }
        base.OnExit(e);
    }
}
