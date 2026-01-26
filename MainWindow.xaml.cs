using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions; // 必须引用 OpenCvSharp.WpfExtensions
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using edge_runtime; // 确认 OnnxHelper.cs 里的 namespace 是什么

namespace edge_runtime
{
    public partial class MainWindow : System.Windows.Window, INotifyPropertyChanged
    {
        // 核心成员变量
        private VideoCapture _capture;
        private CancellationTokenSource _cts;
        private OnnxInferenceService _aiService;
        private int _currentStepIndex = 0; // 当前执行到第几步

        // UI 数据源
        public ObservableCollection<RuntimeNodeView> RuntimeNodes { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this; // 设置数据上下文，方便 XAML 绑定
            RuntimeNodes = new ObservableCollection<RuntimeNodeView>();
        }

        #region 核心逻辑：加载并解析 JSON

        private void BtnLoadProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "流程文件 (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(dlg.FileName);

                    // 配置 JSON 忽略大小写，防止字段名大小写不一致导致读取失败
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    // 1. 反序列化嵌套结构
                    var workflow = JsonSerializer.Deserialize<WorkflowStructure>(json, options);

                    if (workflow == null || workflow.Actions == null)
                    {
                        MessageBox.Show("文件格式不正确，未找到 Actions 节点");
                        return;
                    }

                    // 2. 初始化环境
                    RuntimeNodes.Clear();
                    _currentStepIndex = 0;

                    // 3. 【关键】将嵌套结构“展平”为线性执行列表
                    // 这样运行时只需要傻瓜式地按顺序执行 RuntimeNodes[0] -> [1] -> [2]...
                    foreach (var action in workflow.Actions)
                    {
                        // 添加主动作 (Action)
                        RuntimeNodes.Add(new RuntimeNodeView
                        {
                            NodeName = $"【步骤】{action.Name}", // 加粗或特殊标记
                            StatusColor = Brushes.Gray,
                            TargetLabel = action.Name, // 假设动作本身也作为一种状态（如果动作只作为标题，可设 Threshold=0 跳过）
                            Threshold = 0.5f,          // 默认阈值，或者你可以给 Action 加个字段
                            IsActionHeader = true      // 标记这是主标题
                        });

                        // 添加该动作下的所有子状态 (States)
                        if (action.States != null)
                        {
                            foreach (var state in action.States)
                            {
                                RuntimeNodes.Add(new RuntimeNodeView
                                {
                                    NodeName = $"    ↳ {state.Name}", // 缩进显示
                                    StatusColor = Brushes.Gray,
                                    TargetLabel = state.SelectedLabel,
                                    Threshold = (float)state.Threshold,
                                    IsActionHeader = false
                                });
                            }
                        }
                    }

                    // 标记最后一个节点 (用于UI处理)
                    if (RuntimeNodes.Count > 0)
                        RuntimeNodes.Last().IsLast = true;

                    // 4. 尝试加载 AI 模型
                    LoadAiModel();

                    // 5. 启动摄像头和检测循环
                    StartMonitoringLoop();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载失败: {ex.Message}\n\n堆栈: {ex.StackTrace}");
                }
            }
        }

        private void LoadAiModel()
        {
            // 这里假设模型就在 exe 旁边，或者你可以写死绝对路径测试
            // 如果你有特定的模型路径，请在这里修改
            string modelPath = "model.onnx";

            if (File.Exists(modelPath))
            {
                // 假设 OnnxHelper 能从模型里读出 labels，如果不行，你需要手动传入 string[] labels
                var labels = OnnxHelper.ReadLabelsFromModel(modelPath);
                _aiService = new OnnxInferenceService(modelPath, labels);
                Console.WriteLine("模型加载成功！");
            }
            else
            {
                // 如果没有模型，暂时不报错，方便你先调试流程 UI
                // MessageBox.Show("未找到 model.onnx，将仅显示视频，无法进行 AI 判定。");
                _aiService = null;
            }
        }

        #endregion

        #region 核心逻辑：视频流与推理循环

        private void StartMonitoringLoop()
        {
            // 停止之前的任务
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() =>
            {
                // 1. 打开摄像头 (默认索引 0)
                // TODO: 未来根据 JSON 里的 "CameraDevice" 来选择索引
                _capture = new VideoCapture(0);

                if (!_capture.IsOpened())
                {
                    Application.Current.Dispatcher.Invoke(() => MessageBox.Show("无法打开摄像头！"));
                    return;
                }

                using (Mat frame = new Mat())
                {
                    while (!token.IsCancellationRequested)
                    {
                        // 读取视频帧
                        if (!_capture.Read(frame) || frame.Empty())
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        // 2. 更新 UI 视频显示 (必须切回 UI 线程)
                        // Clone 是为了防止多线程资源竞争
                        var bitmap = frame.Clone().ToBitmapSource();
                        bitmap.Freeze(); // 冻结对象以便跨线程访问

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            VideoFeed.Source = bitmap;

                            // 实时更新当前正在进行的步骤高亮（可选）
                            UpdateCurrentStepVisuals();
                        });

                        // 3. 执行 AI 推理与逻辑判断
                        ProcessLogic(frame);

                        // 控制帧率 (~30FPS)
                        Thread.Sleep(33);
                    }
                }
                _capture.Release();
            }, token);
        }

        private void ProcessLogic(Mat frame)
        {
            // 如果全部步骤已完成，或者没有模型，就不做处理
            if (_currentStepIndex >= RuntimeNodes.Count || _aiService == null)
                return;

            // 获取当前需要检测的节点
            var currentNode = RuntimeNodes[_currentStepIndex];

            // AI 推理
            // 修复：OnnxInferenceService 提供的方法名是 Predict，而不是 Infer
            var result = _aiService.Predict(frame);

            // 逻辑判定：标签匹配且置信度达标
            // 注意：如果 currentNode.TargetLabel 为空，可能是标题节点，直接通过
            bool isPassed = false;

            if (string.IsNullOrEmpty(currentNode.TargetLabel))
            {
                isPassed = true; // 没有目标标签，视为仅展示用的标题，直接通过
            }
            else
            {
                // result 为值类型（struct），这里以 Label 为空判断未识别
                if (!string.IsNullOrEmpty(result.Label) &&
                    result.Label == currentNode.TargetLabel &&
                    result.Confidence >= currentNode.Threshold)
                {
                    isPassed = true;
                }
            }

            // 如果通过
            if (isPassed)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // 变绿
                    currentNode.StatusColor = Brushes.LightGreen;
                    currentNode.IsCompleted = true; // 可以加个勾选图标
                });

                // 移动到下一步
                _currentStepIndex++;

                // 如果全部完成
                if (_currentStepIndex >= RuntimeNodes.Count)
                {
                    Application.Current.Dispatcher.Invoke(() => MessageBox.Show("所有工序已完成！"));
                }
            }
        }

        private void UpdateCurrentStepVisuals()
        {
            // 这里可以用来高亮当前正在检测的那一行（比如加粗或者背景色变黄）
            // 简单实现略
        }

        #endregion

        #region 窗口关闭清理

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _cts?.Cancel();
            if (_capture != null && !_capture.IsDisposed)
                _capture.Release();
        }

        #endregion

        // 实现 INotifyPropertyChanged 接口
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 列表项的 ViewModel，控制 UI 显示
    /// </summary>
    public class RuntimeNodeView : INotifyPropertyChanged
    {
        private Brush _statusColor;
        public string NodeName { get; set; }

        public string TargetLabel { get; set; }
        public float Threshold { get; set; }

        public bool IsActionHeader { get; set; } // 是否是主标题
        public bool IsLast { get; set; } // 是否是最后一行（用于隐藏箭头）
        public bool IsCompleted { get; set; }

        public Brush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}