// SPDX-License-Identifier: AGPL-3.0-or-later
namespace VDF.Core;

/// <summary>Optional failure details on the existing ScanAborted event. Cancellation has no error.</summary>
public sealed class ScanAbortedEventArgs(Exception? error = null) : EventArgs {
    public Exception? Error { get; } = error;
}
