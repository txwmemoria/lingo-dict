using System.Collections;
using System.Linq;
using Avalonia.Data.Converters;

namespace LingoDict.App.Converters;

public static class AppConverters
{
    /// <summary>集合为空时隐藏整段——避免"短语"标题底下空无一物。</summary>
    public static readonly IValueConverter IsNotEmpty =
        new FuncValueConverter<IEnumerable?, bool>(value => value is not null && value.Cast<object?>().Any());

    /// <summary>真题例句往往没有中文译文，空着的话不要占一行的高度。</summary>
    public static readonly IValueConverter IsNotBlank =
        new FuncValueConverter<string?, bool>(value => !string.IsNullOrWhiteSpace(value));
}
