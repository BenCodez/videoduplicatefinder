// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using VDF.Core.Cleanup;
using VDF.Core.Review;
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
	public async Task ExternalProtectionEditIsProjectedFromSavedBytesAndBlocksCleanup() {
		SettingsFile.Instance.NfoFavourites.Enabled = false;
		SettingsFile.SaveSettings();
		var external = JsonSerializer.Deserialize(File.ReadAllBytes(Settings), GuiJsonContext.Default.SettingsFile)!;
		external.NfoFavourites.Enabled = true;
		byte[] newerSettings = JsonSerializer.SerializeToUtf8Bytes(external, GuiJsonContext.Default.SettingsFile);
		File.WriteAllBytes(Settings, newerSettings);
		await SettingsFile.ExportCleanupProtectionPolicyAsync();
		Assert.False(SettingsFile.Instance.NfoFavourites.Enabled);
		Assert.True((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
		string targetPath = Path.Combine(_directory, "target.mkv");
		string keeperPath = Path.Combine(_directory, "keeper.mkv");
		File.WriteAllText(targetPath, "media");
		File.WriteAllText(keeperPath, "media");
		File.WriteAllText(Path.ChangeExtension(targetPath, ".nfo"), "<movie><favorite>true</favorite></movie>");
		var target = Assert.IsType<FileIdentity>(FileIdentity.Capture(targetPath));
		var keeper = Assert.IsType<FileIdentity>(FileIdentity.Capture(keeperPath));
		await Assert.ThrowsAnyAsync<IOException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete));
		Assert.True(target.MatchesCurrent());
		Assert.True(keeper.MatchesCurrent());
		Assert.Equal(newerSettings, File.ReadAllBytes(Settings));
	}

	[Fact]
	public void StaleAutosavePreservesAnExternalProtectionEdit() {
		SettingsFile.SaveSettings();
		var external = JsonSerializer.Deserialize(File.ReadAllBytes(Settings), GuiJsonContext.Default.SettingsFile)!;
		external.NfoFavourites.InheritSeason = true;
		byte[] newerSettings = JsonSerializer.SerializeToUtf8Bytes(external, GuiJsonContext.Default.SettingsFile);
		byte[] originalPolicy = File.ReadAllBytes(Policy);
		File.WriteAllBytes(Settings, newerSettings);
		SettingsFile.Instance.ThemeMode = ThemeMode.Dark;
		Assert.Throws<InvalidDataException>(() => SettingsFile.SaveSettings());
		Assert.Equal(newerSettings, File.ReadAllBytes(Settings));
		Assert.Equal(originalPolicy, File.ReadAllBytes(Policy));
	}

	[Fact]
	public async Task FreshGuiCannotOverwriteSettingsCreatedByAnotherGui() {
		byte[] protectedSettings = File.ReadAllBytes(Settings);
		File.Delete(Settings);
		SettingsFile.LoadSettings(Settings); // This GUI observed a genuinely fresh path.
		SettingsFile.Instance.NfoFavourites.Enabled = false;
		File.WriteAllBytes(Settings, protectedSettings); // Another GUI saved protection.
		await SettingsFile.ExportCleanupProtectionPolicyAsync();
		Assert.Throws<InvalidDataException>(() => SettingsFile.SaveSettings());
		Assert.Equal(protectedSettings, File.ReadAllBytes(Settings));
		Assert.True((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
	}

	[Fact]
	public void SavingAnotherProfileDoesNotForgetTheActiveSettingsBaseline() {
		SettingsFile.SaveSettings();
		SettingsFile.SaveSettings(Path.Combine(_directory, "profile.json"));
		var external = JsonSerializer.Deserialize(File.ReadAllBytes(Settings), GuiJsonContext.Default.SettingsFile)!;
		external.NfoFavourites.InheritSeason = true;
		byte[] newerSettings = JsonSerializer.SerializeToUtf8Bytes(external, GuiJsonContext.Default.SettingsFile);
		File.WriteAllBytes(Settings, newerSettings);
		Assert.Throws<InvalidDataException>(() => SettingsFile.SaveSettings());
		Assert.Equal(newerSettings, File.ReadAllBytes(Settings));
	}

	[Fact]
	public async Task NamedProfileSavePreservesActivePolicyAndExternalActiveEditBlocksCleanup() {
		SettingsFile.Instance.NfoFavourites.Enabled = false;
		SettingsFile.SaveSettings();
		byte[] activeSettings = File.ReadAllBytes(Settings);
		byte[] activePolicy = File.ReadAllBytes(Policy);
		byte[] policyBackup = File.ReadAllBytes(Policy + ".bak");
		SettingsFile.Instance.NfoFavourites.Enabled = true;
		string profile = Path.Combine(_directory, "named-profile.json");
		SettingsFile.SaveSettings(profile);
		Assert.True(JsonSerializer.Deserialize(File.ReadAllBytes(profile), GuiJsonContext.Default.SettingsFile)!.NfoFavourites.Enabled);
		Assert.Equal(activeSettings, File.ReadAllBytes(Settings));
		Assert.Equal(activePolicy, File.ReadAllBytes(Policy));
		Assert.Equal(policyBackup, File.ReadAllBytes(Policy + ".bak"));
		Assert.False((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);

		var external = JsonSerializer.Deserialize(activeSettings, GuiJsonContext.Default.SettingsFile)!;
		external.NfoFavourites.Enabled = true;
		byte[] newerSettings = JsonSerializer.SerializeToUtf8Bytes(external, GuiJsonContext.Default.SettingsFile);
		File.WriteAllBytes(Settings, newerSettings);
		await Assert.ThrowsAsync<InvalidDataException>(() => CleanupProtectionPolicyStore.LoadAsync(State));
		string targetPath = Path.Combine(_directory, "target.mkv");
		string keeperPath = Path.Combine(_directory, "keeper.mkv");
		File.WriteAllText(targetPath, "media");
		File.WriteAllText(keeperPath, "media");
		var target = Assert.IsType<FileIdentity>(FileIdentity.Capture(targetPath));
		var keeper = Assert.IsType<FileIdentity>(FileIdentity.Capture(keeperPath));
		bool removed = false;
		await Assert.ThrowsAsync<InvalidDataException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, _ => removed = true));
		Assert.False(removed);
		Assert.True(target.MatchesCurrent());
		Assert.True(keeper.MatchesCurrent());
		Assert.Equal(newerSettings, File.ReadAllBytes(Settings));
		Assert.Equal(activePolicy, File.ReadAllBytes(Policy));
	}

	[Fact]
	public async Task NamedProfileFirstSaveDoesNotInitializeLivePolicyOrReviewJournal() {
		byte[] activeSettings = File.ReadAllBytes(Settings);
		SettingsFile.Instance.NfoFavourites.Enabled = false;
		string profile = Path.Combine(_directory, "named-profile.json");
		SettingsFile.SaveSettings(profile);
		Assert.False(JsonSerializer.Deserialize(File.ReadAllBytes(profile), GuiJsonContext.Default.SettingsFile)!.NfoFavourites.Enabled);
		Assert.Equal(activeSettings, File.ReadAllBytes(Settings));
		Assert.False(File.Exists(Policy));
		Assert.False(File.Exists(Policy + ".bak"));
		Assert.False(File.Exists(Path.Combine(State, "ReviewedFiles.json")));
		Assert.Throws<InvalidOperationException>(() => { _ = SettingsFile.ExportCleanupProtectionPolicyAsync(profile); });
		Assert.False(File.Exists(Policy));

		await SettingsFile.ExportCleanupProtectionPolicyAsync();
		Assert.True((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
	}

	[Fact]
	public async Task ImportedProfileProjectsActiveSavedSettingsUntilActiveSave() {
		SettingsFile.SaveSettings();
		var profileSettings = JsonSerializer.Deserialize(File.ReadAllBytes(Settings), GuiJsonContext.Default.SettingsFile)!;
		profileSettings.NfoFavourites.Enabled = false;
		string profile = Path.Combine(_directory, "imported-profile.json");
		File.WriteAllBytes(profile, JsonSerializer.SerializeToUtf8Bytes(profileSettings, GuiJsonContext.Default.SettingsFile));
		SettingsFile.LoadSettings(profile);
		Assert.False(SettingsFile.Instance.NfoFavourites.Enabled);
		await SettingsFile.ExportCleanupProtectionPolicyAsync();
		Assert.True((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
		using (var projected = JsonDocument.Parse(File.ReadAllBytes(Policy)))
			Assert.Equal(Settings, projected.RootElement.GetProperty("sourceSettings").GetProperty("path").GetString());

		SettingsFile.SaveSettings(Settings);
		Assert.False((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
		Assert.False(JsonSerializer.Deserialize(File.ReadAllBytes(Settings), GuiJsonContext.Default.SettingsFile)!.NfoFavourites.Enabled);
	}

	[Fact]
	public async Task ExplicitSaveToNormalizedActiveOverrideRefreshesProjection() {
		SettingsFile.Instance.NfoFavourites.Enabled = false;
		string equivalentPath = Path.Combine(_directory, ".", "Settings.json");
		SettingsFile.SaveSettings(equivalentPath);
		Assert.False((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
		await SettingsFile.ExportCleanupProtectionPolicyAsync(equivalentPath);
		using var projected = JsonDocument.Parse(File.ReadAllBytes(Policy));
		Assert.Equal(Settings, projected.RootElement.GetProperty("sourceSettings").GetProperty("path").GetString());
	}

	[Fact]
	public void ExternalMalformedSettingsCannotBeBlessedByAnExport() {
		SettingsFile.SaveSettings();
		byte[] originalPolicy = File.ReadAllBytes(Policy);
		File.WriteAllText(Settings, "{ malformed json");
		Assert.ThrowsAny<JsonException>(() => { _ = SettingsFile.ExportCleanupProtectionPolicyAsync(); });
		Assert.Equal(originalPolicy, File.ReadAllBytes(Policy));
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
