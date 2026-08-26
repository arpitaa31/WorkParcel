using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;

namespace WorkParcel_App.Services;

internal sealed class WorkspacePickerService
{
    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*"); Initialize(picker);
        var files = await picker.PickMultipleFilesAsync(); return files.Where(file => !string.IsNullOrWhiteSpace(file.Path)).Select(file => file.Path).ToList();
    }

    public async Task<string?> PickApplicationAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".exe"); picker.FileTypeFilter.Add(".lnk"); Initialize(picker);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*"); Initialize(picker);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private static void Initialize(object picker)
    {
        var window = (Application.Current as App)?.MainWindow ?? throw new InvalidOperationException("The WorkParcel window is not ready.");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
    }
}
