using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels;

public partial class MainWindowVM {
    public Task ApplyLegacyCustomSelectionAsync(CustomSelectionData data) => RunLegacySelectorAsync(() => RunCustomSelection(data));

    async Task RunLegacySelectorAsync(Action apply) {
        if (IsBusy || IsScanning || HasActiveScanRun || IsAutomationWorking) return;
        await automationWorkGate.WaitAsync();
        IsAutomationWorking = true;
        try {
            await BuildAutomationCandidatesAsync();
            apply();
            await SaveAutomationReviewRecordsAsync();
        }
        catch (Exception ex) { await MessageBoxService.Show($"Selection could not complete: {ex.Message}"); }
        finally { IsAutomationWorking = false; automationWorkGate.Release(); }
    }
}
