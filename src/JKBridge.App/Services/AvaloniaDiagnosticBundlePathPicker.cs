using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace JKBridge.App.Services;

/// <summary>
/// Native save-picker adapter for a diagnostic ZIP. It only obtains the path;
/// DiagnosticBundleService remains responsible for safe archive construction and
/// atomic persistence.
/// </summary>
public sealed class AvaloniaDiagnosticBundlePathPicker : IDiagnosticBundlePathPicker
{
    private static readonly FilePickerFileType ZipFileType = new("JK Bridge Diagnostic Bundle")
    {
        Patterns = ["*.zip"],
        MimeTypes = ["application/zip"],
    };

    private readonly Func<Window?> _getWindow;

    public AvaloniaDiagnosticBundlePathPicker(Func<Window?> getWindow)
    {
        _getWindow = getWindow ?? throw new ArgumentNullException(nameof(getWindow));
    }

    public async Task<string?> PickExportPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = _getWindow()
                     ?? throw new DiagnosticBundleException(
                         "主窗口尚未准备好，无法导出诊断包。");
        var provider = TopLevel.GetTopLevel(window)?.StorageProvider
                       ?? throw new DiagnosticBundleException(
                           "无法访问系统文件选择器。");
        if (!provider.CanSave)
        {
            throw new DiagnosticBundleException(
                "当前系统不支持保存文件选择器。");
        }

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出安全诊断包",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [ZipFileType],
        });
        return file?.TryGetLocalPath()
               ?? (file is null
                   ? null
                   : throw new DiagnosticBundleException(
                       "请选择 Windows 本地文件路径。"));
    }
}
