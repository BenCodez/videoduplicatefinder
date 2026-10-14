using Avalonia.Controls;
using VDF.GUI.Views;

namespace VDF.GUI.HeadlessTests;

public sealed class StartupOwnerVisibilityTests {
    [Fact]
    public Task StartupErrorWaitsForItsOwnerToBecomeVisible() => HeadlessUi.Run(async () => {
        var owner = new Window();
        var ready = MainWindow.WaitForStartupOwnerVisibleAsync(owner);
        Assert.False(ready.IsCompleted);
        try {
            owner.Show();
            HeadlessUi.Pump();
            Assert.True(await ready);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task ClosingTheUnshownOwnerCancelsStartupErrorDisplay() => HeadlessUi.Run(async () => {
        var owner = new Window();
        var ready = MainWindow.WaitForStartupOwnerVisibleAsync(owner);
        owner.Close();
        HeadlessUi.Pump();
        Assert.False(await ready);
    });
}
