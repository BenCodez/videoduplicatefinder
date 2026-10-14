using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI.HeadlessTests;

public class OfflineResultsContrastTests {
    [Theory]
    [InlineData("Dark", false)]
    [InlineData("Dark", true)]
    [InlineData("Light", false)]
    [InlineData("Light", true)]
    [InlineData("HighContrastDark", false)]
    [InlineData("HighContrastDark", true)]
    [InlineData("HighContrastLight", false)]
    [InlineData("HighContrastLight", true)]
    public Task OfflineStatusRemainsReadableInSelectedAndCheckedRows(string theme, bool isChecked) => HeadlessUi.Run(() => {
        bool highContrast = theme.StartsWith("HighContrast");
        var variant = highContrast ? ContrastTests.HighContrastVariant(theme) : theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var vm = ResultsFixture.CreatePopulatedViewModel();
        // An unrooted unavailable path cannot identify a mounted drive on either OS;
        // the production status deliberately classifies it as offline.
        vm.Duplicates[1].ItemInfo.Path = Path.Combine("unmounted", "offline.mp4");
        vm.Duplicates[1].ItemInfo.Folder = "unmounted";
        Assert.True(vm.Duplicates[1].IsOffline);
        vm.Duplicates[1].Checked = isChecked;
        vm.RebuildResultsList();
        var window = new Window { Width = 1300, Height = 950, RequestedThemeVariant = variant,
            Content = new DuplicateResultsView { DataContext = vm } };
        window.Show();
        HeadlessUi.Pump();
        try {
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ResultsList");
            list.SelectedIndex = 2;
            HeadlessUi.Pump();
            Assert.Equal(isChecked, ((ResultsItemRow)list.SelectedItem!).Item.Checked);
            var failures = ContrastTests.Measure(window, variant, highContrast ? ContrastTests.Enhanced : null);
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { window.Close(); }
    });
}
