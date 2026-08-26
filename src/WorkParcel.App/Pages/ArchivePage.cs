using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkParcel_App.Models;

namespace WorkParcel_App.Pages;

public sealed partial class ArchivePage : PageBase
{
    private readonly StackPanel _rows = Ui.Stack(2);
    private readonly TextBox _search = new() { PlaceholderText = "Search archive", Width = 270 };

    public ArchivePage()
    {
        var body = Body(14); body.Children.Add(Header("ARCHIVE", "Saved records kept out of the working list.")); body.Children.Add(_search); body.Children.Add(Ui.Rule()); body.Children.Add(_rows); _search.TextChanged += (_, _) => Refresh(); SetContent(body); Refresh();
    }

    private void Refresh()
    {
        _rows.Children.Clear();
        var term = _search.Text.Trim();
        var list = Store.Archived.Where(parcel => string.IsNullOrWhiteSpace(term) || parcel.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || parcel.Description.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderByDescending(parcel => parcel.ArchivedAt).ToList();
        if (list.Count == 0) { var empty = Ui.Stack(7); empty.Children.Add(Ui.Mono("ARCHIVE EMPTY", 12, "#9BE28F", true)); empty.Children.Add(Ui.Text("Archived parcels will appear here.", 12, false, "#8D9CA2")); _rows.Children.Add(Ui.Card(empty, 20)); return; }
        foreach (var parcel in list)
        {
            var restore = Ui.Button("RESTORE", true); restore.Click += async (_, _) => { try { await Store.RestoreAsync(parcel); Refresh(); } catch (Exception exception) { Services.AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "RESTORE FAILED", "The parcel was not changed."); } };
            var remove = Ui.Button("DELETE"); remove.Click += async (_, _) => { if (await Dialogs.ConfirmPermanentDelete(this, parcel)) { try { await Store.DeleteAsync(parcel); Refresh(); } catch (Exception exception) { Services.AppLogger.LogTechnicalError(exception); await Dialogs.ShowMessage(this, "DELETE FAILED", "The saved record was not removed."); } } };
            var copy = Ui.Stack(3); copy.Children.Add(Ui.Text(parcel.Name, 13, true)); copy.Children.Add(Ui.Text(string.IsNullOrWhiteSpace(parcel.Description) ? "No description" : parcel.Description, 11, false, "#8D9CA2")); copy.Children.Add(Ui.Mono(parcel.ItemSummary, 10, "#8D9CA2"));
            var row = new Border { Child = Ui.Row(copy, Ui.Spacer(), Ui.Tag("ARCHIVED", "#F0B45B"), restore, remove), Padding = new Thickness(9, 7, 8, 7), BorderBrush = Ui.Resource("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
            row.DoubleTapped += (_, _) => NavigateDetails(parcel); Ui.Interactive(row); _rows.Children.Add(row);
        }
    }
}
