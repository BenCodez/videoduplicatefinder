using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using VDF.Core.Review;
using VDF.Core.Utils;

namespace VDF.GUI.ViewModels;

public sealed class QuarantinePreviewItem {
	public string Path { get; init; } = string.Empty;
	public string KeeperPath { get; init; } = string.Empty;
	public string Reason { get; init; } = string.Empty;
	public long SizeBytes { get; init; }
	public bool CanQuarantine { get; init; }
	public bool ByteIdentical { get; init; }
	public string Status => !CanQuarantine ? "Blocked" : ByteIdentical ? "Byte-identical" : "Manual review: different bytes";
	public string Size => SizeBytes.BytesToString();
	internal FileIdentity? TargetIdentity { get; init; }
	internal FileIdentity? KeeperIdentity { get; init; }
}

public sealed class QuarantinePreviewVM : ReactiveObject {
	readonly MainWindowVM host;
	public QuarantinePreviewVM(MainWindowVM host, IEnumerable<QuarantinePreviewItem> items, string destination) {
		this.host = host;
		Items = new(items);
		Destination = destination;
	}
	public ObservableCollection<QuarantinePreviewItem> Items { get; }
	public string Destination { get; }
	public string Summary => $"{Items.Count(i => i.CanQuarantine)} files, {Items.Where(i => i.CanQuarantine).Sum(i => i.SizeBytes).BytesToString()}; {Items.Count(i => !i.CanQuarantine)} blocked.";
	public bool CanExecute => !IsWorking && !IsComplete && Items.Any(i => i.CanQuarantine);
	public bool IsComplete { get; private set; }
	public bool IsWorking {
		get;
		private set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(CanExecute)); }
	}
	public string Status {
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	} = string.Empty;
	public ReactiveCommand<Unit, Unit> ExecuteCommand => ReactiveCommand.CreateFromTask(async () => {
		if (!CanExecute) return;
		IsWorking = true;
		try {
			Status = await host.ExecuteQuarantinePreviewAsync(Items.ToList(), Destination);
			IsComplete = true;
		}
		catch (Exception ex) { Status = $"Quarantine failed: {ex.Message}"; }
		finally { IsWorking = false; }
	});
}
