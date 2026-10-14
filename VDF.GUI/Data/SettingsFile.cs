// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using ReactiveUI;
using VDF.Core.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Data {
	public enum ThumbnailDoubleClickAction { OpenFile, OpenThumbnailComparer }
	/// <summary>Which theme the app uses. System follows what the operating system is set to, live.</summary>
	public enum ThemeMode { System, Light, Dark }

	public class SettingsFile : ReactiveObject {
		static SettingsFile? instance;
		static string? settingsPath;
		readonly Dictionary<string, VDF.Core.Review.FileIdentity?> knownSettingsIdentities = new(VDF.Core.Utils.PathComparer.ForCurrentPlatform);

		[JsonIgnore]
		public static SettingsFile Instance => instance ??= new SettingsFile();

		public SettingsFile() { }

		public VDF.Core.Selection.SelectionOptions AutomationSelection { get; set; } = new();
		public List<VDF.Core.Selection.NamedSelectionProfile> SavedSelectionProfiles { get; set; } = new();
		public VDF.Core.Nfo.NfoOptions NfoFavourites { get; set; } = new();
		public string QuarantineFolder { get; set; } = string.Empty;


		public static void SetSettingsPath(string? path) {
			settingsPath = string.IsNullOrWhiteSpace(path) ? null : path;
		}

		static string ResolveSettingsPath(string? path) {
			if (!string.IsNullOrWhiteSpace(path))
				return path;
			if (!string.IsNullOrWhiteSpace(settingsPath))
				return settingsPath;

			return FileUtils.SafePathCombine(CoreUtils.SettingsFolder, "Settings.json");
		}
		public class CustomActionCommands {
			public string OpenItemInFolder { get; set; } = string.Empty;
			public string OpenMultipleInFolder { get; set; } = string.Empty;
			public string OpenItem { get; set; } = string.Empty;
			public string OpenMultiple { get; set; } = string.Empty;
		}

		[JsonPropertyName("Includes")]
		public ObservableCollection<string> Includes { get; set; } = new();
		[JsonPropertyName("Blacklists")]
		public ObservableCollection<string> Blacklists { get; set; } = new();
		[JsonPropertyName("LastCustomSelectExpression")]
		public string LastCustomSelectExpression {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = string.Empty;

		[JsonPropertyName("ExpressionHistory")]
		public ObservableCollection<string> ExpressionHistory {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = new();

		[JsonPropertyName("ExpressionPresets")]
		public ObservableCollection<ExpressionPreset> ExpressionPresets {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = new();

		[JsonPropertyName("CustomSelectionPresets")]
		public ObservableCollection<CustomSelectionPreset> CustomSelectionPresets {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = new();

		[JsonPropertyName("AutoApplySelectionPresetEnabled")]
		public bool AutoApplySelectionPresetEnabled {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		/// <summary>Name of the custom-selection preset applied automatically after every scan.</summary>
		[JsonPropertyName("AutoApplySelectionPreset")]
		public string AutoApplySelectionPreset {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = string.Empty;

		[JsonPropertyName("MainWindowWidth")]
		public double? MainWindowWidth {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("MainWindowHeight")]
		public double? MainWindowHeight {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("MainWindowPositionX")]
		public int? MainWindowPositionX {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("MainWindowPositionY")]
		public int? MainWindowPositionY {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("MainWindowMaximized")]
		public bool MainWindowMaximized {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("LanguageCode")]
		public string LanguageCode {
			get;
			set => this.RaiseAndSetIfChanged(ref field, ResolveLanguageCode(value));
		} = ResolveDefaultLanguageCode();
		[JsonPropertyName("IgnoreReadOnlyFolders")]
		public bool IgnoreReadOnlyFolders {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ExcludeHardLinks")]
		public bool ExcludeHardLinks {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("IgnoreReparsePoints")]
		public bool IgnoreReparsePoints {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		// IgnoreBlackPixels/IgnoreWhitePixels/CompareHorizontallyFlipped/Percent defaults
		// form the "Edited & altered copies" scan profile — the recommended default for
		// fresh installs (redesign stage 2). Existing settings files carry explicit
		// values for every key, so nobody's configuration changes.
		[JsonPropertyName("IgnoreBlackPixels")]
		public bool IgnoreBlackPixels {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("IgnoreWhitePixels")]
		public bool IgnoreWhitePixels {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("MaxDegreeOfParallelism")]
		public int MaxDegreeOfParallelism {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = -1;
		/// <summary>Worker cap for the CPU-bound matching phases; 0 or less = automatic CPU-headroom cap — see Core setting.</summary>
		[JsonPropertyName("MatchingMaxDegreeOfParallelism")]
		public int MatchingMaxDegreeOfParallelism {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		/// <summary>Per-drive cap for slow drives (spindle HDDs / network shares) — see Core setting.</summary>
		[JsonPropertyName("HddMaxDegreeOfParallelism")]
		public int HddMaxDegreeOfParallelism {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 2;
		/// <summary>Drive root → "SSD"/"HDD" scan-concurrency overrides. No editor UI yet
		/// (planned with the per-drive scan rows); power users can edit Settings.json.</summary>
		[JsonPropertyName("DriveTypeOverrides")]
		public Dictionary<string, string> DriveTypeOverrides {
			get;
			// STJ drops the comparer on deserialization — re-wrap so drive letters stay case-insensitive.
			set => field = value == null
				? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				: new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
		} = new(StringComparer.OrdinalIgnoreCase);
		/// <summary>The Setup screen's "New here?" hint strip, dismissible once.</summary>
		[JsonPropertyName("WelcomeStripDismissed")]
		public bool WelcomeStripDismissed {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		/// <summary>Snapshot of the profile-managed knobs from when the user last left a
		/// custom configuration; selecting the Custom profile restores it.</summary>
		[JsonPropertyName("CustomScanKnobs")]
		public ScanKnobs? CustomScanKnobs {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("HardwareAccelerationMode")]
		public Core.FFTools.FFHardwareAccelerationMode HardwareAccelerationMode {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = Core.FFTools.FFHardwareAccelerationMode.auto;
		// Part of the "Edited & altered copies" default profile (see IgnoreBlackPixels note).
		[JsonPropertyName("CompareHorizontallyFlipped")]
		public bool CompareHorizontallyFlipped {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("IncludeSubDirectories")]
		public bool IncludeSubDirectories {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("IncludeImages")]
		public bool IncludeImages {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("GeneratePreviewThumbnails")]
		public bool GeneratePreviewThumbnails {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("ThumbnailMaxWidth")]
		public int ThumbnailMaxWidth {
			get;
			set {
				int clamped = Math.Clamp(value, 48, 960);
				if (clamped == field) return;
				this.RaiseAndSetIfChanged(ref field, clamped);
				// The old view sized its layout from this extraction width; keep that
				// behavior by moving the results Preview column along. The drag grip can
				// still diverge afterwards (a persisted ResultsPreviewWidth is loaded
				// after this property and wins on startup).
				ResultsPreviewWidth = clamped;
			}
		} = 100;
		[JsonPropertyName("ExtendedFFToolsLogging")]
		public bool ExtendedFFToolsLogging {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("LogExcludedFiles")]
		public bool LogExcludedFiles {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("AlwaysRetryFailedSampling")]
		public bool AlwaysRetryFailedSampling {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = false;
		[JsonPropertyName("UseNativeFfmpegBinding")]
		public bool UseNativeFfmpegBinding {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("CustomFFArguments")]
		public string CustomFFArguments {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = string.Empty;

		[JsonPropertyName("BackupAfterListChanged")]
		public bool BackupAfterListChanged {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("AskToSaveResultsOnExit")]
		public bool AskToSaveResultsOnExit {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("IncludeNonExistingFiles")]
		public bool IncludeNonExistingFiles {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("RememberDeletedContent")]
		public bool RememberDeletedContent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("AutoCheckDeletedContentMatches")]
		public bool AutoCheckDeletedContentMatches {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ScanAgainstEntireDatabase")]
		public bool ScanAgainstEntireDatabase {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("FolderMatchMode")]
		public Core.FolderMatchMode FolderMatchMode {
			get;
			set {
				this.RaiseAndSetIfChanged(ref field, value);
				this.RaisePropertyChanged(nameof(IsFolderMatchModeActive));
			}
		}
		public bool IsFolderMatchModeActive => FolderMatchMode != Core.FolderMatchMode.None;
		[JsonPropertyName("SameFolderDepth")]
		public int SameFolderDepth {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 1;
		[JsonPropertyName("UsePHash")]
		public bool UsePHash {
			get;
			set {
				this.RaiseAndSetIfChanged(ref field, value);
				this.RaisePropertyChanged(nameof(PHashComparisonActive));
			}
		}
		/// <summary>#842: run grayscale AND pHash in one comparison pass and badge which algorithm found each group. Takes precedence over UsePHash.</summary>
		[JsonPropertyName("CombineGrayPHash")]
		public bool CombineGrayPHash {
			get;
			set {
				this.RaiseAndSetIfChanged(ref field, value);
				this.RaisePropertyChanged(nameof(PHashComparisonActive));
			}
		}
		/// <summary>The pHash comparison (and its sample-quorum setting) is in play - alone or combined.</summary>
		[JsonIgnore]
		public bool PHashComparisonActive => UsePHash || CombineGrayPHash;
		/// <summary>Percentage of sampled frame positions that must individually pass the pHash threshold — see Core's PHashRequiredMatchingSampleRatio (0..1).</summary>
		[JsonPropertyName("PHashSampleRatioPercent")]
		public float PHashSampleRatioPercent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, Math.Clamp(value, 1f, 100f));
		} = 60f;
		[JsonPropertyName("UseAiMatching")]
		public bool UseAiMatching {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		/// <summary>Similarity threshold (percent = cosine·100) for the AI matching pass.</summary>
		[JsonPropertyName("AiPercent")]
		public float AiPercent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, Math.Clamp(value, 50f, 100f));
		} = 94f;
		[JsonPropertyName("EnableAiPartialDetection")]
		public bool EnableAiPartialDetection {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		/// <summary>Per-frame hit threshold (percent) for the visual partial-duplicate pass.</summary>
		[JsonPropertyName("AiPartialHitPercent")]
		public float AiPartialHitPercent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, Math.Clamp(value, 70f, 99f));
		} = 89f;
		/// <summary>GUI mirror of Core Settings.NeedsAiComponents — keep the two in sync.</summary>
		[JsonIgnore]
		public bool NeedsAiComponents => UseAiMatching || EnableAiPartialDetection;
		[JsonPropertyName("UseExifCreationDate")]
		public bool UseExifCreationDate {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		// Part of the "Edited & altered copies" default profile (see IgnoreBlackPixels note).
		[JsonPropertyName("Percent")]
		public float Percent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 92f;
		[JsonPropertyName("PercentDurationDifference")]
		public double PercentDurationDifference {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 20d;
		[JsonPropertyName("DurationDifferenceMinSeconds")]
		public int DurationDifferenceMinSeconds {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("DurationDifferenceMaxSeconds")]
		public int DurationDifferenceMaxSeconds {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("MaxSamplingDurationSeconds")]
		public int MaxSamplingDurationSeconds {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("Thumbnails")]
		public int Thumbnails {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 1;
		[JsonPropertyName("CustomCommands")]
		public CustomActionCommands CustomCommands { get; set; } = new();
		[JsonPropertyName("CustomDatabaseFolder")]
		public string CustomDatabaseFolder {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = string.Empty;
		[JsonPropertyName("DatabaseCheckpointIntervalMinutes")]
		public int DatabaseCheckpointIntervalMinutes {
			get;
			set => this.RaiseAndSetIfChanged(ref field, Math.Max(0, value));
		} = 5;

		static readonly object settingsWriteGate = new();
		public static void SaveSettings(string? path = null) {
			if (StartupLoadError is not null) throw new InvalidDataException("Settings are unreadable. Restore a valid settings file and reload or restart before saving cleanup protection settings.");
			lock (settingsWriteGate) {
				path = Path.GetFullPath(ResolveSettingsPath(path));
				bool activeSettings = VDF.Core.Review.SafePaths.Same(path, Path.GetFullPath(ResolveSettingsPath(null)));
				using var lease = VDF.Core.Review.JournalWriteLease.Acquire(path);
				if (Instance.knownSettingsIdentities.TryGetValue(path, out var loaded) &&
					(loaded is null ? VDF.Core.Review.SafePaths.Exists(path) : !loaded.MatchesCurrent(forceHash: true)))
					throw new InvalidDataException("Settings changed outside this GUI. Reload or restart before saving; the newer settings were preserved.");
				if (activeSettings)
					VDF.Core.Cleanup.CleanupProtectionPolicyStore.ValidateSettingsWriteAsync(
						CoreUtils.ResolveDatabaseFolder(Instance.CustomDatabaseFolder)).GetAwaiter().GetResult();
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
				try {
					using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
						JsonSerializer.Serialize(stream, Instance, GuiJsonContext.Default.SettingsFile);
						stream.Flush(flushToDisk: true);
					}
					VDF.Core.Review.DurableFileOperations.Move(temp, path, overwrite: true);
					Instance.knownSettingsIdentities[path] = VDF.Core.Review.FileIdentity.Capture(path)
						?? throw new IOException("The saved settings file could not be validated.");
					// Named profiles are exports. Only the active settings file can
					// authorize or anchor the protection policy used by live cleanup.
					if (activeSettings) ExportCleanupProtectionPolicyAsync(path).GetAwaiter().GetResult();
				}
				finally { if (File.Exists(temp)) File.Delete(temp); }
			}
		}

		public static Task ExportCleanupProtectionPolicyAsync(string? sourceSettingsPath = null) {
			if (StartupLoadError is not null) throw new InvalidDataException("Settings are unreadable; existing cleanup protections were preserved. Restore valid settings and reload or restart before cleanup.");
			string path = Path.GetFullPath(ResolveSettingsPath(null));
			if (!string.IsNullOrWhiteSpace(sourceSettingsPath) &&
				!VDF.Core.Review.SafePaths.Same(path, Path.GetFullPath(sourceSettingsPath)))
				throw new InvalidOperationException("A named settings profile cannot be the source of live cleanup protections. Save it as the active settings before exporting its protection policy.");
			var settings = Instance;
			string stateFolder = CoreUtils.ResolveDatabaseFolder(settings.CustomDatabaseFolder);
			VDF.Core.Review.FileIdentity? source = null;
			if (VDF.Core.Review.SafePaths.Exists(path)) {
				// A second GUI or an external edit may have changed protections since this
				// instance loaded. Project saved bytes, not the stale in-memory instance.
				settings = ReadSavedSettings(path, out var readSource);
				source = readSource;
			}
			return VDF.Core.Cleanup.CleanupProtectionPolicyStore.SaveAsync(
				stateFolder, new VDF.Core.Cleanup.CleanupProtectionPolicy {
					NfoOptions = settings.NfoFavourites,
					FolderRules = settings.AutomationSelection.FolderRules,
					OverlapPolicy = settings.AutomationSelection.OverlapPolicy
				}, source);
		}

		static SettingsFile ReadSavedSettings(string path, out VDF.Core.Review.FileIdentity source,
			Action<VDF.Core.Review.FileIdentity>? observedSource = null) {
			VDF.Core.Review.SafePaths.RequireRegularFile(path);
			if (new FileInfo(path).Length > 32L * 1024 * 1024) throw new InvalidDataException("The settings file exceeds the supported size limit.");
			byte[] bytes = File.ReadAllBytes(path);
			source = VDF.Core.Review.FileIdentity.Capture(path) ?? throw new IOException("The settings file is unavailable or changed during projection.");
			if (source.Length != bytes.Length || source.Sha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)))
				throw new InvalidDataException("GUI settings changed while they were read. Reload or refresh them before cleanup.");
			observedSource?.Invoke(source);
			var settings = JsonSerializer.Deserialize(bytes, GuiJsonContext.Default.SettingsFile)
				?? throw new JsonException($"'{path}' does not contain a settings object.");
			settings.MigrateLegacyValues();
			settings.knownSettingsIdentities[source.Path] = source;
			return settings;
		}

		[JsonPropertyName("UseMica")]
		public bool UseMica {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = false;
		[JsonPropertyName("ThemeMode")]
		public ThemeMode ThemeMode {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = ThemeMode.System;
		/// <summary>
		/// Size of everything in the app in percent. 0 follows the text size set in the
		/// operating system; a percentage is for systems that have no such setting, or for
		/// wanting VDF larger than the rest (#923).
		/// </summary>
		[JsonPropertyName("UiScalePercent")]
		public int UiScalePercent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value <= 0 ? 0 : Math.Clamp(value, 50, 300));
		}
		/// <summary>
		/// The high contrast version of the theme even when the operating system does not ask
		/// for it. The system's own request is always followed.
		/// </summary>
		[JsonPropertyName("AlwaysHighContrast")]
		public bool AlwaysHighContrast {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		/// <summary>
		/// Animations stand still even when the operating system does not say so: for systems
		/// without such a setting, and for users who want it in VDF only. The system's own
		/// "off" is always followed.
		/// </summary>
		[JsonPropertyName("AlwaysReduceMotion")]
		public bool AlwaysReduceMotion {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		/// <summary>
		/// The on/off switch <see cref="ThemeMode"/> replaced, read once from an older settings
		/// file and never written again. It defaulted to on, so "on" says nothing about what
		/// the user wanted and becomes System; "off" was a choice and stays Light.
		/// </summary>
		[JsonPropertyName("DarkMode")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public bool? LegacyDarkMode { get; set; }

		internal void MigrateLegacyValues() {
			if (LegacyDarkMode == false && ThemeMode == ThemeMode.System)
				ThemeMode = ThemeMode.Light;
			LegacyDarkMode = null;
		}
		[JsonPropertyName("ThumbnailComparerWindowWidth")]
		public double? ThumbnailComparerWindowWidth {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ThumbnailComparerWindowHeight")]
		public double? ThumbnailComparerWindowHeight {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ThumbnailComparerWindowPositionX")]
		public double? ThumbnailComparerWindowPositionX {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ThumbnailComparerWindowPositionY")]
		public double? ThumbnailComparerWindowPositionY {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ThumbnailComparerWindowScreenIndex")]
		public int? ThumbnailComparerWindowScreenIndex {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		// Side-by-side first (redesign stage 4, maintainer directive on comparer view modes).
		[JsonPropertyName("ThumbnailComparerMode")]
		public CompareMode ThumbnailComparerMode {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = CompareMode.SideBySide;
		[JsonPropertyName("ThumbnailComparerDiffSensitivity")]
		public double ThumbnailComparerDiffSensitivity {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 0.5;
		[JsonPropertyName("ThumbnailComparerHighlightDifferences")]
		public bool ThumbnailComparerHighlightDifferences {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ShowThumbnailColumn")]
		public bool ShowThumbnailColumn {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("ShowDurationColumn")]
		public bool ShowDurationColumn {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("ShowFormatColumn")]
		public bool ShowFormatColumn {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("ShowBitrateColumn")]
		public bool ShowBitrateColumn {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		/// <summary>Audio and subtitle track languages (#899). Off by default: experts opt in to extra columns.</summary>
		[JsonPropertyName("ShowLanguagesColumn")]
		public bool ShowLanguagesColumn {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		// First-results hint: "drag the Preview handle / raise the thumbnail width" (one-shot)
		[JsonPropertyName("ResultsHintDismissed")]
		public bool ResultsHintDismissed {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ShowSimilarityColumn")]
		public bool ShowSimilarityColumn {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("ShowSizeDateColumn")]
		public bool ShowSizeDateColumn {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("ResultsSortMode")]
		public ViewModels.ResultsSortMode ResultsSortMode {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = ViewModels.ResultsSortMode.WastedSpace;
		[JsonPropertyName("ResultsSortDescending")]
		public bool ResultsSortDescending {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		/// <summary>Show the BEST-badged file at the top of each group, ahead of the sort order (#846).</summary>
		[JsonPropertyName("ResultsBestFirst")]
		public bool ResultsBestFirst {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		/// <summary>Width of the Preview column in the results list; scales the preview frames.
		/// The old 480 cap made thumbnails unresizable past a quarter of a 1080p screen (#834).</summary>
		[JsonPropertyName("ResultsPreviewWidth")]
		public double ResultsPreviewWidth {
			get;
			set => this.RaiseAndSetIfChanged(ref field, Math.Clamp(value, 56, 1600));
		} = 160;
		/// <summary>
		/// Show and use the date a file was last modified instead of the date it was created.
		/// Copying or downloading a file sets its creation date to that moment; the modified
		/// date usually survives both (#907).
		/// </summary>
		[JsonPropertyName("ResultsShowDateModified")]
		public bool ResultsShowDateModified {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ResultsCompactRows")]
		public bool ResultsCompactRows {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ThumbnailDoubleClickAction")]
		public ThumbnailDoubleClickAction ThumbnailDoubleClickAction {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = ThumbnailDoubleClickAction.OpenFile;
		[JsonPropertyName("FilterByFilePathContains")]
		public bool FilterByFilePathContains {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("FilePathContainsTexts")]
		public ObservableCollection<string> FilePathContainsTexts {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = new();
		[JsonPropertyName("FilterByFilePathNotContains")]
		public bool FilterByFilePathNotContains {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("FilePathNotContainsTexts")]
		public ObservableCollection<string> FilePathNotContainsTexts {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = new();
		[JsonPropertyName("FilterByFileSize")]
		public bool FilterByFileSize {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("MaximumFileSize")]
		public int MaximumFileSize {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 999999999;
		[JsonPropertyName("MinimumFileSize")]
		public int MinimumFileSize {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 0;

		[JsonPropertyName("EnablePartialClipDetection")]
		public bool EnablePartialClipDetection {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("PartialClipMinRatioPercent")]
		public double PartialClipMinRatioPercent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 10d;
		[JsonPropertyName("PartialClipSimilarityThresholdPercent")]
		public double PartialClipSimilarityThresholdPercent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 80d;
		[JsonPropertyName("PartialClipRequireVisualMatch")]
		public bool PartialClipRequireVisualMatch {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("PartialClipVisualThresholdPercent")]
		public double PartialClipVisualThresholdPercent {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 85d;

		// Video bitrate ranks above FPS: among equal-resolution re-encodes bitrate is the
		// stronger quality signal, and a marginally higher framerate must not outrank a
		// much better encode (#839). Saved user orders are untouched.
		[JsonPropertyName("QualityCriteriaOrder")]
		public List<string> QualityCriteriaOrder {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = ["Duration", "Resolution", "Bitrate", "FPS", "Bits per pixel", "Audio Bitrate", "Size", "SizeLarger"];
		/// <summary>
		/// Criteria the quality ranking ignores (#885). "Size (larger file wins)" starts
		/// switched off, so nothing changes until someone turns it on (#895).
		/// </summary>
		[JsonPropertyName("QualityCriteriaDisabled")]
		public List<string> QualityCriteriaDisabled {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = ["SizeLarger"];

		[JsonPropertyName("EnableScheduledScan")]
		public bool EnableScheduledScan {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ScheduledScanTime")]
		public string ScheduledScanTime {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = "02:00";
		[JsonPropertyName("ScheduledScanKind")]
		public VDF.Core.Scheduling.ScanScheduleKind ScheduledScanKind {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ScheduledScanWeekdays")]
		public int ScheduledScanWeekdays {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value & 127);
		} = 2;
		[JsonPropertyName("ScheduledScanIntervalMinutes")]
		public int ScheduledScanIntervalMinutes {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 60;
		[JsonPropertyName("ScheduledScanCatchUpHours")]
		public int ScheduledScanCatchUpHours {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = 24;
		/// <summary>Custom means use the current scan settings; named profiles reuse the Setup bundles.</summary>
		[JsonPropertyName("ScheduledScanProfile")]
		public ScanProfile ScheduledScanProfile {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = ScanProfile.Custom;
		/// <summary>Durable UTC watermark claimed before starting; failures and restarts do not replay it.</summary>
		[JsonPropertyName("ScheduledLastClaimedSlotUtc")]
		public DateTimeOffset? ScheduledLastClaimedSlotUtc {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ScheduledLastStartedUtc")]
		public DateTimeOffset? ScheduledLastStartedUtc {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ScheduledLastCompletedUtc")]
		public DateTimeOffset? ScheduledLastCompletedUtc {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ScheduledLastDurationSeconds")]
		public double ScheduledLastDurationSeconds {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}
		[JsonPropertyName("ScheduledLastStatus")]
		public string ScheduledLastStatus {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = "Never run";
		[JsonPropertyName("NotifyOnScheduledScanComplete")]
		public bool NotifyOnScheduledScanComplete {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = true;
		[JsonPropertyName("NotifyOnScanComplete")]
		public bool NotifyOnScanComplete {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		}

		[JsonPropertyName("KeyboardShortcuts")]
		public Dictionary<string, string> KeyboardShortcuts {
			get;
			set => this.RaiseAndSetIfChanged(ref field, value);
		} = new();

		public static void LoadSettings(string? path = null) => LoadSettingsCore(path, null);

		static void LoadSettingsCore(string? path, Action<VDF.Core.Review.FileIdentity>? observedSource) {
			path ??= settingsPath;
			if ((path == null || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) && LoadOldSettings(path))
				return;

			path = ResolveSettingsPath(path);
			if (!File.Exists(path)) {
				if (VDF.Core.Review.SafePaths.Exists(path)) throw new IOException("The settings path is not an available regular file.");
				Instance.knownSettingsIdentities[Path.GetFullPath(path)] = null;
				return;
			}
			var loaded = ReadSavedSettings(path, out _, observedSource);
			// Importing/saving another profile must not forget the baseline of the
			// active settings file and permit a later autosave to replace external edits.
			if (instance is not null)
				foreach (var entry in instance.knownSettingsIdentities) loaded.knownSettingsIdentities.TryAdd(entry.Key, entry.Value);
			instance = loaded;
			StartupLoadError = null;
		}

		/// <summary>
		/// Set when <see cref="LoadSettingsAtStartup"/> had to fall back to default settings;
		/// the GUI shows it once the main window is up.
		/// </summary>
		[JsonIgnore]
		public static string? StartupLoadError { get; private set; }

		/// <summary>
		/// Startup counterpart of <see cref="LoadSettings"/> that never throws. An unreadable
		/// settings file (torn write during save, disk corruption) used to abort startup inside
		/// the MainWindow constructor — before any exception handler or window existed — so the
		/// app silently never opened again (#830). Keep the broken file as "*.corrupt" for
		/// diagnosis and start with default settings instead.
		/// </summary>
		public static void LoadSettingsAtStartup() {
			VDF.Core.Review.FileIdentity? observedSource = null;
			try {
				LoadSettingsCore(null, source => observedSource = source);
				StartupLoadError = null;
			}
			catch (Exception ex) {
				string message = $"Settings could not be loaded: {ex.Message}";
				string jsonPath = ResolveSettingsPath(null);
				// Authorize a later explicit import against only the bytes whose
				// parsing failed, never a newer file written before this catch ran.
				try {
					string fullPath = Path.GetFullPath(jsonPath);
					Instance.knownSettingsIdentities[fullPath] =
						observedSource is not null && VDF.Core.Review.SafePaths.Same(fullPath, observedSource.Path)
							? observedSource : null;
				}
				catch { /* An unavailable baseline cannot authorize replacing an existing file. */ }
				if (File.Exists(jsonPath)) {
					try {
						File.Copy(jsonPath, jsonPath + ".corrupt", overwrite: true);
						message += $" The unreadable file was kept as '{jsonPath}.corrupt'.";
					}
					catch { /* keeping the evidence must never abort startup */ }
				}
				StartupLoadError = message;
				Logger.Instance.Error(message);
			}
		}

		static bool LoadOldSettings(string? path) {
			path ??= FileUtils.SafePathCombine(CoreUtils.CurrentFolder, "Settings.xml");
			if (!File.Exists(path)) return false;
			var xmlSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
			using var reader = XmlReader.Create(path, xmlSettings);
			var xDoc = XDocument.Load(reader);
			foreach (var n in xDoc.Descendants("Include"))
				Instance.Includes.Add(n.Value);
			foreach (var n in xDoc.Descendants("Exclude"))
				Instance.Blacklists.Add(n.Value);
			foreach (var n in xDoc.Descendants("Percent"))
				if (int.TryParse(n.Value, out var value))
					Instance.Percent = value;
			foreach (var n in xDoc.Descendants("MaxDegreeOfParallelism"))
				if (int.TryParse(n.Value, out var value))
					Instance.MaxDegreeOfParallelism = value;
			foreach (var n in xDoc.Descendants("Thumbnails"))
				if (int.TryParse(n.Value, out var value))
					Instance.Thumbnails = value;
			foreach (var n in xDoc.Descendants("IncludeSubDirectories"))
				if (bool.TryParse(n.Value, out var value))
					Instance.IncludeSubDirectories = value;
			foreach (var n in xDoc.Descendants("IncludeImages"))
				if (bool.TryParse(n.Value, out var value))
					Instance.IncludeImages = value;
			foreach (var n in xDoc.Descendants("IgnoreReadOnlyFolders"))
				if (bool.TryParse(n.Value, out var value))
					Instance.IgnoreReadOnlyFolders = value;
			//09.03.21: UseCuda is obsolete and has been replaced with UseHardwareAcceleration.
			foreach (var n in xDoc.Descendants("UseCuda"))
				if (bool.TryParse(n.Value, out var value))
					Instance.HardwareAccelerationMode = value ? Core.FFTools.FFHardwareAccelerationMode.auto : Core.FFTools.FFHardwareAccelerationMode.none;
			foreach (var n in xDoc.Descendants("HardwareAccelerationMode"))
				if (Enum.TryParse<Core.FFTools.FFHardwareAccelerationMode>(n.Value, out var value))
					Instance.HardwareAccelerationMode = value;
			foreach (var n in xDoc.Descendants("GeneratePreviewThumbnails"))
				if (bool.TryParse(n.Value, out var value))
					Instance.GeneratePreviewThumbnails = value;
			foreach (var n in xDoc.Descendants("IgnoreHardlinks"))
				if (bool.TryParse(n.Value, out var value))
					Instance.IgnoreReparsePoints = value;
			foreach (var n in xDoc.Descendants("ExtendedFFToolsLogging"))
				if (bool.TryParse(n.Value, out var value))
					Instance.ExtendedFFToolsLogging = value;
			foreach (var n in xDoc.Descendants("AlwaysRetryFailedSampling"))
				if (bool.TryParse(n.Value, out var value))
					Instance.AlwaysRetryFailedSampling = value;
			foreach (var n in xDoc.Descendants("UseNativeFfmpegBinding"))
				if (bool.TryParse(n.Value, out var value))
					Instance.UseNativeFfmpegBinding = value;
			foreach (var n in xDoc.Descendants("BackupAfterListChanged"))
				if (bool.TryParse(n.Value, out var value))
					Instance.BackupAfterListChanged = value;
			foreach (var n in xDoc.Descendants("IgnoreBlackPixels"))
				if (bool.TryParse(n.Value, out var value))
					Instance.IgnoreBlackPixels = value;
			foreach (var n in xDoc.Descendants("IgnoreWhitePixels"))
				if (bool.TryParse(n.Value, out var value))
					Instance.IgnoreWhitePixels = value;
			foreach (var n in xDoc.Descendants("CustomFFArguments"))
				Instance.CustomFFArguments = n.Value;
			foreach (var n in xDoc.Descendants("LastCustomSelectExpression"))
				Instance.LastCustomSelectExpression = n.Value;
			foreach (var n in xDoc.Descendants("CompareHorizontallyFlipped"))
				if (bool.TryParse(n.Value, out var value))
					Instance.CompareHorizontallyFlipped = value;
			// Parsing has succeeded. Migration itself saves the imported profile,
			// so release the startup guard for that explicit recovery and restore
			// it if saving or preserving the legacy file cannot complete.
			string? startupError = StartupLoadError;
			StartupLoadError = null;
			try {
				SaveSettings(Path.ChangeExtension(path, "json"));
				File.Delete(path);
			}
			catch {
				StartupLoadError = startupError;
				throw;
			}
			return true;
		}

		static string ResolveDefaultLanguageCode() => ResolveLanguageCode(null);

		static string ResolveLanguageCode(string? languageCode) {
			if (!string.IsNullOrWhiteSpace(languageCode))
				return languageCode;

			var culture = CultureInfo.CurrentUICulture;
			if (!string.IsNullOrWhiteSpace(culture.TwoLetterISOLanguageName))
				return culture.TwoLetterISOLanguageName;

			return "en";
		}
	}
}
