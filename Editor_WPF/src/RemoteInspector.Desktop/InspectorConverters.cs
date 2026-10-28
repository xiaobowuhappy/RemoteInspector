using System.Globalization;
using System.Windows.Data;

namespace RemoteInspector.Desktop;

public sealed class StringBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => bool.TryParse(value as string, out bool result) && result;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => (value is true).ToString();
}
public sealed class ComponentTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string name = value?.ToString() ?? "";
        int marker = name.LastIndexOf(" [", StringComparison.Ordinal);
        
        string type = marker < 0 ? name : name[..marker];
        string title = type.Split('.').Last();
        return marker >= 0 && int.TryParse(name[(marker + 2)..].TrimEnd(']'), out int index) && index > 0 ? title + $"（第{index + 1}个）" : title;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class GroupRowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        object item = value;
        while (item is CollectionViewGroup group) item = group.Items.FirstOrDefault();
        return item;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
