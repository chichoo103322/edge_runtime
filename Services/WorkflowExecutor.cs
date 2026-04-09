using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace edge_runtime.Services
{
    /// <summary>
    /// 流程执行器 - 负责工艺流程的执行逻辑
    /// 核心职责：
    /// 1. 管理流程步骤的状态转换（待机 -> 运行中 -> 成功/失败）
    /// 2. 调用AI服务进行动作识别
    /// 3. 检测错误动作和超时
    /// 4. 记录日志和保存错误截图
    /// </summary>
    public class WorkflowExecutor
    {
        private readonly List<ProcessStateViewModel> _executionQueue;
        private readonly OnnxInferenceService _aiService;
        private readonly LogService _logService;
        private readonly Action _onWorkflowComplete;
        private readonly Action<ProcessStateViewModel> _onStepChanged;

        /// <summary>
        /// 当前执行到的步骤索引
        /// </summary>
        private int _currentStepIndex = 0;

        /// <summary>
        /// 每个步骤的最后检测时间（用于超时判断）
        /// </summary>
        private readonly Dictionary<int, DateTime> _stepLastDetectTime = new Dictionary<int, DateTime>();

        /// <summary>
        /// 超时阈值（秒）
        /// </summary>
        private const int DETECTION_TIMEOUT_SECONDS = 10;

        /// <summary>
        /// 错误动作标签集合（AI识别到这些动作时记录为NG）
        /// </summary>
        private static readonly HashSet<string> ERROR_ACTIONS = new HashSet<string>
        {
            "Wrong_Action", "UsingPhone", "WrongHand",
            "NotWearing", "Distracted", "IncorrectPosture"
        };

        // 步骤状态颜色定义
        private readonly Brush COLOR_PENDING = new SolidColorBrush(Color.FromRgb(80, 80, 80));    // 灰色 - 待机
        private readonly Brush COLOR_RUNNING = new SolidColorBrush(Color.FromRgb(52, 152, 219));  // 蓝色 - 运行中
        private readonly Brush COLOR_SUCCESS = new SolidColorBrush(Color.FromRgb(39, 174, 96));   // 绿色 - 成功
        private readonly Brush COLOR_ERROR = new SolidColorBrush(Color.FromRgb(239, 83, 80));     // 红色 - 错误
        private readonly Brush BORDER_HIGHLIGHT = Brushes.Yellow;                                  // 黄色边框 - 高亮

        /// <summary>
        /// 获取当前执行的步骤
        /// </summary>
        public ProcessStateViewModel CurrentStep => _currentStepIndex < _executionQueue.Count 
            ? _executionQueue[_currentStepIndex] 
            : null;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="executionQueue">执行队列（步骤列表）</param>
        /// <param name="aiService">AI推理服务</param>
        /// <param name="logService">日志服务</param>
        /// <param name="onWorkflowComplete">流程完成回调</param>
        /// <param name="onStepChanged">步骤切换回调</param>
        public WorkflowExecutor(
            List<ProcessStateViewModel> executionQueue,
            OnnxInferenceService aiService,
            LogService logService,
            Action onWorkflowComplete,
            Action<ProcessStateViewModel> onStepChanged)
        {
            _executionQueue = executionQueue ?? throw new ArgumentNullException(nameof(executionQueue));
            _aiService = aiService;
            _logService = logService;
            _onWorkflowComplete = onWorkflowComplete;
            _onStepChanged = onStepChanged;
        }

        /// <summary>
        /// 处理每一帧的流程逻辑（主入口）
        /// </summary>
        /// <param name="frame">相机帧（OpenCV Mat格式）</param>
        public void ProcessFrame(Mat frame)
        {
            // 前置条件：AI服务必须已加载
            if (_aiService == null)
                return;

            // 检查是否所有步骤已完成
            if (_currentStepIndex >= _executionQueue.Count)
            {
                HandleWorkflowComplete();
                return;
            }

            var currentStep = _executionQueue[_currentStepIndex];

            // 初始化当前步骤的计时器（首次进入该步骤时）
            if (!_stepLastDetectTime.ContainsKey(_currentStepIndex))
            {
                _stepLastDetectTime[_currentStepIndex] = DateTime.Now;
            }

            // 更新UI：标记当前步骤为运行中
            UpdateStepUI(currentStep);

            // 执行AI识别
            var result = _aiService.Predict(frame);

            // 在图像上绘制检测结果（边界框和标签）
            if (result.Label != "Unknown" && result.Confidence >= 0.25f)
            {
                // 1. 根据 Label 字符串动态计算稳定的 BGR 颜色
                // 使用 HashCode 确保同一个标签每次生成的颜色绝对一致，同时加上 100 保证颜色足够明亮显眼
                int hash = result.Label.GetHashCode();
                int b = (Math.Abs(hash) % 155) + 100;
                int g = (Math.Abs(hash >> 8) % 155) + 100;
                int r = (Math.Abs(hash >> 16) % 155) + 100;
                Scalar dynamicBoxColor = new Scalar(b, g, r);

                // 2. 绘制边界框
                Cv2.Rectangle(frame, result.Box, dynamicBoxColor, 2, LineTypes.AntiAlias);

                // 3. 绘制文字背景和置信度标签
                string text = $"{result.Label} {result.Confidence:F2}";
                var textSize = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, 0.7, 2, out int baseline);
                int textY = Math.Max(0, result.Box.Y - textSize.Height - 10);
                var textBgRect = new OpenCvSharp.Rect(result.Box.X, textY, textSize.Width + 10, textSize.Height + 10);

                Cv2.Rectangle(frame, textBgRect, dynamicBoxColor, -1);
                Cv2.PutText(frame, text, new OpenCvSharp.Point(result.Box.X + 5, textY + textSize.Height + 2), 
                           HersheyFonts.HersheySimplex, 0.7, Scalar.Black, 2, LineTypes.AntiAlias);
            }

            // 检测1：错误动作
            if (ERROR_ACTIONS.Contains(result.Label))
            {
                HandleErrorAction(currentStep, frame);
                return;
            }

            // 检测2：连续超时（工人长时间未完成动作）
            if (IsStepTimeout())
            {
                HandleTimeout(currentStep, frame);
                return;
            }

            // 检测3：正确动作
            if (IsCorrectAction(currentStep, result))
            {
                HandleCorrectAction(currentStep);
                return;
            }

            // 等待状态：识别置信度不够或未检测到目标动作，继续等待
        }

        /// <summary>
        /// 重置流程到初始状态（用于开始新一轮产品）
        /// </summary>
        public void Reset()
        {
            _currentStepIndex = 0;
            _stepLastDetectTime.Clear();

            // 重置所有步骤的UI状态为待机
            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var step in _executionQueue)
                {
                    step.Background = COLOR_PENDING;
                    step.BorderColor = Brushes.Transparent;
                }
            });
        }

        /// <summary>
        /// 处理流程完成逻辑
        /// </summary>
        private void HandleWorkflowComplete()
        {
            // 记录产品完成日志
            _logService?.LogToDb("Product_Complete", "Complete");
            _stepLastDetectTime.Clear();

            // 延迟1秒后重置
            Thread.Sleep(1000);
            Reset();

            // 触发垃圾回收（清理上一轮产品的内存）
            GC.Collect(0, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();

            // 通知UI
            _onWorkflowComplete?.Invoke();
        }

        /// <summary>
        /// 更新步骤UI状态为"运行中"
        /// </summary>
        /// <param name="currentStep">当前步骤</param>
        private void UpdateStepUI(ProcessStateViewModel currentStep)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // 通知MainWindow更新步骤信息
                _onStepChanged?.Invoke(currentStep);

                // 如果是首次进入该步骤，更新背景色和边框
                if (currentStep.Background == COLOR_PENDING)
                {
                    currentStep.Background = COLOR_RUNNING;
                    currentStep.BorderColor = BORDER_HIGHLIGHT;
                }
            });
        }

        /// <summary>
        /// 处理错误动作（NG）
        /// </summary>
        /// <param name="currentStep">当前步骤</param>
        /// <param name="frame">错误帧</param>
        private void HandleErrorAction(ProcessStateViewModel currentStep, Mat frame)
        {
            // 更新UI为红色
            Application.Current?.Dispatcher.Invoke(() =>
            {
                currentStep.Background = COLOR_ERROR;
                currentStep.BorderColor = Brushes.Transparent;
            });

            // 克隆 frame 避免在异步保存时 frame 被修改或释放
            using (Mat frameCopy = frame.Clone())
            {
                // 保存截图并记录到数据库
                string imagePath = _logService?.SaveFrame(frameCopy, currentStep.Name, "NG");
                _logService?.LogToDb(currentStep.Name, "NG", imagePath);
            }
        }

        /// <summary>
        /// 判断当前步骤是否超时
        /// </summary>
        /// <returns>是否超时</returns>
        private bool IsStepTimeout()
        {
            TimeSpan elapsed = DateTime.Now - _stepLastDetectTime[_currentStepIndex];
            return elapsed.TotalSeconds > DETECTION_TIMEOUT_SECONDS;
        }

        /// <summary>
        /// 处理超时情况
        /// </summary>
        /// <param name="currentStep">当前步骤</param>
        /// <param name="frame">超时帧</param>
        private void HandleTimeout(ProcessStateViewModel currentStep, Mat frame)
        {
            // 更新UI为红色
            Application.Current?.Dispatcher.Invoke(() =>
            {
                currentStep.Background = COLOR_ERROR;
                currentStep.BorderColor = Brushes.Transparent;
            });

            // 克隆 frame 避免在异步保存时 frame 被修改或释放
            using (Mat frameCopy = frame.Clone())
            {
                // 保存超时截图
                string timeoutPath = _logService?.SaveFrame(frameCopy, currentStep.Name, "TIMEOUT");
                _logService?.LogToDb(currentStep.Name, "TIMEOUT", timeoutPath);
            }

            // 重置计时器（准备下一次检测）
            _stepLastDetectTime[_currentStepIndex] = DateTime.Now;
        }

        /// <summary>
        /// 判断是否检测到正确动作
        /// </summary>
        /// <param name="currentStep">当前步骤</param>
        /// <param name="result">AI识别结果</param>
        /// <returns>是否正确</returns>
        private bool IsCorrectAction(ProcessStateViewModel currentStep, OnnxInferenceService.Prediction result)
        {
            return !string.IsNullOrEmpty(currentStep.TargetLabel) &&
                   result.Label == currentStep.TargetLabel &&
                   result.Confidence >= currentStep.Threshold;
        }

        /// <summary>
        /// 处理正确动作（步骤完成）
        /// </summary>
        /// <param name="currentStep">当前步骤</param>
        private void HandleCorrectAction(ProcessStateViewModel currentStep)
        {
            // 更新UI为绿色
            Application.Current?.Dispatcher.Invoke(() =>
            {
                currentStep.Background = COLOR_SUCCESS;
                currentStep.BorderColor = Brushes.Transparent;
            });

            // 记录成功日志
            _logService?.LogToDb(currentStep.Name, "OK");

            // 清除计时器
            _stepLastDetectTime.Remove(_currentStepIndex);

            // 进入下一步
            _currentStepIndex++;
        }
    }
}
