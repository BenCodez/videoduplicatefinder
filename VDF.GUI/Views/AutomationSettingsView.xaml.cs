using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views;

public partial class AutomationSettingsView : Window {
	public AutomationSettingsView() : this(null) { }
	public AutomationSettingsView(MainWindowVM? host) {
		AvaloniaXamlLoader.Load(this);
		DataContext = new AutomationSettingsVM(host);
		Utils.Appearance.Attach(this);
		Opened += async (_, _) => await ViewModel.ReloadHistoryAsync();
	}
	public AutomationSettingsVM ViewModel => (AutomationSettingsVM)DataContext!;
	async void Save_Click(object? sender, RoutedEventArgs e) { if (await ViewModel.SaveAsync()) Close(); }
	void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
