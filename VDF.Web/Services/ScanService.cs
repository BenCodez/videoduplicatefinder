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

using VDF.Core;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Web.Services {
	public enum ScanState { Idle, Scanning, Comparing, Done, Aborted, Error }

	/// <summary>Outcome of a batch file operation (delete / move / link).</summary>
	public sealed class FileOpResult {
		public int Done;
		public int Failed;
		public long FreedBytes;
		public List<string> Errors { get; } = new();
	}

	public class ScanProgressArgs {
		public string CurrentFile { get; init; } = string.Empty;
		public int Current { get; init; }
		public int Max { get; init; }
		public TimeSpan Elapsed { get; init; }
		public TimeSpan Remaining { get; init; }
		public string CurrentStage { get; init; } = string.Empty;
		public int StageCurrent { get; init; }
		public int StageMax { get; init; }
	}

	/// <summary>
	/// Singleton service that owns the ScanEngine instance and exposes
	/// scan lifecycle operations to Blazor components via events and state.
	/// </summary>
	public sealed partial class ScanService : IDisposable {
		readonly ScanEngine _engine = new();
		readonly WebSettingsService _settingsService;
		Settings _configuredSettings;
		CancellationTokenSource _cts = new();
		readonly object _operationSync = new();
		bool _scanStarting;
		bool _scanRunActive;
		bool _dataWorkActive;
		bool _disposed;

		bool WorkActiveLocked => FileOpRunning || _scanStarting || _scanRunActive || _dataWorkActive ||
			State is ScanState.Scanning or ScanState.Comparing;

		/// <summary>Includes startup and terminal scan processing, even when an error is displayed.</summary>
		public bool IsBusy { get { lock (_operationSync) return WorkActiveLocked; } }


		public ScanState State { get; private set; } = ScanState.Idle;
		public string? ErrorMessage { get; private set; }
		public ScanProgressArgs? LastProgress { get; private set; }
		/// <summary>Total files hashed (captured when BuildingHashesDone fires).</summary>
		public int FilesHashed { get; private set; }
		public IReadOnlyCollection<DuplicateItem> Duplicates => _engine.Duplicates;
		public Settings Settings => _configuredSettings;
		/// <summary>Test seam: lets tests seed results without driving a real scan.</summary>
		internal ScanEngine Engine => _engine;
		internal Func<Settings, CancellationToken, Task> PrepareScanAsync { get; set; } = DownloadRequiredAiComponentsAsync;

		/// <summary>Caches for the thumbnail endpoints — cleared whenever the results change wholesale.</summary>
		public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> HqThumbCache { get; } = new();
		public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> FullThumbCache { get; } = new();

		void ClearThumbnailCaches() {
			HqThumbCache.Clear();
			FullThumbCache.Clear();
		}

		public event Action? StateChanged;

		public ScanService(WebSettingsService settingsService) {
			_settingsService = settingsService;
			settingsService.Load(_engine.Settings);
			_configuredSettings = _engine.Settings;

			_engine.FilesEnumerated += (_, _) => Notify();
			_engine.BuildingHashesDone += (_, _) => {
				FilesHashed = LastProgress?.Max ?? 0;
				LastProgress = null;
				State = ScanState.Comparing;
				Notify();
			};
			_engine.Progress += (_, e) => {
				LastProgress = new ScanProgressArgs {
					CurrentFile = e.CurrentFile,
					Current = e.CurrentPosition,
					Max = e.MaxPosition,
					Elapsed = e.Elapsed,
					Remaining = e.Remaining,
					CurrentStage = e.CurrentStage ?? string.Empty,
					StageCurrent = e.StageCurrent,
					StageMax = e.StageMax,
				};
				Notify();
			};
			_engine.ScanDone += (_, _) => CompletionTask = CompleteWebScanAsync(false);
			_engine.ScanAborted += (_, _) => CompletionTask = CompleteWebScanAsync(true);
		}

		public Task StartScanAndCompare() => StartWebScanAsync(false, DateTimeOffset.UtcNow);

		async Task StartWebScanAsync(bool scheduled, DateTimeOffset now) {
			lock (_operationSync) {
				if (_disposed || WorkActiveLocked) return;
				_scanStarting = true;
				_scanRunActive = true;
				_cts.Dispose();
				_cts = new CancellationTokenSource();
				State = ScanState.Scanning;
				ErrorMessage = null;
				LastProgress = null;
				FilesHashed = 0;
				_terminalProcessing = false;
			}
			Notify();
			try {
				// Reserve the whole lifecycle before configuration reads, durable
				// schedule claims, first-use downloads or replacing visible results.
				_cts.Token.ThrowIfCancellationRequested();
				if (!await PrepareWebScanAsync(scheduled, now)) {
					lock (_operationSync) { _scanRunActive = false; State = _engine.Duplicates.Count > 0 ? ScanState.Done : ScanState.Idle; }
					return;
				}
				await InvalidatePersistedResultsForScanAsync();
				_engine.Duplicates.Clear();
				ClearCleanupSnapshots();
				ClearThumbnailCaches();
				ClearSelectionPlan();
				NotifyAutomationChanged();
				// Reserve the lifecycle before awaiting a first-use AI download.
				await PrepareScanAsync(_engine.Settings, _cts.Token);
				_cts.Token.ThrowIfCancellationRequested();
				if (StartSearchOverride is { } start) start(); else _engine.StartSearch();
			}
			catch (OperationCanceledException) {
				await CompleteWebScanAsync(true);
				// Stop can raise an early terminal callback before a claim write
				// returns and transfers scheduled ownership. Finish that late claim
				// even when the callback already consumed the terminal guard.
				await FinishScheduledStateAsync("Aborted");
			}
			catch (Exception ex) {
				await FinishScheduledStateAsync("Failed: " + ex.Message);
				lock (_operationSync) _scanRunActive = false;
				SetError(ex);
			}
			finally {
				lock (_operationSync) {
					_scanStarting = false;
					if (_disposed) _cts.Dispose();
				}
				Notify();
			}
		}

		static async Task DownloadRequiredAiComponentsAsync(Settings settings, CancellationToken ct) {
			if (!settings.NeedsAiComponents || VDF.Core.AI.AiComponents.IsReady) return;
			Logger.Instance.Info("Downloading AI components (ONNX Runtime + model, ~100 MB)...");
			await VDF.Core.AI.AiComponents.DownloadAsync(null, ct);
		}

		/// <summary>Called from global exception handlers to surface post-await async void exceptions.</summary>
		public void SetError(Exception ex) {
			lock (_operationSync) {
				State = ScanState.Error;
				ErrorMessage = ex.Message;
				LastProgress = null;
			}
			Notify();
		}

		public void Pause() => _engine.Pause();
		public void Resume() => _engine.Resume();

		public void Stop() {
			lock (_operationSync) {
				if (_disposed || (!_scanStarting && !_scanRunActive)) return;
				_cts.Cancel();
			}
			_engine.Stop();
		}

		public bool SaveSettings() {
			lock (_operationSync) {
				if (_disposed || WorkActiveLocked) return false;
				return _settingsService.Save(Settings);
			}
		}
		public bool ReloadSettings() {
			lock (_operationSync) {
				if (_disposed || WorkActiveLocked) return false;
				var loaded = new Settings();
				if (!_settingsService.Load(loaded)) return false;
				_configuredSettings = loaded;
				return true;
			}
		}

		public void Reset() {
			lock (_operationSync) {
				if (_disposed || WorkActiveLocked) return;
				State = ScanState.Idle;
				ErrorMessage = null;
				LastProgress = null;
				FilesHashed = 0;
				_engine.Duplicates.Clear();
				ClearCleanupSnapshots();
				ClearThumbnailCaches();
			}
			// Keep the configured scan paths when resetting results.
			NotifyAutomationChanged();
		}

		/// <summary>Removes items from the results list without touching the files on disk.</summary>
		public void RemoveFromResults(IEnumerable<DuplicateItem> items) {
			lock (_operationSync) {
				if (_disposed || WorkActiveLocked) return;
				foreach (var item in items.ToList()) {
					_engine.Duplicates.Remove(item);
					_resultSnapshots.Remove(item);
				}
				DropSingletonGroups();
			}
			NotifyAutomationChanged();
		}

		/// <summary>Drops groups that have shrunk to a single item — a group of one is not a duplicate.</summary>
		void DropSingletonGroups() {
			var keep = _engine.Duplicates
				.GroupBy(d => d.GroupId)
				.Where(g => g.Count() > 1)
				.Select(g => g.Key)
				.ToHashSet();
			foreach (var d in _engine.Duplicates.ToList())
				if (!keep.Contains(d.GroupId)) {
					_engine.Duplicates.Remove(d);
					_resultSnapshots.Remove(d);
				}
		}

		// === Batch file operations (delete / move / link) ===

		/// <summary>True while a delete/move/link batch is running.</summary>
		public bool FileOpRunning { get; private set; }
		public string FileOpVerb { get; private set; } = string.Empty;
		public int FileOpCurrent { get; private set; }
		public int FileOpMax { get; private set; }

		bool TryBeginFileOp(string verb, int max, FileOpResult result) {
			if (max == 0) return false;
			lock (_operationSync) {
				if (_disposed || WorkActiveLocked) {
					result.Failed = max;
					result.Errors.Add("A scan or another file/database operation is running. Wait for it to finish.");
					return false;
				}
				FileOpRunning = true;
				FileOpVerb = verb;
				FileOpCurrent = 0;
				FileOpMax = max;
			}
			Notify();
			return true;
		}

		void EndFileOp() {
			lock (_operationSync) {
				FileOpRunning = false;
				FileOpVerb = string.Empty;
			}
			NotifyAutomationChanged();
		}

		bool TryBeginDataWork() {
			lock (_operationSync) {
				if (_disposed || WorkActiveLocked) return false;
				_dataWorkActive = true;
			}
			Notify();
			return true;
		}

		void EndDataWork(bool automationChanged = true) {
			lock (_operationSync) _dataWorkActive = false;
			if (automationChanged) NotifyAutomationChanged(); else Notify();
		}

		/// <summary>Deletes files from disk and removes them from results and the scan database.</summary>
		public Task<FileOpResult> DeleteItemsAsync(IEnumerable<DuplicateItem> items, bool permanent) =>
			RemoveOrReplaceItemsAsync(items, permanent, createLinks: false, hardLinks: false);

		/// <summary>Moves files to a destination folder and updates the scan database paths.</summary>
		public async Task<FileOpResult> MoveItemsAsync(IEnumerable<DuplicateItem> items, string destinationFolder) {
			var list = items.Distinct().ToList();
			var result = new FileOpResult();
			if (!TryBeginFileOp("Moving", list.Count, result)) return result;
			try {
				await Task.Run(async () => {
					if (_resultStateFolder is null)
						throw new IOException("These results have no verified file snapshots. Rescan before moving files.");
					if (!PathComparer.ForCurrentPlatform.Equals(Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()),
						Path.GetFullPath(_resultStateFolder)) && !await LoadDatabaseWithResultsAuthorityAsync(_resultStateFolder)) {
						ClearCleanupSnapshots();
						throw new IOException("The results database could not be loaded; files were retained.");
					}
					var sw = System.Diagnostics.Stopwatch.StartNew();
					foreach (var item in list) {
						try {
							if (!_engine.Duplicates.Contains(item))
								throw new IOException("This file is no longer part of the current results.");
							var identity = CaptureReviewedIdentity(item)
								?? throw new IOException("The file is unavailable or has changed since scanning. Rescan before moving it.");
							using (await AcquireMoveRetentionLeaseAsync())
								await ValidateMoveRetentionAsync(identity);
							Directory.CreateDirectory(destinationFolder);
							string dest = Path.Combine(destinationFolder, Path.GetFileName(item.Path));
							// Avoid overwriting existing files at destination
							int n = 1;
							string ext = Path.GetExtension(dest);
							string nameNoExt = Path.GetFileNameWithoutExtension(dest);
							while (File.Exists(dest))
								dest = Path.Combine(destinationFolder, $"{nameNoExt}_{n++}{ext}");
							if (!identity.MatchesCurrent(forceHash: true))
								throw new IOException("The file changed before moving; it was retained.");
							// Lookup requires the source path to exist; capture the entry before renaming.
							ScanEngine.GetFromDatabase(item.Path, out var dbEntry);
							var aliases = CaptureKeeperAliases(identity);
							using var reviewLease = await AcquireMoveRetentionLeaseAsync();
							await ValidateMoveRetentionAsync(identity);
							try { FileMover(item.Path, dest); }
							finally {
								// Renaming also changes native stamps on existing source aliases.
								RefreshKeeperAliasesAfterLink(aliases);
							}
							if (dbEntry is not null)
								ScanEngine.UpdateFilePathInDatabase(dest, dbEntry);
							_engine.Duplicates.Remove(item);
							_resultSnapshots.Remove(item);
							result.Done++;
						}
						catch (Exception ex) {
							result.Errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
							result.Failed++;
						}
						finally {
							FileOpCurrent++;
							if (sw.ElapsedMilliseconds >= 100) { sw.Restart(); Notify(); }
						}
					}
					if (result.Done > 0) {
						try { ScanEngine.SaveDatabase(); }
						catch (Exception ex) { result.Errors.Add($"Files were moved, but saving the scan database failed: {ex.Message}"); }
						DropSingletonGroups();
					}
				});
			}
			catch (Exception ex) {
				result.Failed = list.Count - result.Done;
				result.Errors.Add($"Move could not complete; remaining files were retained: {ex.Message}");
			}
			finally { await PersistFileOperationResultsAsync(result); EndFileOp(); }
			return result;
		}

		/// <summary>
		/// Replaces each selected file with a hardlink or symlink to the kept file of its
		/// group (the highest-similarity unselected member that still exists on disk).
		/// </summary>
		public Task<FileOpResult> CreateLinksAsync(IEnumerable<DuplicateItem> items, bool hardLinks) =>
			RemoveOrReplaceItemsAsync(items, permanent: true, createLinks: true, hardLinks);

		/// <summary>Removes database entries for files that no longer exist or have errors.</summary>
		public async Task<int> CleanDatabaseAsync() {
			if (!TryBeginDataWork())
				throw new InvalidOperationException("A scan or another file/database operation is running. Wait for it to finish.");
			try {
				if (!await LoadDatabaseWithResultsAuthorityAsync(Settings.CustomDatabaseFolder)) {
					throw new IOException("The configured database could not be loaded; cleanup was not performed.");
				}
				int before = DatabaseEntryCount;
				await Task.Run(() => DatabaseUtils.CleanupDatabase(preserveDeletedContentMemory: Settings.RememberDeletedContent));
				return before - DatabaseEntryCount;
			}
			finally { EndDataWork(); }
		}

		/// <summary>Wipes all entries from the scan database.</summary>
		public async Task ClearDatabaseAsync() {
			if (!TryBeginDataWork())
				throw new InvalidOperationException("A scan or another file/database operation is running. Wait for it to finish.");
			try {
				if (!await LoadDatabaseWithResultsAuthorityAsync(Settings.CustomDatabaseFolder)) {
					throw new IOException("The configured database could not be loaded; it was not cleared.");
				}
				string folder = DatabaseUtils.GetDatabaseFolderPath();
				// Commit the matching empty result generation before clearing the
				// database; a restart must not revive this folder's old rows.
				if (File.Exists(AutomationStore.Path) || File.Exists(Path.Combine(folder, "WebResults.json")))
					await SaveResultsAsync(folder, new());
				ResultsPersistenceWarning = null;
				ScanEngine.ClearDatabase();
				_engine.Duplicates.Clear();
				ClearCleanupSnapshots();
				ClearSelectionPlan();
				_quarantinePreview = null;
			}
			finally { EndDataWork(); }
		}

		/// <summary>Number of file entries currently stored in the scan database.</summary>
		public int DatabaseEntryCount => VDF.Core.Utils.DatabaseUtils.Database.Count;

		/// <summary>
		/// Runs the single-pair detection diagnostic with the current settings and
		/// returns the step-by-step report. See <see cref="ScanEngine.TestFilePairAsync"/>.
		/// </summary>
		public async Task<string> TestFilePairAsync(string fileA, string fileB) {
			if (!TryBeginDataWork())
				return "A scan or another operation is running. Wait for it to finish before running the file pair test.";
			try {
				_engine.Settings = SnapshotConfiguredSettings();
				return await _engine.TestFilePairAsync(fileA, fileB);
			}
			finally { EndDataWork(); }
		}

		void Notify() => StateChanged?.Invoke();

		public void Dispose() {
			lock (_operationSync) {
				if (_disposed) return;
				_disposed = true;
				_cts.Cancel();
				if (!_scanStarting) _cts.Dispose();
			}
			_engine.Stop();
		}
	}
}
