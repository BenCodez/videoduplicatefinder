// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net.Sockets;
using Mono.Unix.Native;
using VDF.Core.Nfo;

namespace VDF.Core.Tests.Nfo;

public sealed class NfoFileSafetyTests : IDisposable {
	readonly string _directory = Directory.CreateTempSubdirectory("vdf-nfo-safety-").FullName;
	readonly NfoFavouriteService _service = new();
	const string PositiveXml = "<movie><favorite>true</favorite></movie>";
	static NfoOptions Enabled() => new() { Enabled = true };
	string Write(string name, string contents = "") {
		string path = Path.Combine(_directory, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents);
		return path;
	}
	public void Dispose() {
		NfoFileReader.OpenedObserver.Value = null;
		try { Directory.Delete(_directory, true); } catch { /* Best-effort fixture cleanup. */ }
	}

	// The rescue writer makes a regression to blocking FIFO open fail within a
	// deadline instead of leaving a stuck test worker. It sends no FIFO bytes.
	async Task<NfoResult> InspectWithFifoDeadline(string video, string fifo) {
		Task<NfoResult> inspection = Task.Run(() => _service.Inspect(video, Enabled()));
		try { return await inspection.WaitAsync(TimeSpan.FromSeconds(5)); }
		finally {
			if (!inspection.IsCompleted) {
				int rescue = Syscall.open(fifo, OpenFlags.O_RDWR | OpenFlags.O_NONBLOCK);
				try {
					try { await inspection.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* Preserve deadline failure. */ }
				}
				finally { if (rescue >= 0) Syscall.close(rescue); }
			}
		}
	}

	[Fact]
	public async Task FifoSidecar_IsUncertainWithoutWaitingForAWriter() {
		if (!OperatingSystem.IsLinux()) return;
		string video = Write("clip.mkv");
		string fifo = Path.Combine(_directory, "clip.nfo");
		Assert.Equal(0, Syscall.mkfifo(fifo, FilePermissions.S_IRUSR | FilePermissions.S_IWUSR));
		NfoResult result = await InspectWithFifoDeadline(video, fifo);
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(0, _service.ParseCount);
	}

	[Theory]
	[InlineData("clip.nfo")]
	[InlineData("other.mkv")]
	public void SocketEntry_IsUncertainAndDoesNotCreateAMovieAssociation(string name) {
		if (!OperatingSystem.IsLinux()) return;
		string video = Write("clip.mkv");
		Write("movie.nfo", PositiveXml);
		using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(_directory, name)));
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(0, _service.ParseCount);
	}

	[Fact]
	public async Task CachedRegularSidecar_ReplacedWithFifo_IsUncertainWithoutOpeningIt() {
		if (!OperatingSystem.IsLinux()) return;
		string video = Write("clip.mkv");
		string sidecar = Write("clip.nfo", PositiveXml);
		Assert.True(_service.Inspect(video, Enabled()).IsFavourite);
		DateTime stamp = Directory.GetLastWriteTimeUtc(_directory);
		File.Delete(sidecar);
		Assert.Equal(0, Syscall.mkfifo(sidecar, FilePermissions.S_IRUSR | FilePermissions.S_IWUSR));
		Directory.SetLastWriteTimeUtc(_directory, stamp);
		NfoResult result = await InspectWithFifoDeadline(video, sidecar);
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(1, _service.DirectoryEnumerationCount);
		Assert.Equal(1, _service.ParseCount);
	}

	[Fact]
	public async Task SidecarReplacedAfterPinningWithFifo_IsUncertainWithoutReadingFifo() {
		if (!OperatingSystem.IsLinux()) return;
		string video = Write("clip.mkv");
		string sidecar = Write("clip.nfo", PositiveXml);
		NfoFileReader.OpenedObserver.Value = path => {
			File.Delete(path);
			Assert.Equal(0, Syscall.mkfifo(path, FilePermissions.S_IRUSR | FilePermissions.S_IWUSR));
		};
		NfoResult result = await InspectWithFifoDeadline(video, sidecar);
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(0, _service.ParseCount);
	}

	[Fact]
	public void SidecarReplacedAfterPinningWithSameSizeAndTimestamp_IsUncertain() {
		string video = Write("clip.mkv");
		string sidecar = Write("clip.nfo", PositiveXml);
		NfoFileReader.OpenedObserver.Value = path => {
			DateTime stamp = File.GetLastWriteTimeUtc(path);
			string replacement = Write("replacement.tmp", PositiveXml);
			File.SetLastWriteTimeUtc(replacement, stamp);
			File.Move(replacement, path, true);
		};
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(0, _service.ParseCount);
	}

	[Fact]
	public void SidecarChangedDuringRead_IsRejectedByHandleAndPathValidation() {
		string sidecar = Write("clip.nfo", PositiveXml);
		using NfoFileReader file = NfoFileReader.Open(sidecar);
		using var reader = new StreamReader(file.Stream, leaveOpen: true);
		Assert.Equal(PositiveXml, reader.ReadToEnd());
		File.WriteAllText(sidecar, "<movie/>");
		Assert.Throws<IOException>(() => file.RequireUnchanged());
	}

	[Fact]
	public void SidecarSymbolicLink_IsUncertainWithoutParsingItsTarget() {
		if (OperatingSystem.IsWindows()) return; // Link creation may require privileges.
		string video = Write("clip.mkv");
		string target = Write("target.txt", PositiveXml);
		File.CreateSymbolicLink(Path.Combine(_directory, "clip.nfo"), target);
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(0, _service.ParseCount);
	}

	[Fact]
	public void DirectorySymbolicLink_IsUncertainWithoutEnumeratingTarget() {
		if (OperatingSystem.IsWindows()) return;
		Write("real/clip.mkv");
		Write("real/clip.nfo", PositiveXml);
		Directory.CreateSymbolicLink(Path.Combine(_directory, "alias"), Path.Combine(_directory, "real"));
		NfoResult result = _service.Inspect(Path.Combine(_directory, "alias", "clip.mkv"), Enabled());
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(0, _service.DirectoryEnumerationCount);
	}

	[Fact]
	public void ParentReplacedWithSymbolicLinkAfterPinning_IsUncertain() {
		if (!OperatingSystem.IsLinux()) return;
		string video = Write("original/clip.mkv");
		Write("original/clip.nfo", PositiveXml);
		Write("target/clip.nfo", PositiveXml);
		NfoFileReader.OpenedObserver.Value = path => {
			string original = Path.GetDirectoryName(path)!;
			Directory.Move(original, Path.Combine(_directory, "moved"));
			Directory.CreateSymbolicLink(original, Path.Combine(_directory, "target"));
		};
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(0, _service.ParseCount);
	}

	[Fact]
	public void LinuxCharacterDeviceSidecar_IsUncertainWithoutDeviceReads() {
		if (!OperatingSystem.IsLinux()) return;
		string video = Write("clip.mkv");
		string device = Path.Combine(_directory, "clip.nfo");
		// Synthetic /dev/null equivalent; unprivileged containers may reject mknod.
		if (Syscall.mknod(device, FilePermissions.S_IFCHR | FilePermissions.S_IRUSR | FilePermissions.S_IWUSR,
			(ulong)0x103) != 0) return;
		NfoResult result = _service.Inspect(video, Enabled());
		Assert.True(result.IsUncertain);
		Assert.False(result.IsFavourite);
		Assert.Equal(0, _service.ParseCount);
	}
}
