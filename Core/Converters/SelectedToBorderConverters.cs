using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AudioMixerWin.Core.Converters;

public class SelectedToBorderBrushConverter : IValueConverter
{
    // Mint accent when selected; the standard hairline stroke otherwise, so
    // selection never adds/removes a border (which would shift layout).
    private static readonly SolidColorBrush Selected =
        new(Color.FromArgb(0xFF, 0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush None =
        new(Color.FromArgb(0xFF, 0x26, 0x26, 0x2C));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Selected : None;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public class SelectedToBorderThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? new Thickness(2) : new Thickness(1);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
