using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LingoDict.App.Controls;
using LingoDict.App.Services;
using LingoDict.App.ViewModels;
using LingoDict.App.Views;
using LingoDict.Grammar;
using LingoDict.Infrastructure.Data;

namespace LingoDict.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainViewModel viewModel;
            var speech = new SpeechService();
            try
            {
                var userDataPath = UserDataPathResolver.Resolve(desktop.Args);
                var dictionaryPath = DictionaryPathResolver.Resolve();
                viewModel = new MainViewModel(
                    new SqliteDictionaryRepository(dictionaryPath),
                    new SqliteUserDataRepository(userDataPath),

                    // 学习库也要认得词典：新词按词频投放，而词频在词典库里。
                    new SqliteStudyRepository(userDataPath, dictionaryPath),
                    userDataPath,
                    speech,
                    new GectorGrammarChecker(),
                    new GrammarModelInstaller());
                viewModel.AppearanceChanged += (_, settings) => Appearance.Apply(this, settings);
            }
            catch (Exception exception)
            {
                viewModel = MainViewModel.CreateUnavailable(
                    $"词典数据库未就绪：{exception.Message} 请先运行 dotnet run --project src/LingoDict.Importer");
            }

            var window = new MainWindow { DataContext = viewModel };
            viewModel.PhoneticsRequested += (_, _) =>
                new PhoneticsWindow { DataContext = viewModel.CreatePhonetics() }.Show(window);

            viewModel.SettingsRequested += (_, _) =>
                new SettingsWindow { DataContext = viewModel }.ShowDialog(window);

            desktop.MainWindow = window;

            // 关窗口时把还没念完的那条掐掉，并放掉 SAPI/子进程——
            // 否则声音会拖到界面消失之后还在响。
            desktop.ShutdownRequested += (_, _) => speech.Dispose();

            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
