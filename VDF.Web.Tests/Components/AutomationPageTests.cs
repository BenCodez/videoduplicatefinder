// SPDX-License-Identifier: AGPL-3.0-or-later
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using VDF.Core;
using VDF.Core.Selection;
using VDF.Core.Scheduling;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.Web.Components.Pages;
using VDF.Web.Services;

namespace VDF.Web.Tests.Components;

[Collection("WebSettingsOverride")]
public sealed class AutomationPageTests : BunitContext {
    static readonly TimeSpan JournalIoTimeout = TimeSpan.FromSeconds(10);
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
        // Initialization writes and verifies durable protection journals. It is
        // not a one-second render-time contract, especially on hosted Windows
        // filesystems. Keep the wait bounded and let IO continuations run.
        await page.WaitForAssertionAsync(() => {
            Assert.Contains("Saved automation settings loaded", page.Markup);
            Assert.False(Button(page, "Save automation settings").HasAttribute("disabled"));
        }, JournalIoTimeout);
        return page;
    }

    [Fact]
    public async Task FolderRuleBrowserUsesLiveScanPathsAndSavesOnlyOnRequest() {
        var root = Directory.CreateDirectory(Path.Combine(folder, "media")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(folder, "other-media")).FullName;
        scan.Settings.IncludeList.Add(root);
        scan.Settings.IncludeList.Add(other);
        var page = await RenderPageAsync();
        await ClickAsync(page, "Add folder rule");
        await ClickAsync(page, "Browse folder…");
        Assert.Equal(root, page.Find(".folder-path-bar input").GetAttribute("value"));
        // Created after opening: reopening a root reads the live filesystem.
        var child = Directory.CreateDirectory(Path.Combine(root, "new-folder")).FullName;
        await page.FindAll(".folder-start-paths button").Single(b => b.TextContent == root).ClickAsync(new MouseEventArgs());
        await page.FindAll(".folder-item").Single(b => b.TextContent.Contains("new-folder")).ClickAsync(new MouseEventArgs());
        await ClickAsync(page, "Select");
        Assert.Equal(child, Input(page, "Directory").GetAttribute("value"));
        Assert.Empty((await store.LoadAsync()).Selection.FolderRules);
        await ClickAsync(page, "Save automation settings");
        Assert.Equal(child, Assert.Single((await store.LoadAsync()).Selection.FolderRules).Directory);
        await ClickAsync(page, "Browse folder…");
        await page.FindAll(".folder-start-paths button").Single(b => b.TextContent == other).ClickAsync(new MouseEventArgs());
        await ClickAsync(page, "Cancel");
        Assert.Equal(child, Input(page, "Directory").GetAttribute("value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAutomationRulesOnResultsUsesSavedFolderProtectionWithoutChangingFiles(bool highlightedOnly) {
        await ScanEngine.LoadDatabase(folder);
        DatabaseUtils.Database.Clear();
        scan.Settings.IncludeList.Add(folder);
        Assert.True(scan.SaveSettings());
        await scan.LoadAutomationAsync();
        var group = Guid.NewGuid(); Seed("protected.mp4", group); Seed("candidate.mp4", group);
        foreach (var item in scan.Duplicates) {
            item.Duration = TimeSpan.FromSeconds(60);
            DatabaseUtils.Database.Add(new FileEntry(item.Path));
        }
        ScanEngine.SaveDatabase(); scan.CaptureCleanupSnapshots(folder); await scan.RemoveFromResultsAsync([]);
        var config = await scan.LoadAutomationAsync();
        config.Selection.HighlightedGroupsOnly = highlightedOnly;
        config.Selection.FolderRules.Add(new() { Directory = folder, Pattern = "protected.mp4", ProtectAll = true });
        await scan.SaveAutomationAsync(config);
        var page = Render<Results>();
        if (highlightedOnly)
            await page.Find(".group-select-all input").ChangeAsync(true);
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Apply automation rules").ClickAsync(new MouseEventArgs());
        await page.WaitForAssertionAsync(() => {
            Assert.NotNull(scan.LastSelectionPlan);
            Assert.DoesNotContain(scan.LastSelectionPlan.Decisions, d => d.Path.EndsWith("protected.mp4") && d.RemoveCandidate);
            Assert.Equal("candidate.mp4", Assert.Single(page.FindAll(".dup-card.selected")).QuerySelector(".card-filename")!.TextContent);
        }, JournalIoTimeout);
        Assert.All(scan.Duplicates, item => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(item.Path)));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.StartsWith("Confirm quarantine", StringComparison.Ordinal));
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
    public async Task OversizedResultsWarningIsVisibleAcrossPagesAndClearsAfterSuccessfulSave() {
        await ScanEngine.LoadDatabase(folder);
        DatabaseUtils.Database.Clear();
        scan.Settings.IncludeList.Add(folder);
        Assert.True(scan.SaveSettings());
        await scan.LoadAutomationAsync();
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        scan.StartSearchOverride = () => {
            var group = Guid.NewGuid();
            Seed("large-ui-a.mp4", group); Seed("large-ui-b.mp4", group);
            string largeFormat = new('x', 17 * 1024 * 1024);
            foreach (var item in scan.Duplicates) {
                item.Format = largeFormat;
                DatabaseUtils.Database.Add(new FileEntry(item.Path));
            }
            ScanEngine.SaveDatabase();
        };
        await scan.StartScanAndCompare();
        await scan.CompleteWebScanAsync(false);
        Assert.Equal(ScanState.Done, scan.State);
        // The on-disk generation remains empty until explicitly saved again.
        // Shorten display metadata to keep the component render bounded.
        foreach (var item in scan.Duplicates) item.Format = "mp4";
        Services.AddSingleton(new FFmpegSetupService());
        var index = Render<VDF.Web.Components.Pages.Index>();
        var results = Render<Results>();
        var automation = await RenderPageAsync();
        foreach (var markup in new[] { index.Markup, results.Markup, automation.Markup }) {
            Assert.Contains("32 MiB save limit", markup);
            Assert.Contains("until the service restarts", markup);
            Assert.Contains("role=\"alert\"", markup);
        }
        await scan.RemoveFromResultsAsync([]);
        await automation.WaitForAssertionAsync(() => {
            Assert.DoesNotContain("32 MiB save limit", automation.Markup);
            // Warning rendering can precede the asynchronous view refresh.
            // Let its journal IO finish before fixture teardown removes state.
            Assert.False(Button(automation, "Save automation settings").HasAttribute("disabled"));
            Assert.False(scan.IsBusy);
        }, JournalIoTimeout);
        await results.WaitForAssertionAsync(() => Assert.DoesNotContain("32 MiB save limit", results.Markup), JournalIoTimeout);
        await index.WaitForAssertionAsync(() => Assert.DoesNotContain("32 MiB save limit", index.Markup), JournalIoTimeout);
    }

    [Fact]
    public async Task OversizedPreviewWarningIsVisibleOnScanAndClearsWithPreviewInvalidation() {
        await scan.LoadAutomationAsync();
        string largeReason = new('x', 17 * 1024 * 1024);
        var plan = new SelectionPlan { Decisions = [new() { Path = "a", Reason = largeReason }, new() { Path = "b", Reason = largeReason }] };
        await scan.SaveSelectionPlanForSessionAsync(folder, plan);
        Services.AddSingleton(new FFmpegSetupService());
        var index = Render<VDF.Web.Components.Pages.Index>();
        Assert.Contains("This selection preview exceeds the 32 MiB save limit", index.Find("[role='alert']").TextContent);
        Assert.Contains("until the service restarts", index.Markup);
        await scan.ResetAsync();
        await index.WaitForAssertionAsync(() => Assert.Empty(index.FindAll("[role='alert']")), JournalIoTimeout);
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task ManualScheduleCheckRefreshesAfterScanWithoutReportingBusyAsFailure(bool due, bool aborted, bool finishDuringStart) {
        await ScanEngine.LoadDatabase(folder);
        DatabaseUtils.Database.Clear();
        scan.Settings.IncludeList.Add(folder);
        Assert.True(scan.SaveSettings());
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = due, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        int started = 0;
        Task? completion = null;
        void PrepareResults() {
            var group = Guid.NewGuid();
            Seed("scheduled-a.mp4", group);
            Seed("scheduled-b.mp4", group);
            foreach (var item in scan.Duplicates) DatabaseUtils.Database.Add(new FileEntry(item.Path));
            ScanEngine.SaveDatabase();
        }
        scan.StartSearchOverride = () => {
            started++;
            if (finishDuringStart) {
                PrepareResults();
                completion = scan.CompleteWebScanAsync(false);
            }
        };
        var page = await RenderPageAsync();
        try {
            await ClickAsync(page, "Check saved schedule now");
            Assert.Equal(due ? 1 : 0, started);
            if (!finishDuringStart) Assert.Equal(due, scan.IsBusy);
            Assert.Empty(page.FindAll("[role='alert']"));
            Assert.Contains("Saved schedule checked.", page.Markup);
            if (!finishDuringStart) Assert.Equal(due, Button(page, "Check saved schedule now").HasAttribute("disabled"));
            if (due) {
                if (finishDuringStart) await completion!;
                else {
                    if (!aborted) PrepareResults();
                    await scan.CompleteWebScanAsync(aborted);
                }
                await page.WaitForAssertionAsync(() => {
                    Assert.False(Button(page, "Check saved schedule now").HasAttribute("disabled"));
                    Assert.Contains(aborted ? "Aborted" : "Completed", page.Markup);
                    if (!aborted) Assert.Contains("scheduled-a.mp4", page.Markup);
                    Assert.Empty(page.FindAll("[role='alert']"));
                }, JournalIoTimeout);
            }
        }
        finally {
            if (completion is not null) await completion;
            if (scan.IsBusy) await scan.CompleteWebScanAsync(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualScheduleCheckTreatsHeldRunLeaseAsBenignContention(bool startupOnly) {
        await ScanEngine.LoadDatabase(folder); DatabaseUtils.Database.Clear();
        scan.Settings.IncludeList.Add(folder); Assert.True(scan.SaveSettings());
        var config = await scan.LoadAutomationAsync(); config.ScanAtStartup = startupOnly;
        config.Schedule = new() { Enabled = !startupOnly, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
        int started = 0; scan.StartSearchOverride = () => started++;
        var page = await RenderPageAsync();
        using (VDF.Core.Review.JournalWriteLease.Acquire(Path.Combine(folder, ScanService.ScheduledRunLeaseFileName))) {
            await ClickAsync(page, "Check saved schedule now");
            Assert.Equal(0, started); Assert.False(scan.IsBusy); Assert.Null(scan.ErrorMessage);
            await page.WaitForAssertionAsync(() => {
                Assert.Empty(page.FindAll("[role='alert']")); Assert.Contains("Saved schedule checked.", page.Markup);
                Assert.False(Button(page, "Check saved schedule now").HasAttribute("disabled"));
            }, JournalIoTimeout);
        }
        try { await ClickAsync(page, "Check saved schedule now"); Assert.Equal(1, started); }
        finally { if (scan.IsBusy) await scan.CompleteWebScanAsync(true); }
    }

    [Theory]
    [InlineData(false, "include")]
    [InlineData(true, "include")]
    [InlineData(false, "concurrency")]
    [InlineData(true, "concurrency")]
    [InlineData(false, "prepare")]
    [InlineData(true, "prepare")]
    public async Task ManualScheduleCheckShowsCurrentStartupFailureAndAllowsRecovery(bool recurring, string fault) {
        await ScanEngine.LoadDatabase(folder); DatabaseUtils.Database.Clear();
        scan.Settings.IncludeList.Add(folder);
        string missing = Path.Combine(folder, "unavailable-media");
        if (fault == "include") scan.Settings.IncludeList.Add(missing);
        int concurrency = scan.Settings.MaxDegreeOfParallelism;
        if (fault == "concurrency") scan.Settings.MaxDegreeOfParallelism = 0;
        Assert.True(scan.SaveSettings());
        var config = await scan.LoadAutomationAsync(); config.ScanAtStartup = true;
        config.Schedule = new() { Enabled = recurring, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        scan.PrepareScanAsync = (_, _) => fault == "prepare" ? Task.FromException(new IOException("Synthetic preparation failure")) : Task.CompletedTask;
        int started = 0; scan.StartSearchOverride = () => started++;
        var page = await RenderPageAsync();
        try {
            await ClickAsync(page, "Check saved schedule now");
            Assert.Equal(ScanState.Error, scan.State); Assert.False(scan.IsBusy); Assert.Equal(0, started);
            await page.WaitForAssertionAsync(() => {
                Assert.Contains(scan.ErrorMessage!, page.Find("[role='alert']").TextContent);
                Assert.DoesNotContain("Saved schedule checked.", page.Markup);
                Assert.False(Button(page, "Check saved schedule now").HasAttribute("disabled"));
            }, JournalIoTimeout);
            if (fault == "include") Directory.CreateDirectory(missing);
            scan.Settings.MaxDegreeOfParallelism = concurrency;
            Assert.True(scan.SaveSettings()); scan.PrepareScanAsync = (_, _) => Task.CompletedTask;
            await ClickAsync(page, "Check saved schedule now");
            await page.WaitForAssertionAsync(() => {
                Assert.Empty(page.FindAll("[role='alert']")); Assert.Contains("Saved schedule checked.", page.Markup);
            }, JournalIoTimeout);
            Assert.Equal(fault == "prepare" ? 0 : 1, started);
        }
        finally { if (scan.IsBusy) await scan.CompleteWebScanAsync(true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResultsDateMenuKeepsItsNamedDateDirectionWithWeightedSettings(bool keepNewest) {
        await ScanEngine.LoadDatabase(folder); DatabaseUtils.Database.Clear();
        var group = Guid.NewGuid(); Seed("menu-older.mp4", group); Seed("menu-newer.mp4", group);
        var older = scan.Duplicates.Single(item => item.Path.EndsWith("menu-older.mp4"));
        var newer = scan.Duplicates.Single(item => item.Path.EndsWith("menu-newer.mp4"));
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(older.Path, date); older.DateModified = File.GetLastWriteTimeUtc(older.Path);
        File.SetLastWriteTimeUtc(newer.Path, date.AddTicks(1)); newer.DateModified = File.GetLastWriteTimeUtc(newer.Path);
        var wanted = keepNewest ? newer : older; var opposite = keepNewest ? older : newer;
        wanted.FrameSize = "320x240"; opposite.FrameSize = "1920x1080";
        foreach (var item in scan.Duplicates) DatabaseUtils.Database.Add(new FileEntry(item.Path));
        ScanEngine.SaveDatabase(); scan.CaptureCleanupSnapshots(folder); await scan.RemoveFromResultsAsync([]);
        var config = await scan.LoadAutomationAsync(); config.Selection.Mode = SelectionMode.Weighted;
        config.Selection.FolderWeight = 10000; config.Selection.Criteria = [new() { Name = "Resolution", Weight = 10000 }];
        config.Selection.FolderRules.Add(new() { Directory = folder, Pattern = Path.GetFileName(opposite.Path), Priority = 1000 });
        await scan.SaveAutomationAsync(config);
        var page = Render<Results>();
        await page.InvokeAsync(() => page.Find(".dropdown-wrap > button").ClickAsync(new MouseEventArgs()));
        string action = keepNewest ? "Oldest (keep newest)" : "Newest (keep oldest)";
        await page.InvokeAsync(() => page.FindAll("button").Single(button => button.TextContent.Trim() == action).ClickAsync(new MouseEventArgs()));
        await page.WaitForAssertionAsync(() => {
            Assert.Equal(Path.GetFileName(opposite.Path), Assert.Single(page.FindAll(".dup-card.selected")).QuerySelector(".card-filename")!.TextContent);
            Assert.Equal(wanted.Path, Assert.Single(scan.LastSelectionPlan!.Decisions, d => d.IsKeeper).Path);
        }, JournalIoTimeout);
        Assert.All(new[] { older.Path, newer.Path }, path => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData("review")]
    [InlineData("service")]
    [InlineData("button-plan")]
    [InlineData("button-config")]
    [InlineData("button-reviews")]
    public async Task InvalidatedPlanClearsAutomaticCandidatesAndPreservesExplicitCheckboxEdits(string trigger) {
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

        if (trigger != "review") {
            string path = trigger == "button-config" ? store.Path : Path.Combine(folder,
                trigger == "button-reviews" ? "ReviewedFiles.json" : "SelectionPlan.json");
            await File.WriteAllTextAsync(path, "{");
            if (trigger is "button-config" or "button-reviews") await File.WriteAllTextAsync(path + ".bak", "{");
            if (trigger.StartsWith("button-")) {
                await page.InvokeAsync(() => page.FindAll("button").First(button => button.TextContent.Trim() == "Keep Best").ClickAsync(new MouseEventArgs()));
                await page.WaitForAssertionAsync(() => Assert.Contains("Selection preview failed:", page.Markup));
            }
            else await Assert.ThrowsAsync<InvalidDataException>(() => scan.PreviewSelectionAsync());
            Assert.Null(scan.LastSelectionPlan);
        }
        else {
            var row = (await scan.GetReviewRowsAsync()).Single(row => row.Item.Path == candidateA);
            await scan.SetReviewAsync(row.Item, ReviewChoice.Keep, true, row.Revision);
        }
        await page.WaitForAssertionAsync(() => {
            Assert.False(Card(page, candidateA).ClassList.Contains("selected"));
            Assert.True(Card(page, candidateB).ClassList.Contains("selected"));
        });
        Assert.All(scan.Duplicates, item => Assert.True(File.Exists(item.Path)));
    }

    [Fact]
    public async Task ResultsPreviewCompletesBeforeNotifiedCircuitCanReserveFollowUpWork() {
        await scan.LoadAutomationAsync();
        var group = Guid.NewGuid(); Seed("reserve-a.mp4", group); Seed("reserve-b.mp4", group);
        scan.CaptureCleanupSnapshots(folder);
        var row = (await scan.GetReviewRowsAsync()).First();
        await scan.PreviewSelectionAsync();
        var page = Render<Results>();
        await page.WaitForAssertionAsync(() => Assert.Single(page.FindAll(".dup-card.selected")));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scan.BeforeReviewCommitAsync = () => release.Task;
        Task? followUp = null;
        long generation = scan.AutomationGeneration;
        void OtherCircuit() {
            if (followUp is null && !scan.IsBusy && generation != scan.AutomationGeneration)
                followUp = scan.SetReviewAsync(row.Item, ReviewChoice.Keep, false, row.Revision);
        }
        scan.StateChanged += OtherCircuit;
        try {
            await page.InvokeAsync(() => page.FindAll("button").First(button => button.TextContent.Trim() == "Keep Best").ClickAsync(new MouseEventArgs()));
            Assert.NotNull(followUp);
            await page.WaitForAssertionAsync(() => Assert.Contains("Selection preview prepared:", page.Markup));
            Assert.DoesNotContain("Selection preview failed:", page.Markup);
        }
        finally {
            scan.StateChanged -= OtherCircuit;
            release.TrySetResult();
            if (followUp is not null) await followUp.WaitAsync(JournalIoTimeout);
            scan.BeforeReviewCommitAsync = null;
        }
        Assert.All(scan.Duplicates, item => Assert.True(File.Exists(item.Path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailureInAnotherCircuitRemovesDiscardedReviewRows(bool scheduled) {
        await ScanEngine.LoadDatabase(folder);
        DatabaseUtils.Database.Clear();
        scan.Settings.IncludeList.Add(folder);
        Assert.True(scan.SaveSettings());
        var config = await scan.LoadAutomationAsync();
        config.Schedule = new() { Enabled = scheduled, Kind = ScanScheduleKind.Interval, IntervalMinutes = 5 };
        await scan.SaveAutomationAsync(config);
        var group = Guid.NewGuid();
        Seed("discarded-a.mp4", group); Seed("discarded-b.mp4", group);
        foreach (var item in scan.Duplicates) DatabaseUtils.Database.Add(new FileEntry(item.Path));
        ScanEngine.SaveDatabase();
        await scan.CompleteWebScanAsync(false);
        await scan.RemoveFromResultsAsync([]);
        await scan.PreviewSelectionAsync();
        var page = await RenderPageAsync();
        Assert.Equal(2, page.FindAll(".action-panel").Count(panel => panel.TextContent.Contains("Review:")));
        Assert.Contains("discarded-a.mp4", page.Markup);
        scan.PrepareScanAsync = (_, _) => Task.FromException(new IOException("Synthetic preparation failure"));
        if (scheduled) await scan.CheckScheduleAsync(DateTimeOffset.UtcNow);
        else await scan.StartScanAndCompare();
        Assert.Equal(ScanState.Error, scan.State);
        Assert.Empty(scan.Duplicates);
        await page.WaitForAssertionAsync(() => {
            Assert.DoesNotContain(page.FindAll(".action-panel"), panel => panel.TextContent.Contains("discarded-"));
            Assert.DoesNotContain("discarded-a.mp4", page.Markup);
            Assert.Empty(page.FindAll("[role='alert']"));
        }, JournalIoTimeout);
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
