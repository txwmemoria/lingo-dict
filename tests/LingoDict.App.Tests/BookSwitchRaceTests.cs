using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LingoDict.App.ViewModels;
using LingoDict.Core.Models;

namespace LingoDict.App.Tests;

/// <summary>
/// 快速切换词书。
/// </summary>
/// <remarks>
/// 词表那次 await 早就有代号保护了（先点 A 再点 B，A 后返回会把词表盖掉）。
/// 但紧接着还有一次 <c>GetBookProgressAsync</c> 取书签，那一处没拦。
///
/// 以前碰不到：Microsoft.Data.Sqlite 全是同步的，取书签就地完成。
/// 用户库挪到线程池之后它成了真正的挂起点——A 的书签回来时用户可能已经在 B 上了。
/// </remarks>
public sealed class BookSwitchRaceTests
{
    [AvaloniaFact]
    public void AStaleBookmarkCannotJumpTheSelectionInsideAnotherBook()
    {
        var dictionary = new FakeDictionaryRepository();
        dictionary.Books.Add(new BookSummary("CET6_1", "六级词汇", "测试", 2));

        var userData = new FakeUserDataRepository();

        // A 的书签落在第二个词上；B 还没有书签，打开时会停在第一个词。
        var second = dictionary.Results[1];
        userData.BookProgress["CET4_1"] =
            new BookProgress("CET4_1", second.Id, second.Headword, DateTimeOffset.Now);

        // 让 A 的书签查询挂住
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        userData.BookProgressGates["CET4_1"] = gate;

        var vm = new MainViewModel(dictionary, userData, new FakeStudyRepository());
        vm.InitializeAsync();
        Pump();

        vm.SelectedBook = vm.Books.Single(x => x.Id == "CET4_1");   // 卡在取书签这一步
        Pump();

        vm.SelectedBook = vm.Books.Single(x => x.Id == "CET6_1");   // 切到 B，一路跑完
        Pump();
        Assert.Contains("六级词汇", vm.StatusMessage);
        var landedInB = vm.SelectedBookWord;

        gate.TrySetResult();                                         // A 这时才返回
        Pump();

        // A 是上一代的，整批作废。没有这道拦截时：_bookmark 会变成 A 的，
        // 选中的词被 A 的书签拽到 cancellation 上，状态栏还报「四级词汇」——
        // 用户人在 B，看到的却是 A 的书签。
        Assert.Equal(landedInB?.Id, vm.SelectedBookWord?.Id);
        Assert.Contains("六级词汇", vm.StatusMessage);
        Assert.DoesNotContain("cancellation", vm.BookmarkHint);
    }

    private static void Pump()
    {
        for (var round = 0; round < 12; round++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
