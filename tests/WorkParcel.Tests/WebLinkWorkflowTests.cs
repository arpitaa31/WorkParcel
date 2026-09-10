using System.Diagnostics;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using Xunit;

namespace WorkParcel.Tests;

public sealed class WebLinkWorkflowTests
{
    [Fact]
    public async Task WebLinksUseDefaultBrowserAndContinueAfterOneLaunchFails()
    {
        var started = new List<ProcessStartInfo>();
        var launcher = new ItemLaunchService(info =>
        {
            started.Add(info);
            if (info.FileName.Contains("/fail", StringComparison.Ordinal)) throw new InvalidOperationException("simulated launch failure");
            return true;
        });
        var items = new[]
        {
            new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.WebLink, DisplayName = "First", Value = "https://example.com/first", LaunchEnabled = true },
            new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.WebLink, DisplayName = "Fails", Value = "https://example.com/fail", LaunchEnabled = true },
            new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.WebLink, DisplayName = "Last", Value = "https://example.com/last", LaunchEnabled = true }
        };

        var results = await launcher.OpenAsync(items);

        Assert.Equal(3, results.Count);
        Assert.Equal(ItemOpenStatus.Opened, results[0].Status);
        Assert.Equal(ItemOpenStatus.Failed, results[1].Status);
        Assert.Equal(ItemOpenStatus.Opened, results[2].Status);
        Assert.Equal(new[] { "https://example.com/first", "https://example.com/fail", "https://example.com/last" }, started.Select(info => info.FileName));
        Assert.All(started, info => Assert.True(info.UseShellExecute));
    }

    [Fact]
    public void LinkParserReportsExactInvalidLineNumbersAndKeepsValidLinks()
    {
        var parsed = WebLinkRules.ParseMany("\nhttps://example.com/one\njavascript:bad\nhttps://example.com/two\nfile:///secret");

        Assert.Equal(new[] { "https://example.com/one", "https://example.com/two" }, parsed.Valid.Select(link => link.AbsoluteUri));
        Assert.Equal(new[] { "javascript:bad", "file:///secret" }, parsed.Invalid);
        Assert.Equal(new[] { 3, 5 }, parsed.InvalidLineNumbers);
    }
}
