using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace NtfsStreamsViewer.ViewModels;

/// <summary>Green when the sidecar is connected, red otherwise.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.LightGreen : Brushes.IndianRed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
