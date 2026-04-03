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
    /// </summary>
    public class CameraManager : IDisposable
    {
        private VideoCapture _capture;
        private CancellationTokenSource _cts;
        private readonly Action<BitmapSource> _onFrameReceived;
        private readonly Action<Mat> _onFrameProcessing;

        public string CurrentCameraId { get; private set; }
        public int? CurrentCameraIndex { get; private set; }
        public bool IsRunning => _capture?.IsOpened() ?? false;

        public CameraManager(Action<BitmapSource> onFrameReceived, Action<Mat> onFrameProcessing)
        {
            _onFrameReceived = onFrameReceived ?? throw new ArgumentNullException(nameof(onFrameReceived));
            _onFrameProcessing = onFrameProcessing;
        }

        /// <summary>
        /// 启动相机监控循环
        /// </summary>
        public Task StartAsync(string cameraId, int? cameraIndex)
        {
            CurrentCameraId = cameraId;
            CurrentCameraIndex = cameraIndex;

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

        private void MonitoringLoop(CancellationToken token)
        {
            try
            {
                // 尝试打开相机
                if (!TryOpenCamera(CurrentCameraId, CurrentCameraIndex))
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        MessageBox.Show($"无法打开指定的摄像头。\n相机ID: {CurrentCameraId}\n请检查相机连接或配置。")
                    );
                    return;
                }

                _capture.Set(VideoCaptureProperties.BufferSize, 1);

                // 等待相机就绪
                WaitForCameraReady(token);

                if (!_capture.IsOpened())
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        MessageBox.Show("无法打开主摄像头，请检查相机设置")
                    );
                    return;
                }

                // 读帧循环
                using (Mat frame = new Mat())
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!_capture.Read(frame) || frame.Empty())
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        // 显示帧
                        var bitmap = frame.ToBitmapSource();
                        bitmap.Freeze();
                        Application.Current?.Dispatcher.Invoke(() => _onFrameReceived(bitmap));

                        // 处理帧
                        _onFrameProcessing?.Invoke(frame);

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
                _capture?.Release();
                _capture?.Dispose();
                _capture = null;
            }
        }

        private bool TryOpenCamera(string cameraId, int? cameraIndex)
        {
            bool opened = false;

            // 优先按名称打开
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

            // 尝试用索引打开
            if (cameraIndex.HasValue && cameraIndex.Value >= 0)
            {
                _capture = new VideoCapture(cameraIndex.Value);
                if (_capture?.IsOpened() == true)
                    return true;
            }

            return false;
        }

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

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
