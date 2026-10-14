using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using VDF.Core.ViewModels;

namespace VDF.GUI.ViewModels;

public partial class MainWindowVM {
	public ReactiveCommand<Unit, Unit> LoadThumbnailsForCheckedItemsCommand => ReactiveCommand.CreateFromTask(
		() => ReloadManualThumbnailsAsync(Duplicates.Where(row => row.Checked).Select(row => row.ItemInfo)), CanStartScan);

	public ReactiveCommand<Unit, Unit> LoadThumbnailsForGroupCommand => ReactiveCommand.CreateFromTask(() => {
		if (GetSelectedDuplicateItem() is not DuplicateItemVM selected) return Task.CompletedTask;
		return ReloadManualThumbnailsAsync(Duplicates.Where(row => row.ItemInfo.GroupId == selected.ItemInfo.GroupId)
			.Select(row => row.ItemInfo));
	}, CanStartScan);

	/// <summary>
	/// Manual reloads own the busy state until all decoder work has drained. Import
	/// and startup already own that state and continue calling Core directly.
	/// </summary>
	async Task ReloadManualThumbnailsAsync(IEnumerable<DuplicateItem> requested) {
		if (ScanOrDataWorkActive) return;
		var items = requested.ToList();
		if (items.Count == 0) return;
		string previousOverlayText = IsBusyOverlayText;
		// Reserve synchronously: the decoder's first await must not admit a scan
		// that changes FFmpeg settings or replaces the active thumbnail cache.
		IsBusy = true;
		ShowThumbnailRetrievalProgressBar = true;
		try {
			IsBusyOverlayText = App.Lang["Busy.LoadMissingThumbnails"];
			SyncCoreSettings();
			await Scanner.RetrieveThumbnailsForItems(items);
		}
		finally {
			ShowThumbnailRetrievalProgressBar = false;
			IsBusyOverlayText = previousOverlayText;
			IsBusy = false;
		}
	}
}
