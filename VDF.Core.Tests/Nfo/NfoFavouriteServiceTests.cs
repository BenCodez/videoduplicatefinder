// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using VDF.Core.Nfo;

namespace VDF.Core.Tests.Nfo;

public sealed class NfoFavouriteServiceTests : IDisposable {
	readonly string _directory = Path.Combine(Path.GetTempPath(), "VDF.Nfo." + Guid.NewGuid().ToString("N"));
	readonly NfoFavouriteService _service = new();

	public NfoFavouriteServiceTests() => Directory.CreateDirectory(_directory);
	public void Dispose() {
		try { Directory.Delete(_directory, recursive: true); } catch { /* Best-effort fixture cleanup. */ }
	}
	static NfoOptions Enabled() => new() { Enabled = true };
	string Write(string relativePath, string contents = "") {
		string path = Path.Combine(_directory, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents, new UTF8Encoding(false));
		return path;
	}
	NfoResult InspectXml(string xml, NfoOptions? options = null) {
		string video = Write("clip.mkv");
		Write("clip.nfo", xml);
		return _service.Inspect(video, options ?? Enabled());
	}

	[Fact]
	public void DefaultOptions_AreDisabledAndExplicit() {
		var options = new NfoOptions();
		Assert.False(options.Enabled);
		Assert.False(options.InheritSeries);
		Assert.False(options.InheritSeason);
		Assert.Equal(new[] { "favorite", "isfavorite" }, options.BooleanElements);
		Assert.Equal(new[] { "Favorite", "Favourite" }, options.TagValues);
		Assert.Equal(new[] { "tag" }, options.TagElements);
	}

	[Fact]
	public void Disabled_DoesNotReadMalformedSidecar() {
		var result = InspectXml("<broken", new NfoOptions());
		Assert.False(result.IsFavourite);
		Assert.False(result.IsUncertain);
		Assert.Empty(result.Sources);
		Assert.Contains("disabled", result.Reason);
		Assert.Equal(0, _service.DirectoryEnumerationCount);
	}

	[Theory]
	[InlineData("<movie><favorite>true</favorite></movie>")]
	[InlineData("<movie><ISFAVORITE> TrUe </ISFAVORITE></movie>")]
	[InlineData("<movie><favorite>1</favorite></movie>")]
	[InlineData("<movie><TaG> fAvOuRiTe </TaG></movie>")]
	[InlineData("<movie><tag>Favorite</tag></movie>")]
	[InlineData("<movie xmlns:c='urn:custom'><c:favorite>true</c:favorite></movie>")]
	[InlineData("<movie><favorite><![CDATA[true]]></favorite></movie>")]
	public void ExplicitPositive_IsFavourite(string xml) {
		NfoResult result = InspectXml(xml);
		Assert.True(result.IsFavourite);
		Assert.False(result.IsUncertain);
		Assert.Equal(Path.Combine(_directory, "clip.nfo"), Assert.Single(result.Sources));
		Assert.Contains("explicit favourite", result.Reason);
	}

	[Theory]
	[InlineData("<movie><favorite>false</favorite></movie>")]
	[InlineData("<movie><isfavorite> FALSE </isfavorite></movie>")]
	[InlineData("<movie><favorite>0</favorite></movie>")]
	public void ExplicitNegative_IsNotFavourite(string xml) {
		NfoResult result = InspectXml(xml);
		Assert.False(result.IsFavourite);
		Assert.False(result.IsUncertain);
		Assert.Contains("negative", result.Reason);
	}

	[Theory]
	[InlineData("<movie><favorite>false</favorite><tag>Favourite</tag></movie>")]
	[InlineData("<movie><favorite>true</favorite><favorite>false</favorite></movie>")]
	[InlineData("<movie><isfavorite>0</isfavorite><favorite>true</favorite></movie>")]
	public void PositiveWinsOverNegative_IncludingAdjacentElements(string xml) {
		NfoResult result = InspectXml(xml);
		Assert.True(result.IsFavourite);
		Assert.False(result.IsUncertain);
	}

	[Theory]
	[InlineData("<movie><rating>10</rating><userrating>10</userrating><watched>true</watched></movie>")]
	[InlineData("<movie><!-- <favorite>true</favorite> --><title>Favourite</title></movie>")]
	[InlineData("<movie><favorite>sometimes</favorite></movie>")]
	[InlineData("<movie><favorite><unrelated>true</unrelated></favorite></movie>")]
	[InlineData("<movie><tag>Not a Favourite</tag></movie>")]
	public void NonExplicitMarkers_AreNotInferred(string xml) {
		NfoResult result = InspectXml(xml);
		Assert.False(result.IsFavourite);
		Assert.False(result.IsUncertain);
		Assert.NotEmpty(result.Reason);
	}

	[Fact]
	public void CustomElementsAndTagValues_MatchIgnoringCaseAndWhitespace() {
		var options = Enabled();
		options.BooleanElements = [" pinned "];
		options.TagElements = [" label "];
		options.TagValues = [" preserve "];
		NfoResult result = InspectXml("<movie><favorite>true</favorite><PINNED>false</PINNED><Label>Preserve</Label></movie>", options);
		Assert.True(result.IsFavourite);
		Assert.False(result.IsUncertain);
	}

	[Fact]
	public void MissingNfo_HasVisibleDiagnostic() {
		NfoResult result = _service.Inspect(Write("clip.mkv"), Enabled());
		Assert.False(result.IsFavourite);
		Assert.False(result.IsUncertain);
		Assert.Empty(result.Sources);
		Assert.Contains("No applicable NFO", result.Reason);
	}

	[Fact]
	public void MissingMarker_HasVisibleDiagnostic() {
		NfoResult result = InspectXml("<movie><title>Example</title></movie>");
		Assert.False(result.IsFavourite);
		Assert.False(result.IsUncertain);
		Assert.Contains("no recognized favourite marker", result.Reason);
	}

	[Theory]
	[InlineData("")]
	[InlineData("<movie><favorite>true</movie>")]
	[InlineData("<movie/><movie/>")]
	[InlineData("<?xml version='1.0' encoding='not-a-real-encoding'?><movie/>")]
	public void MalformedOrUnsupportedXml_IsUncertain(string xml) {
		NfoResult result = InspectXml(xml);
		Assert.True(result.IsUncertain);
		Assert.Contains("XML", result.Reason);
		Assert.Single(result.Sources);
	}

	[Fact]
	public void PositiveBeforeMalformedTail_RemainsProtectedAndUncertain() {
		NfoResult result = InspectXml("<movie><favorite>true</favorite><broken></movie>");
		Assert.True(result.IsFavourite);
		Assert.True(result.IsUncertain);
	}

	[Fact]
	public void ExternalFileEntity_IsNeverExpanded() {
		string secret = Write("entity.txt", "true");
		string uri = new Uri(secret).AbsoluteUri;
		NfoResult result = InspectXml($"<!DOCTYPE movie [<!ENTITY marker SYSTEM '{uri}'>]><movie><favorite>&marker;</favorite></movie>");
		Assert.False(result.IsFavourite);
		Assert.True(result.IsUncertain);
	}

	[Theory]
	[InlineData("<!DOCTYPE movie SYSTEM 'https://invalid.example/remote.dtd'><movie/>")]
	[InlineData("<!DOCTYPE movie [<!ENTITY marker 'true'>]><movie><favorite>&marker;</favorite></movie>")]
	public void DtdAndExternalNetworkDefinitions_AreRejected(string xml) {
		NfoResult result = InspectXml(xml);
		Assert.False(result.IsFavourite);
		Assert.True(result.IsUncertain);
	}

	[Theory]
	[InlineData("utf-8")]
	[InlineData("utf-16")]
	[InlineData("utf-16BE")]
	[InlineData("iso-8859-1")]
	public void CommonDeclaredEncodings_AreReadFromBytes(string encodingName) {
		string video = Write("clip.mkv");
		Encoding encoding = encodingName switch {
			"utf-16" => Encoding.Unicode,
			"utf-16BE" => Encoding.BigEndianUnicode,
			"iso-8859-1" => Encoding.Latin1,
			_ => new UTF8Encoding(true)
		};
		string xml = $"<?xml version='1.0' encoding='{encodingName}'?><movie><title>Caf\u00e9</title><favorite>true</favorite></movie>";
		File.WriteAllText(Path.Combine(_directory, "clip.nfo"), xml, encoding);
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsFavourite);
		Assert.False(result.IsUncertain);
	}

	[Fact]
	public void InvalidUtf8_IsUncertainInsteadOfSilentlyReplaced() {
		string video = Write("clip.mkv");
		byte[] prefix = Encoding.UTF8.GetBytes("<movie><title>");
		byte[] suffix = Encoding.UTF8.GetBytes("</title><favorite>true</favorite></movie>");
		File.WriteAllBytes(Path.Combine(_directory, "clip.nfo"), [.. prefix, 0xC3, 0x28, .. suffix]);
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsUncertain);
	}

	[Fact]
	public void NfoSizeLimit_IsConservative() {
		NfoResult result = InspectXml("<movie><favorite>true</favorite><plot>" +
			new string('x', NfoFavouriteService.MaximumNfoBytes) + "</plot></movie>");
		Assert.True(result.IsUncertain);
		Assert.Contains("size limit", result.Reason);
	}

	[Fact]
	public void ExcessiveXmlNesting_IsConservative() {
		NfoResult result = InspectXml(string.Concat(Enumerable.Repeat("<node>", 80)) +
			"<favorite>true</favorite>" + string.Concat(Enumerable.Repeat("</node>", 80)));
		Assert.True(result.IsUncertain);
		Assert.Contains("nesting limit", result.Reason);
	}

	[Fact]
	public void UnavailableDirectory_IsUncertain() {
		NfoResult result = _service.Inspect(Path.Combine(_directory, "missing", "clip.mkv"), Enabled());
		Assert.True(result.IsUncertain);
		Assert.Contains("could not be read", result.Reason);
	}

	[Fact]
	public void InaccessibleSidecar_IsUncertain() {
		string video = Write("clip.mkv");
		string nfo = Write("clip.nfo", "<movie><favorite>true</favorite></movie>");
		using var locked = new FileStream(nfo, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsUncertain);
		Assert.Contains("could not be read", result.Reason);
	}

	[Fact]
	public void NfoExtension_IsCaseInsensitive() {
		string video = Write("clip.mkv");
		string nfo = Write("clip.NFO", "<movie><favorite>true</favorite></movie>");
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsFavourite);
		Assert.Equal(nfo, Assert.Single(result.Sources));
	}

	[Fact]
	public void SameBaseEpisode_IsAssociatedWithinMultipleVideoDirectory() {
		string first = Write("episode1.mkv");
		string second = Write("episode2.mp4");
		Write("episode1.nfo", "<episodedetails><isfavorite>true</isfavorite></episodedetails>");
		Assert.True(_service.Inspect(first, Enabled()).IsFavourite);
		Assert.False(_service.Inspect(second, Enabled()).IsFavourite);
	}

	[Fact]
	public void MovieNfo_AppliesOnlyToSingleMatchingVideo() {
		string video = Write("film.mkv");
		Write("film.srt", "unrelated subtitle");
		Write("other/extra.mp4");
		string nfo = Write("Movie.NFO", "<movie><favorite>true</favorite></movie>");
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsFavourite);
		Assert.Equal(nfo, Assert.Single(result.Sources));
	}

	[Fact]
	public void MovieNfo_WithMultipleVideos_IsIgnoredWithDiagnostic() {
		string first = Write("film.mkv");
		string second = Write("extra.MP4");
		Write("movie.nfo", "<movie><favorite>true</favorite></movie>");
		foreach (string video in new[] { first, second }) {
			NfoResult result = _service.Inspect(video, Enabled());
			Assert.False(result.IsFavourite);
			Assert.False(result.IsUncertain);
			Assert.Contains("unambiguous", result.Reason);
		}
	}

	[Fact]
	public void MovieNfo_DoesNotApplyToNonexistentDifferentVideo() {
		Write("film.mkv");
		Write("movie.nfo", "<movie><favorite>true</favorite></movie>");
		NfoResult result = _service.Inspect(Path.Combine(_directory, "missing.mkv"), Enabled());
		Assert.False(result.IsFavourite);
	}

	[Fact]
	public void MovieNamedVideo_CanUseExactSidecarEvenWithOtherVideos() {
		string video = Write("movie.mkv");
		Write("extra.mkv");
		Write("movie.nfo", "<movie><favorite>true</favorite></movie>");
		Assert.True(_service.Inspect(video, Enabled()).IsFavourite);
	}

	[Fact]
	public void SeriesInheritance_IsOptInAndPositiveOverridesEpisodeNegative() {
		string video = Write("Show/Season 1/episode.mkv");
		Write("Show/Season 1/episode.nfo", "<episodedetails><favorite>false</favorite></episodedetails>");
		string series = Write("Show/tvshow.nfo", "<tvshow><tag>Favourite</tag></tvshow>");
		Assert.False(_service.Inspect(video, Enabled()).IsFavourite);
		var options = Enabled();
		options.InheritSeries = true;
		NfoResult result = _service.Inspect(video, options);
		Assert.True(result.IsFavourite);
		Assert.False(result.IsUncertain);
		Assert.Contains(series, result.Sources);
	}

	[Fact]
	public void NearestSeriesBoundary_DoesNotInheritUnrelatedParentFavourite() {
		string video = Write("Outer/Inner/Season 1/episode.mkv");
		Write("Outer/tvshow.nfo", "<tvshow><favorite>true</favorite></tvshow>");
		string inner = Write("Outer/Inner/tvshow.nfo", "<tvshow><favorite>false</favorite></tvshow>");
		var options = Enabled();
		options.InheritSeries = true;
		NfoResult result = _service.Inspect(video, options);
		Assert.False(result.IsFavourite);
		Assert.Equal(inner, Assert.Single(result.Sources));
	}

	[Fact]
	public void SeasonInheritance_IsOptInAndHandlesEpisodeSubdirectory() {
		string video = Write("Show/Season 1/Episode 1/episode.mkv");
		string season = Write("Show/Season 1/season.nfo", "<season><favorite>true</favorite></season>");
		Assert.False(_service.Inspect(video, Enabled()).IsFavourite);
		var options = Enabled();
		options.InheritSeason = true;
		NfoResult result = _service.Inspect(video, options);
		Assert.True(result.IsFavourite);
		Assert.Equal(season, Assert.Single(result.Sources));
	}

	[Fact]
	public void SeasonInheritance_DoesNotCrossSeriesBoundary() {
		string video = Write("Outer/Show/Season 1/episode.mkv");
		Write("Outer/season.nfo", "<season><favorite>true</favorite></season>");
		Write("Outer/Show/tvshow.nfo", "<tvshow><favorite>false</favorite></tvshow>");
		var options = Enabled();
		options.InheritSeason = true;
		NfoResult result = _service.Inspect(video, options);
		Assert.False(result.IsFavourite);
		Assert.Empty(result.Sources);
	}

	[Fact]
	public void SeriesInheritance_HasBoundedParentTraversal() {
		string nesting = Path.Combine(Enumerable.Range(0, NfoFavouriteService.MaximumParentLevels + 1).Select(i => "level" + i).ToArray());
		string video = Write(Path.Combine(nesting, "episode.mkv"));
		Write("tvshow.nfo", "<tvshow><favorite>true</favorite></tvshow>");
		var options = Enabled();
		options.InheritSeries = true;
		NfoResult result = _service.Inspect(video, options);
		Assert.False(result.IsFavourite);
		Assert.Empty(result.Sources);
	}

	[Fact]
	public void SameLengthSameTimestampChange_InvalidatesParsedCache() {
		string video = Write("clip.mkv");
		string nfo = Write("clip.nfo", "<movie><favorite>1</favorite></movie>");
		DateTime stamp = File.GetLastWriteTimeUtc(nfo);
		long length = new FileInfo(nfo).Length;
		Assert.True(_service.Inspect(video, Enabled()).IsFavourite);
		File.WriteAllText(nfo, "<movie><favorite>0</favorite></movie>", new UTF8Encoding(false));
		File.SetLastWriteTimeUtc(nfo, stamp);
		Assert.Equal(length, new FileInfo(nfo).Length);
		Assert.Equal(stamp, File.GetLastWriteTimeUtc(nfo));
		Assert.False(_service.Inspect(video, Enabled()).IsFavourite);
		Assert.Equal(2, _service.ParseCount);
	}

	[Fact]
	public void MarkerOptionChange_InvalidatesParsedCache() {
		string video = Write("clip.mkv");
		Write("clip.nfo", "<movie><pinned>true</pinned></movie>");
		var options = Enabled();
		Assert.False(_service.Inspect(video, options).IsFavourite);
		options.BooleanElements.Add("pinned");
		Assert.True(_service.Inspect(video, options).IsFavourite);
		Assert.Equal(2, _service.ParseCount);
	}

	[Fact]
	public void UnchangedMalformedXml_UsesCachedFailure() {
		string video = Write("clip.mkv");
		Write("clip.nfo", "<broken");
		Assert.True(_service.Inspect(video, Enabled()).IsUncertain);
		Assert.True(_service.Inspect(video, Enabled()).IsUncertain);
		Assert.Equal(1, _service.ParseCount);
	}

	[Fact]
	public void DirectoryMembershipChanges_UpdateMovieAssociation() {
		string video = Write("film.mkv");
		Write("movie.nfo", "<movie><favorite>true</favorite></movie>");
		Assert.True(_service.Inspect(video, Enabled()).IsFavourite);
		DateTime stamp = Directory.GetLastWriteTimeUtc(_directory);
		Write("extra.mp4");
		Directory.SetLastWriteTimeUtc(_directory, stamp.AddSeconds(1));
		Assert.False(_service.Inspect(video, Enabled()).IsFavourite);
	}

	[Fact]
	public void ClearCache_RefreshesNewSidecarsOnMountsWithPreservedDirectoryTimestamp() {
		string video = Write("clip.mkv");
		Assert.False(_service.Inspect(video, Enabled()).IsFavourite);
		DateTime stamp = Directory.GetLastWriteTimeUtc(_directory);
		Write("clip.nfo", "<movie><favorite>true</favorite></movie>");
		Directory.SetLastWriteTimeUtc(_directory, stamp);
		_service.ClearCache();
		Assert.True(_service.Inspect(video, Enabled()).IsFavourite);
	}

	[Fact]
	public void RemovedSidecar_IsUncertainUntilDirectoryAssociationRefreshes() {
		string video = Write("clip.mkv");
		string nfo = Write("clip.nfo", "<movie><favorite>true</favorite></movie>");
		Assert.True(_service.Inspect(video, Enabled()).IsFavourite);
		DateTime stamp = Directory.GetLastWriteTimeUtc(_directory);
		File.Delete(nfo);
		Directory.SetLastWriteTimeUtc(_directory, stamp);
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
	}

	[Fact]
	public void LargeSiblingCollection_EnumeratesOnceAndDoesNotReparseUnchangedXml() {
		const int count = 1000;
		string[] videos = new string[count];
		for (int i = 0; i < count; i++) {
			videos[i] = Write($"episode{i}.mkv");
			Write($"episode{i}.nfo", "<episodedetails><tag>Favourite</tag></episodedetails>");
		}
		foreach (string video in videos) Assert.True(_service.Inspect(video, Enabled()).IsFavourite);
		foreach (string video in videos) Assert.True(_service.Inspect(video, Enabled()).IsFavourite);
		Assert.Equal(1, _service.DirectoryEnumerationCount);
		Assert.Equal(count, _service.ParseCount);
	}

	[Fact]
	public void ParallelEpisodes_ReuseDirectoryAndSeriesParse() {
		string[] videos = Enumerable.Range(0, 100).Select(i => Write($"Show/Season 1/episode{i}.mkv")).ToArray();
		Write("Show/tvshow.nfo", "<tvshow><favorite>true</favorite></tvshow>");
		var options = Enabled();
		options.InheritSeries = true;
		Parallel.ForEach(videos, video => Assert.True(_service.Inspect(video, options).IsFavourite));
		Assert.Equal(2, _service.DirectoryEnumerationCount);
		Assert.Equal(1, _service.ParseCount);
	}
}
