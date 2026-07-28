using System.Runtime.InteropServices;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Jalium.UI.NuGetTest.Desktop;

internal static class Program
{
    private static readonly string[] NativeLibraries =
    [
        "jalium.native.core.dll",
        "jalium.native.platform.dll",
        "jalium.native.media.core.dll",
        "jalium.native.media.dll",
        "jalium.native.d3d12.dll",
        "jalium.native.vulkan.dll",
        "jalium.native.software.dll",
        "jalium.native.browser.dll",
        "WebView2Loader.dll",
    ];

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This NuGet consumer smoke must run on Windows.");
            return 2;
        }

        foreach (string library in NativeLibraries)
        {
            string path = Path.Combine(AppContext.BaseDirectory, library);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Missing packaged native payload '{library}'.");
                return 3;
            }

            _ = NativeLibrary.Load(path);
            Console.WriteLine($"[nuget-consumer] loaded {library} from {path}");
        }

        if (args.Contains("--load-only", StringComparer.Ordinal))
        {
            Console.WriteLine("[nuget-consumer] load-only passed");
            return 0;
        }

        // 验证核心类型可以从 NuGet 包解析
        var app = new Application();
        var window = new Window
        {
            Title = "NuGet Package Test - Desktop",
            Width = 800,
            Height = 600,
            Background = new SolidColorBrush(Colors.White)
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Jalium.UI.Desktop NuGet 包测试成功！ Test",
            FontSize = 24,
            Foreground = new SolidColorBrush(Colors.Black)
        });

        panel.Children.Add(new Button
        {
            Content = new TextBlock { Text = "点击测试", FontSize = 16 },
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(16, 8, 16, 8)
        });

        window.Content = panel;
        app.MainWindow = window;

        Console.WriteLine("[Desktop NuGet Test] 所有类型解析成功:");
        Console.WriteLine($"  Application: {app.GetType().FullName}");
        Console.WriteLine($"  Window: {window.GetType().FullName}");
        Console.WriteLine($"  StackPanel: {panel.GetType().FullName}");
        Console.WriteLine($"  TextBlock: {typeof(TextBlock).FullName}");
        Console.WriteLine($"  Button: {typeof(Button).FullName}");
        Console.WriteLine($"  SolidColorBrush: {typeof(SolidColorBrush).FullName}");
        Console.WriteLine("[Desktop NuGet Test] 通过！");
        int lifetimeMilliseconds = int.TryParse(
            Environment.GetEnvironmentVariable("JALIUM_NUGET_SMOKE_MS"),
            out int parsed)
            ? Math.Clamp(parsed, 250, 10_000)
            : 1_500;
        window.Shown += (_, _) =>
        {
            Console.WriteLine("[nuget-consumer] ready");
            var closer = new Thread(() =>
            {
                Thread.Sleep(lifetimeMilliseconds);
                window.Dispatcher.BeginInvoke((Action)window.Close);
            })
            {
                IsBackground = true,
                Name = "NuGet consumer smoke timeout",
            };
            closer.Start();
        };
        window.Closed += (_, _) => Console.WriteLine("[nuget-consumer] completed");
        return app.Run();
    }
}
