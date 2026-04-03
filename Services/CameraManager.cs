using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace edge_runtime.Services
{
    /// <summary>
    /// 相机管理服务 - 负责相机的打开、读取和状态管理
    /// 封装OpenCvSharp VideoCapture的底层操作，提供统一的相机访问接口
    /// </summary>
    public class CameraManager : IDisposable
    {
        private VideoCapture _capture;
        private CancellationTokenSource _cts;
        private readonly Action<BitmapSource> _onFrameReceived;
        private readonly Action<Mat> _onFrameProcessing;

        /// <summary>
        /// 当前正在使用的相机ID（DirectShow设备名称）
        /// </summary>
        public string CurrentCameraId { get; private set; }

        /// <summary>
        /// 当前正在使用的相机索引（0, 1, 2...）
        /// </summary>
        public int? CurrentCameraIndex { get; private set; }

        /// <summary>
        /// 相机是否正在运行
        /// </summary>
        public bool IsRunning => _capture?.IsOpened() ?? false;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="onFrameReceived">帧接收回调 - 用于UI显示</param>
        /// <param name="onFrameProcessing">帧处理回调 - 用于AI推理</param>
        public CameraManager(Action<BitmapSource> onFrameReceived, Action<Mat> onFrameProcessing)
        {
            _onFrameReceived = onFrameReceived ?? throw new ArgumentNullException(nameof(onFrameReceived));
            _onFrameProcessing = onFrameProcessing;
        }

        /// <summary>
        /// 启动相机监控循环（异步）
        /// </summary>
        /// <param name="cameraId">相机设备ID（DirectShow名称或索引字符串）</param>
        /// <param name="cameraIndex">相机索引（如果cameraId无法打开时的备用方案）</param>
        /// <returns>异步任务</returns>
        public Task StartAsync(string cameraId, int? cameraIndex)
        {
            CurrentCameraId = cameraId;
            CurrentCameraIndex = cameraIndex;

            // 停止旧的监控循环
            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            return Task.Run(() => MonitoringLoop(token), token);
        }

        /// <summary>
        /// 停止相机监控
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }

        /// <summary>
        /// 相机监控主循环
        /// </summary>
        /// <param name="token">取消令牌</param>
        private void MonitoringLoop(CancellationToken token)
        {
            try
            {
                // 步骤1: 尝试打开相机
                if (!TryOpenCamera(CurrentCameraId, CurrentCameraIndex))
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        MessageBox.Show($"无法打开指定的摄像头。\n相机ID: {CurrentCameraId}\n请检查相机连接或配置。")
                    );
                    return;
                }

                // 步骤2: 配置相机参数（减少缓冲延迟）
                _capture.Set(VideoCaptureProperties.BufferSize, 1);

                // 步骤3: 等待相机就绪
                WaitForCameraReady(token);

                if (!_capture.IsOpened())
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        MessageBox.Show("无法打开主摄像头，请检查相机设置")
                    );
                    return;
                }

                // 步骤4: 开始读帧循环
                using (Mat frame = new Mat())
                {
                    while (!token.IsCancellationRequested)
                    {
                        // 读取一帧
                        if (!_capture.Read(frame) || frame.Empty())
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        // 转换为WPF BitmapSource用于显示
                        var bitmap = frame.ToBitmapSource();
                        bitmap.Freeze(); // 冻结以便跨线程传递
                        Application.Current?.Dispatcher.Invoke(() => _onFrameReceived(bitmap));

                        // 处理帧（AI推理）
                        _onFrameProcessing?.Invoke(frame);

                        // 控制帧率（约33fps）
                        Thread.Sleep(30);
                    }
                }
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.Invoke(() =>
                    MessageBox.Show($"监控循环错误: {ex.Message}")
                );
            }
            finally
            {
                // 清理资源
                _capture?.Release();
                _capture?.Dispose();
                _capture = null;
            }
        }

        /// <summary>
        /// 尝试打开相机（优先级：名称 > 索引）
        /// </summary>
        /// <param name="cameraId">相机设备ID</param>
        /// <param name="cameraIndex">相机索引</param>
        /// <returns>是否成功打开</returns>
        private bool TryOpenCamera(string cameraId, int? cameraIndex)
        {
            // 方法1: 按DirectShow设备名称打开
            if (!string.IsNullOrEmpty(cameraId))
            {
                try
                {
                    _capture = new VideoCapture($"video={cameraId}", VideoCaptureAPIs.DSHOW);
                    if (_capture?.IsOpened() == true)
                        return true;
                }
                catch { /* 继续尝试其他方式 */ }
            }

            // 方法2: 按索引打开
            if (cameraIndex.HasValue && cameraIndex.Value >= 0)
            {
                _capture = new VideoCapture(cameraIndex.Value);
                if (_capture?.IsOpened() == true)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 等待相机就绪（最多1秒）
        /// </summary>
        /// <param name="token">取消令牌</param>
        private void WaitForCameraReady(CancellationToken token)
        {
            int retries = 0;
            const int maxRetries = 10;

            while (!_capture.IsOpened() && retries < maxRetries && !token.IsCancellationRequested)
            {
                retries++;
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
