using Microsoft.UI.Xaml;
using WorkParcel_App.Models;

namespace WorkParcel_App.Pages;

public sealed record CaptureSeed(string Name, string Description);

public sealed partial class CapturePage : PageBase
{
    private CaptureSeed? _seed;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _seed = e.Parameter as CaptureSeed;
        Build();
    }

    private void Build()
    {
        var back = Ui.Button("← BACK"); back.Click += (_, _) => { if (Frame?.CanGoBack == true) Frame.GoBack(); };
        var body = Body(15); body.Children.Add(Ui.Row(back, Ui.Mono("CAPTURE CURRENT SETUP", 11, "#9BE28F", true))); body.Children.Add(Ui.Text("Setup capture is coming in Part 3.", 28, true)); body.Children.Add(Ui.Text("This preview is intentionally honest: WorkParcel does not inspect running applications, windows or browser tabs yet.", 14, false, "#8D9CA2"));
        var card = Ui.Stack(9); card.Children.Add(Ui.Mono("NO SYSTEM ITEMS DETECTED", 11, "#F0B45B", true)); card.Children.Add(Ui.Text("No fake applications, tabs, files or folders are shown. You can create an empty parcel now and add real capture support later.", 13)); if (_seed is not null) { card.Children.Add(Ui.Mono($"REQUESTED NAME   {_seed.Name}", 10)); if (!string.IsNullOrWhiteSpace(_seed.Description)) card.Children.Add(Ui.Mono($"DESCRIPTION   {_seed.Description}", 10)); } body.Children.Add(Ui.Card(card, 20));
        var create = Ui.Button("CREATE EMPTY PARCEL", true); create.Click += async (_, _) => { if (_seed is null) { Frame?.Navigate(typeof(ParcelsPage)); return; } var parcel = await Store.CreateEmptyAsync(_seed.Name, _seed.Description); Frame?.Navigate(typeof(ParcelDetailsPage), parcel.Id); await Dialogs.ShowMessage(this, "PARCEL CREATED", "Capture is deferred; this parcel contains 0 items."); }; body.Children.Add(create); SetContent(body);
    }
}
