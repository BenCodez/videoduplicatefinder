// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;

namespace VDF.Core.Cleanup;

/// <summary>The shared protection settings used by every destructive frontend.</summary>
public sealed class CleanupProtectionPolicy {
	public NfoOptions NfoOptions { get; set; } = new();
	public List<FolderPriorityRule> FolderRules { get; set; } = new();
	public FolderOverlapPolicy OverlapPolicy { get; set; }
}

/// <summary>Durable, versioned projection beside the scan database and reviews.</summary>
public static class CleanupProtectionPolicyStore {
	public const string FileName = "CleanupProtectionPolicy.json";
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SaveGates = new(VDF.Core.Utils.PathComparer.ForCurrentPlatform);

	// Final mutation shares the exact persistent leases used by policy, settings
	// and review writers. Busy writers cause refusal rather than stale permission.
	internal static async Task<IDisposable> AcquireMutationLeaseAsync(string stateFolder, CancellationToken ct) {
		var leases = new List<IDisposable>();
		try {
			string path = PolicyPath(stateFolder);
			leases.Add(JournalWriteLease.Acquire(path));
			var envelope = await DurableJsonFile.ReadPrimaryAsync(path,
				ProtectionPolicyJsonContext.Default.ProtectionPolicyEnvelope, Validate, ct).ConfigureAwait(false)
				?? throw new InvalidDataException("Shared cleanup protection settings are missing.");
			if (envelope.SourceSettings is { } source) {
				leases.Add(JournalWriteLease.Acquire(source.Path));
				if (!source.HasSameBytes(await Task.Run(() => SettingsIdentity(source.Path, ct), ct).ConfigureAwait(false)))
					throw new InvalidDataException("Saved GUI protections changed; refresh their projection before cleanup.");
			}
			leases.Add(JournalWriteLease.Acquire(Path.Combine(Path.GetDirectoryName(path)!, "ReviewedFiles.json")));
			ct.ThrowIfCancellationRequested();
			return new MutationLease(leases);
		}
		catch {
			for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
			throw;
		}
	}

	sealed class MutationLease(List<IDisposable> leases) : IDisposable {
		public void Dispose() {
			for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
			leases.Clear();
		}
	}

	/// <summary>Checks established protection state before an autosave can recreate missing settings.</summary>
	public static async Task ValidateSettingsWriteAsync(string stateFolder, CancellationToken ct = default) {
		string path = PolicyPath(stateFolder);
		var current = await DurableJsonFile.ReadPrimaryAsync(path,
			ProtectionPolicyJsonContext.Default.ProtectionPolicyEnvelope, Validate, ct).ConfigureAwait(false);
		if (current is null && SafePaths.Exists(path + ".bak"))
			throw new InvalidDataException("The current cleanup protection policy is missing; restore it before saving GUI settings.");
		if (current?.SourceSettings is { } source) {
			if (!SafePaths.Exists(source.Path))
				throw new InvalidDataException("Established GUI settings are missing; restore them before saving startup defaults.");
			SafePaths.RequireRegularFile(source.Path);
		}
	}

	public static async Task SaveAsync(string stateFolder, CleanupProtectionPolicy policy,
		FileIdentity? sourceSettingsIdentity = null, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(policy);
		string path = PolicyPath(stateFolder);
		var gate = SaveGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			using var lease = JournalWriteLease.Acquire(path);
			await SaveCoreAsync(path, policy, sourceSettingsIdentity, ct).ConfigureAwait(false);
		}
		finally { gate.Release(); }
	}

	static async Task SaveCoreAsync(string path, CleanupProtectionPolicy policy, FileIdentity? sourceSettingsIdentity, CancellationToken ct) {
		var envelope = ProtectionPolicyEnvelope.From(policy);
		var current = await DurableJsonFile.ReadPrimaryAsync(path,
			ProtectionPolicyJsonContext.Default.ProtectionPolicyEnvelope, Validate, ct).ConfigureAwait(false);
		if (current is null && SafePaths.Exists(path + ".bak"))
			throw new InvalidDataException("The current cleanup protection policy is missing; restore it before exporting cleanup protection settings.");
		if (sourceSettingsIdentity is null) {
			if (current?.SourceSettings is not null)
				throw new InvalidDataException("An established protection policy references saved GUI settings. Restore those settings before exporting startup defaults.");
		}
		else {
			if (!sourceSettingsIdentity.IsWellFormed()) throw new InvalidDataException("The source settings identity is invalid.");
			var actual = await Task.Run(() => SettingsIdentity(sourceSettingsIdentity.Path, ct), ct).ConfigureAwait(false);
			if (!sourceSettingsIdentity.HasSameBytes(actual))
				throw new InvalidDataException("GUI settings changed while their protection projection was prepared. Reload or refresh them before cleanup.");
			// Retain the identity of the exact bytes that produced this policy. A later
			// settings edit must invalidate it, never lend its new hash to old protections.
			envelope.SourceSettings = sourceSettingsIdentity;
		}
		string reviewsPath = Path.Combine(Path.GetDirectoryName(path)!, "ReviewedFiles.json");
		if (!SafePaths.Exists(reviewsPath) && !SafePaths.Exists(reviewsPath + ".bak")) {
			if (SafePaths.Exists(path) || SafePaths.Exists(path + ".bak"))
				throw new InvalidDataException("An initialized state's review journal is missing; restore it before exporting cleanup protection settings.");
			await new ReviewStore(reviewsPath).InitializeAsync(ct).ConfigureAwait(false);
		}
		await DurableJsonFile.WriteAsync(path, envelope, ProtectionPolicyJsonContext.Default.ProtectionPolicyEnvelope,
			Validate, ct).ConfigureAwait(false);
	}

	public static async Task<CleanupProtectionPolicy> LoadAsync(string stateFolder, CancellationToken ct = default) {
		var envelope = await DurableJsonFile.ReadPrimaryAsync(PolicyPath(stateFolder),
			ProtectionPolicyJsonContext.Default.ProtectionPolicyEnvelope, Validate, ct).ConfigureAwait(false)
			?? throw new InvalidDataException("Shared cleanup protection settings are missing. Initialize this state folder in the GUI before cleanup.");
		if (envelope.SourceSettings is not null) {
			FileIdentity current = await Task.Run(() => SettingsIdentity(envelope.SourceSettings.Path, ct), ct).ConfigureAwait(false);
			if (!envelope.SourceSettings.HasSameBytes(current))
				throw new InvalidDataException("GUI settings changed after the protection projection. Save or refresh them in the GUI before cleanup.");
		}
		return envelope.ToPolicy();
	}

	static string PolicyPath(string stateFolder) {
		string directory = SafePaths.Normalize(stateFolder, rejectTraversal: true);
		SafePaths.RequireDirectory(directory);
		return Path.Combine(directory, FileName);
	}

	static FileIdentity SettingsIdentity(string path, CancellationToken ct) {
		string full = SafePaths.Normalize(path, rejectTraversal: true);
		SafePaths.RequireRegularFile(full);
		if (new FileInfo(full).Length > 32L * 1024 * 1024)
			throw new InvalidDataException("The settings file exceeds the supported size limit.");
		return IdentityReader.Capture(full, true, ct)
			?? throw new IOException("The settings file is unavailable or changed during projection.");
	}

	static bool Validate(ProtectionPolicyEnvelope envelope) {
		if (envelope.Version != 1) throw new NotSupportedException("The shared cleanup protection schema is unsupported; it was preserved.");
		static bool Markers(List<string>? values) => values is not null && values.Count <= 128 &&
			values.All(value => value is not null && value.Length <= 256);
		return envelope.Nfo is not null && Markers(envelope.Nfo.BooleanElements) &&
			Markers(envelope.Nfo.TagElements) && Markers(envelope.Nfo.TagValues) &&
			Enum.IsDefined(envelope.OverlapPolicy) && envelope.FolderRules is not null && envelope.FolderRules.Count <= 10_000 &&
			envelope.FolderRules.All(rule => rule is not null && rule.Directory is not null && rule.Pattern is not null &&
				rule.Directory.Length <= 32_768 && rule.Pattern.Length <= 4096) &&
			(envelope.SourceSettings is null || envelope.SourceSettings.IsWellFormed());
	}
}

// Required storage fields prevent partial, syntactically valid JSON from silently
// turning off an existing marker or protected folder. Public UI models retain
// their backwards-compatible defaults; only the new projection schema is strict.
internal sealed class ProtectionPolicyEnvelope {
	[JsonRequired] public int Version { get; set; } = 1;
	[JsonRequired] public NfoProtectionProjection Nfo { get; set; } = new();
	[JsonRequired] public List<FolderProtectionProjection> FolderRules { get; set; } = new();
	[JsonRequired] public FolderOverlapPolicy OverlapPolicy { get; set; }
	[JsonRequired] public FileIdentity? SourceSettings { get; set; }
	public static ProtectionPolicyEnvelope From(CleanupProtectionPolicy policy) => new() {
		Nfo = NfoProtectionProjection.From(policy.NfoOptions ?? throw new InvalidDataException("NFO protection settings are missing.")),
		FolderRules = (policy.FolderRules ?? throw new InvalidDataException("Folder protection settings are missing.")).Select(FolderProtectionProjection.From).ToList(),
		OverlapPolicy = policy.OverlapPolicy
	};
	public CleanupProtectionPolicy ToPolicy() => new() {
		NfoOptions = Nfo.ToOptions(), FolderRules = FolderRules.Select(rule => rule.ToRule()).ToList(), OverlapPolicy = OverlapPolicy
	};
}

internal sealed class NfoProtectionProjection {
	[JsonRequired] public bool Enabled { get; set; }
	[JsonRequired] public List<string> BooleanElements { get; set; } = new();
	[JsonRequired] public List<string> TagValues { get; set; } = new();
	[JsonRequired] public List<string> TagElements { get; set; } = new();
	[JsonRequired] public bool InheritSeries { get; set; }
	[JsonRequired] public bool InheritSeason { get; set; }
	public static NfoProtectionProjection From(NfoOptions options) => new() {
		Enabled = options.Enabled, BooleanElements = options.BooleanElements?.ToList()!, TagValues = options.TagValues?.ToList()!,
		TagElements = options.TagElements?.ToList()!, InheritSeries = options.InheritSeries, InheritSeason = options.InheritSeason
	};
	public NfoOptions ToOptions() => new() {
		Enabled = Enabled, BooleanElements = BooleanElements.ToList(), TagValues = TagValues.ToList(), TagElements = TagElements.ToList(),
		InheritSeries = InheritSeries, InheritSeason = InheritSeason
	};
}

internal sealed class FolderProtectionProjection {
	[JsonRequired] public string Directory { get; set; } = string.Empty;
	[JsonRequired] public int Priority { get; set; }
	[JsonRequired] public bool Enabled { get; set; }
	[JsonRequired] public bool Recursive { get; set; }
	[JsonRequired] public bool ProtectAll { get; set; }
	[JsonRequired] public string Pattern { get; set; } = string.Empty;
	public static FolderProtectionProjection From(FolderPriorityRule rule) {
		ArgumentNullException.ThrowIfNull(rule);
		return new() { Directory = rule.Directory, Priority = rule.Priority, Enabled = rule.Enabled,
			Recursive = rule.Recursive, ProtectAll = rule.ProtectAll, Pattern = rule.Pattern };
	}
	public FolderPriorityRule ToRule() => new() { Directory = Directory, Priority = Priority, Enabled = Enabled,
		Recursive = Recursive, ProtectAll = ProtectAll, Pattern = Pattern };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProtectionPolicyEnvelope))]
internal partial class ProtectionPolicyJsonContext : JsonSerializerContext { }
