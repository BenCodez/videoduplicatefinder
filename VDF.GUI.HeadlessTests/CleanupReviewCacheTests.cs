// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.HeadlessTests;

public sealed class CleanupReviewCacheTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task AutomaticReviewSaveCannotReplaceUnreadableCurrentLedgerFromCachedDecisions(bool missing) => HeadlessUi.Run(async () => {
		string folder = Directory.CreateTempSubdirectory("vdf-cached-review-").FullName;
		var settings = SettingsFile.Instance;
		string originalDatabase = settings.CustomDatabaseFolder;
		string settingsPath = Path.Combine(folder, "Settings.json");
		try {
			SettingsFile.SetSettingsPath(settingsPath);
			settings.CustomDatabaseFolder = folder;
			SettingsFile.SaveSettings();
			var vm = new MainWindowVM();
			await vm.InitialiseAutomationReviewAsync();
			string path = vm.AutomationReviewFile;
			byte[] backup = File.ReadAllBytes(path + ".bak");
			if (missing) File.Delete(path);
			else File.WriteAllText(path, "{}");
			if (missing) {
				await Assert.ThrowsAsync<InvalidDataException>(() => vm.SaveAutomationReviewRecordsAsync());
				Assert.False(File.Exists(path));
			}
			else {
				await Assert.ThrowsAnyAsync<JsonException>(() => vm.SaveAutomationReviewRecordsAsync());
				Assert.Equal("{}", File.ReadAllText(path));
			}
			Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
			// Closing an empty result set has no decisions to save and must remain
			// possible so the damaged current ledger can be repaired on restart.
			await vm.PrepareAutomationRescanAsync();
			Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
		}
		finally {
			// Appearance and the shared headless shell subscribe to this instance.
			// Preserve it so later tests keep receiving property changes.
			settings.CustomDatabaseFolder = originalDatabase;
			SettingsFile.SetSettingsPath(null);
			try { Directory.Delete(folder, true); } catch { }
		}
	});

	[Fact]
	public Task EmptyStartupFallbackCanPrepareToCloseWithoutRewritingUnreadableSettings() => HeadlessUi.Run(async () => {
		string folder = Directory.CreateTempSubdirectory("vdf-unreadable-close-").FullName;
		var settings = SettingsFile.Instance;
		string originalDatabase = settings.CustomDatabaseFolder;
		string path = Path.Combine(folder, "Settings.json");
		try {
			SettingsFile.SetSettingsPath(path);
			settings.CustomDatabaseFolder = folder;
			SettingsFile.SaveSettings();
			string policy = Path.Combine(folder, VDF.Core.Cleanup.CleanupProtectionPolicyStore.FileName);
			byte[] originalPolicy = File.ReadAllBytes(policy);
			File.WriteAllText(path, "{ malformed json");
			SettingsFile.LoadSettingsAtStartup();
			Assert.NotNull(SettingsFile.StartupLoadError);
			var vm = new MainWindowVM();
			Assert.Empty(vm.Duplicates);
			await vm.PrepareAutomationRescanAsync();
			Assert.Equal("{ malformed json", File.ReadAllText(path));
			Assert.Equal(originalPolicy, File.ReadAllBytes(policy));
		}
		finally {
			// A missing fresh path clears the startup error without replacing the
			// instance observed by the shared shell and appearance subscriptions.
			SettingsFile.SetSettingsPath(Path.Combine(folder, "fresh.json"));
			SettingsFile.LoadSettingsAtStartup();
			settings.CustomDatabaseFolder = originalDatabase;
			SettingsFile.SetSettingsPath(null);
			try { Directory.Delete(folder, true); } catch { }
		}
	});
}
