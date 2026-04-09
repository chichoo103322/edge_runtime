using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using edge_runtime.Services;
using WpfWindow = System.Windows.Window;

namespace edge_runtime
{
  
    public partial class MainWindow : WpfWindow, INotifyPropertyChanged
    {
        #region UI 数据绑定属性

        /// <summary>
        /// 动作列集合（工艺流程的列显示）
        /// </summary>
        public ObservableCollection<ProcessActionViewModel> ActionColumns { get; set; }
            = new ObservableCollection<ProcessActionViewModel>();

        /// <summary>
        /// 设备列表（相机设备状态显示）
        /// </summary>
        public ObservableCollection<DeviceViewModel> DeviceList { get; set; }
            = new ObservableCollection<DeviceViewModel>();

        private ProcessStateViewModel _currentStep;
        /// <summary>
        /// 当前执行的步骤
        /// </summary>
        public ProcessStateViewModel CurrentStep
        {
            get => _currentStep;
            set { _currentStep = value; OnPropertyChanged(); }
        }

        private string _currentStationName = "未知工位";
        /// <summary>
        /// 当前工位名称
        /// </summary>
        public string CurrentStationName
        {
            get => _currentStationName;
            set { _currentStationName = value; OnPropertyChanged(); }
        }

        #endregion

        #region 服务实例

        /// <summary>
        /// 相机管理服务 - 负责相机打开、读取和状态管理（已弃用，使用 VideoSourceManager 替代）
        /// </summary>
        private CameraManager _cameraManager;

        /// <summary>
        /// 视频源管理器 - 统一管理相机和视频文件源
        /// </summary>
        private VideoSourceManager _videoSourceManager;

        /// <summary>
        /// 流程执行器 - 负责工艺流程的执行逻辑
        /// </summary>
        private WorkflowExecutor _workflowExecutor;

        /// <summary>
        /// 工作流加载器 - 负责从JSON加载和解析配置
        /// </summary>
        private WorkflowLoader _workflowLoader;

        /// <summary>
        /// 设备状态监控器 - 负责检测和更新设备在线状态
        /// </summary>
        private DeviceStatusMonitor _deviceStatusMonitor;

        /// <summary>
        /// 外部编辑器启动器 - 负责启动动作树编辑器
        /// </summary>
        private EditorLauncher _editorLauncher;

        /// <summary>
        /// AI模型加载器 - 负责加载ONNX模型
        /// </summary>
        private ModelLoader _modelLoader;

        /// <summary>
        /// 日志服务 - 负责记录错误截图和数据库日志
        /// </summary>
        private LogService _logService;

        /// <summary>
        /// AI推理服务 - 负责动作识别
        /// </summary>
        private OnnxInferenceService _aiService;

        #endregion

        #region 数据

        /// <summary>
        /// 执行队列（线性化的步骤列表）
        /// </summary>
        private List<ProcessStateViewModel> _executionQueue = new List<ProcessStateViewModel>();

        /// <summary>
        /// 当前视频源类型
        /// </summary>
        private VideoSourceManager.VideoSourceType _currentVideoSourceType = VideoSourceManager.VideoSourceType.Camera;

        #endregion

        /// <summary>
        /// 构造函数
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // 初始化所有服务
            InitializeServices();

            UILogManager.Instance.LogInfo("应用程序已启动");
        }

        /// <summary>
        /// 初始化所有服务实例
        /// </summary>
        private void InitializeServices()
        {
            // 1. 视频源管理器（统一管理相机和视频文件源）
            _videoSourceManager = new VideoSourceManager(
                onFrameReceived: bitmap => VideoFeed.Source = bitmap,  // 帧接收回调
                onFrameProcessing: frame => _workflowExecutor?.ProcessFrame(frame)  // 帧处理回调
            );

            // 2. 工作流加载器
            _workflowLoader = new WorkflowLoader();

            // 3. 设备状态监控器
            _deviceStatusMonitor = new DeviceStatusMonitor(
                DeviceList,
                isCameraBusy: () => _videoSourceManager?.IsRunning ?? false,  // 视频源是否正在运行
                getCurrentCameraId: () => _videoSourceManager?.CurrentCameraId,  // 获取当前相机ID
                getCurrentCameraIndex: () => _videoSourceManager?.CurrentCameraIndex  // 获取当前相机索引
            );

            // 4. 编辑器启动器
            _editorLauncher = new EditorLauncher();

            // 5. 模型加载器
            _modelLoader = new ModelLoader();

            // 6. 日志服务
            try
            {
                _logService = new LogService("ErrorLogs");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化日志服务失败: {ex.Message}");
            }
        }

        #region 事件处理器

        /// <summary>
        /// 导入流程JSON按钮点击事件
        /// </summary>
        private void BtnLoadProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "流程文件 (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                LoadWorkflow(dlg.FileName);
            }
        }

        /// <summary>
        /// 打开动作树编辑器按钮点击事件
        /// </summary>
        private void BtnOpenEditor_Click(object sender, RoutedEventArgs e)
        {
            _editorLauncher.OpenEditor(this);
        }

        /// <summary>
        /// 打开错误日志查看器按钮点击事件
        /// </summary>
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

        /// <summary>
        /// 打开视频处理编辑器按钮点击事件
        /// </summary>
        private void BtnOpenVideoProcessor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cameraId = string.Empty;
                int cameraIndex = -1;

                // 优先使用当前步骤的相机配置
                if (_currentStep != null && !string.IsNullOrEmpty(_currentStep.CameraId))
                {
                    cameraId = _currentStep.CameraId;
                    cameraIndex = CameraHelper.GetCameraIndexByName(cameraId);
                    UILogManager.Instance.LogInfo($"使用当前步骤的相机: {cameraId}");
                }

                // 创建视频处理窗口
                VideoProcessorWindow wnd = !string.IsNullOrEmpty(cameraId)
                    ? new VideoProcessorWindow(cameraId, cameraIndex)
                    : new VideoProcessorWindow();

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

        /// <summary>
        /// 清空日志按钮点击事件
        /// </summary>
        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            UILogManager.Instance.ClearLogs();
        }

        /// <summary>
        /// 播放步骤视频按钮点击事件
        /// </summary>
        private void OnStepVideo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ProcessStateViewModel step)
            {
                // 验证视频路径
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

                // 设置视频源并打开播放窗口
                try
                {
                    var fullPath = Path.GetFullPath(step.VideoPath);
                    StandardPlayer.Source = new Uri(fullPath);
                    VideoPopup.IsOpen = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"设置视频源失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 关闭视频播放窗口
        /// </summary>
        private void OnCloseVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Stop();
            StandardPlayer.Source = null;
            VideoPopup.IsOpen = false;
        }

        /// <summary>
        /// 播放视频
        /// </summary>
        private void OnPlayVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Play();
        }

        /// <summary>
        /// 暂停视频
        /// </summary>
        private void OnPauseVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Pause();
        }

        /// <summary>
        /// 停止视频
        /// </summary>
        private void OnStopVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Stop();
        }

        /// <summary>
        /// 导入检测视频按钮点击事件 - 选择视频文件进行动态识别检测
        /// </summary>
        private void BtnImportTestVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "视频文件 (*.mp4;*.avi;*.mov;*.mkv)|*.mp4;*.avi;*.mov;*.mkv|所有文件 (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                _ = StartVideoFileDetection(dlg.FileName);
            }
        }

        /// <summary>
        /// 启用相机检测按钮点击事件
        /// </summary>
        private void BtnEnableCameraDetection_Click(object sender, RoutedEventArgs e)
        {
            if (_executionQueue.Count == 0)
            {
                MessageBox.Show("请先加载工作流配置");
                return;
            }

            _ = StartCameraDetection();
        }

        /// <summary>
        /// 启动摄像头检测
        /// </summary>
        private async Task StartCameraDetection()
        {
            try
            {
                if (_executionQueue.Count == 0)
                    return;

                var primaryCameraId = _executionQueue[0].CameraId;
                int? primaryCameraIndex = !string.IsNullOrEmpty(primaryCameraId)
                    ? CameraHelper.GetCameraIndexByName(primaryCameraId)
                    : 0;

                _currentVideoSourceType = VideoSourceManager.VideoSourceType.Camera;
                await _videoSourceManager.StartCameraAsync(primaryCameraId, primaryCameraIndex);
                UILogManager.Instance.LogInfo("已启用相机检测");
            }
            catch (Exception ex)
            {
                string msg = $"启用相机检测失败: {ex.Message}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogError(msg);
            }
        }

        /// <summary>
        /// 启动视频文件检测
        /// </summary>
        private async Task StartVideoFileDetection(string videoFilePath)
        {
            try
            {
                if (!File.Exists(videoFilePath))
                {
                    MessageBox.Show($"视频文件不存在: {videoFilePath}");
                    return;
                }

                _currentVideoSourceType = VideoSourceManager.VideoSourceType.VideoFile;
                await _videoSourceManager.StartVideoFileAsync(videoFilePath, isLooping: true);
                UILogManager.Instance.LogInfo($"已导入视频进行检测: {Path.GetFileName(videoFilePath)}");
            }
            catch (Exception ex)
            {
                string msg = $"启动视频检测失败: {ex.Message}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogError(msg);
            }
        }

        #endregion

        #region 工作流加载和执行

        /// <summary>
        /// 加载工作流（从JSON文件）
        /// </summary>
        /// <param name="filepath">JSON文件路径</param>
        private void LoadWorkflow(string filepath)
        {
            try
            {
                // 步骤1: 加载工作流配置
                var result = _workflowLoader.LoadFromFile(filepath);

                // 步骤2: 更新UI数据绑定
                ActionColumns.Clear();
                foreach (var col in result.ActionColumns)
                {
                    ActionColumns.Add(col);
                }

                _executionQueue = result.ExecutionQueue;
                TxtCurrentStep.Text = "流程已加载";

                if (_executionQueue.Count > 0)
                    CurrentStationName = _executionQueue[0].StationName;

                // 步骤3: 初始化设备列表
                _deviceStatusMonitor.InitializeDeviceList(result.CameraIds);

                // 步骤4: 加载AI模型（传入模型输入尺寸，支持自适应预处理）
                _aiService = _modelLoader.LoadModel(result.ModelPath, result.ModelInputSize);

                // 步骤5: 创建流程执行器
                _workflowExecutor = new WorkflowExecutor(
                    _executionQueue,
                    _aiService,
                    _logService,
                    onWorkflowComplete: () => Dispatcher.Invoke(() => TxtCurrentStep.Text = "⏹ 产品完成，准备下一轮..."),
                    onStepChanged: step => Dispatcher.Invoke(() =>
                    {
                        TxtCurrentStep.Text = $"当前步骤: {step.Name}";
                        CurrentStep = step;
                        if (CurrentStationName != step.StationName)
                        {
                            CurrentStationName = step.StationName;
                        }
                    })
                );

                // 步骤6: 启动相机监控（使用新的视频源管理器）
                _currentVideoSourceType = VideoSourceManager.VideoSourceType.Camera;
                _ = _videoSourceManager.StartCameraAsync(result.PrimaryCameraId, result.PrimaryCameraIndex);

                // 步骤7: 延迟1.5秒后检测设备状态（等待相机初始化）
                Task.Delay(1500).ContinueWith(_ => _deviceStatusMonitor.CheckAllDevicesAsync());
            }
            catch (Exception ex)
            {
                string errMsg = $"加载失败: {ex.Message}";
                MessageBox.Show(errMsg);
                UILogManager.Instance.LogError(errMsg);
            }
        }

        #endregion

        #region INotifyPropertyChanged 实现

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 触发属性变更通知
        /// </summary>
        /// <param name="name">属性名（自动获取）</param>
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion

        #region 清理资源

        /// <summary>
        /// 窗口关闭时的清理工作
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _videoSourceManager?.Dispose();
            _cameraManager?.Dispose();
            _logService = null;
            base.OnClosed(e);
        }

        #endregion
    }
}