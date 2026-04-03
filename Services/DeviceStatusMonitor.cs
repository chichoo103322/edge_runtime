using OpenCvSharp;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace edge_runtime.Services
{
    /// <summary>
    /// 设备状态监控器 - 负责检测和更新设备在线状态
    /// </summary>
    public class DeviceStatusMonitor
    {
        private readonly ObservableCollection<DeviceViewModel> _deviceList;
        private readonly Func<bool> _isCameraBusy;
        private readonly Func<string> _getCurrentCameraId;
        private readonly Func<int?> _getCurrentCameraIndex;

        private readonly Brush STATUS_ONLINE = Brushes.LightGreen;
        private readonly Brush STATUS_OFFLINE = Brushes.Red;
        private readonly Brush STATUS_CHECKING = Brushes.Orange;

        public DeviceStatusMonitor(
            ObservableCollection<DeviceViewModel> deviceList,
            Func<bool> isCameraBusy,
            Func<string> getCurrentCameraId,
            Func<int?> getCurrentCameraIndex)
        {
            _deviceList = deviceList ?? throw new ArgumentNullException(nameof(deviceList));
            _isCameraBusy = isCameraBusy ?? throw new ArgumentNullException(nameof(isCameraBusy));
            _getCurrentCameraId = getCurrentCameraId ?? throw new ArgumentNullException(nameof(getCurrentCameraId));
            _getCurrentCameraIndex = getCurrentCameraIndex ?? throw new ArgumentNullException(nameof(getCurrentCameraIndex));
        }

        /// <summary>
        /// 初始化设备列表
        /// </summary>
        public void InitializeDeviceList(System.Collections.Generic.HashSet<string> cameraIds)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _deviceList.Clear();
                foreach (var camId in cameraIds)
                {
                    _deviceList.Add(new DeviceViewModel
                    {
                        DeviceId = camId,
                        DeviceName = $"Camera {camId}",
                        Status = "等待检测...",
                        StatusColor = STATUS_CHECKING
                    });
                }
            });
        }

        /// <summary>
        /// 检测所有设备的在线状态
        /// </summary>
        public Task CheckAllDevicesAsync()
        {
            return Task.Run(() =>
            {
                foreach (var device in _deviceList)
                {
                    UpdateDeviceStatus(device, "正在连接...", STATUS_CHECKING);

                    bool isOnline = CheckDeviceOnline(device.DeviceId);

                    UpdateFinalDeviceStatus(device, isOnline);
                }
            });
        }

        /// <summary>
        /// 检测单个设备是否在线
        /// </summary>
        private bool CheckDeviceOnline(string deviceId)
        {
            bool isOnline = false;

            try
            {
                // 如果 DeviceId 是数字索引
                if (int.TryParse(deviceId, out int camIndex))
                {
                    isOnline = TestCameraByIndex(camIndex);
                }
                else
                {
                    // 先尝试用 DirectShow 名称直接打开
                    isOnline = TestCameraByName(deviceId);

                    // 如果按名称打开失败，尝试通过映射到索引再打开
                    if (!isOnline)
                    {
                        int mappedIndex = CameraHelper.GetCameraIndexByName(deviceId);
                        if (mappedIndex >= 0)
                        {
                            isOnline = TestCameraByIndex(mappedIndex);
                        }
                    }
                }
            }
            catch
            {
                isOnline = false;
            }

            return isOnline;
        }

        /// <summary>
        /// 通过索引测试相机
        /// </summary>
        private bool TestCameraByIndex(int index)
        {
            try
            {
                using (var tempCap = new VideoCapture(index))
                {
                    tempCap.Set(VideoCaptureProperties.BufferSize, 1);
                    return tempCap.IsOpened();
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 通过名称测试相机
        /// </summary>
        private bool TestCameraByName(string name)
        {
            try
            {
                using (var tempCap = new VideoCapture($"video={name}", VideoCaptureAPIs.DSHOW))
                {
                    tempCap.Set(VideoCaptureProperties.BufferSize, 1);
                    return tempCap.IsOpened();
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 更新设备最终状态
        /// </summary>
        private void UpdateFinalDeviceStatus(DeviceViewModel device, bool isOnline)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (isOnline)
                {
                    device.Status = "在线";
                    device.StatusColor = STATUS_ONLINE;
                }
                else
                {
                    // 如果当前主相机就是此设备，并且相机正在运行，则标记运行中
                    bool isCurrent = IsCurrentDevice(device.DeviceId);

                    if (isCurrent)
                    {
                        device.Status = "运行中";
                        device.StatusColor = STATUS_ONLINE;
                    }
                    else
                    {
                        device.Status = "离线/占用";
                        device.StatusColor = STATUS_OFFLINE;
                    }
                }
            });
        }

        /// <summary>
        /// 判断是否为当前正在使用的设备
        /// </summary>
        private bool IsCurrentDevice(string deviceId)
        {
            if (!_isCameraBusy())
                return false;

            string currentCameraId = _getCurrentCameraId();
            int? currentCameraIndex = _getCurrentCameraIndex();

            // 匹配名称
            if (!string.IsNullOrEmpty(currentCameraId) &&
                deviceId.Equals(currentCameraId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 匹配索引
            if (int.TryParse(deviceId, out int idx) &&
                currentCameraIndex.HasValue &&
                idx == currentCameraIndex.Value)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 更新设备状态（UI线程安全）
        /// </summary>
        private void UpdateDeviceStatus(DeviceViewModel device, string status, Brush color)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                device.Status = status;
                device.StatusColor = color;
            });
        }
    }
}
