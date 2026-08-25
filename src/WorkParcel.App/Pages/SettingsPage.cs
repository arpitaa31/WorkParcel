using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkParcel_App.Services;

namespace WorkParcel_App.Pages;

public sealed partial class SettingsPage : PageBase
{
    private readonly StackPanel _data = Ui.Stack(8);

    public SettingsPage()
    {
        var body = Body(15); body.Children.Add(Header("SETTINGS", "Local data, appearance and honest feature boundaries.")); body.Children.Add(Section("APPEARANCE", Appearance())); body.Children.Add(Section("LOCAL DATA", _data)); body.Children.Add(Section("PART 3 PREVIEW", Preview())); body.Children.Add(Section("ABOUT", About())); SetContent(body); _ = LoadDataAsync();
    }

    private Border Section(string title, UIElement content) { var stack = Ui.Stack(10); stack.Children.Add(Ui.Mono(title, 11, "#9BE28F", true)); stack.Children.Add(content); return Ui.Card(stack, 15); }
    private UIElement Appearance()
    {
        var stack = Ui.Stack(7); stack.Children.Add(Ui.Text("Theme", 13, true)); var row = Ui.Row(); foreach (var theme in new[] { "System", "Light", "Dark" }) { var button = Ui.Button(theme.ToUpperInvariant()); button.Click += async (_, _) => { await Store.SetThemeAsync(theme); RequestedTheme = theme == "Dark" ? ElementTheme.Dark : theme == "Light" ? ElementTheme.Light : ElementTheme.Default; }; row.Children.Add(button); } stack.Children.Add(row); return stack;
    }

    private UIElement Preview()
    {
        var stack = Ui.Stack(7); stack.Children.Add(Ui.Text("Real applications, windows, browser tabs, files and folders are not captured or restored yet.", 12, false, "#8D9CA2")); var button = Ui.Button("CAPTURE CONNECTION — PART 3"); button.IsEnabled = false; stack.Children.Add(button); return stack;
    }

    private UIElement About() { var stack = Ui.Stack(4); stack.Children.Add(Ui.Text("WorkParcel", 14, true)); stack.Children.Add(Ui.Mono("LOCAL PARCEL UTILITY", 10)); stack.Children.Add(Ui.Text("Save a setup. Pack it away. Open it when you return.", 12, false, "#8D9CA2")); return stack; }

    private async Task LoadDataAsync()
    {
        try
        {
            var counts = await Store.GetCountsAsync(); _data.Children.Clear(); _data.Children.Add(Ui.Text($"DATABASE STATUS   {Store.DatabaseStatus}", 12, true)); _data.Children.Add(Ui.Text($"DATABASE SIZE   {FormatSize(Store.DatabaseSize)}", 12)); _data.Children.Add(Ui.Text($"PARCELS   {counts.Parcels}", 12)); _data.Children.Add(Ui.Text($"TODAY ITEMS   {counts.TodayItems}", 12)); _data.Children.Add(Ui.Mono($"DATA FOLDER   {Store.Paths.DataDirectory}", 10)); _data.Children.Add(Ui.Mono($"LAST INITIALIZED   {Store.LastSuccessfulInitializationUtc?.ToLocalTime():g}", 10));
            var row = Ui.Row(); var open = Ui.Button("OPEN DATA FOLDER"); open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(Store.Paths.DataDirectory) { UseShellExecute = true }); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); } }; var backup = Ui.Button("BACK UP DATA"); backup.Click += async (_, _) => { try { await Store.BackupAsync(); await Dialogs.ShowMessage(this, "BACKUP CREATED", "A new SQLite backup was saved without overwriting an earlier backup."); } catch (Exception exception) { AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "BACKUP FAILED", "The local database could not be backed up."); } }; row.Children.Add(open); row.Children.Add(backup); _data.Children.Add(row);
        }
        catch (Exception exception) { AppLogger.LogTechnicalError(exception); _data.Children.Clear(); _data.Children.Add(Ui.Text("Local data information is unavailable.", 12, false, "#F0B45B")); }
    }

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / (1024d * 1024):0.0} MB";
}
