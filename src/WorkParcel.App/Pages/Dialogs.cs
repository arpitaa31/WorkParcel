using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.ExceptionServices;
using WorkParcel_App.Models;
using WorkParcel_App.Services;

namespace WorkParcel_App.Pages;

internal sealed record NewParcelDialogResult(bool Captured, string Name, string Description, Parcel? Parcel);
internal sealed record ConfirmedOperationResult(bool Confirmed, bool Succeeded);

internal static class Dialogs
{
    public static async Task<NewParcelDialogResult?> ShowNewParcelAsync(Page page, Func<string, string, Task<Parcel>> create, Action<string, string>? capture = null)
    {
        var name = new TextBox { Header = "PARCEL NAME", PlaceholderText = "Enter a name", MaxLength = 80, TabIndex = 0 };
        var description = new TextBox { Header = "DESCRIPTION — OPTIONAL", PlaceholderText = "What is this setup for?", MaxLength = 240, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 76, TabIndex = 1 };
        var saveState = Ui.Mono("READY TO SAVE", 10, "#8D9CA2", true);
        var content = new Border { Background = Ui.Resource("SurfaceBrush"), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(20), Child = Ui.Stack(12) };
        var stack = (StackPanel)content.Child;
        stack.Children.Add(Ui.Mono("CREATE NEW PARCEL", 11, "#9BE28F", true));
        stack.Children.Add(Ui.Text("Save a setup you want to return to later.", 14, true));
        stack.Children.Add(Ui.Text("Create a blank parcel now, or continue to select open windows, files, folders, links and notes.", 11, false, "#8D9CA2"));
        stack.Children.Add(name);
        stack.Children.Add(description);
        stack.Children.Add(saveState);
        var dialog = new ContentDialog { Title = "CREATE NEW PARCEL", Content = content, PrimaryButtonText = "CREATE EMPTY PARCEL", SecondaryButtonText = "CAPTURE CURRENT SETUP", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = page.XamlRoot };
        NewParcelDialogResult? result = null;
        var busy = false;
        name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy) return;
            if (!ValidateFields(name, description)) return;
            busy = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            saveState.Text = "SAVING PARCEL…";
            try
            {
                var parcel = await create(name.Text.Trim(), description.Text.Trim());
                result = new NewParcelDialogResult(false, name.Text.Trim(), description.Text.Trim(), parcel);
                dialog.Hide();
            }
            catch (Exception exception)
            {
                AppLogger.LogTechnicalError(exception);
                name.Description = "Could not save this parcel. Your entered values are still here.";
                saveState.Text = "SAVE FAILED — TRY AGAIN";
                busy = false;
                dialog.IsPrimaryButtonEnabled = true;
                dialog.IsSecondaryButtonEnabled = true;
                name.Focus(FocusState.Programmatic);
            }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (busy || !ValidateFields(name, description)) return;
            result = new NewParcelDialogResult(true, name.Text.Trim(), description.Text.Trim(), null);
            capture?.Invoke(name.Text.Trim(), description.Text.Trim());
            dialog.Hide();
        };
        await Ui.ShowDialog(dialog);
        return result;
    }

    public static async Task<bool> ShowEditParcelAsync(Page page, Parcel parcel)
    {
        var originalName = parcel.Name;
        var originalDescription = parcel.Description;
        var name = new TextBox { Header = "PARCEL NAME", Text = originalName, MaxLength = 80, TabIndex = 0 };
        var description = new TextBox { Header = "DESCRIPTION — OPTIONAL", Text = originalDescription, MaxLength = 240, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 76, TabIndex = 1 };
        var state = Ui.Mono("NO UNSAVED CHANGES", 10, "#8D9CA2", true);
        var content = Ui.Stack(12); content.Children.Add(Ui.Mono("EDIT PARCEL", 11, "#9BE28F", true)); content.Children.Add(name); content.Children.Add(description); content.Children.Add(state);
        var dialog = new ContentDialog { Title = "EDIT PARCEL", Content = new Border { Background = Ui.Resource("SurfaceBrush"), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(20), Child = content }, PrimaryButtonText = "SAVE CHANGES", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Primary, XamlRoot = page.XamlRoot };
        var dirty = false;
        void UpdateDirty(object? _, TextChangedEventArgs __) { dirty = name.Text != originalName || description.Text != originalDescription; state.Text = dirty ? "UNSAVED CHANGES" : "NO UNSAVED CHANGES"; state.Foreground = dirty ? Ui.Brush("#F0B45B") : Ui.Resource("SecondaryTextBrush"); }
        name.TextChanged += UpdateDirty; description.TextChanged += UpdateDirty; name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
        var busy = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy || !ValidateFields(name, description)) return;
            busy = true; dialog.IsPrimaryButtonEnabled = false; dialog.PrimaryButtonText = "SAVING…";
            try { parcel.Name = name.Text.Trim(); parcel.Description = description.Text.Trim(); await WorkspaceStore.Current.UpdateParcelAsync(parcel, originalName, originalDescription); dialog.Hide(); }
            catch (Exception exception) { parcel.Name = originalName; parcel.Description = originalDescription; AppLogger.LogTechnicalError(exception); name.Description = "Could not save this parcel. Try again."; busy = false; dialog.IsPrimaryButtonEnabled = true; dialog.PrimaryButtonText = "SAVE CHANGES"; }
        };
        dialog.CloseButtonClick += async (_, args) =>
        {
            if (!dirty) return;
            args.Cancel = true;
            if (await Confirm(page, "DISCARD CHANGES?", "Your unsaved parcel edits will be lost.", "DISCARD")) dialog.Hide();
        };
        await Ui.ShowDialog(dialog);
        return !busy && (parcel.Name != originalName || parcel.Description != originalDescription);
    }

    public static async Task<bool> Confirm(Page page, string title, string message, string confirm = "CONFIRM") => await Ui.ShowDialog(new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = confirm, CloseButtonText = "CANCEL", XamlRoot = page.XamlRoot }) == ContentDialogResult.Primary;

    public static async Task<ConfirmedOperationResult> ConfirmAndRunAsync(Page page, string title, string message, string confirm, Func<Task<bool>> operation)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = "CANCEL",
            XamlRoot = page.XamlRoot
        };
        var busy = false;
        var confirmed = false;
        var succeeded = false;
        Exception? failure = null;

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy) return;
            busy = true;
            confirmed = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.PrimaryButtonText = "REMOVING…";
            try { succeeded = await operation(); }
            catch (Exception exception) { failure = exception; }
            dialog.Hide();
        };
        dialog.CloseButtonClick += (_, args) => { if (busy) args.Cancel = true; };

        await Ui.ShowDialog(dialog);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return new ConfirmedOperationResult(confirmed, succeeded);
    }

    public static async Task ShowMessage(Page page, string title, string message) => await Ui.ShowDialog(new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK", XamlRoot = page.XamlRoot });

    public static async Task ShowBrowserTabsHowItWorksAsync(Page page)
    {
        var content = Ui.Stack(10);
        content.Children.Add(Ui.Text("The WorkParcel browser extension lets the desktop app see the tabs you choose to include in a parcel. When you pack the parcel away, WorkParcel can close those selected tabs. Opening the parcel restores their saved links.", 13));
        content.Children.Add(Ui.Text(@"WorkParcel saves the tab title and URL.
It does not save passwords.
It does not save cookies.
It does not read page contents.
It does not capture keystrokes.
Private browsing tabs are excluded.
The browser extension is required only for browser-tab features.", 12, false, "#8D9CA2"));
        await Ui.ShowDialog(new ContentDialog
        {
            Title = "HOW BROWSER TABS WORK",
            Content = content,
            CloseButtonText = "GOT IT",
            XamlRoot = page.XamlRoot
        });
    }

    public static async Task<bool> ConfirmPermanentDelete(Page page, Parcel parcel)
    {
        var input = new TextBox { Header = "TYPE THE PARCEL NAME TO CONFIRM", PlaceholderText = parcel.Name };
        var content = Ui.Stack(10); content.Children.Add(Ui.Text($"Only WorkParcel’s saved record for {parcel.Name} will be removed.", 13)); content.Children.Add(Ui.Text("Original files, folders, applications and URLs are never touched.", 12, false, "#F0B45B")); content.Children.Add(input);
        var dialog = new ContentDialog { Title = "DELETE PARCEL PERMANENTLY", Content = content, PrimaryButtonText = "DELETE RECORD", CloseButtonText = "CANCEL", IsPrimaryButtonEnabled = false, XamlRoot = page.XamlRoot };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(input.Text.Trim(), parcel.Name, StringComparison.Ordinal);
        return await Ui.ShowDialog(dialog) == ContentDialogResult.Primary;
    }

    private static bool ValidateFields(TextBox name, TextBox description)
    {
        name.Description = string.IsNullOrWhiteSpace(name.Text) ? "A parcel name is required." : name.Text.Trim().Length > 80 ? "Use 80 characters or fewer." : null;
        description.Description = description.Text.Trim().Length > 240 ? "Use 240 characters or fewer." : null;
        return string.IsNullOrWhiteSpace(name.Description?.ToString()) && string.IsNullOrWhiteSpace(description.Description?.ToString());
    }
}
