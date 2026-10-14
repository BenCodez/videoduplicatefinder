using System.Text;
using MemoryPack;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Core.Tests.Utils;

public sealed class ImageCreationDateProvenanceTests : IDisposable {
	readonly string folder = Directory.CreateTempSubdirectory("vdf-image-date-").FullName;
	public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

	string CaptureFile() {
		string path = Path.Combine(folder, "capture.tif");
		using var stream = File.Create(path);
		using var writer = new BinaryWriter(stream);
		writer.Write(new byte[] { (byte)'I', (byte)'I' });
		writer.Write((ushort)42);
		writer.Write(8u);
		writer.Write((ushort)1);
		writer.Write((ushort)0x0132);
		writer.Write((ushort)2);
		writer.Write(20u);
		writer.Write(26u);
		writer.Write(0u);
		writer.Write(Encoding.ASCII.GetBytes("2014:06:15 18:30:45\0"));
		return path;
	}

	[Fact]
	public void ExifCaptureProvenanceSurvivesDatabaseAndDuplicateProjection() {
		var entry = new FileEntry(CaptureFile());
		DateTime modified = entry.DateModified;
		ScanEngine.RefreshImageCreationDate(entry, true);
		Assert.True(entry.Flags.Has(EntryFlags.ExifCreationDate));
		Assert.Equal(new DateTime(2014, 6, 15, 18, 30, 45, DateTimeKind.Utc), entry.DateCreated);
		Assert.Equal(modified, entry.DateModified);
		var saved = MemoryPackSerializer.Deserialize<FileEntry>(MemoryPackSerializer.Serialize(entry))!;
		var item = new DuplicateItem(saved, 0, Guid.Empty, DuplicateFlags.AiMatched);
		Assert.Equal(entry.DateCreated, item.DateCreated);
		Assert.Equal(DuplicateFlags.AiMatched | DuplicateFlags.ExifCreationDate, item.Flags);
		File.Delete(entry.Path);
		ScanEngine.RefreshImageCreationDate(saved, false);
		Assert.True(saved.Flags.Has(EntryFlags.ExifCreationDate));
		Assert.Equal(entry.DateCreated, saved.DateCreated);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DisabledOrUnavailableExifRestoresFilesystemDateAndClearsProvenance(bool removeExif) {
		var entry = new FileEntry(CaptureFile());
		ScanEngine.RefreshImageCreationDate(entry, true);
		Assert.True(entry.Flags.Has(EntryFlags.ExifCreationDate));
		if (removeExif) File.WriteAllText(entry.Path, "image without EXIF");
		ScanEngine.RefreshImageCreationDate(entry, removeExif);
		Assert.False(entry.Flags.Has(EntryFlags.ExifCreationDate));
		Assert.Equal(new FileInfo(entry.Path).CreationTimeUtc, entry.DateCreated);
		var item = new DuplicateItem(entry, 0, Guid.Empty, DuplicateFlags.ExifCreationDate);
		Assert.Equal(DuplicateFlags.None, item.Flags & DuplicateFlags.ExifCreationDate);
	}

	[Fact]
	public void OldDatabaseFixtureAndFreshFilesystemMetadataHaveNoCaptureProvenance() {
		var old = LegacyDatabaseReader.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "legacy-wrapper.db")));
		Assert.All(old.Entries, entry => Assert.False(entry.Flags.Has(EntryFlags.ExifCreationDate)));
		var fresh = new FileEntry(CaptureFile());
		Assert.False(fresh.Flags.Has(EntryFlags.ExifCreationDate));
		Assert.Equal(new FileInfo(fresh.Path).CreationTimeUtc, fresh.DateCreated);
	}
}
