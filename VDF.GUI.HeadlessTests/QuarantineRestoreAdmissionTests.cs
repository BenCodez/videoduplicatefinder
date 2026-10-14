using System.Reflection;
using System.Reactive.Threading.Tasks;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Scheduling;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.HeadlessTests;

public sealed class QuarantineRestoreAdmissionTests {
	static SemaphoreSlim CoreGate => (SemaphoreSlim)typeof(QuarantineService).Assembly
		.GetType("VDF.Core.Cleanup.CleanupOperationGate")!.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
	static T HostField<T>(MainWindowVM vm, string name) => (T)typeof(MainWindowVM)
		.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

	static async Task WithSettings(Func<string, MainWindowVM, QuarantineRecord, Task> work) {
		string folder = Directory.CreateTempSubdirectory("vdf-restore-admission-").FullName;
		var settings = SettingsFile.Instance;
		string previousDatabase = settings.CustomDatabaseFolder;
		try {
			settings.CustomDatabaseFolder = folder;
			SettingsFile.SetSettingsPath(Path.Combine(folder, "Settings.json"));
			SettingsFile.SaveSettings();
			string source = Path.Combine(folder, "target.mp4");
			string keeper = Path.Combine(folder, "keeper.mp4");
			File.WriteAllText(source, "synthetic media bytes");
			File.WriteAllText(keeper, "synthetic media bytes");
			var vm = new MainWindowVM();
			var record = await new QuarantineService(vm.QuarantineHistoryFile).QuarantineExactAsync(
				FileIdentity.Capture(source)!, FileIdentity.Capture(keeper)!, Path.Combine(folder, "quarantine"));
			Assert.False(File.Exists(source));
			await work(folder, vm, record);
		}
		finally {
			settings.CustomDatabaseFolder = previousDatabase;
			SettingsFile.SetSettingsPath(null);
			try { Directory.Delete(folder, true); } catch { }
		}
	}

	static void Released(MainWindowVM vm, AutomationSettingsVM draft) {
		Assert.False(vm.IsBusy);
		Assert.False(vm.IsBusyCancelable);
		Assert.False(vm.IsAutomationWorking);
		Assert.False(vm.HasActiveScanRun);
		Assert.False(draft.IsWorking);
		Assert.Equal(1, HostField<SemaphoreSlim>(vm, "cleanupOperationGate").CurrentCount);
		Assert.Equal(1, HostField<SemaphoreSlim>(vm, "automationWorkGate").CurrentCount);
	}

	[Fact]
	public Task ModelessRestoreReservesHostRefusesConcurrentCallerAndAllowsRetry() => HeadlessUi.Run(() => WithSettings(async (_, vm, record) => {
		var first = new AutomationSettingsVM(vm);
		var second = new AutomationSettingsVM(vm);
		byte[] originalHistory = File.ReadAllBytes(vm.QuarantineHistoryFile);
		var gate = CoreGate;
		await gate.WaitAsync();
		Task restore;
		try {
			restore = first.RestoreQuarantineCommand.Execute(record).ToTask();
			HeadlessUi.Pump();
			Assert.True(vm.IsBusy);
			Assert.True(vm.IsAutomationWorking);
			Assert.True(vm.HasActiveScanRun);
			Assert.True(vm.IsBusyCancelable);
			await second.RestoreQuarantineCommand.Execute(record).ToTask();
			Assert.Contains("before restoring a file", second.Status);
			Assert.False(second.IsWorking);
			await vm.StartScanCommand.Execute("FullScan").ToTask();
			await vm.PreviewQuarantineCommand.Execute().ToTask();
			await vm.DeleteCheckedItemsCommand.Execute().ToTask();
			HeadlessUi.Pump();
			await vm.LoadStartupStateAsync();
			Assert.False(vm.IsStartupLoading);
			Assert.True(vm.IsBusy);
			Assert.False(File.Exists(record.OriginalPath));
			Assert.Equal(originalHistory, File.ReadAllBytes(vm.QuarantineHistoryFile));
			await Assert.ThrowsAsync<InvalidOperationException>(() => vm.CreateQuarantinePreviewAsync());
		}
		finally { gate.Release(); }
		await restore.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.StartsWith("Restored:", first.Status);
		Assert.Equal("synthetic media bytes", File.ReadAllText(record.OriginalPath));
		Released(vm, first);
		await second.RestoreQuarantineCommand.Execute(record).ToTask();
		Assert.StartsWith("Restored:", second.Status);
		Released(vm, second);
	}));

	[Theory]
	[InlineData("scan")]
	[InlineData("cleanup")]
	[InlineData("automation")]
	[InlineData("busy")]
	[InlineData("scanning")]
	[InlineData("thumbnails")]
	public Task ReservedOperationRefusesActualRestoreCommandAndCanBeRetried(string operation) => HeadlessUi.Run(() => WithSettings(async (_, vm, record) => {
		var draft = new AutomationSettingsVM(vm);
		var scan = HostField<ScanRunGate>(vm, "scanRunGate");
		SemaphoreSlim? gate = operation switch {
			"cleanup" => HostField<SemaphoreSlim>(vm, "cleanupOperationGate"),
			"automation" => HostField<SemaphoreSlim>(vm, "automationWorkGate"),
			_ => null
		};
		if (gate != null) await gate.WaitAsync();
		if (operation == "scan") Assert.True(scan.TryBegin());
		if (operation == "busy") vm.IsBusy = true;
		if (operation == "scanning") vm.IsScanning = true;
		if (operation == "thumbnails") vm.ShowThumbnailRetrievalProgressBar = true;
		try {
			await draft.RestoreQuarantineCommand.Execute(record).ToTask();
			Assert.Contains("before restoring a file", draft.Status);
			Assert.False(File.Exists(record.OriginalPath));
			Assert.False(draft.IsWorking);
			if (operation == "scan") Assert.True(vm.HasActiveScanRun);
			if (operation == "busy") Assert.True(vm.IsBusy);
		}
		finally {
			gate?.Release();
			if (operation == "scan") scan.Complete();
			vm.IsBusy = false;
			vm.IsScanning = false;
			vm.ShowThumbnailRetrievalProgressBar = false;
		}
		await draft.RestoreQuarantineCommand.Execute(record).ToTask();
		Assert.StartsWith("Restored:", draft.Status);
		Released(vm, draft);
	}));

	[Fact]
	public Task StartupQueuedInRecoveryRefusesRestoreBeforeChangingMedia() => HeadlessUi.Run(() => WithSettings(async (_, vm, record) => {
		var draft = new AutomationSettingsVM(vm);
		var gate = CoreGate;
		await gate.WaitAsync();
		Task startup;
		try {
			startup = vm.LoadStartupStateAsync();
			Assert.True(vm.IsStartupLoading);
			Assert.True(vm.IsBusy);
			await draft.RestoreQuarantineCommand.Execute(record).ToTask();
			Assert.Contains("before restoring a file", draft.Status);
			Assert.False(File.Exists(record.OriginalPath));
			Assert.False(draft.IsWorking);
		}
		finally { gate.Release(); }
		await startup.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.False(vm.IsStartupLoading);
		await draft.RestoreQuarantineCommand.Execute(record).ToTask();
		Assert.StartsWith("Restored:", draft.Status);
		Released(vm, draft);
	}));

	[Fact]
	public Task BusyCancelAndRestoreFailureReleaseAllReservationsForRetry() => HeadlessUi.Run(() => WithSettings(async (_, vm, record) => {
		var draft = new AutomationSettingsVM(vm);
		var gate = CoreGate;
		await gate.WaitAsync();
		try {
			Task cancelled = draft.RestoreQuarantineCommand.Execute(record).ToTask();
			HeadlessUi.Pump();
			Assert.True(vm.IsBusyCancelable);
			await vm.CancelBusyCommand.Execute().ToTask();
			await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Equal("Restore cancelled.", draft.Status);
			Assert.False(File.Exists(record.OriginalPath));
			Released(vm, draft);
		}
		finally { gate.Release(); }
		File.WriteAllText(record.OriginalPath, "unrelated occupied path");
		await draft.RestoreQuarantineCommand.Execute(record).ToTask();
		Assert.StartsWith("Restore failed:", draft.Status);
		Assert.Equal("unrelated occupied path", File.ReadAllText(record.OriginalPath));
		Released(vm, draft);
		File.Delete(record.OriginalPath);
		await draft.RestoreQuarantineCommand.Execute(record).ToTask();
		Assert.StartsWith("Restored:", draft.Status);
		Released(vm, draft);
	}));
}
