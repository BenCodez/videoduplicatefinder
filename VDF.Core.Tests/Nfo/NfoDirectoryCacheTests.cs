// SPDX-License-Identifier: AGPL-3.0-or-later

using VDF.Core.Nfo;

namespace VDF.Core.Tests.Nfo;

public sealed class NfoDirectoryCacheTests : IDisposable {
	const int SidecarBudget = 32_768;
	readonly string _directory = Directory.CreateTempSubdirectory("vdf-nfo-cache-").FullName;
	readonly NfoFavouriteService _service = new();
	static NfoOptions Enabled() => new() { Enabled = true };
	string CreateSidecars(string name, int count) {
		string directory = Directory.CreateDirectory(Path.Combine(_directory, name)).FullName;
		string video = Path.Combine(directory, "clip.mkv");
		File.WriteAllText(video, "");
		File.WriteAllText(Path.Combine(directory, "clip.nfo"), "<movie><favorite>true</favorite></movie>");
		for (int i = 1; i < count; i++) {
			using var file = File.Create(Path.Combine(directory, $"unused{i}.nfo"));
		}
		return video;
	}
	public void Dispose() {
		try { Directory.Delete(_directory, true); } catch { /* Best-effort fixture cleanup. */ }
	}

	[Fact]
	public void DirectoryExceedingSidecarBudget_IsInspectedButNeverRetained() {
		string small = CreateSidecars("small", 1);
		Assert.True(_service.Inspect(small, Enabled()).IsFavourite);
		string large = CreateSidecars("large", SidecarBudget + 1);
		for (int i = 0; i < 2; i++) {
			NfoResult result = _service.Inspect(large, Enabled());
			Assert.True(result.IsFavourite);
			Assert.False(result.IsUncertain);
			Assert.Equal(1, _service.CachedDirectoryCount);
			Assert.Equal(1, _service.CachedSidecarCount);
		}
		Assert.True(_service.Inspect(small, Enabled()).IsFavourite);
		Assert.Equal(3, _service.DirectoryEnumerationCount);
	}

	[Fact]
	public void FullActualSidecarCost_EvictsOldestSnapshotWhenTotalExceedsBudget() {
		string first = CreateSidecars("first", SidecarBudget / 2);
		string second = CreateSidecars("second", SidecarBudget / 2 + 1);
		Assert.True(_service.Inspect(first, Enabled()).IsFavourite);
		Assert.Equal(SidecarBudget / 2, _service.CachedSidecarCount);
		Assert.True(_service.Inspect(second, Enabled()).IsFavourite);
		Assert.Equal(1, _service.CachedDirectoryCount);
		Assert.Equal(SidecarBudget / 2 + 1, _service.CachedSidecarCount);
		Assert.True(_service.Inspect(second, Enabled()).IsFavourite);
		Assert.Equal(2, _service.DirectoryEnumerationCount);
		Assert.True(_service.Inspect(first, Enabled()).IsFavourite);
		Assert.Equal(3, _service.DirectoryEnumerationCount);
		Assert.Equal(1, _service.CachedDirectoryCount);
		Assert.Equal(SidecarBudget / 2, _service.CachedSidecarCount);
		_service.ClearCache();
		Assert.Equal(0, _service.CachedDirectoryCount);
		Assert.Equal(0, _service.CachedSidecarCount);
	}
}
