namespace LingoDict.Core.Models;

public sealed record WordSummary(
    long Id,
    long EntryId,
    string Headword,
    string Phonetic,
    string Translation,
    int BookCount) : IWordReference
{
    /// <summary>
    /// 典型搭配，只有搜索结果会带。中文一个词常对应英文好几个
    /// （看 → see/look/watch/read），并排看各自跟什么搭，区别自己就出来了。
    /// </summary>
    public IReadOnlyList<string> Collocations { get; init; } = [];

    long IWordReference.WordId => Id;
}

public sealed record BookSummary(
    string Id,
    string Title,
    string Tags,
    int WordCount);

public sealed record TextPair(string Primary, string Secondary);

/// <summary>
/// 一条例句。<paramref name="Emphasis"/> 是原文里被 &lt;b&gt; 标出的目标词，
/// <paramref name="Source"/> 是真题出处（如「CET4 2017.6 阅读理解」），普通例句为空。
/// </summary>
public sealed record ExampleSentence(
    string Text,
    string Translation,
    string Emphasis,
    string Source)
{
    public bool IsExam => Source.Length > 0;
}

/// <summary>一组同义词：共享同一个词性和释义的若干词头。</summary>
public sealed record SynonymGroup(string Meaning, IReadOnlyList<string> Words);

/// <summary>可跳转的关联词条。</summary>
public sealed record WordLink(string Word, string Note);

public sealed record WordDetail(
    long Id,
    long EntryId,
    string Headword,
    string UkPhone,
    string UsPhone,
    IReadOnlyList<string> Translations,
    IReadOnlyList<ExampleSentence> Sentences,
    IReadOnlyList<ExampleSentence> ExamSentences,
    IReadOnlyList<TextPair> Phrases,
    IReadOnlyList<SynonymGroup> Synonyms,
    IReadOnlyList<WordLink> RelatedWords,
    IReadOnlyList<string> SourceBooks);

public sealed record DictionaryStats(int WordCount, int BookCount, int MembershipCount);

/// <summary>
/// 同一词头在其他词书里的另一份释义。搜索只会返回命中的那一条，
/// 其余的靠这个在详情页露出来，否则用户不知道还有别的说法。
/// </summary>
/// <summary>
/// 一条短语命中。<paramref name="WordId"/> 指向它所属的词条，点开即可跳转。
/// </summary>
public sealed record PhraseHit(long WordId, string Headword, string Text, string Translation);

public sealed record AlternateEntry(long EntryId, string Translation, string Books);

/// <summary>
/// 词头的极简信息，用于例句里的悬停预览。整份索引常驻内存（约两万条），
/// 悬停判定和预览都是查表，不回数据库、也就不需要防抖。
/// </summary>
public sealed record WordPreview(string Headword, string Phonetic, string Translation);
