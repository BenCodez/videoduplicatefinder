// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using VDF.Core.Cleanup;
using VDF.GUI.Data;

namespace VDF.GUI.Tests;

[Collection("Automation settings singleton")]
public sealed class CleanupPolicyProjectionTests : IDisposable {
	readonly string _directory = Directory.CreateTempSubdirectory("vdf-protection-projection-").FullName;
	readonly string _original = JsonSerializer.Serialize(SettingsFile.Instance, GuiJsonContext.Default.SettingsFile);
	string State => Path.Combine(_directory, "state");
	string Settings => Path.Combine(_directory, "Settings.json");
	string Policy => Path.Combine(State, CleanupProtectionPolicyStore.FileName);
	public CleanupPolicyProjectionTests() {
		Directory.CreateDirectory(State);
		var settings = new SettingsFile { CustomDatabaseFolder = State };
		settings.NfoFavourites.Enabled = true;
		settings.NfoFavourites.InheritSeries = true;
		settings.AutomationSelection.FolderRules = [new() { Directory = Path.Combine(_directory, "media"), Priority = 1000, ProtectAll = true }];
		File.WriteAllText(Settings, JsonSerializer.Serialize(settings, GuiJsonContext.Default.SettingsFile));
		SettingsFile.SetSettingsPath(Settings);
		SettingsFile.LoadSettings(Settings);
	}
	public void Dispose() {
		try {
			string original = Path.Combine(_directory, "original.json");
			File.WriteAllText(original, _original);
			SettingsFile.LoadSettings(original);
			SettingsFile.SetSettingsPath(null);
			Directory.Delete(_directory, true);
		}
		catch { }
	}

	[Fact]
	public async Task EverySuccessfulSettingsSaveRefreshesSharedProjection() {
		SettingsFile.SaveSettings();
		var initial = await CleanupProtectionPolicyStore.LoadAsync(State);
		Assert.True(initial.NfoOptions.Enabled);
		Assert.True(initial.NfoOptions.InheritSeries);
		Assert.True(Assert.Single(initial.FolderRules).ProtectAll);
		SettingsFile.Instance.ThemeMode = ThemeMode.Dark;
		SettingsFile.Instance.EnableScheduledScan = true;
		SettingsFile.SaveSettings();
		var afterUnrelatedSave = await CleanupProtectionPolicyStore.LoadAsync(State);
		Assert.True(afterUnrelatedSave.NfoOptions.Enabled);
		Assert.True(Assert.Single(afterUnrelatedSave.FolderRules).ProtectAll);
	}

	[Fact]
	public async Task ReviewInitializationExportWritesCurrentProtectionSettings() {
		await SettingsFile.ExportCleanupProtectionPolicyAsync();
		var policy = await CleanupProtectionPolicyStore.LoadAsync(State);
		Assert.True(policy.NfoOptions.Enabled);
		Assert.True(File.Exists(Path.Combine(State, "ReviewedFiles.json")));
	}

	[Fact]
	public async Task KnownSettingsLoadFailurePreservesPriorPolicyUntilValidReload() {
		SettingsFile.SaveSettings();
		byte[] originalSettings = File.ReadAllBytes(Settings);
		byte[] originalPolicy = File.ReadAllBytes(Policy);
		File.WriteAllText(Settings, "{ malformed json");
		SettingsFile.LoadSettingsAtStartup();
		Assert.NotNull(SettingsFile.StartupLoadError);
		SettingsFile.Instance.NfoFavourites.Enabled = false; // Simulate permissive fallback defaults.
		Assert.Throws<InvalidDataException>(() => { _ = SettingsFile.ExportCleanupProtectionPolicyAsync(); });
		Assert.Throws<InvalidDataException>(() => SettingsFile.SaveSettings());
		Assert.Equal(originalPolicy, File.ReadAllBytes(Policy));
		Assert.Equal("{ malformed json", File.ReadAllText(Settings));
		File.WriteAllBytes(Settings, originalSettings);
		SettingsFile.LoadSettings(Settings);
		Assert.Null(SettingsFile.StartupLoadError);
		SettingsFile.SaveSettings();
		Assert.True((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
	}

	[Fact]
	public async Task MissingEstablishedSettingsCannotBeRecreatedByAnUnrelatedAutosave() {
		SettingsFile.SaveSettings();
		byte[] settings = File.ReadAllBytes(Settings);
		byte[] policy = File.ReadAllBytes(Policy);
		File.Delete(Settings);
		SettingsFile.Instance.ThemeMode = ThemeMode.Dark;
		Assert.Throws<InvalidDataException>(() => SettingsFile.SaveSettings());
		Assert.False(File.Exists(Settings));
		Assert.Equal(policy, File.ReadAllBytes(Policy));
		File.WriteAllBytes(Settings, settings);
		SettingsFile.LoadSettings(Settings);
		SettingsFile.SaveSettings();
		Assert.True((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
	}

	[Fact]
	public async Task ProjectionPersistenceFailureIsReportedAndCleanupCannotLoadIt() {
		SettingsFile.SaveSettings();
		File.Delete(Policy);
		Directory.CreateDirectory(Policy);
		Assert.ThrowsAny<IOException>(() => SettingsFile.SaveSettings());
		await Assert.ThrowsAnyAsync<IOException>(() => CleanupProtectionPolicyStore.LoadAsync(State));
	}
}
