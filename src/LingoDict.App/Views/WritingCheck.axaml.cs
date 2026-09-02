using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LingoDict.App.ViewModels;

namespace LingoDict.App.Views;

public partial class WritingCheck : UserControl
{
    public WritingCheck()
    {
        InitializeComponent();

        // 选文件得走 StorageProvider，而它挂在窗口上——ViewModel 够不着，
        // 所以由视图把这一步接上。
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WritingViewModel model)
            {
                model.PickModelFile = PickAsync;
            }
        };
    }

    private async Task<string?> PickAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage })
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 gector-int8.onnx",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("ONNX 模型") { Patterns = ["*.onnx"] }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
