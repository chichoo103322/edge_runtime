using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace edge_runtime
{
    public partial class MainWindow : System.Windows.Window, INotifyPropertyChanged
    {
        // 流程 UI 数据
        public ObservableCollection<ProcessActionViewModel> ActionColumns { get; set; }
            = new ObservableCollection<ProcessActionViewModel>();

        // [新增] 设备列表 UI 数据
        public ObservableCollection<DeviceViewModel> DeviceList { get; set; }
            = new ObservableCollection<DeviceViewModel>();

        private List<ProcessStateViewModel> _executionQueue = new List<ProcessStateViewModel>();
        private int _currentStepIndex = 0;

        // [新增] 当前步骤绑定用
        private ProcessStateViewModel _currentStep;
        public ProcessStateViewModel CurrentStep
        {
            get => _currentStep;
            set { _currentStep = value; OnPropertyChanged(); }
        }

        private string _currentStationName = "未知工位";
        public string CurrentStationName
        {
            get => _currentStationName;
            set { _currentStationName = value; OnPropertyChanged(); }
        }

        private VideoCapture _capture;
        private CancellationTokenSource _cts;
        private OnnxInferenceService _aiService;

        // 颜色定义
        private readonly Brush COLOR_PENDING = new SolidColorBrush(Color.FromRgb(80, 80, 80));
        private readonly Brush COLOR_RUNNING = new SolidColorBrush(Color.FromRgb(52, 152, 219));
        private readonly Brush COLOR_SUCCESS = new SolidColorBrush(Color.FromRgb(39, 174, 96));
        private readonly Brush BORDER_HIGHLIGHT = Brushes.Yellow;

        // 设备状态颜色
        private readonly Brush STATUS_ONLINE = Brushes.LightGreen;
        private readonly Brush STATUS_OFFLINE = Brushes.Red;
        private readonly Brush STATUS_CHECKING = Brushes.Orange;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void BtnLoadProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "流程文件 (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                LoadAndParseJson(dlg.FileName);
            }
        }

        private void LoadAndParseJson(string filepath)
        {
            try
            {
                string json = File.ReadAllText(filepath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var workflow = JsonSerializer.Deserialize<WorkflowStructure>(json, options);

                if (workflow == null || workflow.Actions == null) return;

                ActionColumns.Clear();
                _executionQueue.Clear();
                DeviceList.Clear(); // 清空旧设备列表

                // 用于去重收集相机ID
                HashSet<string> uniqueCameraIds = new HashSet<string>();

                _currentStepIndex = 0;

                foreach (var actionData in workflow.Actions)
                {
                    var columnVM = new ProcessActionViewModel { Name = actionData.Name };

                    if (actionData.States != null)
                    {
                        foreach (var stateData in actionData.States)
                        {
                            var stateVM = new ProcessStateViewModel
                            {
                                Id = stateData.Id,
                                Name = stateData.Name,
                                TargetLabel = stateData.SelectedLabel,
                                Threshold = stateData.Threshold,
                                Background = COLOR_PENDING,
                                StationName = actionData.StationName ?? "未配置",
                                CameraId = stateData.CameraDevice, // 记录该步骤需要的相机
                                VideoPath = stateData.StandardVideoPath // [新增] 记录标准作业指导视频路径
                            };

                            // [新增] 根据 VideoPath 是否为空设置 HasVideo
                            stateVM.HasVideo = !string.IsNullOrEmpty(stateVM.VideoPath);

                            // 收集相机ID
                            if (!string.IsNullOrEmpty(stateVM.CameraId))
                            {
                                uniqueCameraIds.Add(stateVM.CameraId);
                            }

                            columnVM.States.Add(stateVM);
                            _executionQueue.Add(stateVM);
                        }
                    }
                    ActionColumns.Add(columnVM);
                }

                // 初始化设备列表 UI
                foreach (var camId in uniqueCameraIds)
                {
                    DeviceList.Add(new DeviceViewModel
                    {
                        DeviceId = camId,
                        DeviceName = $"Camera {camId}",
                        Status = "等待检测...",
                        StatusColor = STATUS_CHECKING
                    });
                }

                TxtCurrentStep.Text = "流程已加载";
                if (_executionQueue.Count > 0)
                    CurrentStationName = _executionQueue[0].StationName;

                // [修改] 从 JSON 中读取动态的 ModelPath
                LoadAiModel(workflow.ModelPath);

                // 1. 先检测设备状态
                CheckAllDevicesStatus();

                // 2. 再启动主流程 (防止占用冲突，实际工程中最好用单例管理相机)
                StartMonitoringLoop();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载失败: {ex.Message}");
            }
        }

        // [新增] 检测所有节点定义的相机是否在线
        private void CheckAllDevicesStatus()
        {
            Task.Run(() =>
            {
                foreach (var device in DeviceList)
                {
                    // 更新为检测中
                    Dispatcher.Invoke(() => {
                        device.Status = "正在连接...";
                        device.StatusColor = STATUS_CHECKING;
                    });

                    bool isOnline = false;
                    try
                    {
                        // 尝试解析 ID (假设是数字索引 "0", "1")
                        if (int.TryParse(device.DeviceId, out int camIndex))
                        {
                            // 尝试打开相机
                            using (var tempCap = new VideoCapture(camIndex))
                            {
                                if (tempCap.IsOpened())
                                {
                                    isOnline = true;
                                }
                            }
                        }
                        else
                        {
                            // 如果ID是 RTSP 流地址或其他字符串，这里可以加其他检测逻辑
                            isOnline = false; // 暂时只支持数字索引
                        }
                    }
                    catch
                    {
                        isOnline = false;
                    }

                    // 更新结果
                    Dispatcher.Invoke(() => {
                        if (isOnline)
                        {
                            device.Status = "在线";
                            device.StatusColor = STATUS_ONLINE;
                        }
                        else
                        {
                            // 特殊情况：如果是当前正在使用的主相机（比如索引0），
                            // VideoCapture 可能会因为被占用而打不开，这里做个简单兼容
                            if (device.DeviceId == "0" && _capture != null && _capture.IsOpened())
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
            });
        }

        private void StartMonitoringLoop()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() =>
            {
                // 这里暂时默认打开索引 0 的相机作为主视频流
                // 未来可以根据 CurrentStep.CameraId 动态切换 _capture
                _capture = new VideoCapture(0);

                if (!_capture.IsOpened())
                {
                    Dispatcher.Invoke(() => MessageBox.Show("无法打开主摄像头 (ID: 0)"));
                    return;
                }

                using (Mat frame = new Mat())
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!_capture.Read(frame) || frame.Empty())
                        {
                            Thread.Sleep(10); continue;
                        }

                        var bitmap = frame.Clone().ToBitmapSource();
                        bitmap.Freeze();
                        Dispatcher.Invoke(() => VideoFeed.Source = bitmap);

                        ProcessFlowLogic(frame);

                        Thread.Sleep(30);
                    }
                }
                _capture.Release();
            }, token);
        }

        private void ProcessFlowLogic(Mat frame)
        {
            if (_currentStepIndex >= _executionQueue.Count)
            {
                Dispatcher.Invoke(() => TxtCurrentStep.Text = "所有流程已完成！");
                return;
            }

            var currentStep = _executionQueue[_currentStepIndex];

            Dispatcher.Invoke(() =>
            {
                TxtCurrentStep.Text = $"当前步骤: {currentStep.Name}";

                // [新增] 更新 CurrentStep 绑定，用于按钮的 IsEnabled 状态
                CurrentStep = currentStep;

                if (CurrentStationName != currentStep.StationName)
                {
                    CurrentStationName = currentStep.StationName;
                }

                if (currentStep.Background == COLOR_PENDING)
                {
                    currentStep.Background = COLOR_RUNNING;
                    currentStep.BorderColor = BORDER_HIGHLIGHT;
                }
            });

            bool isPassed = false;
            if (_aiService != null && !string.IsNullOrEmpty(currentStep.TargetLabel))
            {
                var result = _aiService.Predict(frame);
                if (result.Label == currentStep.TargetLabel && result.Confidence >= currentStep.Threshold)
                {
                    isPassed = true;
                }
            }
            else
            {
                Thread.Sleep(500);
                isPassed = true;
            }

            if (isPassed)
            {
                Dispatcher.Invoke(() =>
                {
                    currentStep.Background = COLOR_SUCCESS;
                    currentStep.BorderColor = Brushes.Transparent;
                });
                _currentStepIndex++;
            }
        }

        private void LoadAiModel(string modelPath)
        {
            // [修改] 移除硬编码，直接使用传入的 modelPath 参数
            if (string.IsNullOrEmpty(modelPath))
            {
                Dispatcher.Invoke(() => MessageBox.Show("模型路径为空，将跳过 AI 模型加载"));
                return;
            }

            if (!File.Exists(modelPath))
            {
                Dispatcher.Invoke(() => MessageBox.Show($"模型文件不存在: {modelPath}"));
                return;
            }

            try
            {
                var labels = OnnxHelper.ReadLabelsFromModel(modelPath);
                _aiService = new OnnxInferenceService(modelPath, labels);
                Dispatcher.Invoke(() => MessageBox.Show($"AI 模型已加载: {modelPath}"));
            }
            catch (Exception ex)
             {
                 Dispatcher.Invoke(() => MessageBox.Show($"加载 AI 模型失败: {ex.Message}"));
             }
         }

        // [新增] 打开视频播放窗口
        private void OnWatchVideo_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentStep == null || string.IsNullOrEmpty(CurrentStep.VideoPath))
            {
                MessageBox.Show("当前步骤没有关联的视频");
                return;
            }

            if (!File.Exists(CurrentStep.VideoPath))
            {
                MessageBox.Show($"视频文件不存在: {CurrentStep.VideoPath}");
                return;
            }

            // 设置视频源
            StandardPlayer.Source = new Uri(CurrentStep.VideoPath, UriKind.Absolute);

            // 打开 Popup
            VideoPopup.IsOpen = true;
        }

        // [新增] 关闭视频播放窗口
        private void OnCloseVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Stop();
            StandardPlayer.Source = null;
            VideoPopup.IsOpen = false;
        }

        // [新增] 播放视频
        private void OnPlayVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Play();
        }

        // [新增] 暂停视频
        private void OnPauseVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Pause();
        }

        // [新增] 停止视频
        private void OnStopVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Stop();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            _capture?.Dispose();
            base.OnClosed(e);
        }
    }
}