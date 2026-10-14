// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using VDF.Core.Review;
using VDF.Core.Utils;

namespace VDF.Core.Nfo;

/// <summary>
/// Reads bounded, local NFO files without changing them or resolving external
/// resources. Content hashes validate the XML cache on every inspection.
/// </summary>
public sealed class NfoFavouriteService {
	public const int MaximumNfoBytes = 1024 * 1024;
	public const int MaximumParentLevels = 8;
	const int MaximumXmlDepth = 64;
	const int MaximumDirectoryEntries = 100_000;
	const int MaximumSources = 32;
	const int MaximumCachedSidecars = 32_768;
	readonly object _cacheGate = new();
	readonly BoundedCache<DirectoryCacheEntry> _directories = new(2048, PathComparer.ForCurrentPlatform, MaximumCachedSidecars);
	readonly BoundedCache<ParseCacheEntry> _parsed = new(4096, PathComparer.ForCurrentPlatform);
	long _parseCount;
	long _directoryEnumerationCount;

	// Used to check the explicitly required cache/large-directory behavior in tests.
	internal long ParseCount => Interlocked.Read(ref _parseCount);
	internal long DirectoryEnumerationCount => Interlocked.Read(ref _directoryEnumerationCount);
	internal int CachedDirectoryCount { get { lock (_cacheGate) return _directories.Count; } }
	internal int CachedSidecarCount { get { lock (_cacheGate) return _directories.Cost; } }

	/// <summary>
	/// Clears directory association snapshots and parsed XML. Call at the beginning
	/// of a rescan on mounts that do not update directory timestamps reliably.
	/// </summary>
	public void ClearCache() {
		lock (_cacheGate) {
			_directories.Clear();
			_parsed.Clear();
		}
	}
	internal void RefreshDirectoryCache() {
		lock (_cacheGate) _directories.Clear();
	}

	public NfoResult Inspect(string videoPath, NfoOptions options) => InspectCore(videoPath, options, null);

	internal NfoResult InspectClaimedVideo(string videoPath, string claimPath, NfoOptions options) =>
		InspectCore(videoPath, options, claimPath);

	NfoResult InspectCore(string videoPath, NfoOptions options, string? claimPath) {
		ArgumentNullException.ThrowIfNull(options);
		if (!options.Enabled)
			return new(false, false, "NFO favourite protection is disabled.", Array.Empty<string>());

		string fullVideoPath;
		string directory;
		try {
			if (string.IsNullOrWhiteSpace(videoPath))
				return new(false, true, "The video path is missing.", Array.Empty<string>());
			fullVideoPath = Path.GetFullPath(videoPath);
			directory = Path.GetDirectoryName(fullVideoPath) ?? throw new ArgumentException("Missing directory.");
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return new(false, true, "The video path is invalid.", Array.Empty<string>());
		}

		var markers = MarkerOptions.From(options);
		var sources = new List<string>();
		var inspected = new HashSet<string>(PathComparer.ForCurrentPlatform);
		var diagnostics = new List<string>();
		bool favourite = false;
		bool uncertain = false;
		bool sourceLimitReported = false;

		void InspectSource(string path) {
			if (!inspected.Add(path)) return;
			if (sources.Count >= MaximumSources) {
				uncertain = true;
				if (!sourceLimitReported) diagnostics.Add("Too many applicable NFO files; inspection is uncertain.");
				sourceLimitReported = true;
				return;
			}
			sources.Add(path);
			ParseResult result = ReadNfo(path, markers);
			favourite |= result.IsFavourite;
			uncertain |= result.IsUncertain;
			diagnostics.Add($"{Path.GetFileName(path)}: {result.Reason}");
		}

		DirectorySnapshot local = GetDirectory(directory);
		if (local.IsUncertain) {
			uncertain = true;
			diagnostics.Add(local.Reason);
		}
		else {
			int videoCount = local.VideoCount;
			string? soleVideo = local.SoleVideo;
			if (claimPath is not null) {
				try {
					SafePaths.RequireRegularFile(claimPath);
					if (!PathComparer.ForCurrentPlatform.Equals(Path.GetDirectoryName(claimPath), directory) || SafePaths.Exists(fullVideoPath))
						throw new IOException("The claimed video association is unavailable.");
					if (FileUtils.IsVideoExtension(Path.GetExtension(fullVideoPath))) {
						videoCount = Math.Min(2, videoCount + 1);
						soleVideo = videoCount == 1 ? fullVideoPath : null;
					}
				}
				catch (Exception ex) when (IsFileError(ex)) {
					uncertain = true;
					diagnostics.Add("The original video path changed during its private claim; favourite status is uncertain.");
				}
			}
			string baseName = Path.GetFileNameWithoutExtension(fullVideoPath);
			if (local.ByBaseName.TryGetValue(baseName, out string[]? sidecars))
				foreach (string path in sidecars) InspectSource(path);

			foreach (string path in NamedNfos(local, "movie")) {
				if (inspected.Contains(path)) continue;
				if (videoCount == 1 && PathComparer.ForCurrentPlatform.Equals(soleVideo, fullVideoPath))
					InspectSource(path);
				else
					diagnostics.Add("movie.nfo: ignored because it is not associated with one unambiguous video.");
			}
		}

		if (options.InheritSeries || options.InheritSeason) {
			bool findSeries = options.InheritSeries;
			bool findSeason = options.InheritSeason;
			string? ancestor = directory;
			for (int level = 0; ancestor != null && level <= MaximumParentLevels && (findSeries || findSeason); level++) {
				DirectorySnapshot snapshot = level == 0 ? local : GetDirectory(ancestor);
				if (snapshot.IsUncertain) {
					uncertain = true;
					if (level != 0) diagnostics.Add(snapshot.Reason);
					// Do not jump over an unreadable nearer series or season boundary.
					break;
				}
				string[] series = NamedNfos(snapshot, "tvshow").ToArray();
				if (findSeason) {
					string[] seasons = NamedNfos(snapshot, "season").ToArray();
					foreach (string path in seasons) InspectSource(path);
					if (seasons.Length > 0 || series.Length > 0) findSeason = false;
				}
				if (findSeries && series.Length > 0) {
					foreach (string path in series) InspectSource(path);
					findSeries = false;
				}
				ancestor = Path.GetDirectoryName(ancestor);
			}
		}

		if (sources.Count == 0)
			diagnostics.Add("No applicable NFO file found for this video.");
		return new(favourite, uncertain, string.Join(" ", diagnostics), sources.AsReadOnly());
	}

	static string[] NamedNfos(DirectorySnapshot directory, string name) => name switch {
		"movie" => directory.MovieNfos,
		"tvshow" => directory.SeriesNfos,
		"season" => directory.SeasonNfos,
		_ => []
	};

	DirectorySnapshot GetDirectory(string path) {
		DateTime stamp;
		try {
			SafePaths.RequireDirectory(path);
			stamp = Directory.GetLastWriteTimeUtc(path);
		}
		catch (Exception ex) when (IsFileError(ex)) {
			return DirectorySnapshot.Uncertain("The NFO directory could not be read; favourite status is uncertain.");
		}

		DirectoryCacheEntry entry;
		lock (_cacheGate) {
			if (!_directories.TryGet(path, out entry!) || entry.Stamp != stamp) {
				entry = new(stamp, new Lazy<DirectorySnapshot>(() => EnumerateDirectory(path, stamp),
					LazyThreadSafetyMode.ExecutionAndPublication));
				_directories.Set(path, entry);
			}
		}
		DirectorySnapshot snapshot = entry.Snapshot.Value;
		lock (_cacheGate) {
			if (_directories.TryGet(path, out DirectoryCacheEntry? current) && ReferenceEquals(current, entry))
				_directories.Set(path, entry, Math.Max(snapshot.SidecarCount, 1));
		}
		return snapshot;
	}

	DirectorySnapshot EnumerateDirectory(string path, DateTime initialStamp) {
		Interlocked.Increment(ref _directoryEnumerationCount);
		var nfos = new List<string>();
		int videoCount = 0;
		int entryCount = 0;
		string? soleVideo = null;
		try {
			var enumerationOptions = new EnumerationOptions {
				AttributesToSkip = 0,
				IgnoreInaccessible = false,
				RecurseSubdirectories = false
			};
			foreach (FileSystemInfo file in new DirectoryInfo(path).EnumerateFileSystemInfos("*", enumerationOptions)) {
				if (++entryCount > MaximumDirectoryEntries)
					return DirectorySnapshot.Uncertain("The NFO directory exceeds the inspection limit; favourite status is uncertain.");
				if ((file.Attributes & FileAttributes.Directory) != 0) continue;
				string extension = Path.GetExtension(file.Name);
				if (extension.Equals(".nfo", StringComparison.OrdinalIgnoreCase) || FileUtils.IsVideoExtension(extension))
					SafePaths.RequireRegularFile(file.FullName);
				if (extension.Equals(".nfo", StringComparison.OrdinalIgnoreCase)) nfos.Add(file.FullName);
				if (FileUtils.IsVideoExtension(extension)) {
					videoCount = Math.Min(2, videoCount + 1);
					soleVideo = videoCount == 1 ? file.FullName : null;
				}
			}
			SafePaths.RequireDirectory(path);
			if (Directory.GetLastWriteTimeUtc(path) != initialStamp)
				return DirectorySnapshot.Uncertain("The NFO directory changed during inspection; favourite status is uncertain.");
			nfos.Sort(StringComparer.Ordinal);
			string[] Named(string name) => nfos.Where(file =>
				Path.GetFileNameWithoutExtension(file).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
			var byBaseName = nfos.GroupBy(Path.GetFileNameWithoutExtension, PathComparer.ForCurrentPlatform)
				.ToDictionary(group => group.Key!, group => group.ToArray(), PathComparer.ForCurrentPlatform);
			return new(byBaseName, Named("movie"), Named("tvshow"), Named("season"), nfos.Count,
				videoCount, soleVideo, false, string.Empty);
		}
		catch (Exception ex) when (IsFileError(ex)) {
			return DirectorySnapshot.Uncertain("The NFO directory could not be read; favourite status is uncertain.");
		}
	}

	ParseResult ReadNfo(string path, MarkerOptions options) {
		byte[] bytes;
		try {
			using var file = NfoFileReader.Open(path);
			FileStream stream = file.Stream;
			// An extra byte detects growth beyond the limit without an unbounded read.
			if (stream.Length > MaximumNfoBytes)
				return new(false, true, "NFO exceeds the size limit; favourite status is uncertain.");
			using var buffer = new MemoryStream();
			byte[] chunk = new byte[8192];
			int count;
			while ((count = stream.Read(chunk, 0, Math.Min(chunk.Length, MaximumNfoBytes + 1 - (int)buffer.Length))) > 0) {
				buffer.Write(chunk, 0, count);
				if (buffer.Length > MaximumNfoBytes)
					return new(false, true, "NFO exceeds the size limit; favourite status is uncertain.");
			}
			bytes = buffer.ToArray();
			file.RequireUnchanged();
		}
		catch (Exception ex) when (IsFileError(ex)) {
			return new(false, true, "NFO could not be read; favourite status is uncertain.");
		}

		byte[] hash = SHA256.HashData(bytes);
		string key = options.Signature + ":" + path;
		// Serialize parse-cache misses so parallel scans reuse the same parse, too.
		lock (_cacheGate) {
			if (_parsed.TryGet(key, out ParseCacheEntry? existing) && existing.Hash.AsSpan().SequenceEqual(hash))
				return existing.Result;
			Interlocked.Increment(ref _parseCount);
			ParseResult result = Parse(bytes, options);
			_parsed.Set(key, new(hash, result));
			return result;
		}
	}

	static ParseResult Parse(byte[] bytes, MarkerOptions options) {
		bool favourite = false;
		bool sawMarker = false;
		bool sawNegative = false;
		var active = new List<ActiveMarker>();
		try {
			using var stream = new MemoryStream(bytes, writable: false);
			using var reader = XmlReader.Create(stream, new XmlReaderSettings {
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = MaximumNfoBytes,
				MaxCharactersFromEntities = 1024,
				CheckCharacters = true,
				IgnoreComments = true,
				IgnoreProcessingInstructions = true
			});
			while (reader.Read()) {
				if (reader.Depth > MaximumXmlDepth)
					return new(favourite, true, "XML exceeds the nesting limit; favourite status is uncertain.");
				if (reader.NodeType == XmlNodeType.Element) {
					foreach (ActiveMarker marker in active) marker.HasChild = true;
					bool boolean = options.BooleanElements.Contains(reader.LocalName);
					bool tag = options.TagElements.Contains(reader.LocalName);
					if (!boolean && !tag) continue;
					sawMarker = true;
					if (!reader.IsEmptyElement) active.Add(new(reader.Depth, boolean, tag));
				}
				else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace) {
					foreach (ActiveMarker marker in active)
						if (!marker.HasChild) marker.Value.Append(reader.Value);
				}
				else if (reader.NodeType == XmlNodeType.EndElement && active.Count > 0 && active[^1].Depth == reader.Depth) {
					ActiveMarker marker = active[^1];
					active.RemoveAt(active.Count - 1);
					if (marker.HasChild) continue;
					string value = marker.Value.ToString().Trim();
					if (marker.Boolean) {
						favourite |= value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
						sawNegative |= value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0";
					}
					if (marker.Tag) favourite |= options.TagValues.Contains(value);
				}
			}
		}
		catch (Exception ex) when (ex is XmlException or ArgumentException or NotSupportedException) {
			return new(favourite, true, "Malformed or unsupported XML; favourite status is uncertain.");
		}
		return new(favourite, false, favourite ? "explicit favourite marker detected." :
			sawNegative ? "explicit negative marker; no positive favourite marker." :
			sawMarker ? "no positive favourite marker in the configured elements." : "no recognized favourite marker.");
	}

	static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or
		System.Security.SecurityException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException;

	sealed record DirectorySnapshot(IReadOnlyDictionary<string, string[]> ByBaseName, string[] MovieNfos,
		string[] SeriesNfos, string[] SeasonNfos, int SidecarCount, int VideoCount, string? SoleVideo,
		bool IsUncertain, string Reason) {
		public static DirectorySnapshot Uncertain(string reason) => new(new Dictionary<string, string[]>(), [], [], [], 0, 0, null, true, reason);
	}
	sealed record DirectoryCacheEntry(DateTime Stamp, Lazy<DirectorySnapshot> Snapshot);
	sealed record ParseResult(bool IsFavourite, bool IsUncertain, string Reason);
	sealed record ParseCacheEntry(byte[] Hash, ParseResult Result);
	sealed record MarkerOptions(HashSet<string> BooleanElements, HashSet<string> TagElements, HashSet<string> TagValues, string Signature) {
		public static MarkerOptions From(NfoOptions options) {
			static HashSet<string> Normalize(IEnumerable<string>? values) => new(
				(values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()), StringComparer.OrdinalIgnoreCase);
			HashSet<string> boolean = Normalize(options.BooleanElements);
			HashSet<string> elements = Normalize(options.TagElements);
			HashSet<string> values = Normalize(options.TagValues);
			var signature = new StringBuilder();
			foreach (HashSet<string> group in new[] { boolean, elements, values }) {
				signature.Append('[');
				foreach (string value in group.Select(value => value.ToUpperInvariant()).OrderBy(value => value, StringComparer.Ordinal))
					signature.Append(value.Length).Append(':').Append(value);
				signature.Append(']');
			}
			return new(boolean, elements, values, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()))));
		}
	}
	sealed class ActiveMarker(int depth, bool boolean, bool tag) {
		public int Depth { get; } = depth;
		public bool Boolean { get; } = boolean;
		public bool Tag { get; } = tag;
		public StringBuilder Value { get; } = new();
		public bool HasChild { get; set; }
	}

	// Small LRU caches keep a long-lived GUI session bounded. All access is under _cacheGate.
	sealed class BoundedCache<T>(int capacity, StringComparer comparer, int maximumCost = int.MaxValue) {
		readonly Dictionary<string, (T Value, LinkedListNode<string> Node, int Cost)> _entries = new(comparer);
		readonly LinkedList<string> _order = new();
		int _cost;
		public int Count => _entries.Count;
		public int Cost => _cost;
		public bool TryGet(string key, out T value) {
			if (!_entries.TryGetValue(key, out var entry)) {
				value = default!;
				return false;
			}
			_order.Remove(entry.Node);
			_order.AddLast(entry.Node);
			value = entry.Value;
			return true;
		}
		public void Set(string key, T value, int cost = 1) {
			if (_entries.TryGetValue(key, out var existing)) {
				_order.Remove(existing.Node);
				_cost -= existing.Cost;
				_entries.Remove(key);
			}
			// An oversized snapshot can be inspected, but must not be retained or
			// evict useful smaller snapshots while trying to fit the budget.
			if (cost > maximumCost) return;
			_entries[key] = (value, _order.AddLast(key), cost);
			_cost += cost;
			while (_entries.Count > capacity || _cost > maximumCost) {
				string oldest = _order.First!.Value;
				_cost -= _entries[oldest].Cost;
				_order.RemoveFirst();
				_entries.Remove(oldest);
			}
		}
		public void Clear() {
			_entries.Clear();
			_order.Clear();
			_cost = 0;
		}
	}
}
