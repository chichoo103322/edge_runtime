using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace edge_runtime
{
    // 对应界面上的一张“小卡片”（具体步骤）
    public class ProcessStateViewModel : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }      // 显示文字，如 "比对上料表"
        public string TargetLabel { get; set; } // AI需要识别的目标
        public double Threshold { get; set; }   // 阈值

        // 新增：表示该步骤所属的工位名称
        private string _stationName = "未配置";
        public string StationName
        {
            get => _stationName;
            set { _stationName = value; OnPropertyChanged(); }
        }

        // 新增：该步骤使用的相机ID（和 JSON 的 cameraDevice 对应）
        private string _cameraId = string.Empty;
        public string CameraId
        {
            get => _cameraId;
            set { _cameraId = value; OnPropertyChanged(); }
        }

        private Brush _background = new SolidColorBrush(Color.FromRgb(80, 80, 80)); // 默认深灰色
        public Brush Background
        {
            get => _background;
            set { _background = value; OnPropertyChanged(); }
        }

        private Brush _borderColor = Brushes.Transparent;
        public Brush BorderColor
        {
            get => _borderColor;
            set { _borderColor = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // 对应界面上的一“列”（动作模块）
    public class ProcessActionViewModel
    {
        public string Name { get; set; } // 列标题，如 "穿料"

        // 这一列里包含的所有步骤
        public ObservableCollection<ProcessStateViewModel> States { get; set; }
            = new ObservableCollection<ProcessStateViewModel>();
    }

    // 新增：设备（相机）在 UI 中的视图模型
    public class DeviceViewModel : INotifyPropertyChanged
    {
        private string _deviceId;
        public string DeviceId
        {
            get => _deviceId;
            set { _deviceId = value; OnPropertyChanged(); }
        }

        private string _deviceName;
        public string DeviceName
        {
            get => _deviceName;
            set { _deviceName = value; OnPropertyChanged(); }
        }

        private string _status;
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        private Brush _statusColor = Brushes.Gray;
        public Brush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public DeviceViewModel()
        {
            DeviceId = string.Empty;
            DeviceName = "Unknown";
            Status = "未知";
            StatusColor = Brushes.Gray;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}