using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views;

public partial class QuarantinePreviewView : Window {
	public QuarantinePreviewView() {
		AvaloniaXamlLoader.Load(this);
		Utils.Appearance.Attach(this);
	}
	public QuarantinePreviewView(QuarantinePreviewVM viewModel) : this() => DataContext = viewModel;
	void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
