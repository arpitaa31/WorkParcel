using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;
using WorkParcel_App.Models;

namespace WorkParcel_App.Pages;

public sealed partial class TodayPage : PageBase
{
    private readonly StackPanel _tasks = Ui.Stack(3);
    private readonly TextBox _input = new() { PlaceholderText = "Add a task for today", Width = 360, MaxLength = 200 };

    public TodayPage()
    {
        var add = Ui.Button("ADD TASK", true); add.Click += async (_, _) => await AddTaskAsync(); _input.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) await AddTaskAsync(); };
        var clear = Ui.Button("CLEAR COMPLETED"); clear.Click += async (_, _) => { if (await Dialogs.Confirm(this, "CLEAR COMPLETED TASKS?", "Completed Today items will be removed.", "CLEAR")) { await Store.ClearCompletedTodayTasksAsync(); Refresh(); } };
        var body = Body(14); body.Children.Add(Header("TODAY", "A lightweight list for what needs attention today.")); body.Children.Add(Ui.Row(_input, add, Ui.Spacer(), clear)); body.Children.Add(Ui.Rule()); body.Children.Add(_tasks); body.Children.Add(Ui.Rule()); body.Children.Add(Ui.SectionHeader("PARCELS USED TODAY", "Parcels marked open or packed today.")); body.Children.Add(ParcelsUsedToday()); SetContent(body); Refresh();
    }

    private async Task AddTaskAsync()
    {
        try { await Store.AddTodayTaskAsync(_input.Text); _input.Text = string.Empty; Refresh(); }
        catch (ArgumentException exception) { _input.Description = exception.Message; }
        catch (Exception exception) { Services.AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "TASK NOT SAVED", "The local data could not be updated."); }
    }

    private void Refresh()
    {
        _tasks.Children.Clear();
        if (Store.TodayTasks.Count == 0) { _tasks.Children.Add(Ui.Text("No tasks yet.", 13, false, "#8D9CA2")); return; }
        foreach (var task in Store.TodayTasks)
        {
            var check = new CheckBox { IsChecked = task.IsCompleted, VerticalAlignment = VerticalAlignment.Center }; check.Checked += async (_, _) => { await Store.ToggleTodayTaskAsync(task, true); Refresh(); }; check.Unchecked += async (_, _) => { await Store.ToggleTodayTaskAsync(task, false); Refresh(); };
            var label = Ui.Text(task.Text, 13, task.IsCompleted); if (task.IsCompleted) label.TextDecorations = TextDecorations.Strikethrough;
            var edit = Ui.Button("EDIT"); edit.Click += async (_, _) => { await EditTaskAsync(task); Refresh(); };
            var remove = Ui.Button("DELETE"); remove.Click += async (_, _) => { if (await Dialogs.Confirm(this, "DELETE TASK?", "Remove this Today item?", "DELETE")) { await Store.DeleteTodayTaskAsync(task); Refresh(); } };
            UIElement link = Ui.Mono(string.Empty, 10);
            if (task.ParcelId is Guid id && Store.Parcels.Concat(Store.Archived).FirstOrDefault(parcel => parcel.Id == id) is Parcel parcel)
            {
                var openLinked = Ui.Button("OPEN"); openLinked.Click += (_, _) => NavigateDetails(parcel);
                link = Ui.Row(Ui.Mono($"↗ {parcel.Name}", 10, "#9BE28F"), openLinked);
            }
            _tasks.Children.Add(new Border { Child = Ui.Row(check, label, link, Ui.Spacer(), edit, remove), Padding = new Thickness(8, 5, 8, 5), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }

    private async Task EditTaskAsync(TodayTask task)
    {
        var input = new TextBox { Text = task.Text, MaxLength = 200, Header = "TASK" }; var parcelPicker = ParcelPicker(task.ParcelId);
        var content = Ui.Stack(10); content.Children.Add(input); content.Children.Add(new TextBlock { Text = "LINK TO PARCEL — OPTIONAL", Foreground = Ui.Resource("SecondaryTextBrush"), FontSize = 11 }); content.Children.Add(parcelPicker);
        var dialog = new ContentDialog { Title = "EDIT TODAY ITEM", Content = content, PrimaryButtonText = "SAVE", CloseButtonText = "CANCEL", XamlRoot = XamlRoot };
        if (await Ui.ShowDialog(dialog) != ContentDialogResult.Primary) return;
        try { await Store.UpdateTodayTaskAsync(task, input.Text, task.IsCompleted, SelectedParcelId(parcelPicker)); }
        catch (ArgumentException exception) { await Dialogs.ShowMessage(this, "TASK NOT SAVED", exception.Message); }
        catch (Exception exception) { Services.AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "TASK NOT SAVED", "The local data could not be updated."); }
    }

    private ComboBox ParcelPicker(Guid? selected)
    {
        var picker = new ComboBox { Width = 320 }; picker.Items.Add(new ComboBoxItem { Content = "No linked parcel", Tag = null });
        foreach (var parcel in Store.Parcels.Concat(Store.Archived).OrderBy(parcel => parcel.Name, StringComparer.OrdinalIgnoreCase)) picker.Items.Add(new ComboBoxItem { Content = parcel.Name, Tag = parcel.Id });
        picker.SelectedIndex = selected is null ? 0 : Math.Max(0, picker.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => item.Tag is Guid id && id == selected.Value)); return picker;
    }

    private static Guid? SelectedParcelId(ComboBox picker) => (picker.SelectedItem as ComboBoxItem)?.Tag is Guid id ? id : null;

    private UIElement ParcelsUsedToday()
    {
        var stack = Ui.Stack(3); var parcels = Store.Parcels.Concat(Store.Archived).Where(parcel => parcel.LastOpenedAt?.Date == DateTime.Today || parcel.LastPackedAt?.Date == DateTime.Today).OrderByDescending(parcel => parcel.UpdatedAt).ToList();
        if (parcels.Count == 0) { stack.Children.Add(Ui.Text("No parcel activity recorded today.", 12, false, "#8D9CA2")); return stack; }
        foreach (var parcel in parcels) { var open = Ui.Button("OPEN"); open.Click += (_, _) => NavigateDetails(parcel); stack.Children.Add(Ui.Card(Ui.Row(Ui.Text(parcel.Name, 13, true), Ui.Spacer(), Ui.Tag(parcel.StatusText, Ui.StateColor(parcel.Status)), open), 9)); }
        return stack;
    }
}
