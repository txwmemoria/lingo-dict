using Avalonia;
using LingoDict.Core.Models;

namespace LingoDict.App.Controls;

/// <summary>
/// 把一份外观偏好整体落到 <see cref="Application"/> 上。
/// </summary>
/// <remarks>
/// 字号和调色板是两套独立的东西，但它们必须一起应用。分成两个调用点的时候，
/// 无头截图那条路径只记得调字号、漏了调色板——换主题色渲染出来毫无变化，
/// 而这既不报错也不会有测试失败。收成一个入口，漏不掉。
/// </remarks>
public static class Appearance
{
    public static void Apply(Application application, AppearanceSettings settings)
    {
        Typography.Apply(application, settings);
        Palette.Apply(application, settings);
    }
}
