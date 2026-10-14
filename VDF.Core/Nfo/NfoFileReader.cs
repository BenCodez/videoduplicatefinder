// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using VDF.Core.Review;

namespace VDF.Core.Nfo;

/// <summary>Opens only proven regular NFO files and rejects changes while reading.</summary>
internal sealed partial class NfoFileReader : IDisposable {
	readonly string _path;
	readonly Snapshot _initial;
	internal FileStream Stream { get; }
	// A deterministic replacement-race seam, isolated to the current test context.
	internal static readonly AsyncLocal<Action<string>?> OpenedObserver = new();

	NfoFileReader(string path, FileStream stream, Snapshot initial) {
		_path = path;
		Stream = stream;
		_initial = initial;
	}

	internal static NfoFileReader Open(string path) {
		SafePaths.RequireRegularFile(path);
		FileStream? stream = null;
		try {
			if (OperatingSystem.IsLinux()) {
				// O_PATH never opens a FIFO/device for I/O. Walk from the root using
				// anchored no-follow descriptors so ancestor swaps cannot redirect us.
				using SafeFileHandle pinned = PinLinuxFile(path);
				Snapshot initial = ReadHandle(pinned);
				OpenedObserver.Value?.Invoke(path);
				// Reopen the proven regular inode, not its replaceable pathname. The
				// normal FileStream open also preserves .NET FileShare locking. An
				// unavailable proc filesystem deliberately makes inspection uncertain.
				stream = new FileStream("/proc/self/fd/" + pinned.DangerousGetHandle().ToInt32(),
					FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
				if (ReadHandle(stream.SafeFileHandle) != initial) throw Unavailable();
				var file = new NfoFileReader(path, stream, initial);
				file.RequireUnchanged();
				return file;
			}
			using SafeFileHandle handle = OpenOtherPlatform(path);
			Snapshot snapshot = ReadHandle(handle);
			OpenedObserver.Value?.Invoke(path);
			// Transfer ownership to FileStream only after proving the handle regular.
			var owned = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: true);
			handle.SetHandleAsInvalid();
			try { stream = new FileStream(owned, FileAccess.Read, 4096, isAsync: false); }
			catch { owned.Dispose(); throw; }
			var result = new NfoFileReader(path, stream, snapshot);
			result.RequireUnchanged();
			return result;
		}
		catch { stream?.Dispose(); throw; }
	}

	internal void RequireUnchanged() {
		SafePaths.RequireRegularFile(_path);
		if (ReadHandle(Stream.SafeFileHandle) != _initial) throw Unavailable();
		if (OperatingSystem.IsLinux()) {
			if (!LinuxFileSystem.TryRead(_path, out var stat) || LinuxSnapshot(stat) != _initial)
				throw Unavailable();
		}
		else if (OperatingSystem.IsWindows()) {
			using SafeFileHandle current = OpenOtherPlatform(_path);
			if (ReadHandle(current) != _initial) throw Unavailable();
		}
		else {
			if (Mono.Unix.Native.Syscall.lstat(_path, out var stat) != 0 || UnixSnapshot(stat) != _initial)
				throw Unavailable();
		}
	}

	public void Dispose() => Stream.Dispose();
	static IOException Unavailable() => new("A regular, unchanged NFO file could not be validated.");
	sealed record Snapshot(string Id, long Size, long Modified, long ModifiedFraction, long Changed, long ChangedFraction);

	static Snapshot ReadHandle(SafeFileHandle handle) {
		if (OperatingSystem.IsLinux()) {
			if (!LinuxFileSystem.TryReadHandle(handle, out var stat)) throw Unavailable();
			return LinuxSnapshot(stat);
		}
		if (OperatingSystem.IsWindows()) {
			if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) ||
				(info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
				(info.IndexHigh | info.IndexLow) == 0 ||
				!GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInformation>()))
				throw Unavailable();
			return new($"win:{info.Volume:X}:{info.IndexHigh:X}:{info.IndexLow:X}",
				((long)info.SizeHigh << 32) | info.SizeLow,
				((long)info.Modified.High << 32) | info.Modified.Low, 0, basic.ChangeTime, 0);
		}
		if (Mono.Unix.Native.Syscall.fstat(handle.DangerousGetHandle().ToInt32(), out var unix) != 0)
			throw Unavailable();
		return UnixSnapshot(unix);
	}

	static Snapshot LinuxSnapshot(LinuxFileSystem.StatxBuffer stat) {
		if (!LinuxFileSystem.IsRegular(stat) || (stat.Mask & LinuxFileSystem.ChangeMask) == 0) throw Unavailable();
		return new(LinuxFileSystem.FileId(stat), (long)stat.Size, stat.Modification.Seconds,
			stat.Modification.Nanoseconds, stat.Change.Seconds, stat.Change.Nanoseconds);
	}
	static Snapshot UnixSnapshot(Mono.Unix.Native.Stat stat) {
		if (((uint)stat.st_mode & 0xF000u) != 0x8000u || stat.st_ino == 0 || stat.st_size < 0)
			throw Unavailable();
		return new($"unix:{stat.st_dev:X}:{stat.st_ino:X}", stat.st_size, stat.st_mtime,
			stat.st_mtime_nsec, stat.st_ctime, stat.st_ctime_nsec);
	}

	static SafeFileHandle PinLinuxFile(string path) {
		// ARM/AArch64 override DIRECTORY and NOFOLLOW, but inherit O_PATH
		// (1 << 21) from include/uapi/asm-generic/fcntl.h, like x86/x64.
		int directoryFlags = LinuxFileSystem.DirectoryOpenFlags(RuntimeInformation.ProcessArchitecture) | 0x200000; // O_PATH
		SafeFileHandle current = NativeHandle(OpenAt(-100, "/", directoryFlags));
		try {
			string[] components = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < components.Length; i++) {
				int flags = i == components.Length - 1
					? directoryFlags & ~(RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 ? 0x4000 : 0x10000)
					: directoryFlags;
				SafeFileHandle next = NativeHandle(OpenAt(current.DangerousGetHandle().ToInt32(), components[i], flags));
				current.Dispose();
				current = next;
			}
			return current;
		}
		catch { current.Dispose(); throw; }
	}
	static SafeFileHandle NativeHandle(int descriptor) {
		if (descriptor < 0) throw Unavailable();
		return new(new IntPtr(descriptor), ownsHandle: true);
	}
	static SafeFileHandle OpenOtherPlatform(string path) {
		if (OperatingSystem.IsWindows()) {
			SafeFileHandle handle = CreateFileW(path, 0x80000000, 1 | 2 | 4, IntPtr.Zero, 3,
				0x00200000 | 0x08000000, IntPtr.Zero); // OPEN_REPARSE_POINT | SEQUENTIAL_SCAN
			if (handle.IsInvalid) { handle.Dispose(); throw Unavailable(); }
			return handle;
		}
		if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD()) throw Unavailable();
		// Mono maps these symbolic flags to the host Unix ABI. Nonblocking open
		// prevents a raced FIFO from waiting for a writer; fstat rejects it before
		// any read. Missing native support fails closed rather than using attributes.
		SafeFileHandle unix = NativeHandle(Mono.Unix.Native.Syscall.open(path, Mono.Unix.Native.OpenFlags.O_RDONLY |
			Mono.Unix.Native.OpenFlags.O_NOFOLLOW | Mono.Unix.Native.OpenFlags.O_NONBLOCK));
		// Match FileShare.ReadWrite's shared advisory lock, without waiting on
		// a .NET FileShare.None holder. The handle owns the lock until disposal.
		try {
			if (Flock(unix.DangerousGetHandle().ToInt32(), 1 | 4) != 0) throw Unavailable();
			return unix;
		}
		catch { unix.Dispose(); throw; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct FileTime { public uint Low, High; }
	[StructLayout(LayoutKind.Sequential)]
	struct FileInformation {
		public uint Attributes;
		public FileTime Created, Accessed, Modified;
		public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
	}
	[StructLayout(LayoutKind.Sequential)]
	struct BasicInformation {
		public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
		public uint Attributes;
	}
	[LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int OpenAt(int directory, string path, int flags);
	[LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
	private static partial int Flock(int descriptor, int operation);
	[LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial uint GetFileType(SafeFileHandle handle);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out BasicInformation information, uint size);
}
