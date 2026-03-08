using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace edge_runtime
{
    public partial class VideoProcessorWindow : System.Windows.Window
    {
        private DispatcherTimer _timer;
        private bool _isDragging = false;

        // Recording related
        private VideoWriter _videoWriter;
        private volatile bool _isRecording = false;
        private string _selectedCameraId = string.Empty;
        private int _selectedCameraIndex = -1;
        private CancellationTokenSource _recordingCts;

        // 视频截取相关
        private string _currentVideoPath = string.Empty;
        private TimeSpan _clipStartTime = TimeSpan.Zero;
        private TimeSpan _clipEndTime = TimeSpan.Zero;
        private bool _hasStartMark = false;
        private bool _hasEndMark = false;

        public VideoProcessorWindow()
        {
            InitializeComponent();
            InitializeWindow(string.Empty, -1);
        }

        public VideoProcessorWindow(string cameraId)
        {
            InitializeComponent();
            InitializeWindow(cameraId, -1);
        }

        public VideoProcessorWindow(int cameraIndex)
        {
            InitializeComponent();
            InitializeWindow(string.Empty, cameraIndex);
        }

        public VideoProcessorWindow(string cameraId, int cameraIndex)
        {
            InitializeComponent();
            InitializeWindow(cameraId, cameraIndex);
        }

        private void InitializeWindow(string cameraId, int cameraIndex)
        {
            _selectedCameraId = cameraId;
            _selectedCameraIndex = cameraIndex;

            // 按钮事件
            BtnImportVideo.Click += BtnImportVideo_Click;
            BtnPlay.Click += BtnPlay_Click;
            BtnPause.Click += BtnPause_Click;
            BtnStop.Click += BtnStop_Click;
            BtnStartRecording.Click += BtnStartRecording_Click;
            BtnStopRecording.Click += BtnStopRecording_Click;
            BtnReturnHome.Click += BtnReturnHome_Click;

            // 截取功能按钮事件
            BtnMarkStart.Click += BtnMarkStart_Click;
            BtnMarkEnd.Click += BtnMarkEnd_Click;
            BtnExportClip.Click += BtnExportClip_Click;

            // 初始化计时器（更高频率更新，提高精度）
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            VideoPlayer.MediaOpened += VideoPlayer_MediaOpened;
            VideoPlayer.MediaEnded += VideoPlayer_MediaEnded;

            // 在控件加载完成后订阅 Slider 的 Thumb 事件
            TimelineSlider.Loaded += TimelineSlider_Loaded;
        }

        private void TimelineSlider_Loaded(object sender, RoutedEventArgs e)
        {
            // 获取 Slider 内部的 Thumb 控件
            var thumb = FindVisualChild<Thumb>(TimelineSlider);
            if (thumb != null)
            {
                thumb.DragStarted += Thumb_DragStarted;
                thumb.DragDelta += Thumb_DragDelta;
                thumb.DragCompleted += Thumb_DragCompleted;
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                {
                    return result;
                }
                var foundChild = FindVisualChild<T>(child);
                if (foundChild != null)
                {
                    return foundChild;
                }
            }
            return null;
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
            
            // 强制暂停播放
            try { VideoPlayer.Pause(); } catch { }
        }

        private void Thumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // 拖动过程中实时更新视频位置
            if (!_isDragging) return;

            try
            {
                if (VideoPlayer.Source == null || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                double percent = TimelineSlider.Value / 1000.0;
                var newPos = TimeSpan.FromSeconds(total.TotalSeconds * percent);
                
                // 实时更新视频位置和时间显示
                VideoPlayer.Position = newPos;
                TxtCurrentTime.Text = FormatTimePrecise(newPos);
            }
            catch { /* 忽略异常 */ }
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isDragging = false;
            
            try
            {
                if (VideoPlayer.Source == null || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                double percent = TimelineSlider.Value / 1000.0;
                var newPos = TimeSpan.FromSeconds(total.TotalSeconds * percent);
                VideoPlayer.Position = newPos;
                
                // 拖动完成后保持暂停状态
                VideoPlayer.Pause();
            }
            catch { }
        }

        private void BtnImportVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "视频文件 (*.mp4;*.avi;*.mkv)|*.mp4;*.avi;*.mkv";
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    _currentVideoPath = dlg.FileName;
                    var uri = new Uri(dlg.FileName);
                    VideoPlayer.Source = uri;
                    VideoPlayer.Play();

                    // 重置截取标记
                    ResetClipMarks();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法打开视频: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ResetClipMarks()
        {
            _clipStartTime = TimeSpan.Zero;
            _clipEndTime = TimeSpan.Zero;
            _hasStartMark = false;
            _hasEndMark = false;
            TxtStartTime.Text = "起点: --:--";
            TxtEndTime.Text = "终点: --:--";
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                TxtTotalTime.Text = FormatTimePrecise(total);
            }
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            // 停止并重置进度
            VideoPlayer.Stop();
            TimelineSlider.Value = 0;
            TxtCurrentTime.Text = "00:00.000";
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Play();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Pause();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Stop();
            TimelineSlider.Value = 0;
            TxtCurrentTime.Text = "00:00.000";
        }

        private void BtnReturnHome_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"返回主页失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (VideoPlayer.Source == null || !VideoPlayer.NaturalDuration.HasTimeSpan) return;

                // 如果正在拖动，不更新 Slider
                if (_isDragging) return;

                var position = VideoPlayer.Position;
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                if (total.TotalSeconds <= 0) return;

                // 使用 1000 作为最大值，提高精度
                double percent = (position.TotalSeconds / total.TotalSeconds) * 1000.0;
                TimelineSlider.Value = percent;
                TxtCurrentTime.Text = FormatTimePrecise(position);
            }
            catch { /* 忽略计时器异常 */ }
        }

        private void TimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 点击进度条时强制暂停
            if (!_isDragging)
            {
                try { VideoPlayer.Pause(); } catch { }
            }
        }

        private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            // 点击进度条后跳转到对应位置，保持暂停
            if (!_isDragging)
            {
                try
                {
                    if (VideoPlayer.Source == null || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
                    var total = VideoPlayer.NaturalDuration.TimeSpan;
                    double percent = TimelineSlider.Value / 1000.0;
                    var newPos = TimeSpan.FromSeconds(total.TotalSeconds * percent);
                    VideoPlayer.Position = newPos;
                    TxtCurrentTime.Text = FormatTimePrecise(newPos);
                    
                    // 保持暂停状态
                    VideoPlayer.Pause();
                }
                catch { }
            }
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // 此事件现在主要用于点击进度条时的实时预览（非拖动 Thumb 时）
            // Thumb 拖动时的实时预览由 Thumb_DragDelta 处理
        }

        private string FormatTime(TimeSpan ts)
        {
            return string.Format("{0:D2}:{1:D2}", (int)ts.TotalMinutes, ts.Seconds);
        }

        private string FormatTimePrecise(TimeSpan ts)
        {
            return string.Format("{0:D2}:{1:D2}.{2:D3}", (int)ts.TotalMinutes, ts.Seconds, ts.Milliseconds);
        }

        // Recording logic
        private void BtnStartRecording_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording)
            {
                MessageBox.Show("已经在录制中");
                return;
            }

            // 每次都显示相机选择对话框，让用户选择要录制的相机
            ShowCameraSelectionDialog();
            
            // 如果用户没有选择相机，则返回
            if (string.IsNullOrEmpty(_selectedCameraId) && _selectedCameraIndex < 0)
            {
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Filter = "MP4 视频 (*.mp4)|*.mp4|AVI 视频 (*.avi)|*.avi";
            dlg.DefaultExt = "mp4";
            if (dlg.ShowDialog() == true)
            {
                string savePath = dlg.FileName;
                StartRecording(savePath);
            }
        }

        private void ShowCameraSelectionDialog()
        {
            try
            {
                var cameras = CameraHelper.GetAllCameraDevices();
                if (cameras == null || cameras.Count == 0)
                {
                    MessageBox.Show("未找到可用的摄像头设备", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var cameraWindow = new System.Windows.Window
                {
                    Title = "选择摄像头",
                    Width = 450,
                    Height = 300,
                    Owner = this,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
                };

                var stackPanel = new StackPanel { Margin = new Thickness(10) };

                var label = new System.Windows.Controls.Label { Content = "选择要录制的摄像头：", FontSize = 14 };
                stackPanel.Children.Add(label);

                var listBox = new System.Windows.Controls.ListBox { Height = 200 };
                foreach (var camera in cameras)
                {
                    listBox.Items.Add(camera);
                }

                if (listBox.Items.Count > 0)
                {
                    listBox.SelectedIndex = 0;
                }

                stackPanel.Children.Add(listBox);

                var buttonPanel = new System.Windows.Controls.StackPanel 
                { 
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0)
                };

                var okButton = new System.Windows.Controls.Button 
                { 
                    Content = "确定", 
                    Width = 80, 
                    Height = 30,
                    Margin = new Thickness(0, 0, 10, 0)
                };

                var cancelButton = new System.Windows.Controls.Button 
                { 
                    Content = "取消", 
                    Width = 80, 
                    Height = 30 
                };

                okButton.Click += (s, e) =>
                {
                    if (listBox.SelectedItem is string selectedCamera)
                    {
                        // 从格式 "[0] 相机名称" 中解析索引和名称
                        ParseCameraSelection(selectedCamera);
                        cameraWindow.DialogResult = true;
                    }
                };

                cancelButton.Click += (s, e) => cameraWindow.DialogResult = false;

                buttonPanel.Children.Add(okButton);
                buttonPanel.Children.Add(cancelButton);

                stackPanel.Children.Add(buttonPanel);

                cameraWindow.Content = stackPanel;
                cameraWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"获取摄像头列表失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ParseCameraSelection(string cameraString)
        {
            try
            {
                // 从格式 "[0] 相机名称" 中解析索引
                if (cameraString.StartsWith("[") && cameraString.Contains("]"))
                {
                    int endBracket = cameraString.IndexOf("]");
                    string indexStr = cameraString.Substring(1, endBracket - 1);
                    
                    if (int.TryParse(indexStr, out int index))
                    {
                        _selectedCameraIndex = index;
                        // 提取相机名称（去掉 "[0] " 部分）
                        _selectedCameraId = cameraString.Substring(endBracket + 2).Trim();
                    }
                    else
                    {
                        _selectedCameraIndex = -1;
                        _selectedCameraId = cameraString;
                    }
                }
                else
                {
                    _selectedCameraId = cameraString;
                    _selectedCameraIndex = -1;
                }

                UILogManager.Instance.LogInfo($"选择摄像头: {_selectedCameraId} (索引: {_selectedCameraIndex})");
            }
            catch (Exception ex)
            {
                UILogManager.Instance.LogError($"解析摄像头信息失败: {ex.Message}");
                _selectedCameraIndex = -1;
                _selectedCameraId = string.Empty;
            }
        }

        private void StartRecording(string savePath)
        {
            try
            {
                _recordingCts = new CancellationTokenSource();
                var token = _recordingCts.Token;

                // 切换显示：隐藏 VideoPlayer，显示 CameraPreview
                VideoPlayer.Visibility = Visibility.Collapsed;
                CameraPreview.Visibility = Visibility.Visible;

                Task.Run(() =>
                {
                    VideoCapture cap = null;
                    try
                    {
                        // 使用已解析的相机索引打开摄像头
                        if (_selectedCameraIndex >= 0)
                        {
                            cap = new VideoCapture(_selectedCameraIndex);
                        }
                        else
                        {
                            // 降级到默认相机
                            cap = new VideoCapture(0);
                        }

                        if (!cap?.IsOpened() == true)
                        {
                            Dispatcher.Invoke(() => 
                            {
                                MessageBox.Show($"无法打开摄像头。\n相机: {_selectedCameraId}\n" +
                                              $"索引: {_selectedCameraIndex}\n" +
                                              $"请检查相机连接或配置。");
                                // 恢复显示
                                CameraPreview.Visibility = Visibility.Collapsed;
                                VideoPlayer.Visibility = Visibility.Visible;
                            });
                            return;
                        }

                        cap.Set(VideoCaptureProperties.BufferSize, 1);

                        int fourcc = FourCC.MP4V;
                        double fps = cap.Fps > 0 ? cap.Fps : 25;
                        int width = cap.FrameWidth;
                        int height = cap.FrameHeight;

                        // 初始化 VideoWriter
                        using (var writer = new VideoWriter(savePath, fourcc, fps, new OpenCvSharp.Size(width, height)))
                        {
                            if (!writer.IsOpened())
                            {
                                Dispatcher.Invoke(() => 
                                {
                                    MessageBox.Show("无法初始化视频写入器");
                                    CameraPreview.Visibility = Visibility.Collapsed;
                                    VideoPlayer.Visibility = Visibility.Visible;
                                });
                                return;
                            }

                            _isRecording = true;
                            Dispatcher.Invoke(() => UILogManager.Instance.LogInfo($"开始录制视频 - 摄像头: {_selectedCameraId} (索引: {_selectedCameraIndex})"));

                            var mat = new Mat();
                            while (!token.IsCancellationRequested)
                            {
                                if (!cap.Read(mat) || mat.Empty())
                                {
                                    Thread.Sleep(10);
                                    continue;
                                }

                                // 写入视频文件
                                writer.Write(mat);

                                // 实时显示摄像头画面到 CameraPreview
                                try
                                {
                                    var bitmapSource = mat.ToBitmapSource();
                                    bitmapSource.Freeze(); // 跨线程使用必须 Freeze
                                    Dispatcher.BeginInvoke(() =>
                                    {
                                        CameraPreview.Source = bitmapSource;
                                    }, DispatcherPriority.Render);
                                }
                                catch { /* 忽略显示异常 */ }
                            }

                            mat.Dispose();
                            _isRecording = false;
                            
                            Dispatcher.Invoke(() =>
                            {
                                UILogManager.Instance.LogInfo("录制已停止");
                                // 切换回 VideoPlayer 显示
                                CameraPreview.Visibility = Visibility.Collapsed;
                                VideoPlayer.Visibility = Visibility.Visible;
                                MessageBox.Show($"视频已保存到:\n{savePath}");
                            });
                        }
                    }
                    finally
                    {
                        cap?.Dispose();
                    }
                }, token);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"开始录制失败: {ex.Message}");
                UILogManager.Instance.LogError($"录制异常: {ex.Message}");
                // 恢复显示
                CameraPreview.Visibility = Visibility.Collapsed;
                VideoPlayer.Visibility = Visibility.Visible;
            }
        }

        private void BtnStopRecording_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording)
            {
                MessageBox.Show("当前未在录制");
                return;
            }

            try
            {
                _recordingCts?.Cancel();
                _isRecording = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止录制失败: {ex.Message}");
            }
        }

        // ========== 视频截取功能 ==========

        private void BtnMarkStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VideoPlayer.Source == null)
                {
                    MessageBox.Show("请先导入视频", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _clipStartTime = VideoPlayer.Position;
                _hasStartMark = true;
                TxtStartTime.Text = $"起点: {FormatTime(_clipStartTime)}";
                UILogManager.Instance.LogInfo($"标记截取起点: {FormatTime(_clipStartTime)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"标记起点失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnMarkEnd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VideoPlayer.Source == null)
                {
                    MessageBox.Show("请先导入视频", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _clipEndTime = VideoPlayer.Position;
                _hasEndMark = true;
                TxtEndTime.Text = $"终点: {FormatTime(_clipEndTime)}";
                UILogManager.Instance.LogInfo($"标记截取终点: {FormatTime(_clipEndTime)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"标记终点失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExportClip_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 验证条件
                if (string.IsNullOrEmpty(_currentVideoPath))
                {
                    MessageBox.Show("请先导入视频", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!_hasStartMark || !_hasEndMark)
                {
                    MessageBox.Show("请先标记起点和终点", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (_clipEndTime <= _clipStartTime)
                {
                    MessageBox.Show("终点时间必须大于起点时间", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 选择保存路径
                var dlg = new Microsoft.Win32.SaveFileDialog();
                dlg.Filter = "MP4 视频 (*.mp4)|*.mp4|AVI 视频 (*.avi)|*.avi";
                dlg.DefaultExt = "mp4";
                dlg.FileName = $"clip_{DateTime.Now:yyyyMMdd_HHmmss}";

                if (dlg.ShowDialog() == true)
                {
                    string savePath = dlg.FileName;
                    ExportVideoClip(_currentVideoPath, savePath, _clipStartTime, _clipEndTime);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                UILogManager.Instance.LogError($"导出片段异常: {ex.Message}");
            }
        }

        private void ExportVideoClip(string sourcePath, string outputPath, TimeSpan startTime, TimeSpan endTime)
        {
            try
            {
                // 暂停当前播放
                VideoPlayer.Pause();

                UILogManager.Instance.LogInfo($"开始导出视频片段: {FormatTime(startTime)} - {FormatTime(endTime)}");

                Task.Run(() =>
                {
                    try
                    {
                        using (var capture = new VideoCapture(sourcePath))
                        {
                            if (!capture.IsOpened())
                            {
                                Dispatcher.Invoke(() => MessageBox.Show("无法打开源视频文件", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
                                return;
                            }

                            double fps = capture.Fps > 0 ? capture.Fps : 25;
                            int width = capture.FrameWidth;
                            int height = capture.FrameHeight;
                            int fourcc = FourCC.MP4V;

                            // 定位到起始时间
                            double startMs = startTime.TotalMilliseconds;
                            double endMs = endTime.TotalMilliseconds;
                            capture.Set(VideoCaptureProperties.PosMsec, startMs);

                            using (var writer = new VideoWriter(outputPath, fourcc, fps, new OpenCvSharp.Size(width, height)))
                            {
                                if (!writer.IsOpened())
                                {
                                    Dispatcher.Invoke(() => MessageBox.Show("无法初始化视频写入器", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
                                    return;
                                }

                                var mat = new Mat();
                                int frameCount = 0;
                                int totalFrames = (int)((endMs - startMs) / 1000 * fps);

                                while (true)
                                {
                                    // 检查当前位置是否超过终点
                                    double currentMs = capture.Get(VideoCaptureProperties.PosMsec);
                                    if (currentMs >= endMs)
                                    {
                                        break;
                                    }

                                    if (!capture.Read(mat) || mat.Empty())
                                    {
                                        break;
                                    }

                                    writer.Write(mat);
                                    frameCount++;

                                    // 每100帧更新一次进度
                                    if (frameCount % 100 == 0)
                                    {
                                        int progress = totalFrames > 0 ? (int)((double)frameCount / totalFrames * 100) : 0;
                                        Dispatcher.BeginInvoke(() =>
                                        {
                                            UILogManager.Instance.LogInfo($"导出进度: {Math.Min(progress, 100)}%");
                                        });
                                    }
                                }

                                mat.Dispose();
                            }
                        }

                        Dispatcher.Invoke(() =>
                        {
                            UILogManager.Instance.LogInfo($"视频片段导出完成: {outputPath}");
                            MessageBox.Show($"视频片段已成功导出到:\n{outputPath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"导出视频片段失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                            UILogManager.Instance.LogError($"导出视频片段异常: {ex.Message}");
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出视频片段失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                UILogManager.Instance.LogError($"导出视频片段异常: {ex.Message}");
            }
        }
    }
}