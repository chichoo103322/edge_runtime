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
    /// 核心职责：
    /// 1. 初始化设备列表UI
    /// 2. 检测相机设备是否在线
    /// 3. 更新设备状态显示（在线/离线/运行中）
    /// 4. 区分正在使用的设备和空闲设备
    /// </summary>
    public class DeviceStatusMonitor
    {
        private readonly ObservableCollection<DeviceViewModel> _deviceList;
        private readonly Func<bool> _isCameraBusy;
        private readonly Func<string> _getCurrentCameraId;
        private readonly Func<int?> _getCurrentCameraIndex;

        // 设备状态颜色定义
        private readonly Brush STATUS_ONLINE = Brushes.LightGreen;  // 绿色 - 在线
        private readonly Brush STATUS_OFFLINE = Brushes.Red;        // 红色 - 离线/占用
        private readonly Brush STATUS_CHECKING = Brushes.Orange;    // 橙色 - 检测中

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="deviceList">设备列表（UI绑定的集合）</param>
        /// <param name="isCameraBusy">判断相机是否正在使用的函数</param>
        /// <param name="getCurrentCameraId">获取当前相机ID的函数</param>
        /// <param name="getCurrentCameraIndex">获取当前相机索引的函数</param>
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
        /// 初始化设备列表（从工作流配置中提取的相机ID）
        /// </summary>
        /// <param name="cameraIds">相机ID集合</param>
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
        /// 检测所有设备的在线状态（异步）
        /// 注意：相机可能正在被其他程序占用，此时会显示"离线/占用"
        /// </summary>
        /// <returns>异步任务</returns>
        public Task CheckAllDevicesAsync()
        {
            return Task.Run(() =>
            {
                foreach (var device in _deviceList)
                {
                    // 步骤1: 更新状态为"正在连接..."
                    UpdateDeviceStatus(device, "正在连接...", STATUS_CHECKING);

                    // 步骤2: 检测设备是否在线
                    bool isOnline = CheckDeviceOnline(device.DeviceId);

                    // 步骤3: 更新最终状态
                    UpdateFinalDeviceStatus(device, isOnline);
                }
            });
        }

        /// <summary>
        /// 检测单个设备是否在线
        /// </summary>
        /// <param name="deviceId">设备ID（可以是索引"0"或DirectShow名称"USB Camera"）</param>
        /// <returns>是否在线</returns>
        private bool CheckDeviceOnline(string deviceId)
        {
            bool isOnline = false;

            try
            {
                // 情况1: DeviceId是数字索引（如"0", "1"）
                if (int.TryParse(deviceId, out int camIndex))
                {
                    isOnline = TestCameraByIndex(camIndex);
                }
                else
                {
                    // 情况2: DeviceId是DirectShow设备名称（如"USB Camera"）
                    isOnline = TestCameraByName(deviceId);

                    // 如果按名称打开失败，尝试通过CameraHelper映射到索引再打开
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
        /// 通过索引测试相机是否可打开
        /// </summary>
        /// <param name="index">相机索引</param>
        /// <returns>是否可打开</returns>
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
        /// 通过DirectShow名称测试相机是否可打开
        /// </summary>
        /// <param name="name">DirectShow设备名称</param>
        /// <returns>是否可打开</returns>
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
        /// <param name="device">设备视图模型</param>
        /// <param name="isOnline">是否在线</param>
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
                    // 如果当前主相机正在运行且就是此设备，则标记为"运行中"
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
        /// <param name="deviceId">设备ID</param>
        /// <returns>是否为当前设备</returns>
        private bool IsCurrentDevice(string deviceId)
        {
            // 如果相机没在运行，直接返回false
            if (!_isCameraBusy())
                return false;

            string currentCameraId = _getCurrentCameraId();
            int? currentCameraIndex = _getCurrentCameraIndex();

            // 匹配1: 按名称匹配
            if (!string.IsNullOrEmpty(currentCameraId) &&
                deviceId.Equals(currentCameraId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 匹配2: 按索引匹配
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
        /// <param name="device">设备视图模型</param>
        /// <param name="status">状态文本</param>
        /// <param name="color">状态颜色</param>
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
