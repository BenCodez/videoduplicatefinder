using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.HeadlessTests;

public sealed class UnixThumbnailTheoryAttribute : TheoryAttribute {
	public UnixThumbnailTheoryAttribute() {
		if (OperatingSystem.IsWindows()) Skip = "The blocked FFmpeg fixture requires /bin/sh and Unix executable permissions.";
	}
}

public sealed class ThumbnailReloadReservationsTests : IDisposable {
	readonly string folder = Directory.CreateTempSubdirectory("vdf-manual-thumbnails-").FullName;
	public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

	static readonly Type FfmpegType = typeof(ScanEngine).Assembly.GetType("VDF.Core.FFTools.FfmpegEngine", throwOnError: true)!;
	static readonly FieldInfo FfmpegPathField = FfmpegType.GetFields(BindingFlags.Static | BindingFlags.NonPublic)
		.Single(field => field.Name.Contains("FFmpegPath", StringComparison.Ordinal));
	static readonly PropertyInfo NativeBinding = FfmpegType.GetProperty("UseNativeBinding", BindingFlags.Static | BindingFlags.Public)!;

	DuplicateItemVM Add(MainWindowVM vm) {
		string path = Path.Combine(folder, "video.mp4");
		File.WriteAllBytes(path, [1, 2, 3, 4]);
		var info = new FileInfo(path);
		var row = new DuplicateItemVM(new DuplicateItem {
			Path = path, Folder = folder, GroupId = Guid.NewGuid(), SizeLong = info.Length,
			DateModified = info.LastWriteTimeUtc, DateCreated = info.CreationTimeUtc,
			Duration = TimeSpan.FromMinutes(1), FrameSize = "1920x1080", Format = "h264"
		});
		vm.Duplicates.Add(row);
		row.Checked = true;
		vm.NewResultsSelectionProvider = () => [row];
		return row;
	}

	ResultsGroupHeader Header(DuplicateItemVM row) => new() {
		GroupId = row.ItemInfo.GroupId, Rows = [new ResultsItemRow(row)]
	};

	async Task WithSettings(Func<MainWindowVM, DuplicateItemVM, Task> work) {
		var shell = HeadlessUi.Shell();
		var settings = SettingsFile.Instance;
		var previous = (settings.CustomDatabaseFolder, settings.Includes, settings.Thumbnails,
			settings.ThumbnailMaxWidth, settings.MaxDegreeOfParallelism, settings.UseNativeFfmpegBinding,
			settings.CustomFFArguments, settings.EnableScheduledScan, settings.ScheduledScanProfile,
			settings.ScheduledLastClaimedSlotUtc, settings.ScheduledLastStartedUtc,
			settings.ScheduledLastCompletedUtc, settings.ScheduledLastDurationSeconds,
			settings.ScheduledLastStatus, settings.AutomationSelection, settings.NfoFavourites);
		bool shellGathered = shell.ViewModel.IsGathered;
		bool shellReady = shell.ViewModel.IsReadyToCompare;
		string? previousSettingsPath = (string?)typeof(SettingsFile).GetField("settingsPath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
		object? previousFfmpeg = FfmpegPathField.GetValue(null);
		object? previousNative = NativeBinding.GetValue(null);
		object? previousArguments = FfmpegType.GetField("CustomFFArguments", BindingFlags.Static | BindingFlags.Public)!.GetValue(null);
		object? previousAcceleration = FfmpegType.GetField("HardwareAccelerationMode", BindingFlags.Static | BindingFlags.Public)!.GetValue(null);
		var previousProvider = ThumbCacheHelpers.Provider;
		MainWindowVM? vm = null;
		try {
			shell.ViewModel.IsGathered = false;
			shell.ViewModel.IsReadyToCompare = false;
			settings.CustomDatabaseFolder = folder;
			settings.Includes = new ObservableCollection<string> { folder };
			settings.Thumbnails = 1;
			settings.ThumbnailMaxWidth = 240;
			settings.MaxDegreeOfParallelism = 1;
			settings.UseNativeFfmpegBinding = false;
			settings.CustomFFArguments = string.Empty;
			settings.EnableScheduledScan = false;
			settings.ScheduledScanProfile = ScanProfile.Custom;
			settings.ScheduledLastStatus = "Unchanged during thumbnail reload";
			settings.AutomationSelection = new();
			settings.NfoFavourites = new();
			SettingsFile.SetSettingsPath(Path.Combine(folder, "Settings.json"));
			vm = new MainWindowVM();
			var row = Add(vm);
			vm.InitializeScheduledScanning();
			StopTimer(vm);
			await work(vm, row);
		}
		finally {
			if (vm is not null) {
				StopTimer(vm);
				if (vm.IsScanning || vm.HasActiveScanRun) vm.Scanner.Stop();
				await vm.PrepareAutomationRescanAsync();
				vm.Duplicates.Clear();
			}
			if (!ReferenceEquals(ThumbCacheHelpers.Provider, previousProvider)) {
				ThumbCacheHelpers.Provider?.Dispose();
				ThumbCacheHelpers.Provider = previousProvider;
			}
			FfmpegPathField.SetValue(null, previousFfmpeg);
			NativeBinding.SetValue(null, previousNative);
			FfmpegType.GetField("CustomFFArguments", BindingFlags.Static | BindingFlags.Public)!.SetValue(null, previousArguments);
			FfmpegType.GetField("HardwareAccelerationMode", BindingFlags.Static | BindingFlags.Public)!.SetValue(null, previousAcceleration);
			settings.CustomDatabaseFolder = previous.CustomDatabaseFolder;
			settings.Includes = previous.Includes;
			settings.Thumbnails = previous.Thumbnails;
			settings.ThumbnailMaxWidth = previous.ThumbnailMaxWidth;
			settings.MaxDegreeOfParallelism = previous.MaxDegreeOfParallelism;
			settings.UseNativeFfmpegBinding = previous.UseNativeFfmpegBinding;
			settings.CustomFFArguments = previous.CustomFFArguments;
			settings.EnableScheduledScan = previous.EnableScheduledScan;
			settings.ScheduledScanProfile = previous.ScheduledScanProfile;
			settings.ScheduledLastClaimedSlotUtc = previous.ScheduledLastClaimedSlotUtc;
			settings.ScheduledLastStartedUtc = previous.ScheduledLastStartedUtc;
			settings.ScheduledLastCompletedUtc = previous.ScheduledLastCompletedUtc;
			settings.ScheduledLastDurationSeconds = previous.ScheduledLastDurationSeconds;
			settings.ScheduledLastStatus = previous.ScheduledLastStatus;
			settings.AutomationSelection = previous.AutomationSelection;
			settings.NfoFavourites = previous.NfoFavourites;
			SettingsFile.SetSettingsPath(previousSettingsPath);
			shell.ViewModel.IsGathered = shellGathered;
			shell.ViewModel.IsReadyToCompare = shellReady;
		}
	}

	static void StopTimer(MainWindowVM vm) =>
		((DispatcherTimer)typeof(MainWindowVM).GetField("scheduledScanTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!).Stop();

		static async Task AssertCommandReadyAsync(IObservable<bool> canExecute) =>
			Assert.True(await canExecute.FirstAsync(ready => ready).ToTask().WaitAsync(TimeSpan.FromSeconds(3)));

	static string QuoteForShell(string path) => "'" + path.Replace("'", "'\"'\"'") + "'";

	string CreateBlockedDecoder() {
		string executable = Path.Combine(folder, "ffmpeg");
		File.WriteAllText(executable, "#!/bin/sh\n" +
			$"printf '%s\\n' \"$$\" > {QuoteForShell(Path.Combine(folder, "decoder.pid"))}\n" +
			$"while [ ! -f {QuoteForShell(Path.Combine(folder, "decoder.release"))} ]; do /bin/sleep 0.02; done\n" +
			"exit 1\n");
		if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		FfmpegPathField.SetValue(null, executable);
		return executable;
	}

	async Task<int> WaitForDecoderAsync() {
		string marker = Path.Combine(folder, "decoder.pid");
		var until = DateTime.UtcNow.AddSeconds(8);
		while (DateTime.UtcNow < until) {
			if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out int pid)) return pid;
			await Task.Delay(10);
		}
		throw new TimeoutException("The real thumbnail retry did not start the fake FFmpeg process.");
	}

	[UnixThumbnailTheory]
	[InlineData(0, false)]
	[InlineData(1, false)]
	[InlineData(2, false)]
	[InlineData(0, true)]
	[InlineData(1, true)]
	[InlineData(2, true)]
	public Task ManualThumbnailReloadBlocksScansUntilFailedOrInterruptedDecoderDrains(int entryPoint, bool interrupt) =>
		HeadlessUi.Run(() => WithSettings(async (vm, row) => {
			using var checkedCommand = vm.LoadThumbnailsForCheckedItemsCommand;
			using var groupCommand = vm.LoadThumbnailsForGroupCommand;
			using var headerCommand = vm.LoadThumbnailsForGroupHeaderCommand;
			using var startCommand = vm.StartScanCommand;
			using var scheduledCommand = vm.RunScheduledScanNowCommand;
			CreateBlockedDecoder();
			var cache = ThumbCacheHelpers.Provider;
			var beforeStarted = SettingsFile.Instance.ScheduledLastStartedUtc;
			string beforeStatus = SettingsFile.Instance.ScheduledLastStatus;
			Task reload = entryPoint switch {
				0 => checkedCommand.Execute().ToTask(),
				1 => groupCommand.Execute().ToTask(),
				_ => headerCommand.Execute(Header(row)).ToTask()
			};
			try {
				// The reservation exists before awaiting decoder startup or yielding the UI.
				Assert.True(vm.IsBusy);
				Assert.True(vm.ShowThumbnailRetrievalProgressBar);
				int pid = await WaitForDecoderAsync();
				Assert.False(reload.IsCompleted);
				Assert.False(await startCommand.CanExecute.FirstAsync());
				Assert.False(await scheduledCommand.CanExecute.FirstAsync());
				Assert.False(await checkedCommand.CanExecute.FirstAsync());
				Assert.False(await groupCommand.CanExecute.FirstAsync());
				Assert.False(await headerCommand.CanExecute.FirstAsync());
				await startCommand.Execute("FullScan").ToTask();
				await scheduledCommand.Execute().ToTask();
				Assert.False(vm.IsScanning || vm.HasActiveScanRun);
				Assert.Equal(beforeStarted, SettingsFile.Instance.ScheduledLastStartedUtc);
				Assert.Equal(beforeStatus, SettingsFile.Instance.ScheduledLastStatus);
				Assert.Same(cache, ThumbCacheHelpers.Provider);
				Assert.Same(row, Assert.Single(vm.Duplicates));
				Assert.Equal(240, vm.Scanner.Settings.ThumbnailMaxWidth);
				using var decoder = Process.GetProcessById(pid);
				Assert.False(decoder.HasExited);
				if (interrupt) decoder.Kill(entireProcessTree: true);
				// Release every fixture worker after interrupting the observed child.
				// Inherited pipes or another decoder attempt must also be able to drain.
				File.WriteAllText(Path.Combine(folder, "decoder.release"), string.Empty);
				await reload.WaitAsync(TimeSpan.FromSeconds(10));
				Assert.True(decoder.WaitForExit(5000));
				Assert.False(vm.IsBusy || vm.ShowThumbnailRetrievalProgressBar);
					// ReactiveCommand clears its own IsExecuting after delivering completion.
					await AssertCommandReadyAsync(startCommand.CanExecute);
					await AssertCommandReadyAsync(scheduledCommand.CanExecute);
					await AssertCommandReadyAsync(checkedCommand.CanExecute);
					await AssertCommandReadyAsync(groupCommand.CanExecute);
					await AssertCommandReadyAsync(headerCommand.CanExecute);
				Assert.Same(cache, ThumbCacheHelpers.Provider);
				Assert.Same(row, Assert.Single(vm.Duplicates));
			}
			finally {
				File.WriteAllText(Path.Combine(folder, "decoder.release"), string.Empty);
				await reload.WaitAsync(TimeSpan.FromSeconds(20));
			}
		}));

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public Task InvalidReloadConfigurationReleasesReservationOnException(int entryPoint) =>
		HeadlessUi.Run(() => WithSettings(async (vm, row) => {
			SettingsFile.Instance.MaxDegreeOfParallelism = 0;
			using var checkedCommand = vm.LoadThumbnailsForCheckedItemsCommand;
			using var groupCommand = vm.LoadThumbnailsForGroupCommand;
			using var headerCommand = vm.LoadThumbnailsForGroupHeaderCommand;
			using var checkedErrors = checkedCommand.ThrownExceptions.Subscribe(_ => { });
			using var groupErrors = groupCommand.ThrownExceptions.Subscribe(_ => { });
			using var headerErrors = headerCommand.ThrownExceptions.Subscribe(_ => { });
			Task reload = entryPoint switch {
				0 => checkedCommand.Execute().ToTask(),
				1 => groupCommand.Execute().ToTask(),
				_ => headerCommand.Execute(Header(row)).ToTask()
			};
			await Assert.ThrowsAnyAsync<ArgumentException>(() => reload);
			Assert.False(vm.IsBusy || vm.ShowThumbnailRetrievalProgressBar);
			Assert.True(await vm.StartScanCommand.CanExecute.FirstAsync());
			Assert.Same(row, Assert.Single(vm.Duplicates));
		}));
}
