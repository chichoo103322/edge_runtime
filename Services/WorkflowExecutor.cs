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
    /// </summary>
    public class WorkflowExecutor
    {
        private readonly List<ProcessStateViewModel> _executionQueue;
        private readonly OnnxInferenceService _aiService;
        private readonly LogService _logService;
        private readonly Action _onWorkflowComplete;
        private readonly Action<ProcessStateViewModel> _onStepChanged;

        private int _currentStepIndex = 0;
        private readonly Dictionary<int, DateTime> _stepLastDetectTime = new Dictionary<int, DateTime>();
        private const int DETECTION_TIMEOUT_SECONDS = 10;

        // 错误动作定义
        private static readonly HashSet<string> ERROR_ACTIONS = new HashSet<string>
        {
            "Wrong_Action", "UsingPhone", "WrongHand",
            "NotWearing", "Distracted", "IncorrectPosture"
        };

        // 颜色定义
        private readonly Brush COLOR_PENDING = new SolidColorBrush(Color.FromRgb(80, 80, 80));
        private readonly Brush COLOR_RUNNING = new SolidColorBrush(Color.FromRgb(52, 152, 219));
        private readonly Brush COLOR_SUCCESS = new SolidColorBrush(Color.FromRgb(39, 174, 96));
        private readonly Brush COLOR_ERROR = new SolidColorBrush(Color.FromRgb(239, 83, 80));
        private readonly Brush BORDER_HIGHLIGHT = Brushes.Yellow;

        public ProcessStateViewModel CurrentStep => _currentStepIndex < _executionQueue.Count 
            ? _executionQueue[_currentStepIndex] 
            : null;

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
        /// 处理每一帧的流程逻辑
        /// </summary>
        public void ProcessFrame(Mat frame)
        {
            // 如果 AI 服务未加载，不做任何处理
            if (_aiService == null)
                return;

            // 检查是否所有步骤已完成
            if (_currentStepIndex >= _executionQueue.Count)
            {
                HandleWorkflowComplete();
                return;
            }

            var currentStep = _executionQueue[_currentStepIndex];

            // 初始化当前步骤的计时器
            if (!_stepLastDetectTime.ContainsKey(_currentStepIndex))
            {
                _stepLastDetectTime[_currentStepIndex] = DateTime.Now;
            }

            // 更新UI：标记当前步骤为运行中
            UpdateStepUI(currentStep);

            // 获取AI识别结果
            var result = _aiService.Predict(frame);

            // 异常检测：检测到错误动作
            if (ERROR_ACTIONS.Contains(result.Label))
            {
                HandleErrorAction(currentStep, frame);
                return;
            }

            // 异常检测：连续超时
            if (IsStepTimeout())
            {
                HandleTimeout(currentStep, frame);
                return;
            }

            // 正确行为判定
            if (IsCorrectAction(currentStep, result))
            {
                HandleCorrectAction(currentStep);
                return;
            }

            // 等待状态（低置信度或未识别到目标标签）
        }

        /// <summary>
        /// 重置流程到初始状态
        /// </summary>
        public void Reset()
        {
            _currentStepIndex = 0;
            _stepLastDetectTime.Clear();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var step in _executionQueue)
                {
                    step.Background = COLOR_PENDING;
                    step.BorderColor = Brushes.Transparent;
                }
            });
        }

        private void HandleWorkflowComplete()
        {
            _logService?.LogToDb("Product_Complete", "Complete");
            _stepLastDetectTime.Clear();

            Thread.Sleep(1000);
            Reset();

            // 强制垃圾回收
            GC.Collect(0, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();

            _onWorkflowComplete?.Invoke();
        }

        private void UpdateStepUI(ProcessStateViewModel currentStep)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _onStepChanged?.Invoke(currentStep);

                if (currentStep.Background == COLOR_PENDING)
                {
                    currentStep.Background = COLOR_RUNNING;
                    currentStep.BorderColor = BORDER_HIGHLIGHT;
                }
            });
        }

        private void HandleErrorAction(ProcessStateViewModel currentStep, Mat frame)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                currentStep.Background = COLOR_ERROR;
                currentStep.BorderColor = Brushes.Transparent;
            });

            string imagePath = _logService?.SaveFrame(frame, currentStep.Name, "NG");
            _logService?.LogToDb(currentStep.Name, "NG", imagePath);
        }

        private bool IsStepTimeout()
        {
            TimeSpan elapsed = DateTime.Now - _stepLastDetectTime[_currentStepIndex];
            return elapsed.TotalSeconds > DETECTION_TIMEOUT_SECONDS;
        }

        private void HandleTimeout(ProcessStateViewModel currentStep, Mat frame)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                currentStep.Background = COLOR_ERROR;
                currentStep.BorderColor = Brushes.Transparent;
            });

            string timeoutPath = _logService?.SaveFrame(frame, currentStep.Name, "TIMEOUT");
            _logService?.LogToDb(currentStep.Name, "TIMEOUT", timeoutPath);

            // 重置计时器，准备下一次检测
            _stepLastDetectTime[_currentStepIndex] = DateTime.Now;
        }

        private bool IsCorrectAction(ProcessStateViewModel currentStep, OnnxInferenceService.Prediction result)
        {
            return !string.IsNullOrEmpty(currentStep.TargetLabel) &&
                   result.Label == currentStep.TargetLabel &&
                   result.Confidence >= currentStep.Threshold;
        }

        private void HandleCorrectAction(ProcessStateViewModel currentStep)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                currentStep.Background = COLOR_SUCCESS;
                currentStep.BorderColor = Brushes.Transparent;
            });

            _logService?.LogToDb(currentStep.Name, "OK");
            _stepLastDetectTime.Remove(_currentStepIndex);

            // 进入下一步
            _currentStepIndex++;
        }
    }
}
