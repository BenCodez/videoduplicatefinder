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

using System.CommandLine;
using System.Text.Json;
using VDF.CLI.Actions;
using VDF.Core.ViewModels;

namespace VDF.CLI.Commands {
	internal static class MarkCommand {
		internal static Command Build() {
			var cmd = new Command("mark",
				"Read a JSON results file and mark files for deletion based on a strategy.\n\n" +
				"WARNING: Automatic deletion is not recommended. Always use --dry-run first to review what would be deleted.");

			var inputOpt = new Option<FileInfo>("--input", "-i") {
				Description = "Path to a JSON results file produced by scan-and-compare --format json.",
				Required = true
			};
			var strategyOpt = new Option<Strategy>("--strategy") {
				Description = $"Selection strategy: {string.Join(", ", StrategyParser.Names)}.",
				Required = true,
				CustomParser = StrategyParser.Parse
			};
			var dryRunOpt = new Option<bool>("--dry-run") {
				Description = "Print which files would be deleted without deleting anything (default).",
				DefaultValueFactory = _ => true
			};
			var deleteOpt = new Option<bool>("--delete") {
				Description = "Move files to the system recycle bin / trash."
			};
			var deletePermanentOpt = new Option<bool>("--delete-permanent") {
				Description = "Permanently delete files. WARNING: irreversible."
			};
			var stateOpt = new Option<string?>("--state", "--db") {
				Description = "Explicit shared database/state folder containing protection settings and reviews. Required for deletion."
			};

			cmd.Options.Add(inputOpt);
			cmd.Options.Add(strategyOpt);
			cmd.Options.Add(dryRunOpt);
			cmd.Options.Add(deleteOpt);
			cmd.Options.Add(deletePermanentOpt);
			cmd.Options.Add(stateOpt);

			cmd.SetAction(async (parseResult, ct) => {
				var inputFile = parseResult.GetValue(inputOpt)!;
				if (!inputFile.Exists) {
					Console.Error.WriteLine($"Error: input file not found: {inputFile.FullName}");
					return 1;
				}

				List<DuplicateItem>? duplicates;
				try {
					await using var stream = inputFile.OpenRead();
					// The JSON is an array of groups, each with an Items array
					var groups = await JsonSerializer.DeserializeAsync(stream, Output.CliJsonContext.Default.ListDuplicateGroup, ct);
					duplicates = groups?.SelectMany(g => g.Items).ToList();
				}
				catch (Exception ex) {
					Console.Error.WriteLine($"Error reading results file: {ex.Message}");
					return 1;
				}

				if (duplicates == null || duplicates.Count == 0) {
					// A valid but empty results file is a no-op, not a failure.
					Console.Error.WriteLine("No duplicates found in the results file.");
					return 0;
				}

				var strategy = parseResult.GetValue(strategyOpt);
				var marked = DeletionStrategy.SelectForDeletion(duplicates, strategy);

				bool doPermanent = parseResult.GetValue(deletePermanentOpt);
				bool doDelete = parseResult.GetValue(deleteOpt) || doPermanent;
				bool dryRun = !doDelete || parseResult.GetValue(dryRunOpt);

				int failed = await ExecuteDeletion(marked, dryRun, doPermanent, parseResult.GetValue(stateOpt), duplicates, ct);
				return failed > 0 ? 1 : 0;
			});

			return cmd;
		}

		/// <summary>Returns the number of files that failed to delete (0 in dry-run).</summary>
		internal static async Task<int> ExecuteDeletion(IReadOnlyList<DuplicateItem> marked, bool dryRun, bool permanent,
			string? stateFolder = null, IReadOnlyList<DuplicateItem>? allItems = null, CancellationToken ct = default) {
			if (marked.Count == 0) {
				Console.Error.WriteLine("No files selected for deletion by the chosen strategy.");
				return 0;
			}
			if (string.IsNullOrWhiteSpace(stateFolder)) {
				if (!dryRun) {
					Console.Error.WriteLine("Cleanup refused: --state (or --db) is required. Exported result paths do not identify protection state.");
					return marked.Count;
				}
				Console.Error.WriteLine("Dry-run has no state context; protection and keeper checks are unverified. Supply --state to preview guarded cleanup.");
				foreach (var item in marked) Console.WriteLine($"[dry-run] would delete: {item.Path}");
				return 0;
			}
			if (allItems is null) {
				Console.Error.WriteLine("Cleanup refused: complete duplicate groups are required to validate remaining keepers.");
				return marked.Count;
			}
			try { await VDF.Core.Cleanup.CleanupProtectionPolicyStore.LoadAsync(stateFolder, ct); }
			catch (Exception ex) when (ex is not OperationCanceledException) {
				Console.Error.WriteLine($"Cleanup refused: {ex.Message}");
				return marked.Count;
			}
			if (!dryRun) {
				Console.Error.WriteLine($"WARNING: {marked.Count} candidate(s) will be {(permanent ? "permanently deleted" : "moved to trash")} only after live protection and keeper checks.");
				Console.Error.WriteLine("Proceeding in 3 seconds... (Ctrl+C to abort)");
				await Task.Delay(3000, ct);
			}
			int failed = await GuardedDeletion.ExecuteAsync(marked, allItems, stateFolder, permanent, dryRun, ct);
			Console.Error.WriteLine(dryRun ? $"[dry-run] {marked.Count - failed} verified removal candidate(s), {failed} refused." :
				$"Done. {marked.Count - failed} deleted, {failed} refused or failed.");
			return failed;
		}

	}
}
