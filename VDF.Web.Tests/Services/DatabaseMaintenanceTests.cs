// SPDX-License-Identifier: AGPL-3.0-or-later

using VDF.Core;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.Web.Services;

namespace VDF.Web.Tests.Services;

[Collection("WebSettingsOverride")]
public sealed class DatabaseMaintenanceTests : IAsyncLifetime {
	readonly string folder = Path.Combine(Path.GetTempPath(), "VDF.WebDatabaseTests." + Guid.NewGuid().ToString("N"));
	readonly string databaseA;
	readonly string databaseB;
	readonly string mediaPath;
	readonly ScanService scan;
	byte[] databaseABefore = [];

	public DatabaseMaintenanceTests() {
		databaseA = Path.Combine(folder, "database-a");
		databaseB = Path.Combine(folder, "database-b");
		mediaPath = Path.Combine(folder, "existing.mp4");
		Directory.CreateDirectory(databaseA);
		Directory.CreateDirectory(databaseB);
		WebSettingsService.TestOverrideSettingsPath = Path.Combine(folder, "web-settings.json");
		scan = new ScanService(new WebSettingsService());
	}

	public async Task InitializeAsync() {
		Assert.True(await ScanEngine.LoadDatabase(databaseA));
		DatabaseUtils.Database.Clear();
		File.WriteAllBytes(mediaPath, [1, 2, 3, 4]);
		DatabaseUtils.Database.Add(new FileEntry(mediaPath));
		ScanEngine.SaveDatabase();
		Assert.True(await ScanEngine.LoadDatabase(databaseA));
		Assert.Equal(mediaPath, Assert.Single(DatabaseUtils.Database).Path);
		databaseABefore = File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db"));
		scan.Settings.CustomDatabaseFolder = databaseA;
	}

	public Task DisposeAsync() {
		scan.Dispose();
		WebSettingsService.TestOverrideSettingsPath = null;
		DatabaseUtils.Database.Clear();
		DatabaseUtils.CustomDatabaseFolder = null;
		DatabaseUtils.InvalidateDatabaseFolder();
		Directory.Delete(folder, recursive: true);
		return Task.CompletedTask;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CleanDatabaseAfterFolderSwitchStartsWithEmptyDatabase(bool zeroByteDatabase) {
		Assert.Empty(Directory.EnumerateFileSystemEntries(databaseB));
		if (zeroByteDatabase) File.WriteAllBytes(Path.Combine(databaseB, "ScannedFiles.db"), []);
		scan.Settings.CustomDatabaseFolder = databaseB;

		int removed = await scan.CleanDatabaseAsync();

		Assert.Equal(0, removed);
		await AssertOnlyDatabaseBIsEmpty();
	}

	[Fact]
	public async Task ClearDatabaseAfterFolderSwitchLeavesPreviousDatabaseUnchanged() {
		Assert.Empty(Directory.EnumerateFileSystemEntries(databaseB));
		scan.Settings.CustomDatabaseFolder = databaseB;

		await scan.ClearDatabaseAsync();

		await AssertOnlyDatabaseBIsEmpty();
	}

	async Task AssertOnlyDatabaseBIsEmpty() {
		Assert.False(scan.IsBusy);
		Assert.Equal(Path.GetFullPath(databaseB), Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()));
		Assert.Empty(DatabaseUtils.Database);
		Assert.True(File.Exists(Path.Combine(databaseB, "ScannedFiles.db")));
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		Assert.True(await ScanEngine.LoadDatabase(databaseB));
		Assert.Empty(DatabaseUtils.Database);
		Assert.True(await ScanEngine.LoadDatabase(databaseA));
		Assert.Equal(mediaPath, Assert.Single(DatabaseUtils.Database).Path);
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
	}

	async Task SeedSavedResultsInBothFoldersAsync(bool saveDestinationResults) {
		scan.Settings.IncludeList.Add(folder); Assert.True(scan.SaveSettings()); await scan.LoadAutomationAsync();
		string second = Path.Combine(folder, "second.mp4"); File.WriteAllBytes(second, [1, 2, 3, 4]);
		DatabaseUtils.Database.Add(new FileEntry(second));
		var group = Guid.NewGuid();
		scan.Engine.Duplicates = DatabaseUtils.Database.Select(entry => new DuplicateItem(entry, .01f, group, DuplicateFlags.None)).ToHashSet();
		ScanEngine.SaveDatabase(); databaseABefore = File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db"));
		scan.CaptureCleanupSnapshots(databaseA); await scan.RemoveFromResultsAsync([]); await scan.PreviewSelectionAsync();
		Assert.True(await ScanEngine.LoadDatabase(databaseB)); DatabaseUtils.Database.Clear();
		foreach (string path in new[] { mediaPath, second }) DatabaseUtils.Database.Add(new FileEntry(path));
		string marker = Path.Combine(folder, "destination-only.mp4"); File.WriteAllBytes(marker, [1, 2, 3, 4]);
		DatabaseUtils.Database.Add(new FileEntry(marker)); ScanEngine.SaveDatabase();
		if (saveDestinationResults) {
			using var destination = new ScanService(new WebSettingsService()); destination.Settings.CustomDatabaseFolder = databaseB;
			var destinationGroup = Guid.NewGuid();
			destination.Engine.Duplicates = DatabaseUtils.Database.Select(entry => new DuplicateItem(entry, .01f, destinationGroup, DuplicateFlags.None)).ToHashSet();
			destination.CaptureCleanupSnapshots(databaseB); await destination.RemoveFromResultsAsync([]);
		}
	}

	Action HideAutomationConfiguration() {
		string path = Path.Combine(folder, "web-settings.automation.json");
		byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
		File.Delete(path); File.Delete(path + ".bak");
		return () => { File.WriteAllBytes(path, primary); File.WriteAllBytes(path + ".bak", backup); };
	}

	[Theory]
	[InlineData(false, false, false)]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(true, true, false)]
	[InlineData(false, false, true)]
	[InlineData(true, false, true)]
	[InlineData(false, true, true)]
	[InlineData(true, true, true)]
	public async Task ClearAfterFolderSwitchInvalidatesProducingAndDestinationResults(bool freshService, bool destinationResults, bool missingConfig) {
		await SeedSavedResultsInBothFoldersAsync(destinationResults);
		using var fresh = freshService ? new ScanService(new WebSettingsService()) : null;
		var actor = fresh ?? scan;
		if (freshService) { actor.Settings.CustomDatabaseFolder = databaseA; await actor.InitializeWebAutomationAsync(); }
		Assert.Equal(2, actor.Duplicates.Count);
		actor.Settings.CustomDatabaseFolder = databaseB; Assert.True(actor.SaveSettings());
		Action? restore = missingConfig ? HideAutomationConfiguration() : null;
		try { await actor.ClearDatabaseAsync(); }
		finally { restore?.Invoke(); }
		Assert.False(actor.IsBusy); Assert.Empty(actor.Duplicates); Assert.Empty(DatabaseUtils.Database);
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		foreach (string state in new[] { databaseA, databaseB }) {
			using var restarted = new ScanService(new WebSettingsService()); restarted.Settings.CustomDatabaseFolder = state;
			await restarted.InitializeWebAutomationAsync();
			Assert.Empty(restarted.Duplicates); Assert.Null(restarted.LastSelectionPlan);
			Assert.Equal(state == databaseA ? 2 : 0, restarted.DatabaseEntryCount);
		}
		Assert.All(Directory.GetFiles(folder, "*.mp4"), path => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path)));
	}

	[Theory]
	[InlineData(false, "corrupt")]
	[InlineData(true, "corrupt")]
	[InlineData(false, "future")]
	[InlineData(true, "future")]
	[InlineData(false, "lease")]
	[InlineData(true, "lease")]
	[InlineData(false, "missing")]
	[InlineData(true, "missing")]
	public async Task ProducingResultStorageFaultRefusesClearAcrossFoldersUntilRepaired(bool missingConfig, string fault) {
		await SeedSavedResultsInBothFoldersAsync(true);
		byte[] databaseBBefore = File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db"));
		string path = Path.Combine(databaseA, "WebResults.json"), held = Path.Combine(folder, "held-a");
		byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
		scan.Settings.CustomDatabaseFolder = databaseB; Assert.True(scan.SaveSettings());
		Action? restoreConfig = missingConfig ? HideAutomationConfiguration() : null;
		if (fault == "corrupt") File.WriteAllText(path, "{");
		if (fault == "future") {
			var future = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; future["version"] = 2;
			File.WriteAllText(path, future.ToJsonString());
		}
		if (fault == "missing") Directory.Move(databaseA, held);
		byte[]? invalid = fault == "missing" ? null : File.ReadAllBytes(path);
		try {
			using (fault == "lease" ? JournalWriteLease.Acquire(path) : null) for (int retry = 0; retry < 2; retry++) {
				if (fault == "future") await Assert.ThrowsAsync<NotSupportedException>(() => scan.ClearDatabaseAsync());
				else if (fault == "corrupt") await Assert.ThrowsAsync<InvalidDataException>(() => scan.ClearDatabaseAsync());
				else if (fault == "missing") await Assert.ThrowsAnyAsync<IOException>(() => scan.ClearDatabaseAsync());
				else await Assert.ThrowsAsync<IOException>(() => scan.ClearDatabaseAsync());
				Assert.False(scan.IsBusy); Assert.Equal(2, scan.Duplicates.Count);
				Assert.Equal(databaseBBefore, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
				if (fault == "missing") Assert.False(Directory.Exists(databaseA));
				else Assert.Equal(invalid, File.ReadAllBytes(path));
			}
		}
		finally {
			if (fault == "missing") Directory.Move(held, databaseA);
			File.WriteAllBytes(path, primary); File.WriteAllBytes(path + ".bak", backup);
			restoreConfig?.Invoke();
		}
		await scan.ClearDatabaseAsync(); Assert.Empty(scan.Duplicates); Assert.Empty(DatabaseUtils.Database);
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		using var restarted = new ScanService(new WebSettingsService()); restarted.Settings.CustomDatabaseFolder = databaseA;
		await restarted.InitializeWebAutomationAsync(); Assert.Empty(restarted.Duplicates); Assert.Null(restarted.LastSelectionPlan);
		Assert.All(Directory.GetFiles(folder, "*.mp4"), media => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(media)));
	}

	public static IEnumerable<object[]> RevokedOriginTransitions() {
		foreach (string revocation in new[] { "clear", "clean", "move", "delete-load", "quarantine", "false", "throw" })
			foreach (string discard in new[] { "clear", "reset", "scan" })
				foreach (bool missingConfig in new[] { false, true }) yield return [revocation, discard, missingConfig];
	}

	async Task RevokeProducingAuthorityAsync(string operation) {
		var item = scan.Duplicates.First();
		if (operation == "delete-load") scan.DatabaseLoader = async requested => { Assert.True(await ScanEngine.LoadDatabase(requested)); return false; };
		if (operation is "clear" or "clean" or "false" or "throw") {
			scan.Settings.CustomDatabaseFolder = databaseA;
			if (operation is "false" or "throw") scan.DatabaseLoader = async requested => {
				Assert.True(await ScanEngine.LoadDatabase(requested));
				if (operation == "throw") throw new IOException("Synthetic database load failure");
				return false;
			};
			if (operation == "clean") await Assert.ThrowsAnyAsync<IOException>(() => scan.CleanDatabaseAsync());
			else await Assert.ThrowsAnyAsync<IOException>(() => scan.ClearDatabaseAsync());
			scan.DatabaseLoader = ScanEngine.LoadDatabase;
		}
		else if (operation == "quarantine") await Assert.ThrowsAnyAsync<IOException>(() => scan.PreviewQuarantineAsync([item]));
		else {
			var result = operation == "move" ? await scan.MoveItemsAsync([item], Path.Combine(folder, "must-not-move"))
				: await scan.DeleteItemsAsync([item], permanent: true);
			Assert.Equal(0, result.Done); Assert.Equal(1, result.Failed); Assert.NotEmpty(result.Errors);
		}
		scan.DatabaseLoader = ScanEngine.LoadDatabase;
		Assert.False(scan.IsBusy); Assert.Equal(2, scan.Duplicates.Count);
	}

	[Theory]
	[MemberData(nameof(RevokedOriginTransitions))]
	public async Task RevokedAuthorityRetainsOriginForDiscardAndRetry(string revocation, string discard, bool missingConfig) {
		await SeedSavedResultsInBothFoldersAsync(true);
		string held = Path.Combine(folder, "held-a");
		if (revocation is not ("false" or "throw" or "delete-load")) Directory.Move(databaseA, held);
		await RevokeProducingAuthorityAsync(revocation);
		if (revocation is "false" or "throw" or "delete-load") Directory.Move(databaseA, held);
		scan.Settings.CustomDatabaseFolder = databaseB; Assert.True(scan.SaveSettings());
		byte[] databaseBBefore = File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db"));
		Action? restore = missingConfig ? HideAutomationConfiguration() : null;
		int prepared = 0;
		scan.PrepareScanAsync = (_, _) => { prepared++; throw new IOException("Synthetic preparation after invalidation"); };
		try {
			for (int retry = 0; retry < 2; retry++) {
				if (discard == "clear") await Assert.ThrowsAnyAsync<IOException>(() => scan.ClearDatabaseAsync());
				else if (discard == "reset") await Assert.ThrowsAnyAsync<IOException>(() => scan.ResetAsync());
				else { await scan.StartScanAndCompare(); Assert.Equal(ScanState.Error, scan.State); Assert.Equal(0, prepared); }
				Assert.False(scan.IsBusy); Assert.Equal(2, scan.Duplicates.Count); Assert.False(Directory.Exists(databaseA));
				Assert.Equal(databaseBBefore, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
			}
		}
		finally { Directory.Move(held, databaseA); restore?.Invoke(); }
		// Restoring storage must not restore revoked file-operation authority.
		var retained = await scan.DeleteItemsAsync([scan.Duplicates.First()], permanent: true);
		Assert.Equal(0, retained.Done); Assert.Equal(1, retained.Failed);
		if (discard == "clear") await scan.ClearDatabaseAsync();
		else if (discard == "reset") await scan.ResetAsync();
		else { await scan.StartScanAndCompare(); Assert.Equal(1, prepared); }
		Assert.Empty(scan.Duplicates); Assert.False(scan.IsBusy);
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		if (discard != "clear") Assert.Equal(databaseBBefore, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
		using var restarted = new ScanService(new WebSettingsService()); restarted.Settings.CustomDatabaseFolder = databaseA;
		await restarted.InitializeWebAutomationAsync(); Assert.Empty(restarted.Duplicates); Assert.Null(restarted.LastSelectionPlan);
		Assert.All(Directory.GetFiles(folder, "*.mp4"), media => Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(media)));
	}

	[Theory]
	[InlineData("clear")]
	[InlineData("move")]
	[InlineData("delete")]
	[InlineData("quarantine")]
	public async Task RevokedAuthorityDismissalCannotReviveSavedRows(string revocation) {
		await SeedSavedResultsInBothFoldersAsync(true);
		string held = Path.Combine(folder, "held-a"); Directory.Move(databaseA, held);
		await RevokeProducingAuthorityAsync(revocation); Directory.Move(held, databaseA);
		await scan.RemoveFromResultsAsync(scan.Duplicates.ToArray());
		using var restarted = new ScanService(new WebSettingsService()); restarted.Settings.CustomDatabaseFolder = databaseA;
		await restarted.InitializeWebAutomationAsync(); Assert.Empty(restarted.Duplicates); Assert.Null(restarted.LastSelectionPlan);
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UnavailableExplicitFolderMaintenanceCannotBorrowFallbackState(bool clear) {
		Directory.Delete(databaseB);
		scan.Settings.CustomDatabaseFolder = databaseB;
		int loads = 0;
		// Model Core's fallback with copied A state; never touch the process's
		// real default configuration while demonstrating this boundary.
		scan.DatabaseLoader = async _ => { loads++; return await ScanEngine.LoadDatabase(databaseA); };
		for (int retry = 0; retry < 2; retry++) {
			await Assert.ThrowsAnyAsync<IOException>(async () => {
				if (clear) await scan.ClearDatabaseAsync();
				else await scan.CleanDatabaseAsync();
			});
			Assert.False(scan.IsBusy);
			Assert.Equal(0, loads);
			Assert.Equal(mediaPath, Assert.Single(DatabaseUtils.Database).Path);
			Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
			Assert.False(Directory.Exists(databaseB));
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ResetAfterFolderSwitchOnlyDiscardsTheVisibleResultGeneration(bool missingConfig) {
		await SeedSavedResultsInBothFoldersAsync(true);
		byte[] databaseBBefore = File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db"));
		scan.Settings.CustomDatabaseFolder = databaseB; Assert.True(scan.SaveSettings());
		Action? restore = missingConfig ? HideAutomationConfiguration() : null;
		try { await scan.ResetAsync(); } finally { restore?.Invoke(); }
		Assert.Empty(scan.Duplicates); Assert.False(scan.IsBusy);
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		Assert.Equal(databaseBBefore, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
		foreach (string state in new[] { databaseA, databaseB }) {
			using var restored = new ScanService(new WebSettingsService()); restored.Settings.CustomDatabaseFolder = state;
			await restored.InitializeWebAutomationAsync(); Assert.Equal(state == databaseA ? 0 : 3, restored.Duplicates.Count);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DestinationLoadFailurePreservesProducingResultsUntilClearCanSucceed(bool unavailable) {
		await SeedSavedResultsInBothFoldersAsync(true);
		string primaryPath = Path.Combine(databaseA, "WebResults.json"), held = Path.Combine(folder, "held-b");
		byte[] primary = File.ReadAllBytes(primaryPath), backup = File.ReadAllBytes(primaryPath + ".bak");
		string targetDatabase = Path.Combine(databaseB, "ScannedFiles.db"); byte[] databaseBBefore = File.ReadAllBytes(targetDatabase);
		scan.Settings.CustomDatabaseFolder = databaseB; Assert.True(scan.SaveSettings());
		Assert.True(await ScanEngine.LoadDatabase(databaseA));
		if (unavailable) Directory.Move(databaseB, held);
		try {
			for (int retry = 0; retry < 2; retry++) {
				// Core quarantines a damaged primary; reinject it to exercise another failed load.
				if (!unavailable) File.WriteAllBytes(targetDatabase, "VDFDB002"u8.ToArray());
				await Assert.ThrowsAnyAsync<IOException>(() => scan.ClearDatabaseAsync());
				Assert.False(scan.IsBusy); Assert.Equal(2, scan.Duplicates.Count);
				Assert.Equal(primary, File.ReadAllBytes(primaryPath)); Assert.Equal(backup, File.ReadAllBytes(primaryPath + ".bak"));
				Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
			}
		}
		finally { if (unavailable) Directory.Move(held, databaseB); else File.WriteAllBytes(targetDatabase, databaseBBefore); }
		await scan.ClearDatabaseAsync(); Assert.Empty(scan.Duplicates); Assert.Empty(DatabaseUtils.Database);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task ExplicitFolderMaintenanceRequiresTheLoadedRequestedFolder(bool clear, bool folderRemains) {
		scan.Settings.CustomDatabaseFolder = databaseB;
		bool invoked = false;
		scan.DatabaseLoader = async requested => {
			Assert.Equal(databaseB, requested);
			invoked = true;
			if (!folderRemains) Directory.Move(databaseB, databaseB + ".offline");
			return await ScanEngine.LoadDatabase(databaseA);
		};
		await Assert.ThrowsAnyAsync<IOException>(async () => {
			if (clear) await scan.ClearDatabaseAsync();
			else await scan.CleanDatabaseAsync();
		});
		Assert.True(invoked);
		Assert.False(scan.IsBusy);
		Assert.Equal(mediaPath, Assert.Single(DatabaseUtils.Database).Path);
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		Assert.Empty(Directory.EnumerateFileSystemEntries(folderRemains ? databaseB : databaseB + ".offline"));
	}

	[Fact]
	public async Task UnavailableOtherFolderMaintenanceKeepsProducingResultsUsable() {
		var item = PublishResult();
		Directory.Delete(databaseB);
		scan.Settings.CustomDatabaseFolder = databaseB;
		scan.DatabaseLoader = _ => throw new InvalidOperationException("An unavailable selection must not invoke the loader.");
		await Assert.ThrowsAnyAsync<IOException>(() => scan.ClearDatabaseAsync());
		string destination = Path.Combine(folder, "moved-after-unavailable-selection");
		var result = await scan.MoveItemsAsync([item], destination);
		Assert.Equal(1, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Empty(result.Errors);
		Assert.False(File.Exists(mediaPath));
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(destination, Path.GetFileName(mediaPath))));
		Assert.Equal(Path.GetFullPath(databaseA), Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()));
		Assert.False(Directory.Exists(databaseB));
	}

	[Fact]
	public async Task UnavailableResultsFolderCannotLoadFallbackOnRetry() {
		var item = PublishResult();
		scan.Settings.CustomDatabaseFolder = databaseB;
		Assert.Equal(0, await scan.CleanDatabaseAsync());
		byte[] beforeB = File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db"));
		Directory.Move(databaseA, databaseA + ".offline");
		int loads = 0;
		scan.DatabaseLoader = async _ => { loads++; return await ScanEngine.LoadDatabase(databaseB); };
		string destination = Path.Combine(folder, "unavailable-results-move");
		for (int retry = 0; retry < 2; retry++) {
			var result = await scan.MoveItemsAsync([item], destination);
			Assert.Equal(0, result.Done);
			Assert.Equal(1, result.Failed);
			Assert.NotEmpty(result.Errors);
			Assert.True(File.Exists(mediaPath));
			Assert.False(Directory.Exists(destination));
			Assert.False(Directory.Exists(databaseA));
			Assert.Equal(0, loads);
			Assert.Equal(beforeB, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
			Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA + ".offline", "ScannedFiles.db")));
		}
	}

	DuplicateItem PublishResult(bool capture = true) {
		var item = new DuplicateItem(new FileEntry(mediaPath), 0.02f, Guid.NewGuid(), DuplicateFlags.None);
		scan.Engine.Duplicates.Add(item);
		if (capture) scan.CaptureCleanupSnapshots(DatabaseUtils.GetDatabaseFolderPath());
		return item;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MoveRefusesBusyReviewWriterIncludingFirstInitialization(bool initialized) {
		string reviews = Path.Combine(databaseA, "ReviewedFiles.json");
		if (initialized) await new ReviewStore(reviews).SaveAsync([]);
		var item = PublishResult();
		string destination = Path.Combine(folder, "busy-review-move");
		using (var writer = JournalWriteLease.Acquire(reviews)) {
			var result = await scan.MoveItemsAsync([item], destination);
			Assert.Equal(0, result.Done);
			Assert.Equal(1, result.Failed);
			Assert.NotEmpty(result.Errors);
			Assert.False(Directory.Exists(destination));
			Assert.True(File.Exists(mediaPath));
			Assert.Contains(item, scan.Duplicates);
			Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		}
		var identity = Assert.IsType<FileIdentity>(FileIdentity.Capture(mediaPath));
		await new ReviewStore(reviews).SaveAsync([new() { Identity = identity, ManuallyProtected = true }]);
		var retry = await scan.MoveItemsAsync([item], destination);
		Assert.Equal(0, retry.Done);
		Assert.True(identity.MatchesCurrent());
		Assert.False(Directory.Exists(destination));
	}

	[Fact]
	public async Task MoveHoldsReviewLeaseThroughRenameAndReleasesItAfterCommit() {
		var item = PublishResult();
		string reviews = Path.Combine(databaseA, "ReviewedFiles.json");
		string destination = Path.Combine(folder, "leased-move");
		bool invoked = false;
		scan.FileMover = (source, target) => {
			invoked = true;
			Assert.ThrowsAny<IOException>(() => {
				using var competingWriter = JournalWriteLease.Acquire(reviews);
			});
			File.Move(source, target);
		};
		var result = await scan.MoveItemsAsync([item], destination);
		Assert.True(invoked);
		Assert.Equal(1, result.Done);
		Assert.Equal(0, result.Failed);
		using var released = JournalWriteLease.Acquire(reviews);
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(destination, Path.GetFileName(mediaPath))));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MoveAfterMaintenanceSwitchUpdatesOnlyTheResultsDatabase(bool malformedDatabaseB) {
		var item = PublishResult();
		string databaseBPath = Path.Combine(databaseB, "ScannedFiles.db");
		if (malformedDatabaseB) File.WriteAllBytes(databaseBPath, "VDFDB002"u8.ToArray());
		scan.Settings.CustomDatabaseFolder = databaseB;
		if (malformedDatabaseB)
			await Assert.ThrowsAsync<IOException>(() => scan.CleanDatabaseAsync());
		else Assert.Equal(0, await scan.CleanDatabaseAsync());
		string preservedBPath = malformedDatabaseB ? Path.ChangeExtension(databaseBPath, "_DAMAGED.db") : databaseBPath;
		byte[] databaseBBefore = File.ReadAllBytes(preservedBPath);
		string destination = Path.Combine(folder, "moved");
		string movedPath = Path.Combine(destination, Path.GetFileName(mediaPath));

		var result = await scan.MoveItemsAsync([item], destination);

		Assert.Equal(1, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Empty(result.Errors);
		Assert.False(File.Exists(mediaPath));
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(movedPath));
		Assert.Equal(databaseBBefore, File.ReadAllBytes(preservedBPath));
		Assert.Equal(!malformedDatabaseB, File.Exists(databaseBPath));
		Assert.Equal(Path.GetFullPath(databaseA), Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()));
		Assert.False(scan.IsBusy);
		Assert.DoesNotContain(item, scan.Duplicates);
		Assert.True(await ScanEngine.LoadDatabase(databaseA));
		Assert.Equal(movedPath, Assert.Single(DatabaseUtils.Database).Path);
		Assert.True(await ScanEngine.LoadDatabase(databaseB));
		Assert.Empty(DatabaseUtils.Database);
		Assert.Equal(databaseBBefore, File.ReadAllBytes(preservedBPath));
	}

	[Fact]
	public async Task MoveBatchKeepsVerifiedSourceAliasesUsableAfterTheFirstRename() {
		var first = PublishResult();
		string aliasPath = Path.Combine(folder, "alias.mp4");
		HardLinkUtils.CreateHardLink(aliasPath, mediaPath);
		var aliasEntry = new FileEntry(aliasPath);
		var second = new DuplicateItem(aliasEntry, 0.02f, first.GroupId, DuplicateFlags.None);
		scan.Engine.Duplicates.Add(second);
		DatabaseUtils.Database.Add(aliasEntry);
		ScanEngine.SaveDatabase();
		scan.CaptureCleanupSnapshots(DatabaseUtils.GetDatabaseFolderPath());
		scan.Settings.CustomDatabaseFolder = databaseB;
		Assert.Equal(0, await scan.CleanDatabaseAsync());
		byte[] databaseBBefore = File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db"));
		string destination = Path.Combine(folder, "moved");

		var result = await scan.MoveItemsAsync([first, second], destination);

		Assert.Equal(2, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Empty(result.Errors);
		Assert.False(File.Exists(mediaPath));
		Assert.False(File.Exists(aliasPath));
		Assert.True(HardLinkUtils.AreSameFile(Path.Combine(destination, "existing.mp4"), Path.Combine(destination, "alias.mp4")));
		Assert.Equal(databaseBBefore, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
		Assert.True(await ScanEngine.LoadDatabase(databaseA));
		Assert.Equal(new[] { Path.Combine(destination, "existing.mp4"), Path.Combine(destination, "alias.mp4") }.ToHashSet(),
			DatabaseUtils.Database.Select(entry => entry.Path).ToHashSet());
		Assert.False(scan.IsBusy);
	}

	[Fact]
	public async Task MoveRejectsMalformedResultsDatabaseBeforeCreatingDestinationOrMovingMedia() {
		var item = PublishResult();
		scan.Settings.CustomDatabaseFolder = databaseB;
		Assert.Equal(0, await scan.CleanDatabaseAsync());
		byte[] databaseBBefore = File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db"));
		string databaseAPath = Path.Combine(databaseA, "ScannedFiles.db");
		byte[] malformed = "VDFDB002"u8.ToArray();
		File.WriteAllBytes(databaseAPath, malformed);
		string destination = Path.Combine(folder, "blocked-move");

		var result = await scan.MoveItemsAsync([item], destination);

		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.Contains(result.Errors, error => error.Contains("database could not be loaded", StringComparison.Ordinal));
		Assert.False(Directory.Exists(destination));
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(mediaPath));
		Assert.Contains(item, scan.Duplicates);
		Assert.False(scan.IsBusy);
		Assert.False(File.Exists(databaseAPath));
		Assert.False(File.Exists(Path.Combine(databaseA, "ScannedFiles_new.db")));
		Assert.Equal(malformed, File.ReadAllBytes(Path.ChangeExtension(databaseAPath, "_DAMAGED.db")));
		Assert.Equal(databaseBBefore, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
		var retry = await scan.MoveItemsAsync([item], destination);
		Assert.Equal(0, retry.Done);
		Assert.Equal(1, retry.Failed);
		Assert.Contains(retry.Errors, error => error.Contains("no verified file snapshots", StringComparison.Ordinal));
		Assert.False(Directory.Exists(destination));
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(mediaPath));
		Assert.False(File.Exists(databaseAPath));
		Assert.Equal(databaseBBefore, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
	}


	async Task<(DuplicateItem Target, DuplicateItem Keeper, byte[] DatabaseB)> PrepareMalformedResultsDatabase() {
		var target = PublishResult(capture: false);
		string keeperPath = Path.Combine(folder, "keeper.mp4");
		File.WriteAllBytes(keeperPath, [5, 6, 7, 8]);
		var keeperEntry = new FileEntry(keeperPath);
		var keeper = new DuplicateItem(keeperEntry, 0.04f, target.GroupId, DuplicateFlags.None);
		scan.Engine.Duplicates.Add(keeper);
		DatabaseUtils.Database.Add(keeperEntry);
		ScanEngine.SaveDatabase();
		await CleanupProtectionPolicyStore.SaveAsync(databaseA, new CleanupProtectionPolicy());
		scan.CaptureCleanupSnapshots(databaseA);
		scan.Settings.CustomDatabaseFolder = databaseB;
		Assert.Equal(0, await scan.CleanDatabaseAsync());
		// Keep B nonempty so a failed A load really retains a foreign wrapper.
		string foreignPath = Path.Combine(folder, "foreign.mp4");
		File.WriteAllBytes(foreignPath, [9, 10, 11, 12]);
		DatabaseUtils.Database.Add(new FileEntry(foreignPath));
		ScanEngine.SaveDatabase();
		byte[] beforeB = File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db"));
		File.WriteAllBytes(Path.Combine(databaseA, "ScannedFiles.db"), "VDFDB002"u8.ToArray());
		return (target, keeper, beforeB);
	}

	Task<FileOpResult> RunFileOperation(DuplicateItem item, int operation) => operation switch {
		0 => scan.DeleteItemsAsync([item], permanent: true),
		1 => scan.DeleteItemsAsync([item], permanent: false),
		2 => scan.CreateLinksAsync([item], hardLinks: true),
		3 => scan.CreateLinksAsync([item], hardLinks: false),
		4 => scan.MoveItemsAsync([item], Path.Combine(folder, "blocked-move")),
		_ => throw new ArgumentOutOfRangeException(nameof(operation))
	};

	void AssertFailedResultsRetained(FileOpResult result, DuplicateItem target, DuplicateItem keeper, byte[] databaseBBefore) {
		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.Equal(0, result.FreedBytes);
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target.Path));
		Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(keeper.Path));
		Assert.Null(new FileInfo(target.Path).LinkTarget);
		Assert.False(HardLinkUtils.AreSameFile(target.Path, keeper.Path));
		Assert.Contains(target, scan.Duplicates);
		Assert.Contains(keeper, scan.Duplicates);
		Assert.False(scan.IsBusy);
		Assert.False(Directory.Exists(Path.Combine(folder, "blocked-move")));
		Assert.False(File.Exists(Path.Combine(databaseA, "ScannedFiles.db")));
		Assert.False(File.Exists(Path.Combine(databaseA, "ScannedFiles_new.db")));
		Assert.Equal("VDFDB002"u8.ToArray(), File.ReadAllBytes(Path.ChangeExtension(Path.Combine(databaseA, "ScannedFiles.db"), "_DAMAGED.db")));
		Assert.Equal(databaseBBefore, File.ReadAllBytes(Path.Combine(databaseB, "ScannedFiles.db")));
		Assert.False(File.Exists(Path.Combine(databaseA, "LegacyCleanupHistory.json")));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public async Task CleanupRejectsMalformedResultsDatabaseAndRepeatedRetries(int operation) {
		var (target, keeper, databaseBBefore) = await PrepareMalformedResultsDatabase();
		int callbacks = 0;
		scan.TrashMover = (_, _) => { callbacks++; return false; };
		scan.LinkCreator = (_, _, _) => { callbacks++; throw new IOException("Unexpected link attempt."); };

		var initial = await RunFileOperation(target, operation);

		Assert.Contains(initial.Errors, error => error.Contains("database could not be loaded", StringComparison.Ordinal));
		AssertFailedResultsRetained(initial, target, keeper, databaseBBefore);
		Assert.Equal(Path.GetFullPath(databaseA), Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()));
		for (int retry = 0; retry < 2; retry++) {
			var refused = await RunFileOperation(target, operation);
			Assert.Contains(refused.Errors, error => error.Contains("no verified file snapshots", StringComparison.Ordinal));
			AssertFailedResultsRetained(refused, target, keeper, databaseBBefore);
		}
		Assert.Equal(0, callbacks);
	}

	[Theory]
	[InlineData(false, 0)]
	[InlineData(false, 1)]
	[InlineData(false, 2)]
	[InlineData(false, 3)]
	[InlineData(false, 4)]
	[InlineData(true, 0)]
	[InlineData(true, 1)]
	[InlineData(true, 2)]
	[InlineData(true, 3)]
	[InlineData(true, 4)]
	public async Task FailedMaintenanceOfResultsDatabaseRevokesFileOperationAuthority(bool clear, int operation) {
		var (target, keeper, databaseBBefore) = await PrepareMalformedResultsDatabase();
		scan.Settings.CustomDatabaseFolder = databaseA;
		await Assert.ThrowsAsync<IOException>(async () => {
			if (clear) await scan.ClearDatabaseAsync();
			else await scan.CleanDatabaseAsync();
		});
		Assert.Equal(Path.GetFullPath(databaseA), Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()));
		Assert.EndsWith("foreign.mp4", Assert.Single(DatabaseUtils.Database).Path);

		for (int retry = 0; retry < 2; retry++) {
			var result = await RunFileOperation(target, operation);
			Assert.Contains(result.Errors, error => error.Contains("no verified file snapshots", StringComparison.Ordinal));
			AssertFailedResultsRetained(result, target, keeper, databaseBBefore);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MoveRequiresAnchoredCurrentResults(bool replaceScannedFile) {
		var item = PublishResult(capture: replaceScannedFile);
		if (replaceScannedFile) {
			var scanned = Assert.IsType<FileIdentity>(FileIdentity.Capture(mediaPath));
			File.Move(mediaPath, mediaPath + ".previous");
			File.WriteAllBytes(mediaPath, [1, 2, 3, 4]);
			File.SetCreationTimeUtc(mediaPath, scanned.CreationUtc);
			File.SetLastWriteTimeUtc(mediaPath, scanned.LastWriteUtc);
			Assert.NotEqual(scanned.StableFileId, Assert.IsType<FileIdentity>(FileIdentity.Capture(mediaPath)).StableFileId);
		}
		string destination = Path.Combine(folder, "blocked-move");

		var result = await scan.MoveItemsAsync([item], destination);

		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.NotEmpty(result.Errors);
		Assert.False(Directory.Exists(destination));
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(mediaPath));
		Assert.Contains(item, scan.Duplicates);
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		Assert.False(scan.IsBusy);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MaintenanceRejectsMalformedDatabaseWithoutSavingPreviousEntries(bool clear) {
		byte[] malformed = "VDFDB002"u8.ToArray();
		string databasePath = Path.Combine(databaseB, "ScannedFiles.db");
		File.WriteAllBytes(databasePath, malformed);
		scan.Settings.CustomDatabaseFolder = databaseB;
		var result = new DuplicateItem(new FileEntry(mediaPath), 0.02f, Guid.NewGuid(), DuplicateFlags.None);
		scan.Engine.Duplicates.Add(result);

		var error = await Assert.ThrowsAsync<IOException>(async () => {
			if (clear) await scan.ClearDatabaseAsync();
			else await scan.CleanDatabaseAsync();
		});

		Assert.Contains("could not be loaded", error.Message);
		Assert.False(scan.IsBusy);
		Assert.Contains(result, scan.Duplicates);
		Assert.False(File.Exists(databasePath));
		Assert.False(File.Exists(Path.Combine(databaseB, "ScannedFiles_new.db")));
		Assert.Equal(malformed, File.ReadAllBytes(Path.ChangeExtension(databasePath, "_DAMAGED.db")));
		Assert.Equal(databaseABefore, File.ReadAllBytes(Path.Combine(databaseA, "ScannedFiles.db")));
		Assert.True(await ScanEngine.LoadDatabase(databaseA));
		Assert.Equal(mediaPath, Assert.Single(DatabaseUtils.Database).Path);
	}
}
