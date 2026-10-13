namespace VDF.Core.Tests;

/// <summary>Report unsupported Windows path/native-volume scenarios as skipped, never as passed.</summary>
public sealed class WindowsFactAttribute : FactAttribute {
	public WindowsFactAttribute() {
		if (!OperatingSystem.IsWindows())
			Skip = "Requires Windows drive-letter, UNC, or native volume semantics.";
	}
}

public sealed class UnixFactAttribute : FactAttribute {
	public UnixFactAttribute() {
		if (OperatingSystem.IsWindows())
			Skip = "Requires Unix absolute paths and mount-root semantics.";
	}
}
