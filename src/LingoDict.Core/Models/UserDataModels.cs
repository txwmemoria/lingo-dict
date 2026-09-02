namespace LingoDict.Core.Models;

/// <summary>
/// 指向某个词条的最小信息集。收藏与历史把这些字段冗余存进用户库，
/// 这样用户库和只读内容库可以是两个独立文件，无需跨库联表。
/// </summary>
public interface IWordReference
{
    long WordId { get; }

    long EntryId { get; }

    string Headword { get; }

    string Phonetic { get; }

    string Translation { get; }
}

/// <summary>不带时间戳的轻量引用，用于把 <see cref="WordDetail"/> 传给用户库。</summary>
public sealed record WordReference(
    long WordId,
    long EntryId,
    string Headword,
    string Phonetic,
    string Translation) : IWordReference;

public sealed record FavoriteEntry(
    long WordId,
    long EntryId,
    string Headword,
    string Phonetic,
    string Translation,
    DateTimeOffset CreatedAt) : IWordReference;

public sealed record HistoryEntry(
    long WordId,
    long EntryId,
    string Headword,
    string Phonetic,
    string Translation,
    DateTimeOffset ViewedAt,
    int ViewCount) : IWordReference;

public sealed record UserDataStats(int FavoriteCount, int HistoryCount, int NoteCount);

/// <summary>
/// 一本词书上次看到哪儿了——纸质词典里夹的那根书签。
/// </summary>
/// <remarks>
/// 连词头一起存，是为了词书内容换了之后（重新生成 dictionary.db、word_id 变了）
/// 还能告诉用户「上次看到 abandon」，而不是一个查不到的数字。
/// </remarks>
public sealed record BookProgress(string BookId, long WordId, string Headword, DateTimeOffset UpdatedAt);

/// <summary>
/// 界面与朗读偏好。<paramref name="FontScale"/> 会整体乘到字号阶梯上，
/// <paramref name="FontFamily"/> 为空表示用内置的默认字体栈，
/// <paramref name="SpeechVoice"/> 为空表示用系统默认嗓音。
/// </summary>
public sealed record AppearanceSettings(
    double FontScale,
    string? FontFamily,
    string? SpeechVoice = null,
    double LeftPaneWidth = AppearanceSettings.DefaultPaneWidth,
    string ThemeMode = AppearanceTheme.System,
    string Accent = AppearanceTheme.DefaultAccent)
{
    public const double MinScale = 0.85;

    public const double MaxScale = 1.4;

    public const double DefaultPaneWidth = 360;

    public const double MinPaneWidth = 300;

    public const double MaxPaneWidth = 560;

    public static AppearanceSettings Default { get; } = new(1.0, null);

    public AppearanceSettings Clamp() => this with
    {
        FontScale = double.IsFinite(FontScale) ? Math.Clamp(FontScale, MinScale, MaxScale) : 1.0,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? null : FontFamily.Trim(),
        SpeechVoice = string.IsNullOrWhiteSpace(SpeechVoice) ? null : SpeechVoice.Trim(),
        LeftPaneWidth = double.IsFinite(LeftPaneWidth)
            ? Math.Clamp(LeftPaneWidth, MinPaneWidth, MaxPaneWidth)
            : DefaultPaneWidth,
        ThemeMode = AppearanceTheme.NormalizeMode(ThemeMode),
        // 主题色只存 id，认不出来的（旧版本存的、手改坏的）退回默认，
        // 而不是让界面拿一个不存在的 id 去查表。
        Accent = string.IsNullOrWhiteSpace(Accent) ? DefaultAccentId : Accent.Trim().ToLowerInvariant(),
    };

    private const string DefaultAccentId = AppearanceTheme.DefaultAccent;
}

/// <summary>明暗模式的取值。放在 Core 里，因为它要随 <see cref="AppearanceSettings"/> 一起存。</summary>
public static class AppearanceTheme
{
    /// <summary>跟随系统。</summary>
    public const string System = "system";

    public const string Light = "light";

    public const string Dark = "dark";

    /// <summary>默认主题色的 id。具体色值由界面层的调色板决定，这里只认这个字符串。</summary>
    public const string DefaultAccent = "blue";

    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Light => Light,
        Dark => Dark,
        _ => System,
    };
}
