// SPDX-License-Identifier: AGPL-3.0-or-later
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using VDF.Core;
using VDF.Core.Selection;
using VDF.Core.ViewModels;
using VDF.Web.Components.Pages;
using VDF.Web.Services;

namespace VDF.Web.Tests.Components;

[Collection("WebSettingsOverride")]
public sealed class AutomationPageTests : BunitContext {
    readonly string folder = Path.Combine(Path.GetTempPath(), "VDF.WebAutomationPage." + Guid.NewGuid().ToString("N"));
    readonly ScanService scan;
    readonly WebAutomationStore store;

    public AutomationPageTests() {
        Directory.CreateDirectory(folder);
        WebSettingsService.TestOverrideSettingsPath = Path.Combine(folder, "settings.json");
        var settings = new WebSettingsService();
        scan = new ScanService(settings);
        scan.Settings.CustomDatabaseFolder = folder;
        store = new WebAutomationStore(Path.Combine(folder, "settings.automation.json"));
        Services.AddSingleton(settings);
        Services.AddSingleton(scan);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        scan.Dispose();
        WebSettingsService.TestOverrideSettingsPath = null;
        Directory.Delete(folder, recursive: true);
    }

    async Task<IRenderedComponent<Automation>> RenderPageAsync() {
        var page = Render<Automation>();
        // Initialization performs durable journal IO. Yield the test thread
        // while the renderer completes it, rather than blocking its continuations.
        await page.WaitForAssertionAsync(() => {
            Assert.Contains("Saved automation settings loaded", page.Markup);
            Assert.False(Button(page, "Save automation settings").HasAttribute("disabled"));
        });
        return page;
    }

    [Fact]
    public async Task LocalDraftDoesNotChangeDurableSelectionOrProtectionUntilSaved() {
        var page = await RenderPageAsync();
        await ChangeCheckboxAsync(page, "Automatically prepare selection after scans", true);
        await ChangeCheckboxAsync(page, "Enable local NFO protection", true);

        var original = await store.LoadAsync();
        Assert.False(original.Selection.AutoAfterScan);
        Assert.False(original.Nfo.Enabled);
        Assert.Contains("Unsaved draft changes", page.Markup);
        Assert.True(Button(page, "Preview saved selection").HasAttribute("disabled"));

        await ClickAsync(page, "Save automation settings");
        await page.WaitForAssertionAsync(() => Assert.Contains("Automation settings saved.", page.Markup));
        var saved = await store.LoadAsync();
        Assert.True(saved.Selection.AutoAfterScan);
        Assert.True(saved.Nfo.Enabled);
    }

    [Fact]
    public async Task StaleSaveKeepsDraftAndShowsConflictUntilExplicitReload() {
        var page = await RenderPageAsync();
        await ChangeCheckboxAsync(page, "Automatically prepare selection after scans", true);
        var concurrent = await store.LoadAsync();
        concurrent.Nfo.Enabled = true;
        await store.SaveAsync(concurrent, folder);

        await ClickAsync(page, "Save automation settings");
        await page.WaitForAssertionAsync(() => Assert.Contains("Newer Web automation settings were saved", page.Find("[role='alert']").TextContent));
        Assert.True(Input(page, "Automatically prepare selection after scans").IsChecked());
        Assert.False(Input(page, "Enable local NFO protection").IsChecked());
        Assert.False((await store.LoadAsync()).Selection.AutoAfterScan);

        await ClickAsync(page, "Reload saved settings");
        await page.WaitForAssertionAsync(() => {
            Assert.Empty(page.FindAll("[role='alert']"));
            Assert.False(Input(page, "Automatically prepare selection after scans").IsChecked());
            Assert.True(Input(page, "Enable local NFO protection").IsChecked());
        });
    }

    [Fact]
    public async Task NamedProfilePreservesSelectionSnapshotWhileDraftChanges() {
        var page = await RenderPageAsync();
        await page.InvokeAsync(() => Input(page, "Profile name").ChangeAsync("Archive"));
        await ClickAsync(page, "Save current selection as named profile");
        var preferences = page.FindAll("label").Single(label => label.TextContent.Contains("Preferred codecs"));
        await page.InvokeAsync(() => preferences.QuerySelector("textarea")!.ChangeAsync("av1\nhevc"));
        await ClickAsync(page, "Save automation settings");
        await page.WaitForAssertionAsync(() => Assert.Contains("Automation settings saved.", page.Markup));

        var saved = await store.LoadAsync();
        Assert.Equal(new[] { "av1", "hevc" }, saved.Selection.PreferredCodecs);
        Assert.Empty(Assert.Single(saved.Profiles).Options.PreferredCodecs);
        Assert.Equal("Archive", saved.Profiles[0].Name);
    }

    [Fact]
    public async Task InvalidatedPlanClearsAutomaticCandidatesAndPreservesExplicitCheckboxEdits() {
        await scan.LoadAutomationAsync();
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        Seed("group-a-1.mp4", groupA); Seed("group-a-2.mp4", groupA);
        Seed("group-b-1.mp4", groupB); Seed("group-b-2.mp4", groupB);
        scan.CaptureCleanupSnapshots(folder);
        var plan = await scan.PreviewSelectionAsync();
        var candidateA = plan.Decisions.Single(decision => decision.GroupId == groupA && decision.RemoveCandidate).Path;
        var candidateB = plan.Decisions.Single(decision => decision.GroupId == groupB && decision.RemoveCandidate).Path;
        var page = Render<Results>();
        await page.WaitForAssertionAsync(() => Assert.Equal(2, page.FindAll(".dup-card.selected").Count));

        // Deliberately deselect/reselect B so its checkbox belongs to the user.
        await page.InvokeAsync(() => Card(page, candidateB).QuerySelector(".card-check input")!.ChangeAsync(false));
        await page.WaitForAssertionAsync(() => Assert.False(Card(page, candidateB).ClassList.Contains("selected")));
        await page.InvokeAsync(() => Card(page, candidateB).QuerySelector(".card-check input")!.ChangeAsync(true));
        await page.WaitForAssertionAsync(() => Assert.True(Card(page, candidateB).ClassList.Contains("selected")));

        var row = (await scan.GetReviewRowsAsync()).Single(row => row.Item.Path == candidateA);
        await scan.SetReviewAsync(row.Item, ReviewChoice.Keep, true, row.Revision);
        await page.WaitForAssertionAsync(() => {
            Assert.False(Card(page, candidateA).ClassList.Contains("selected"));
            Assert.True(Card(page, candidateB).ClassList.Contains("selected"));
        });
        Assert.All(scan.Duplicates, item => Assert.True(File.Exists(item.Path)));
    }

    void Seed(string name, Guid group) {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        scan.Engine.Duplicates.Add(new DuplicateItem(new FileEntry(path), .02f, group, DuplicateFlags.None));
    }

    static IElement Card(IRenderedComponent<Results> page, string path) => page.FindAll(".dup-card")
        .Single(card => card.QuerySelector(".card-filename")!.TextContent == Path.GetFileName(path));

    static IElement Input(IRenderedComponent<Automation> page, string labelText) =>
        page.FindAll("label").Single(label => label.TextContent.Trim().StartsWith(labelText, StringComparison.Ordinal)).QuerySelector("input")!;

    static IElement Button(IRenderedComponent<Automation> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    static async Task ChangeCheckboxAsync(IRenderedComponent<Automation> page, string labelText, bool value) {
        // bUnit's synchronous event helpers can return while dispatch is still
        // queued. Await the renderer event before querying/sending the next one.
        await page.InvokeAsync(() => Input(page, labelText).ChangeAsync(value));
        await page.WaitForAssertionAsync(() => Assert.Equal(value, Input(page, labelText).IsChecked()));
    }

    [Fact]
    public async Task TwoAutomationTabsRefreshSavedReviewsWithoutReadNotificationPingPong() {
        var group = Guid.NewGuid();
        var rows = new List<DuplicateItem>();
        foreach (var name in new[] { "tab-a.mp4", "tab-b.mp4" }) {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var item = new DuplicateItem(new FileEntry(path), .01f, group, DuplicateFlags.None);
            rows.Add(item);
            scan.Engine.Duplicates.Add(item);
        }
        scan.CaptureCleanupSnapshots(folder);
        var first = await RenderPageAsync();
        var second = await RenderPageAsync();
        var panel = first.FindAll(".action-panel").Single(element => element.TextContent.Contains(rows[0].Path));
        var keep = panel.QuerySelectorAll("button").Single(element => element.TextContent.Trim() == "Keep");
        await first.InvokeAsync(() => keep.ClickAsync(new MouseEventArgs()));
        await first.WaitForAssertionAsync(() => Assert.Contains("Review decision saved.", first.Markup));
        await first.WaitForAssertionAsync(() => Assert.Contains("Review: Keep.", first.Markup));
        await second.WaitForAssertionAsync(() => Assert.Contains("Review: Keep.", second.Markup));
        Assert.Empty(first.FindAll("[role='alert']"));
        Assert.Empty(second.FindAll("[role='alert']"));
        await first.WaitForAssertionAsync(() => Assert.False(scan.IsBusy));
        int notifications = 0;
        scan.StateChanged += Count;
        try {
            await Task.Delay(150);
            Assert.Equal(0, Volatile.Read(ref notifications));
            Assert.False(scan.IsBusy);
        }
        finally { scan.StateChanged -= Count; }
        void Count() => Interlocked.Increment(ref notifications);
    }

    static Task ClickAsync(IRenderedComponent<Automation> page, string text) =>
        page.InvokeAsync(() => Button(page, text).ClickAsync(new MouseEventArgs()));
}
