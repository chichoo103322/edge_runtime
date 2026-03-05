using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace edge_runtime
{
    /// <summary>
    /// 将日志等级转换为对应的颜色
    /// </summary>
    [ValueConversion(typeof(string), typeof(SolidColorBrush))]
    public class LevelToColorConverter : MarkupExtension, IValueConverter
    {
        private static LevelToColorConverter _instance;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return _instance ??= new LevelToColorConverter();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string level)
            {
                return level switch
                {
                    "INFO" => new SolidColorBrush(Color.FromRgb(100, 200, 100)),    // 绿色
                    "WARNING" => new SolidColorBrush(Color.FromRgb(255, 200, 87)),  // 橙色
                    "ERROR" => new SolidColorBrush(Color.FromRgb(239, 83, 80)),     // 红色
                    _ => new SolidColorBrush(Color.FromRgb(170, 170, 170))          // 灰色
                };
            }
            return new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
