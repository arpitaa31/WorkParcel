using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkParcel_App.Services;

namespace WorkParcel_App.Pages;

public sealed partial class SettingsPage : PageBase
{
    private readonly StackPanel _data = Ui.Stack(8);

    public SettingsPage()
    {
        BuildPage();
        _ = LoadDataAsync();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadDataAsync();
    }

    private void BuildPage()
    {
        var body = Body(15);
        body.Children.Add(Header("SETTINGS", "Make WorkParcel fit the way you work."));
        body.Children.Add(Section("APPEARANCE", Appearance()));
        body.Children.Add(Section("LOCAL DATA", _data));
        body.Children.Add(Section("DIAGNOSTICS", Diagnostics()));
        body.Children.Add(Section("ABOUT", About()));
        SetContent(body);
    }

    private static Border Section(string title, UIElement content)
    {
        var stack = Ui.Stack(10);
        stack.Children.Add(Ui.Mono(title, 11, "#9BE28F", true));
        stack.Children.Add(content);
        return Ui.Card(stack, 15);
    }

    private UIElement Appearance()
    {
        var stack = Ui.Stack(7);
        stack.Children.Add(Ui.Text("Choose the look that is most comfortable for you.", 13));
        var feedback = Ui.Text($"CURRENT THEME: {Store.Theme.ToUpperInvariant()}", 10, false, "#8D9CA2");
        var row = Ui.Row();
        foreach (var theme in new[] { "System", "Light", "Dark" })
        {
            var button = Ui.Button(theme.ToUpperInvariant());
            button.Click += async (_, _) => await RunButtonActionAsync(button, feedback, "SAVING", async () =>
            {
                await Store.SetThemeAsync(theme);
                RequestedTheme = theme == "Dark" ? ElementTheme.Dark : theme == "Light" ? ElementTheme.Light : ElementTheme.Default;
                feedback.Text = $"CURRENT THEME: {theme.ToUpperInvariant()}";
            }, "THEME SAVED");
            row.Children.Add(button);
        }
        stack.Children.Add(row);
        stack.Children.Add(feedback);
        return stack;
    }

    private UIElement Diagnostics()
    {
        var stack = Ui.Stack(7);
        stack.Children.Add(Ui.Text("WorkParcel uses local SQLite data and normal Windows shell actions. Web links are saved as URLs; page contents are never fetched just to save a link.", 12, false, "#8D9CA2"));
        var feedback = Ui.Text("READY", 10, false, "#8D9CA2");
        var row = Ui.Row();
        var refresh = Ui.Button("REFRESH STATUS");
        refresh.Click += async (_, _) => await RunButtonActionAsync(refresh, feedback, "REFRESHING", LoadDataAsync, "STATUS REFRESHED");
        var openLogs = Ui.Button("OPEN LOG FOLDER");
        openLogs.Click += async (_, _) => await RunButtonActionAsync(openLogs, feedback, "OPENING", OpenLogFolderAsync, "LOG FOLDER OPENED");
        row.Children.Add(refresh);
        row.Children.Add(openLogs);
        stack.Children.Add(row);
        stack.Children.Add(feedback);
        return stack;
    }

    private UIElement About()
    {
        var stack = Ui.Stack(5);
        stack.Children.Add(Ui.Text("WorkParcel", 14, true));
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.2.0-beta.3";
        version = version.Replace("-beta.", " Beta ", StringComparison.OrdinalIgnoreCase).Replace("-", " ", StringComparison.OrdinalIgnoreCase);
        stack.Children.Add(Ui.Mono($"VERSION {version.ToUpperInvariant()}", 10));
        stack.Children.Add(Ui.Mono("LOCAL PARCEL UTILITY", 10));
        stack.Children.Add(Ui.Text("Save the apps, files, folders and web links connected to a task, pack the setup away and reopen it later.", 12, false, "#8D9CA2"));
        stack.Children.Add(Ui.Text("Web links reopen in the Windows default browser. Chrome and Edge are captured only as whole application windows; individual tabs are not available.", 12, false, "#8D9CA2"));
        return stack;
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var counts = await Store.GetCountsAsync();
            _data.Children.Clear();
            _data.Children.Add(Ui.Text($"DATABASE STATUS   {Store.DatabaseStatus}", 12, true));
            _data.Children.Add(Ui.Text($"DATABASE SIZE   {FormatSize(Store.DatabaseSize)}", 12));
            _data.Children.Add(Ui.Text($"PARCELS   {counts.Parcels}", 12));
            _data.Children.Add(Ui.Text($"TODAY ITEMS   {counts.TodayItems}", 12));
            _data.Children.Add(Ui.Mono($"DATA FOLDER   {Store.Paths.DataDirectory}", 10));
            _data.Children.Add(Ui.Mono($"LAST INITIALIZED   {Store.LastSuccessfulInitializationUtc?.ToLocalTime():g}", 10));

            var row = Ui.Row();
            var feedback = Ui.Text("READY", 10, false, "#8D9CA2");
            var open = Ui.Button("OPEN DATA FOLDER");
            open.Click += async (_, _) => await RunButtonActionAsync(open, feedback, "OPENING", OpenDataFolderAsync, "DATA FOLDER OPENED");
            var backup = Ui.Button("BACK UP DATA");
            backup.Click += async (_, _) => await RunButtonActionAsync(backup, feedback, "BACKING UP", async () =>
            {
                var path = await Store.BackupAsync();
                await Dialogs.ShowMessage(this, "BACKUP CREATED", $"A SQLite backup was saved here:\n{path}");
            }, "BACKUP CREATED");
            var clear = Ui.Button("CLEAR ICON CACHE");
            clear.Click += async (_, _) => await RunButtonActionAsync(clear, feedback, "CLEARING", ClearIconCacheAsync, "CACHE CLEARED");
            row.Children.Add(open);
            row.Children.Add(backup);
            row.Children.Add(clear);
            _data.Children.Add(row);
            _data.Children.Add(feedback);
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            _data.Children.Clear();
            _data.Children.Add(Ui.Text("Local data information is unavailable.", 12, false, "#F0B45B"));
        }
    }

    private static Task OpenDataFolderAsync()
    {
        var result = ExternalLaunchService.TryOpenFolder(WorkspaceStore.Current.Paths.DataDirectory);
        if (!result.Succeeded) throw new UserFacingActionException(result.Message);
        return Task.CompletedTask;
    }

    private static Task OpenLogFolderAsync()
    {
        var result = ExternalLaunchService.TryOpenFolder(WorkspaceStore.Current.Paths.LogDirectory);
        if (!result.Succeeded) throw new UserFacingActionException(result.Message);
        return Task.CompletedTask;
    }

    private async Task ClearIconCacheAsync()
    {
        try
        {
            var path = Store.Paths.IconCacheDirectory;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            Store.Paths.EnsureDirectories();
            await Dialogs.ShowMessage(this, "ICON CACHE CLEARED", "Local file and application icons were removed. Saved parcel records were not changed.");
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            throw new UserFacingActionException("The icon cache could not be cleared.");
        }
    }

    private async Task RunButtonActionAsync(Button button, TextBlock feedback, string busyLabel, Func<Task> action, string successLabel)
    {
        if (!button.IsEnabled) return;
        var original = button.Content is TextBlock text ? text.Text : button.Content?.ToString() ?? string.Empty;
        button.IsEnabled = false;
        button.Content = Ui.Mono($"{busyLabel}...", 11, null, true);
        feedback.Text = $"{busyLabel}...";
        feedback.Foreground = Ui.Brush("#F0B45B");
        try
        {
            await action();
            feedback.Text = successLabel;
            feedback.Foreground = Ui.Brush("#9BE28F");
        }
        catch (Exception exception)
        {
            AppLogger.LogTechnicalError(exception);
            var message = exception is UserFacingActionException ? exception.Message : "ACTION FAILED - TRY AGAIN";
            feedback.Text = message;
            feedback.Foreground = Ui.Brush("#EF7777");
            await Dialogs.ShowMessage(this, "ACTION FAILED", message);
        }
        finally
        {
            button.Content = Ui.Mono(original, 11, null, true);
            button.IsEnabled = true;
        }
    }

    private sealed class UserFacingActionException : Exception
    {
        public UserFacingActionException(string message) : base(message) { }
    }

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / (1024d * 1024):0.0} MB";
}
