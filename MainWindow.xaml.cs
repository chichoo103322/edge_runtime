using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.Loader;
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

        // 当前正在使用的相机信息（用于设备状态同步）
        private string _currentCameraId = null;
        private int? _currentCameraIndex = null;

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
            
            // 初始化 UI 日志系统
            UILogManager.Instance.LogInfo("应用程序已启动");

            // 绑定视频处理按钮的点击事件（在 XAML 中声明了 x:Name）
            BtnOpenVideoProcessor.Click += BtnOpenVideoProcessor_Click;
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
                UILogManager.Instance.LogInfo($"开始加载流程文件: {filepath}");
                
                string json = File.ReadAllText(filepath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var workflow = JsonSerializer.Deserialize<WorkflowStructure>(json, options);

                if (workflow == null || workflow.Actions == null)
                {
                    UILogManager.Instance.LogError("流程文件格式无效");
                    return;
                }

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

                // 选择首个相机作为默认主相机（如果有指定）
                string primaryCam = null;
                foreach (var id in uniqueCameraIds)
                {
                    primaryCam = id;
                    break;
                }
                if (!string.IsNullOrEmpty(primaryCam))
                {
                    _currentCameraId = primaryCam;
                    // 解析为索引（如果是数字，会返回数字；否则尝试按名称查找）
                    // 注意：GetCameraIndexByName 返回 -1 表示未找到，不应该用作相机索引
                    int mappedIndex = CameraHelper.GetCameraIndexByName(_currentCameraId);
                    if (mappedIndex >= 0)
                    {
                        _currentCameraIndex = mappedIndex;
                    }
                    // 如果 mappedIndex 为 -1，则 _currentCameraIndex 保持为 null
                }

                // [修改] 从 JSON 中读取动态的 ModelPath
                LoadAiModel(workflow.ModelPath);

                // 初始化日志服务
                InitializeLogService();

                UILogManager.Instance.LogInfo($"流程加载完成: {_executionQueue.Count} 个步骤, {DeviceList.Count} 个设备");

                // 1. 先启动主流程（优先占用相机）
                StartMonitoringLoop();

                // 2. 延迟后再检测设备状态（等待相机初始化）
                Task.Delay(1500).ContinueWith(_ => CheckAllDevicesStatus());
            }
            catch (Exception ex)
            {
                string errMsg = $"加载失败: {ex.Message}";
                MessageBox.Show(errMsg);
                UILogManager.Instance.LogError(errMsg);
            }
        }

        // [新增] 检测所有节点定义的相机是否在线
        private void CheckAllDevicesStatus()
        {
            Task.Run(() =>
            {
                foreach (var device in DeviceList)
                {
                    Dispatcher.Invoke(() => { device.Status = "正在连接..."; device.StatusColor = STATUS_CHECKING; });

                    bool isOnline = false;
                    try
                    {
                        // 如果 DeviceId 是数字索引
                        if (int.TryParse(device.DeviceId, out int camIndex))
                        {
                            using (var tempCap = new VideoCapture(camIndex))
                            {
                                tempCap.Set(VideoCaptureProperties.BufferSize, 1);
                                isOnline = tempCap.IsOpened();
                            }
                        }
                        else
                        {
                            // 先尝试用 DirectShow 名称直接打开
                            try
                            {
                                using (var tempCap = new VideoCapture($"video={device.DeviceId}", VideoCaptureAPIs.DSHOW))
                                {
                                    tempCap.Set(VideoCaptureProperties.BufferSize, 1);
                                    if (tempCap.IsOpened()) isOnline = true;
                                }
                            }
                            catch { isOnline = false; }

                            // 如果按名称打开失败，尝试通过 CameraHelper 映射到索引再打开
                            if (!isOnline)
                            {
                                int mappedIndex = CameraHelper.GetCameraIndexByName(device.DeviceId);
                                if (mappedIndex >= 0)
                                {
                                    try
                                    {
                                        using (var tempCap = new VideoCapture(mappedIndex))
                                        {
                                            tempCap.Set(VideoCaptureProperties.BufferSize, 1);
                                            if (tempCap.IsOpened()) isOnline = true;
                                        }
                                    }
                                    catch { isOnline = false; }
                                }
                            }
                        }
                    }
                    catch { isOnline = false; }

                    Dispatcher.Invoke(() =>
                    {
                        if (isOnline)
                        {
                            device.Status = "在线";
                            device.StatusColor = STATUS_ONLINE;
                        }
                        else
                        {
                            // 如果当前主相机就是此设备（匹配名称或索引），并且 _capture 已打开，则标记运行中
                            bool isCurrent = false;
                            if (!string.IsNullOrEmpty(_currentCameraId) && device.DeviceId.Equals(_currentCameraId, StringComparison.OrdinalIgnoreCase))
                                isCurrent = (_capture != null && _capture.IsOpened());
                            if (!isCurrent && int.TryParse(device.DeviceId, out int idx) && _currentCameraIndex.HasValue && idx == _currentCameraIndex.Value)
                                isCurrent = (_capture != null && _capture.IsOpened());

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
                    // 优先按名称打开（如果有指定名称）
                    bool opened = false;
                    if (!string.IsNullOrEmpty(_currentCameraId))
                    {
                        try
                        {
                            // 使用 DirectShow 打开指定名字的设备
                            _capture = new VideoCapture($"video={_currentCameraId}", VideoCaptureAPIs.DSHOW);
                            if (_capture?.IsOpened() == true) opened = true;
                        }
                        catch { opened = false; }
                    }

                    // 如果未打开，尝试用映射到的索引打开
                    if (!opened && _currentCameraIndex.HasValue && _currentCameraIndex.Value >= 0)
                    {
                        _capture = new VideoCapture(_currentCameraIndex.Value);
                        if (_capture?.IsOpened() == true) opened = true;
                    }

                    // 如果指定的相机未能打开，显示错误并返回，不自动回退到默认相机
                    if (!opened)
                    {
                        Dispatcher.Invoke(() => 
                            MessageBox.Show($"无法打开指定的摄像头。\n相机ID: {_currentCameraId}\n" +
                                          $"请检查相机连接或配置。")
                        );
                        return;
                    }

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
                        Dispatcher.Invoke(() => MessageBox.Show("无法打开主摄像头，请检查相机设置"));
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
                string msg = "模型路径为空，将跳过 AI 模型加载";
                Dispatcher.Invoke(() => MessageBox.Show(msg));
                UILogManager.Instance.LogWarning(msg);
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
                    UILogManager.Instance.LogInfo($"模型路径已自动更正: {finalModelPath}");
                }
            }

            if (!File.Exists(finalModelPath))
            {
                string msg = $"模型文件不存在: {modelPath}";
                Dispatcher.Invoke(() => MessageBox.Show(msg));
                UILogManager.Instance.LogError(msg);
                return;
            }

            try
            {
                UILogManager.Instance.LogInfo($"正在加载 AI 模型: {finalModelPath}");
                var labels = OnnxHelper.ReadLabelsFromModel(finalModelPath);
                _aiService = new OnnxInferenceService(finalModelPath, labels);
                string msg = $"AI 模型已成功加载: {finalModelPath}";
                Dispatcher.Invoke(() => MessageBox.Show(msg));
                UILogManager.Instance.LogInfo(msg);
            }
            catch (Exception ex)
            {
                string msg = $"加载 AI 模型失败: {ex.Message}";
                Dispatcher.Invoke(() => MessageBox.Show(msg));
                UILogManager.Instance.LogError(msg);
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

        // [新增] 清空日志
        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            UILogManager.Instance.ClearLogs();
        }

        // 打开外部编辑器（edge 仓库的动作树编辑器）
        private void BtnOpenEditor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1) 从配置读取编辑器路径
                string editorPath = ConfigManager.Instance.GetEditorPath();

                // 2) 如果未配置，提示用户选择可执行文件或项目目录
                if (string.IsNullOrEmpty(editorPath) || (!File.Exists(editorPath) && !Directory.Exists(editorPath)))
                {
                    var dlg = new OpenFileDialog();
                    dlg.Filter = "可执行文件 (*.exe)|*.exe|库文件 (*.dll)|*.dll|项目目录|*.*";
                    dlg.Title = "请选择动作树编辑器的可执行文件、DLL 或 edge 项目目录";
                    if (dlg.ShowDialog() == true)
                    {
                        editorPath = dlg.FileName;
                        // 如果用户选择的是目录通过 FolderBrowserDialog 会更合适, but OpenFileDialog returns a file — allow manual entry
                        var ok = ConfigManager.Instance.SetEditorPath(editorPath);
                        UILogManager.Instance.LogInfo($"编辑器路径已保存: {ok}");
                    }
                    else
                    {
                        UILogManager.Instance.LogWarning("未选择编辑器可执行文件或目录");
                        return;
                    }
                }

                // 3) 如果是可执行文件，直接启动进程
                if (File.Exists(editorPath) && editorPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo(editorPath)
                    {
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(startInfo);
                    UILogManager.Instance.LogInfo($"已启动外部编辑器进程: {editorPath}");
                    return;
                }

                // 4) 如果是 DLL 文件，尝试加载并查找 Window 派生类型
                if (File.Exists(editorPath) && editorPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryLoadAndShowWindowFromAssembly(editorPath))
                    {
                        UILogManager.Instance.LogInfo($"已在当前进程中加载编辑器: {editorPath}");
                        return;
                    }
                    else
                    {
                        throw new Exception("未在 DLL 中找到可用的 Window 类型");
                    }
                }

                // 5) 如果是目录（例如 edge 仓库根目录），尝试搜索常见输出路径并加载第一个合适的 DLL
                if (Directory.Exists(editorPath))
                {
                    // 搜索候选输出目录
                    var candidates = new List<string>
                    {
                        Path.Combine(editorPath, "bin", "Debug"),
                        Path.Combine(editorPath, "bin", "Debug", "net8.0"),
                        Path.Combine(editorPath, "bin", "Debug", "net8.0-windows"),
                        Path.Combine(editorPath, "out"),
                        Path.Combine(editorPath, "build")
                    };

                    foreach (var dir in candidates)
                    {
                        if (!Directory.Exists(dir)) continue;
                        var dlls = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
                        foreach (var dll in dlls)
                        {
                            try
                            {
                                if (TryLoadAndShowWindowFromAssembly(dll))
                                {
                                    UILogManager.Instance.LogInfo($"已在当前进程中加载编辑器 DLL: {dll}");
                                    return;
                                }
                            }
                            catch { /* ignore and try next */ }
                        }
                    }

                    // 如果没有找到任何合适的 dll，提示用户先编译 edge 项目或选择 exe
                    throw new Exception("在指定目录中未找到可加载的编辑器输出 (DLL/EXE)。请先编译 edge 项目或手动选择编辑器的可执行文件。");
                }

                throw new Exception("提供的编辑器路径无效");
            }
            catch (Exception ex)
            {
                string msg = $"启动编辑器失败: {ex.Message}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogError(msg);
            }
        }

        // 打开错误日志查看器
        private void BtnOpenErrorViewer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var viewer = new ErrorLogViewerWindow();
                viewer.Owner = this;
                viewer.Show();
                UILogManager.Instance.LogInfo("已打开 错误日志查看器 窗口");
            }
            catch (Exception ex)
            {
                string msg = $"打开错误日志查看器失败: {ex.Message}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogError(msg);
            }
        }

        // 打开视频处理窗口
        private void BtnOpenVideoProcessor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 优先使用当前步骤的相机配置
                string cameraId = string.Empty;
                int cameraIndex = -1;

                if (_currentStep != null && !string.IsNullOrEmpty(_currentStep.CameraId))
                {
                    cameraId = _currentStep.CameraId;
                    cameraIndex = CameraHelper.GetCameraIndexByName(cameraId);
                    UILogManager.Instance.LogInfo($"使用当前步骤的相机: {cameraId}");
                }

                VideoProcessorWindow wnd;
                if (!string.IsNullOrEmpty(cameraId))
                {
                    wnd = new VideoProcessorWindow(cameraId, cameraIndex);
                }
                else
                {
                    wnd = new VideoProcessorWindow();
                }

                wnd.Owner = this;
                wnd.Show();
                UILogManager.Instance.LogInfo("已打开 视频处理编辑器 窗口");
            }
            catch (Exception ex)
            {
                string msg = $"打开视频处理编辑器失败: {ex.Message}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogError(msg);
            }
        }

        // 尝试在当前 AppDomain 中加载程序集并显示第一个找到的 Window 派生类型
        private bool TryLoadAndShowWindowFromAssembly(string assemblyPath)
        {
            try
            {
                // Load assembly into load context
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

                foreach (var type in asm.GetExportedTypes())
                {
                    if (typeof(System.Windows.Window).IsAssignableFrom(type))
                    {
                        // 创建窗口实例并显示（在 UI 线程）
                        Dispatcher.Invoke(() =>
                        {
                            var wnd = (System.Windows.Window)Activator.CreateInstance(type);
                            wnd.Owner = this;
                            wnd.Show();
                        });

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                UILogManager.Instance.LogError($"加载程序集失败: {assemblyPath} -> {ex.Message}");
            }

            return false;
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