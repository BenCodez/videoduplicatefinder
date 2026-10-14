using System.Threading;

namespace VDF.Core.Scheduling;

/// <summary>One reservation covers preflight, hashing, comparison and result processing.</summary>
public sealed class ScanRunGate {
	int active;
	public bool IsActive => Volatile.Read(ref active) != 0;
	public bool TryBegin() => Interlocked.CompareExchange(ref active, 1, 0) == 0;
	public void Complete() => Interlocked.Exchange(ref active, 0);
}
