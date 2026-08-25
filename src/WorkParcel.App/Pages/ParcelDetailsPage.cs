using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkParcel_App.Models;

namespace WorkParcel_App.Pages;

public sealed partial class ParcelDetailsPage : PageBase
{
    private Parcel? _parcel;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Parameter is Guid id) _parcel = Store.Parcels.Concat(Store.Archived).FirstOrDefault(parcel => parcel.Id == id);
        else if (e.Parameter is Parcel parcel) _parcel = Store.Parcels.Concat(Store.Archived).FirstOrDefault(item => item.Id == parcel.Id);
        Build();
    }

    private void Build()
    {
        if (_parcel is null) { SetContent(Ui.Card(Ui.Text("Parcel not found.", 14), 20)); return; }
        var back = Ui.Button("← BACK"); back.Click += (_, _) => { if (Frame?.CanGoBack == true) Frame.GoBack(); };
        var edit = Ui.Button("EDIT PARCEL"); edit.Click += async (_, _) => { await Dialogs.ShowEditParcelAsync(this, _parcel); Build(); };
        var action = Ui.Button(_parcel.Status == ParcelStatus.Open ? "PACK AWAY" : "OPEN PARCEL", _parcel.Status == ParcelStatus.Open); action.Click += async (_, _) => { await Dialogs.ShowStateChangeAsync(this, _parcel); Build(); };
        var menu = Ui.IconButton("…", "Parcel actions"); var flyout = new MenuFlyout();
        var archive = new MenuFlyoutItem { Text = "Archive" }; archive.Click += async (_, _) => { if (await Dialogs.Confirm(this, "ARCHIVE PARCEL", "Move this saved record to Archive?", "ARCHIVE")) { await Store.ArchiveAsync(_parcel); if (Frame?.CanGoBack == true) Frame.GoBack(); } };
        var delete = new MenuFlyoutItem { Text = "Delete permanently" }; delete.Click += async (_, _) => { if (await Dialogs.ConfirmPermanentDelete(this, _parcel)) { await Store.DeleteAsync(_parcel); if (Frame?.CanGoBack == true) Frame.GoBack(); } };
        flyout.Items.Add(archive); flyout.Items.Add(delete); menu.Flyout = flyout;
        var header = Ui.Stack(10); header.Children.Add(Ui.Row(back, Ui.Spacer(), edit, action, menu)); header.Children.Add(Ui.Text(_parcel.Name, 28, true)); if (!string.IsNullOrWhiteSpace(_parcel.Description)) header.Children.Add(Ui.Text(_parcel.Description, 13, false, "#8D9CA2")); header.Children.Add(Ui.Row(Ui.Tag(_parcel.StatusText, Ui.StateColor(_parcel.Status)), Ui.Mono("0 ITEMS", 10), Ui.Mono($"CREATED { _parcel.CreatedAt:g}".ToUpperInvariant(), 10)));
        var body = Body(15); body.Children.Add(header); body.Children.Add(Ui.Rule());
        var empty = Ui.Stack(7); empty.Children.Add(Ui.Mono("EMPTY SETUP", 11, "#9BE28F", true)); empty.Children.Add(Ui.Text("This parcel has 0 saved items. Application, window, browser, file and folder capture will be connected in Part 3.", 13, false, "#8D9CA2")); body.Children.Add(Ui.Card(empty, 18));
        body.Children.Add(Ui.SectionHeader("PARCEL HISTORY", "Meaningful saved changes only."));
        if (_parcel.History.Count == 0) body.Children.Add(Ui.Text("No history yet.", 12, false, "#8D9CA2"));
        foreach (var entry in _parcel.History.OrderByDescending(entry => entry.Timestamp).Take(50)) body.Children.Add(HistoryRow(entry));
        SetContent(body);
    }

    private static Border HistoryRow(ParcelHistoryEntry entry) => new() { Child = Ui.Row(Ui.Mono(entry.Timestamp.ToString("HH:mm"), 10, "#9BE28F", true), Ui.Tag(entry.EventType.ToString().ToUpperInvariant(), "#8D9CA2"), Ui.Text(entry.Summary, 12)), Padding = new Thickness(8, 6, 8, 6), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
}
