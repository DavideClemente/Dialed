using System;
using Microsoft.UI.Xaml.Data;

namespace AudioMixerWin.Core.Converters;

public class ProcessNameDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string processName ? AudioManager.GetDisplayName(processName) : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
