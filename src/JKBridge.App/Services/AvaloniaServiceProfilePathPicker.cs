using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace JKBridge.App.Services;

/// <summary>
/// Native Avalonia file-picker adapter for the non-secret Service Profile JSON format.
/// </summary>
public sealed class AvaloniaServiceProfilePathPicker : IServiceProfilePathPicker
{
    private static readonly FilePickerFileType JsonFileType = new("JK Bridge Service Profile")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    private readonly Func<Window?> _getWindow;

    public AvaloniaServiceProfilePathPicker(Func<Window?> getWindow)
    {
        _getWindow = getWindow ?? throw new ArgumentNullException(nameof(getWindow));
    }

    public async Task<string?> PickExportPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = GetStorageProvider();
        if (!provider.CanSave)
        {
            throw new ServiceProfileTransferException("当前系统不支持保存文件选择器。");
        }

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 JK Bridge 服务配置",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices = [JsonFileType],
        });
        return file?.TryGetLocalPath()
               ?? (file is null
                   ? null
                   : throw new ServiceProfileTransferException("请选择 Windows 本地文件路径。"));
    }

    public async Task<string?> PickImportPathAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = GetStorageProvider();
        if (!provider.CanOpen)
        {
            throw new ServiceProfileTransferException("当前系统不支持打开文件选择器。");
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "预览 JK Bridge 服务配置",
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType],
        });
        var file = files.SingleOrDefault();
        return file?.TryGetLocalPath()
               ?? (file is null
                   ? null
                   : throw new ServiceProfileTransferException("请选择 Windows 本地文件路径。"));
    }

    private IStorageProvider GetStorageProvider()
    {
        var window = _getWindow()
                     ?? throw new ServiceProfileTransferException("主窗口尚未准备好，无法打开文件选择器。");
        return TopLevel.GetTopLevel(window)?.StorageProvider
               ?? throw new ServiceProfileTransferException("无法访问系统文件选择器。");
    }
}
