using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace BetterGenshinImpact.View.Converters;

[ValueConversion(typeof(KeyValuePair<string, string>), typeof(string))]
class CultureInfoNameToKVPConverter : IValueConverter
{
    public static string GetDisplayName(string cultureInfoName)
    {
        return "English";
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return null;
        }

        return new KeyValuePair<string, string>("en", "English");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return "en";
    }
}
