using System.Runtime.ExceptionServices;

namespace LingoDict.Core.Tests;

/// <summary>
/// 在一个专属线程上跑一段代码。
/// </summary>
/// <remarks>
/// 「查询没跑在调用线程上」这个断言，直接在测试方法里写是不成立的：
/// xunit 的测试方法本身就跑在线程池线程上，await 一让出去，那个线程就回到池子里，
/// 完全可能被 Task.Run 挑去执行查询——于是线程号相等，但代码是对的。
///
/// 换成自己新建的线程就没有这个歧义：线程池永远不会用它跑委托。
/// 于是「相等」只剩一种解释——查询就地同步跑了。
/// </remarks>
internal static class DedicatedThread
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
