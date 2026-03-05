using DirectShowLib;
using System;
using System.Collections.Generic;

namespace edge_runtime
{
    /// <summary>
    /// 摄像头硬件辅助类
    /// 使用 DirectShowLib 库遍历系统视频设备
    /// </summary>
    public class CameraHelper
    {
    /// <summary>
        /// 根据设备名称获取摄像头索引
        /// </summary>
        /// <param name="deviceName">摄像头设备名称</param>
        /// <returns>设备索引；如果未找到，返回 -1</returns>
        public static int GetCameraIndexByName(string deviceName)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceName))
                    return -1;

                DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);

                if (devices == null || devices.Length == 0)
                    return -1;

                for (int i = 0; i < devices.Length; i++)
                {
                    if (devices[i].Name.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return i;
                    }
                }

                // 未找到匹配的设备，返回 -1 表示未找到
                return -1;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"获取摄像头列表失败: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 获取所有可用的摄像头设备列表
        /// </summary>
        /// <returns>摄像头名称列表</returns>
        public static List<string> GetAllCameraDevices()
        {
            var cameras = new List<string>();

            try
            {
                DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);

                if (devices != null)
                {
                    for (int i = 0; i < devices.Length; i++)
                    {
                        cameras.Add($"[{i}] {devices[i].Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"枚举摄像头设备失败: {ex.Message}");
            }

            return cameras;
        }
    }
}
