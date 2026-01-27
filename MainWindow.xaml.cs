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
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        private LogService _logService;

        // 错误动作定义（工业级质量控制）
        private static readonly HashSet<string> ERROR_ACTIONS = new HashSet<string>
        {
            "Wrong_Action",
            "UsingPhone",
            "WrongHand",
            "NotWearing",
            "Distracted",
            "IncorrectPosture"
        };

        // 步骤计时器，用于检测连续无操作超时
        private Dictionary<int, DateTime> _stepLastDetectTime = new Dictionary<int, DateTime>();
        private const int DETECTION_TIMEOUT_SECONDS = 10;

        // 内存优化：复用 WriteableBitmap 缓冲区，避免频繁创建
        private WriteableBitmap _reusableBitmap;
        private readonly object _bitmapLock = new object();

        // 颜色定义
        private readonly Brush COLOR_PENDING = new SolidColorBrush(Color.FromRgb(80, 80, 80));
        private readonly Brush COLOR_RUNNING = new SolidColorBrush(Color.FromRgb(52, 152, 219));
        private readonly Brush COLOR_SUCCESS = new SolidColorBrush(Color.FromRgb(39, 174, 96));
        private readonly Brush COLOR_ERROR = new SolidColorBrush(Color.FromRgb(239, 83, 80)); // 红色错误
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

                // 初始化日志服务
                InitializeLogService();

                // 1. 先启动主流程（优先占用相机）
                StartMonitoringLoop();

                // 2. 延迟后再检测设备状态（等待相机初始化）
                Task.Delay(1500).ContinueWith(_ => CheckAllDevicesStatus());
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
                            // 尝试打开相机，添加超时控制
                            var capTask = Task.Run(() =>
                            {
                                try
                                {
                                    using (var tempCap = new VideoCapture(camIndex))
                                    {
                                        // 设置打开超时
                                        tempCap.Set(VideoCaptureProperties.BufferSize, 1);
                                        if (tempCap.IsOpened())
                                        {
                                            return true;
                                        }
                                    }
                                }
                                catch { }
                                return false;
                            });

                            // 等待最多 2 秒
                            if (capTask.Wait(TimeSpan.FromSeconds(2)))
                            {
                                isOnline = capTask.Result;
                            }
                            else
                            {
                                isOnline = false; // 超时视为离线
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
                try
                {
                    _capture = new VideoCapture(0);
                    _capture.Set(VideoCaptureProperties.BufferSize, 1);

                    int retries = 0;
                    const int maxRetries = 10;
                    while (!_capture.IsOpened() && retries < maxRetries && !token.IsCancellationRequested)
                    {
                        retries++;
                        Thread.Sleep(100);
                    }

                    if (!_capture.IsOpened())
                    {
                        Dispatcher.Invoke(() => MessageBox.Show("无法打开主摄像头 (ID: 0)，请检查相机是否被占用或掉线"));
                        return;
                    }

                    using (Mat frame = new Mat())
                    {
                        while (!token.IsCancellationRequested)
                        {
                            if (!_capture.Read(frame) || frame.Empty())
                            {
                                Thread.Sleep(10);
                                continue;
                            }

                            // 初始化 Bitmap 缓冲区（仅第一次）
                            if (_reusableBitmap == null)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    InitializeBitmapBuffer(frame.Width, frame.Height);
                                });
                            }

                            // 优化：直接用 ToBitmapSource 而不创建副本
                            var bitmap = frame.ToBitmapSource();
                            bitmap.Freeze();
                            Dispatcher.Invoke(() => VideoFeed.Source = bitmap);

                            ProcessFlowLogic(frame);

                            Thread.Sleep(30);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MessageBox.Show($"监控循环错误: {ex.Message}"));
                }
                finally
                {
                    _capture?.Release();
                    _capture?.Dispose();
                    _capture = null;
                    
                    // 清理 Bitmap 缓冲区
                    _reusableBitmap = null;
                }
            }, token);
        }

        private void ProcessFlowLogic(Mat frame)
        {
            // 严格模式：如果 AI 服务未加载，绝不做任何处理
            if (_aiService == null)
            {
                return;
            }

            // 无限循环：检查是否所有步骤已完成
            if (_currentStepIndex >= _executionQueue.Count)
            {
                Dispatcher.Invoke(() => TxtCurrentStep.Text = "⏹ 产品完成，准备下一轮...");
                
                // 记录产品完成
                _logService?.LogToDb("Product_Complete", "Complete");

                // 重置计时器
                _stepLastDetectTime.Clear();

                Thread.Sleep(1000);
                ResetUI();
                _currentStepIndex = 0;

                // 强制垃圾回收，清理上一轮产品的内存
                GC.Collect(0, GCCollectionMode.Optimized);
                GC.WaitForPendingFinalizers();

                return;
            }

            var currentStep = _executionQueue[_currentStepIndex];

            // 初始化当前步骤的计时器（第一次进入此步骤）
            if (!_stepLastDetectTime.ContainsKey(_currentStepIndex))
            {
                _stepLastDetectTime[_currentStepIndex] = DateTime.Now;
            }

            // 更新 UI：标记当前步骤为运行中
            Dispatcher.Invoke(() =>
            {
                TxtCurrentStep.Text = $"当前步骤: {currentStep.Name}";
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

            // 获取 AI 识别结果
            var result = _aiService.Predict(frame);

            // 异常检测：检测到错误动作
            if (ERROR_ACTIONS.Contains(result.Label))
            {
                Dispatcher.Invoke(() =>
                {
                    currentStep.Background = COLOR_ERROR;
                    currentStep.BorderColor = Brushes.Transparent;
                });

                // 保存错误图片并记录到数据库
                string imagePath = _logService?.SaveFrame(frame, currentStep.Name, "NG");
                _logService?.LogToDb(
                    currentStep.Name,
                    "NG",
                    imagePath
                );

                return;
            }

            // 异常检测：连续超时（10秒）未检测到正确动作
            TimeSpan elapsed = DateTime.Now - _stepLastDetectTime[_currentStepIndex];
            if (elapsed.TotalSeconds > DETECTION_TIMEOUT_SECONDS)
            {
                Dispatcher.Invoke(() =>
                {
                    currentStep.Background = COLOR_ERROR;
                    currentStep.BorderColor = Brushes.Transparent;
                });

                // 记录超时事件
                string timeoutPath = _logService?.SaveFrame(frame, currentStep.Name, "TIMEOUT");
                _logService?.LogToDb(
                    currentStep.Name,
                    "TIMEOUT",
                    timeoutPath
                );

                // 重置计时器，准备下一次检测
                _stepLastDetectTime[_currentStepIndex] = DateTime.Now;
                return;
            }

            // 正确行为判定
            if (!string.IsNullOrEmpty(currentStep.TargetLabel) && 
                result.Label == currentStep.TargetLabel && 
                result.Confidence >= currentStep.Threshold)
            {
                // 更新 UI 为绿色（成功）
                Dispatcher.Invoke(() =>
                {
                    currentStep.Background = COLOR_SUCCESS;
                    currentStep.BorderColor = Brushes.Transparent;
                });

                // 记录成功日志
                _logService?.LogToDb(
                    currentStep.Name,
                    "OK"
                );

                // 清除该步骤的计时器
                _stepLastDetectTime.Remove(_currentStepIndex);

                // 进入下一步
                _currentStepIndex++;
                return;
            }

            // 情况 C：等待（低置信度或未识别到目标标签）
            // 保持当前状态运行，继续等待
        }

        private void ResetUI()
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var column in ActionColumns)
                {
                    foreach (var state in column.States)
                    {
                        state.Background = COLOR_PENDING;
                        state.BorderColor = Brushes.Transparent;
                    }
                }
            });
        }

        private void InitializeLogService()
        {
            try
            {
                _logService = new LogService("ErrorLogs");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化日志服务失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化可复用的 Bitmap 缓冲区
        /// </summary>
        private void InitializeBitmapBuffer(int width, int height)
        {
            if (_reusableBitmap == null)
            {
                _reusableBitmap = new System.Windows.Media.Imaging.WriteableBitmap(
                    width, height, 96, 96, 
                    System.Windows.Media.PixelFormats.Bgr24, null);
                _reusableBitmap.Freeze();
            }
        }

        private void LoadAiModel(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
            {
                Dispatcher.Invoke(() => MessageBox.Show("模型路径为空，将跳过 AI 模型加载"));
                return;
            }

            // 模型路径容错：如果绝对路径不存在，在 BaseDirectory 下寻找同名文件
            string finalModelPath = modelPath;
            if (!File.Exists(finalModelPath))
            {
                string fileName = Path.GetFileName(modelPath);
                string alternativePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (File.Exists(alternativePath))
                {
                    finalModelPath = alternativePath;
                }
            }

            if (!File.Exists(finalModelPath))
            {
                Dispatcher.Invoke(() => MessageBox.Show($"模型文件不存在: {modelPath}"));
                return;
            }

            try
            {
                var labels = OnnxHelper.ReadLabelsFromModel(finalModelPath);
                _aiService = new OnnxInferenceService(finalModelPath, labels);
                Dispatcher.Invoke(() => MessageBox.Show($"AI 模型已加载: {finalModelPath}"));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => MessageBox.Show($"加载 AI 模型失败: {ex.Message}"));
            }
        }

        // [新增] 打开步骤关联的视频播放窗口（通过步骤卡片上的按钮点击）
        private void OnStepVideo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ProcessStateViewModel step)
            {
                if (string.IsNullOrEmpty(step.VideoPath))
                {
                    MessageBox.Show("该步骤没有关联的视频");
                    return;
                }

                if (!File.Exists(step.VideoPath))
                {
                    MessageBox.Show($"视频文件不存在: {step.VideoPath}");
                    return;
                }

                // 设置视频源
                try
                {
                    var fullPath = Path.GetFullPath(step.VideoPath);
                    StandardPlayer.Source = new Uri(fullPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"设置视频源失败: {ex.Message}");
                    return;
                }

                // 打开 Popup
                VideoPopup.IsOpen = true;
            }
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
            _logService = null;
            base.OnClosed(e);
        }
    }
}