using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace edge_runtime
{
    public partial class MainWindow : System.Windows.Window
    {
        // UI 绑定的数据源 (对应界面上的几大列)
        public ObservableCollection<ProcessActionViewModel> ActionColumns { get; set; }
            = new ObservableCollection<ProcessActionViewModel>();

        // 逻辑执行队列 (扁平化，严格顺序)
        // 这里的对象引用和 ActionColumns 里的是同一个，所以改这里的颜色，UI会自动变
        private List<ProcessStateViewModel> _executionQueue = new List<ProcessStateViewModel>();
        private int _currentStepIndex = 0;

        // 核心组件
        private VideoCapture _capture;
        private CancellationTokenSource _cts;
        private OnnxInferenceService _aiService;

        // 颜色定义
        private readonly Brush COLOR_PENDING = new SolidColorBrush(Color.FromRgb(80, 80, 80));   // 灰
        private readonly Brush COLOR_RUNNING = new SolidColorBrush(Color.FromRgb(52, 152, 219)); // 蓝
        private readonly Brush COLOR_SUCCESS = new SolidColorBrush(Color.FromRgb(39, 174, 96));  // 绿
        private readonly Brush COLOR_FAIL = new SolidColorBrush(Color.FromRgb(192, 57, 43));  // 红
        private readonly Brush BORDER_HIGHLIGHT = Brushes.Yellow;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this; // 这一步至关重要，让 XAML 能找到 ActionColumns
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

                // 1. 反序列化 (使用你原来的 WorkflowModel 类)
                var workflow = JsonSerializer.Deserialize<WorkflowStructure>(json, options);

                if (workflow == null || workflow.Actions == null) return;

                // 2. 重置数据
                ActionColumns.Clear();
                _executionQueue.Clear();
                _currentStepIndex = 0;

                // 3. 构建 ViewModel 和 执行队列
                foreach (var actionData in workflow.Actions)
                {
                    // 创建 UI 列
                    var columnVM = new ProcessActionViewModel { Name = actionData.Name };

                    if (actionData.States != null)
                    {
                        foreach (var stateData in actionData.States)
                        {
                            // 创建单个步骤卡片
                            var stateVM = new ProcessStateViewModel
                            {
                                Id = stateData.Id,
                                Name = stateData.Name,
                                TargetLabel = stateData.SelectedLabel, // AI 目标
                                Threshold = stateData.Threshold,
                                Background = COLOR_PENDING
                            };

                            // 同时添加到 UI结构 和 执行队列
                            columnVM.States.Add(stateVM);
                            _executionQueue.Add(stateVM);
                        }
                    }
                    ActionColumns.Add(columnVM);
                }

                TxtCurrentStep.Text = "流程已加载，准备就绪";

                // 4. 加载模型并启动
                LoadAiModel(); // 你之前的逻辑
                StartMonitoringLoop();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载失败: {ex.Message}");
            }
        }

        // --- 核心逻辑循环 ---
        private void StartMonitoringLoop()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() =>
            {
                // 打开摄像头 (索引0)
                _capture = new VideoCapture(0);
                if (!_capture.IsOpened()) return;

                using (Mat frame = new Mat())
                {
                    while (!token.IsCancellationRequested)
                    {
                        // 1. 获取画面
                        if (!_capture.Read(frame) || frame.Empty())
                        {
                            Thread.Sleep(10); continue;
                        }

                        // 2. 更新 UI 视频流
                        var bitmap = frame.Clone().ToBitmapSource();
                        bitmap.Freeze();
                        Dispatcher.Invoke(() => VideoFeed.Source = bitmap);

                        // 3. 执行流程逻辑
                        ProcessFlowLogic(frame);

                        Thread.Sleep(30); // 约 30 FPS
                    }
                }
                _capture.Release();
            }, token);
        }

        private void ProcessFlowLogic(Mat frame)
        {
            // 如果全部完成，停止检测
            if (_currentStepIndex >= _executionQueue.Count)
            {
                Dispatcher.Invoke(() => TxtCurrentStep.Text = "所有流程已完成！");
                return;
            }

            // 获取当前必须完成的步骤 (指针指向这里)
            var currentStep = _executionQueue[_currentStepIndex];

            Dispatcher.Invoke(() =>
            {
                TxtCurrentStep.Text = $"当前步骤: {currentStep.Name}";

                // 视觉反馈：将当前步骤设为蓝色（进行中）
                if (currentStep.Background == COLOR_PENDING)
                {
                    currentStep.Background = COLOR_RUNNING;
                    currentStep.BorderColor = BORDER_HIGHLIGHT;
                }
            });

            // 如果没有 AI 服务，或者该步骤没有目标标签（可能是人工确认项），暂时演示为自动通过
            // 实际项目中这里可能需要等待人工按钮点击
            bool isPassed = false;

            if (_aiService != null && !string.IsNullOrEmpty(currentStep.TargetLabel))
            {
                var result = _aiService.Predict(frame);

                // 判定逻辑
                if (result.Label == currentStep.TargetLabel && result.Confidence >= currentStep.Threshold)
                {
                    isPassed = true;
                }
            }
            else
            {
                // 模拟没有AI目标时的自动通过 (调试用，你可以删掉)
                Thread.Sleep(500);
                isPassed = true;
            }

            if (isPassed)
            {
                Dispatcher.Invoke(() =>
                {
                    // 1. 变绿
                    currentStep.Background = COLOR_SUCCESS;
                    currentStep.BorderColor = Brushes.Transparent;
                });

                // 2. 只有当前步骤成功了，索引才 +1，进入下一个（哪怕下一个在另一列）
                _currentStepIndex++;
            }
        }

        private void LoadAiModel()
        {
            // 复用你原来的代码，只需确保路径正确
            string modelPath = "model.onnx";
            if (File.Exists(modelPath))
            {
                var labels = OnnxHelper.ReadLabelsFromModel(modelPath);
                _aiService = new OnnxInferenceService(modelPath, labels);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            _capture?.Dispose();
            base.OnClosed(e);
        }
    }
}