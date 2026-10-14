using VDF.Core.Nfo;
using VDF.Core.Selection;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

[CollectionDefinition("Automation settings singleton", DisableParallelization = true)]
public sealed class AutomationSettingsCollection { }

[Collection("Automation settings singleton")]
public sealed class AutomationSettingsTests {
	[Fact]
	public void DefaultsUseModificationDateAndAllGroupsWithoutAutomaticCleanup() {
		var settings = new SettingsFile();
		Assert.Equal(SelectionDateSource.Modification, settings.AutomationSelection.DateSource);
		Assert.False(settings.AutomationSelection.HighlightedGroupsOnly);
		Assert.False(settings.AutomationSelection.AutoAfterScan);
		Assert.False(settings.NfoFavourites.Enabled);
		Assert.DoesNotContain(settings.AutomationSelection.Criteria, c => c.Name == "Size");
	}

	[Fact]
	public void SelectionDraftCopiesRulesCriteriaAndPreferenceLists() {
		var original = new SelectionOptions {
			FolderRules = [new() { Directory = Path.GetTempPath(), Priority = 15, Recursive = false, ProtectAll = true }],
			Criteria = [new() { Name = "Codec", Weight = 2.5, PreferLower = true }], PreferredCodecs = ["hevc"]
		};
		var copy = AutomationSettingsVM.CloneSelectionOptions(original);
		copy.FolderRules[0].Priority = 100;
		copy.Criteria[0].Weight = 9;
		copy.PreferredCodecs.Add("av1");
		Assert.Equal(15, original.FolderRules[0].Priority);
		Assert.Equal(2.5, original.Criteria[0].Weight);
		Assert.Single(original.PreferredCodecs);
		Assert.False(copy.FolderRules[0].Recursive);
		Assert.True(copy.FolderRules[0].ProtectAll);
	}

	[Fact]
	public void NfoDraftCopiesMarkerListsAndInheritance() {
		var original = new NfoOptions { Enabled = true, InheritSeason = true, BooleanElements = ["favorite"] };
		var copy = AutomationSettingsVM.CloneNfoOptions(original);
		copy.BooleanElements.Clear();
		Assert.Single(original.BooleanElements);
		Assert.True(copy.Enabled && copy.InheritSeason);
		Assert.Equal(new[] { "hevc", "av1", "h264" }, AutomationSettingsVM.ParseList(" hevc, AV1\nHEVC\r\nh264").Select(s => s.ToLowerInvariant()));
	}

	[Fact]
	public void ProtectedIgnoredAndRejectedRowsCannotBeChecked() {
		var item = new DuplicateItemVM { ItemInfo = new() };
		item.IsProtected = true;
		item.Checked = true;
		Assert.False(item.Checked);
		item.IsProtected = false;
		foreach (var choice in new[] { ReviewChoice.Ignore, ReviewChoice.RejectGroup }) {
			item.ManualReviewChoice = choice;
			item.Checked = true;
			Assert.False(item.Checked);
		}
		item.ManualReviewChoice = ReviewChoice.None;
		item.Checked = true;
		Assert.True(item.Checked);
	}

	[Fact]
	public async Task FailedSaveToDirectoryLeavesPreviousSettingsAndFavouriteProtectionActive() {
		string folder = Directory.CreateTempSubdirectory("vdf-automation-save-").FullName;
		var settings = SettingsFile.Instance;
		var originalSelection = settings.AutomationSelection;
		var originalProfiles = settings.SavedSelectionProfiles;
		var originalNfo = settings.NfoFavourites;
		string originalQuarantine = settings.QuarantineFolder;
		try {
			var selection = new SelectionOptions {
				Profile = SelectionProfile.OldestFirst, FolderRules = [new() { Directory = folder, Priority = 42, ProtectAll = true }]
			};
			var profiles = new List<NamedSelectionProfile> { new() { Name = "Existing profile", Options = selection } };
			var nfo = new NfoOptions { Enabled = true, BooleanElements = ["favorite"] };
			settings.AutomationSelection = selection;
			settings.SavedSelectionProfiles = profiles;
			settings.NfoFavourites = nfo;
			settings.QuarantineFolder = Path.Combine(folder, "original-quarantine");
			string video = Path.Combine(folder, "movie.mp4");
			File.WriteAllBytes(video, [1, 2, 3]);
			File.WriteAllText(Path.ChangeExtension(video, ".nfo"), "<movie><favorite>true</favorite></movie>");
			var draft = new AutomationSettingsVM();
			draft.Selection.Profile = SelectionProfile.NewestFirst;
			draft.FolderRules[0].Priority = -7;
			draft.FolderRules[0].ProtectAll = false;
			draft.Nfo.Enabled = false;
			draft.BooleanElementsText = "isfavorite";
			draft.SavedProfiles.Clear();
			draft.SavedProfiles.Add(new() { Name = "Draft profile", Options = new() });
			draft.QuarantineFolder = Path.Combine(folder, "draft-quarantine");
			SettingsFile.SetSettingsPath(folder); // An existing directory cannot be the settings file.

			Assert.False(await draft.SaveAsync());

			Assert.Same(selection, settings.AutomationSelection);
			Assert.Same(profiles, settings.SavedSelectionProfiles);
			Assert.Same(nfo, settings.NfoFavourites);
			Assert.Equal(Path.Combine(folder, "original-quarantine"), settings.QuarantineFolder);
			Assert.Equal(42, new FolderRuleMatcher(settings.AutomationSelection.FolderRules, FolderOverlapPolicy.MostSpecific).Match(video).Priority);
			Assert.True(new NfoFavouriteService().Inspect(video, settings.NfoFavourites).IsFavourite);
			Assert.Equal("Existing profile", settings.SavedSelectionProfiles.Single().Name);
			Assert.False(draft.IsWorking);
			Assert.Contains("Previous settings remain active", draft.Status);
			Assert.True(Directory.Exists(folder));
		}
		finally {
			SettingsFile.SetSettingsPath(null);
			settings.AutomationSelection = originalSelection;
			settings.SavedSelectionProfiles = originalProfiles;
			settings.NfoFavourites = originalNfo;
			settings.QuarantineFolder = originalQuarantine;
			try { Directory.Delete(folder, true); } catch { }
		}
	}
}
