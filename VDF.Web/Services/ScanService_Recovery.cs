namespace VDF.Web.Services;

public sealed partial class ScanService {
    sealed class DiscardRequest { public bool Resetting; }
    DiscardRequest? _pendingDiscard;
    string? _recoveryStatus, _recoveryError;

    public bool IsDiscardingResults { get { lock (_operationSync) return _pendingDiscard is not null; } }
    public bool IsDiscardPending { get { lock (_operationSync) return _pendingDiscard is { Resetting: false }; } }
    public string? RecoveryStatus { get { lock (_operationSync) return _recoveryStatus; } }
    public string? RecoveryError { get { lock (_operationSync) return _recoveryError; } }

    void ClearRecoveryMessagesLocked() => _recoveryStatus = _recoveryError = null;
    public void DismissRecoveryMessages() {
        lock (_operationSync) {
            if (_pendingDiscard is not null) return;
            ClearRecoveryMessagesLocked();
        }
        Notify();
    }

    public void RequestDiscardResults() {
        DiscardRequest request;
        lock (_operationSync) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pendingDiscard is not null) return;
            if (OperationActiveLocked && !_scanStarting && !_scanRunActive)
                throw new InvalidOperationException("Wait for the current operation to finish, then discard results.");
            _pendingDiscard = request = new();
            _recoveryError = null;
            _recoveryStatus = "Stopping scan. Results will be discarded after the operation finishes.";
        }
        try { Stop(); }
        catch {
            lock (_operationSync) {
                if (ReferenceEquals(_pendingDiscard, request) && !request.Resetting) _pendingDiscard = null;
            }
            Notify();
            throw;
        }
        Notify();
    }

    public void CancelPendingDiscard() {
        lock (_operationSync) {
            if (_pendingDiscard is not { Resetting: false }) return;
            _pendingDiscard = null;
            _recoveryStatus = "Pending discard cancelled. The scan remains stopped.";
        }
        Notify();
    }

    // State notifications belong to the singleton service, not a page circuit.
    // Reserve the reset before releasing the pending request's admission block.
    void TryCompletePendingDiscard() {
        DiscardRequest request;
        lock (_operationSync) {
            if (_disposed || _pendingDiscard is not { Resetting: false } pending || OperationActiveLocked) return;
            request = pending;
            request.Resetting = true;
            _dataWorkActive = true;
        }
        _ = CompletePendingDiscardAsync(request);
    }

    async Task CompletePendingDiscardAsync(DiscardRequest request) {
        try {
            Notify();
            await ResetResultsCoreAsync();
            lock (_operationSync) _recoveryStatus = "Scan results discarded. Media files and saved settings were kept.";
        }
        catch (Exception ex) {
            lock (_operationSync) {
                _recoveryStatus = null;
                _recoveryError = "Results could not be discarded: " + ex.Message;
            }
        }
        finally {
            lock (_operationSync) {
                _dataWorkActive = false;
                if (ReferenceEquals(_pendingDiscard, request)) _pendingDiscard = null;
            }
            NotifyAutomationChanged();
        }
    }
}
