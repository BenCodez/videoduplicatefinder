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
		}
		finally {
			// Appearance and the shared headless shell subscribe to this instance.
			// Preserve it so later tests keep receiving property changes.
			settings.CustomDatabaseFolder = originalDatabase;
			SettingsFile.SetSettingsPath(null);
			try { Directory.Delete(folder, true); } catch { }
		}
	});
}
