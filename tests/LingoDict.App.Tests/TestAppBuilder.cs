using Avalonia;
using Avalonia.Headless;
using LingoDict.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace LingoDict.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<LingoDict.App.App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
