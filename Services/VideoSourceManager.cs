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
    /// 视频源管理器 - 统一管理相机和视频文件两种源
    /// 支持在相机和视频文件之间动态切换
    /// </summary>
    public class VideoSourceManager : IDisposable
    {
        /// <summary>
        /// 视频源类型枚举
        /// </summary>
        public enum VideoSourceType
        {
            Camera,     // 相机源
            VideoFile   // 视频文件源
        }

        private VideoCapture _capture;
        private CancellationTokenSource _cts;
        private readonly Action<BitmapSource> _onFrameReceived;
        private readonly Action<Mat> _onFrameProcessing;
        private VideoSourceType _currentSourceType = VideoSourceType.Camera;
        private string _currentVideoFile;
        private bool _isLooping = false;

        /// <summary>
        /// 当前源类型
        /// </summary>
        public VideoSourceType CurrentSourceType => _currentSourceType;

        /// <summary>
        /// 当前视频文件路径
        /// </summary>
        public string CurrentVideoFile => _currentVideoFile;

        /// <summary>
        /// 当前正在使用的相机ID（DirectShow设备名称）
        /// </summary>
        public string CurrentCameraId { get; private set; }

        /// <summary>
        /// 当前正在使用的相机索引（0, 1, 2...）
        /// </summary>
        public int? CurrentCameraIndex { get; private set; }

        /// <summary>
        /// 视频源是否正在运行
        /// </summary>
        public bool IsRunning => _capture?.IsOpened() ?? false;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="onFrameReceived">帧接收回调 - 用于UI显示</param>
        /// <param name="onFrameProcessing">帧处理回调 - 用于AI推理</param>
        public VideoSourceManager(Action<BitmapSource> onFrameReceived, Action<Mat> onFrameProcessing)
        {
            _onFrameReceived = onFrameReceived ?? throw new ArgumentNullException(nameof(onFrameReceived));
            _onFrameProcessing = onFrameProcessing;
        }

        /// <summary>
        /// 启动相机源
        /// </summary>
        /// <param name="cameraId">相机设备ID（DirectShow名称或索引字符串）</param>
        /// <param name="cameraIndex">相机索引（如果cameraId无法打开时的备用方案）</param>
        /// <returns>异步任务</returns>
        public Task StartCameraAsync(string cameraId, int? cameraIndex)
        {
            CurrentCameraId = cameraId;
            CurrentCameraIndex = cameraIndex;
            _currentSourceType = VideoSourceType.Camera;
            _currentVideoFile = null;

            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            return Task.Run(() => MonitoringLoop(token), token);
        }

        /// <summary>
        /// 启动视频文件源
        /// </summary>
        /// <param name="videoFilePath">视频文件路径</param>
        /// <param name="isLooping">是否循环播放</param>
        /// <returns>异步任务</returns>
        public Task StartVideoFileAsync(string videoFilePath, bool isLooping = false)
        {
            if (string.IsNullOrEmpty(videoFilePath))
                throw new ArgumentException("视频文件路径不能为空", nameof(videoFilePath));

            _currentSourceType = VideoSourceType.VideoFile;
            _currentVideoFile = videoFilePath;
            _isLooping = isLooping;

            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            return Task.Run(() => MonitoringLoop(token), token);
        }

        /// <summary>
        /// 停止视频源
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }

        /// <summary>
        /// 视频监控主循环
        /// </summary>
        /// <param name="token">取消令牌</param>
        private void MonitoringLoop(CancellationToken token)
        {
            try
            {
                // 步骤1: 根据源类型打开相应的源
                if (_currentSourceType == VideoSourceType.Camera)
                {
                    if (!TryOpenCamera(CurrentCameraId, CurrentCameraIndex))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                            MessageBox.Show($"无法打开指定的摄像头。\n相机ID: {CurrentCameraId}\n请检查相机连接或配置。")
                        );
                        return;
                    }
                }
                else // VideoFile
                {
                    if (!TryOpenVideoFile(_currentVideoFile))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                            MessageBox.Show($"无法打开视频文件。\n路径: {_currentVideoFile}\n请检查文件是否存在或格式是否支持。")
                        );
                        return;
                    }
                }

                if (!_capture.IsOpened())
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        MessageBox.Show("无法打开视频源，请检查设置")
                    );
                    return;
                }

                // 步骤2: 配置源参数
                _capture.Set(VideoCaptureProperties.BufferSize, 1);

                // 步骤3: 等待就绪
                WaitForSourceReady(token);

                // 步骤4: 开始读帧循环
                using (Mat frame = new Mat())
                {
                    while (!token.IsCancellationRequested)
                    {
                        // 读取一帧
                        if (!_capture.Read(frame) || frame.Empty())
                        {
                            // 如果是视频文件且启用循环，则从头开始播放
                            if (_currentSourceType == VideoSourceType.VideoFile && _isLooping)
                            {
                                _capture.Set(VideoCaptureProperties.PosFrames, 0);
                                Thread.Sleep(10);
                                continue;
                            }

                            Thread.Sleep(10);
                            continue;
                        }

                        // 步骤1: 先触发 AI 推理处理帧
                        // 在此过程中 WorkflowExecutor 会直接在 frame 上绘制检测框和置信度
                        _onFrameProcessing?.Invoke(frame);

                        // 步骤2: 再将被画过框的 frame 转换为 BitmapSource 交给 UI 显示
                        // 这样 UI 上显示的画面就包含了 AI 检测结果的可视化
                        var bitmap = frame.ToBitmapSource();
                        bitmap.Freeze(); // 冻结以便跨线程传递
                        Application.Current?.Dispatcher.Invoke(() => _onFrameReceived(bitmap));

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
        /// 尝试打开视频文件
        /// </summary>
        /// <param name="videoFilePath">视频文件路径</param>
        /// <returns>是否成功打开</returns>
        private bool TryOpenVideoFile(string videoFilePath)
        {
            try
            {
                _capture = new VideoCapture(videoFilePath);
                if (_capture?.IsOpened() == true)
                    return true;
            }
            catch { /* 继续尝试其他方式 */ }

            return false;
        }

        /// <summary>
        /// 等待视频源就绪（最多1秒）
        /// </summary>
        /// <param name="token">取消令牌</param>
        private void WaitForSourceReady(CancellationToken token)
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
