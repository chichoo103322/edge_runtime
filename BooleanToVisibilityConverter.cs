using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace edge_runtime
{
    // 定义取反的布尔转可见性转换器：如果是最后一个节点(True)，则隐藏箭头(Collapsed)
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue)
                return Visibility.Collapsed; // 是最后一步，不显示箭头
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 普通的布尔到可见性转换器：true => Visible, false => Collapsed
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility vis)
                return vis == Visibility.Visible;
            return DependencyProperty.UnsetValue;
        }
    }
}