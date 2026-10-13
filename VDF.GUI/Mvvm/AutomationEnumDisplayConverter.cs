using System.Globalization;
using Avalonia.Data.Converters;
using VDF.Core.Selection;

namespace VDF.GUI.Mvvm;

public sealed class AutomationEnumDisplayConverter : IValueConverter {
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch {
		SelectionProfile.FolderPriorityFirst => "Folder priority first", SelectionProfile.QualityFirst => "Quality first",
		SelectionProfile.NewestFirst => "Newest first", SelectionProfile.OldestFirst => "Oldest first", SelectionProfile.Custom => "Custom criteria",
		SelectionMode.StrictFolderPriority => "Strict folder priority", SelectionMode.Weighted => "Weighted score",
		SelectionDateSource.Modification => "Modification date", SelectionDateSource.Creation => "Creation date", SelectionDateSource.Embedded => "Embedded media date",
		FolderOverlapPolicy.MostSpecific => "Most specific folder", FolderOverlapPolicy.HighestPriority => "Highest matching priority",
		_ => value?.ToString() ?? string.Empty
	};
	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
