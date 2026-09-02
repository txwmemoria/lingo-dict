namespace LingoDict.App.ViewModels;

/// <summary>
/// 「上一个 / 下一个」翻的是哪个列表。跟随当前标签页——
/// 在词书页翻词书，在收藏页翻收藏，不必为每个场景记特例。
/// </summary>
public enum BrowseScope
{
    Search,
    Book,
    Favorites,
    History,

    /// <summary>学习页没有列表可翻。</summary>
    None,
}
