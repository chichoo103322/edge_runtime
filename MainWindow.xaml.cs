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
        // UI 数据绑定
        public ObservableCollection<ProcessActionViewModel> ActionColumns { get; set; }
            = new ObservableCollection<ProcessActionViewModel>();

        public ObservableCollection<DeviceViewModel> DeviceList { get; set; }
            = new ObservableCollection<DeviceViewModel>();

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

        // 服务类
        private CameraManager _cameraManager;
        private WorkflowExecutor _workflowExecutor;
        private WorkflowLoader _workflowLoader;
        private DeviceStatusMonitor _deviceStatusMonitor;
        private EditorLauncher _editorLauncher;
        private ModelLoader _modelLoader;
        private LogService _logService;
        private OnnxInferenceService _aiService;

        // 数据
        private List<ProcessStateViewModel> _executionQueue = new List<ProcessStateViewModel>();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            InitializeServices();

            UILogManager.Instance.LogInfo("应用程序已启动");
        }

        /// <summary>
        /// 初始化所有服务
        /// </summary>
        private void InitializeServices()
        {
            // 相机管理器
            _cameraManager = new CameraManager(
                onFrameReceived: bitmap => VideoFeed.Source = bitmap,
                onFrameProcessing: frame => _workflowExecutor?.ProcessFrame(frame)
            );

            // 工作流加载器
            _workflowLoader = new WorkflowLoader();

            // 设备状态监控器
            _deviceStatusMonitor = new DeviceStatusMonitor(
                DeviceList,
                isCameraBusy: () => _cameraManager?.IsRunning ?? false,
                getCurrentCameraId: () => _cameraManager?.CurrentCameraId,
                getCurrentCameraIndex: () => _cameraManager?.CurrentCameraIndex
            );

            // 编辑器启动器
            _editorLauncher = new EditorLauncher();

            // 模型加载器
            _modelLoader = new ModelLoader();

            // 日志服务
            try
            {
                _logService = new LogService("ErrorLogs");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化日志服务失败: {ex.Message}");
            }
        }

        // ==================== 事件处理器 ====================

        private void BtnLoadProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "流程文件 (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                LoadWorkflow(dlg.FileName);
            }
        }

        private void BtnOpenEditor_Click(object sender, RoutedEventArgs e)
        {
            _editorLauncher.OpenEditor(this);
        }

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

        private void BtnOpenVideoProcessor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cameraId = string.Empty;
                int cameraIndex = -1;

                if (_currentStep != null && !string.IsNullOrEmpty(_currentStep.CameraId))
                {
                    cameraId = _currentStep.CameraId;
                    cameraIndex = CameraHelper.GetCameraIndexByName(cameraId);
                    UILogManager.Instance.LogInfo($"使用当前步骤的相机: {cameraId}");
                }

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

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            UILogManager.Instance.ClearLogs();
        }

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

        private void OnCloseVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Stop();
            StandardPlayer.Source = null;
            VideoPopup.IsOpen = false;
        }

        private void OnPlayVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Play();
        }

        private void OnPauseVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Pause();
        }

        private void OnStopVideo_Click(object sender, RoutedEventArgs e)
        {
            StandardPlayer.Stop();
        }

        // ==================== 工作流加载和执行 ====================

        private void LoadWorkflow(string filepath)
        {
            try
            {
                // 加载工作流
                var result = _workflowLoader.LoadFromFile(filepath);

                // 更新UI数据
                ActionColumns.Clear();
                foreach (var col in result.ActionColumns)
                {
                    ActionColumns.Add(col);
                }

                _executionQueue = result.ExecutionQueue;
                TxtCurrentStep.Text = "流程已加载";

                if (_executionQueue.Count > 0)
                    CurrentStationName = _executionQueue[0].StationName;

                // 初始化设备列表
                _deviceStatusMonitor.InitializeDeviceList(result.CameraIds);

                // 加载AI模型
                _aiService = _modelLoader.LoadModel(result.ModelPath);

                // 创建工作流执行器
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

                // 启动相机监控
                _ = _cameraManager.StartAsync(result.PrimaryCameraId, result.PrimaryCameraIndex);

                // 延迟检测设备状态
                Task.Delay(1500).ContinueWith(_ => _deviceStatusMonitor.CheckAllDevicesAsync());
            }
            catch (Exception ex)
            {
                string errMsg = $"加载失败: {ex.Message}";
                MessageBox.Show(errMsg);
                UILogManager.Instance.LogError(errMsg);
            }
        }

        // ==================== INotifyPropertyChanged ====================

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ==================== 清理资源 ====================

        protected override void OnClosed(EventArgs e)
        {
            _cameraManager?.Dispose();
            _logService = null;
            base.OnClosed(e);
        }
    }
}