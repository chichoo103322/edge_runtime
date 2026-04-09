using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace edge_runtime.Services
{
    /// <summary>
    /// 工作流加载器 - 负责从JSON加载和解析工作流配置
    /// 核心职责：
    /// 1. 读取JSON配置文件
    /// 2. 反序列化为C#对象
    /// 3. 构建UI视图模型（ViewModel）
    /// 4. 提取相机设备信息
    /// </summary>
    public class WorkflowLoader
    {
        private readonly Brush COLOR_PENDING = new SolidColorBrush(Color.FromRgb(80, 80, 80));

        /// <summary>
        /// 加载结果数据类
        /// </summary>
        public class LoadResult
        {
            /// <summary>
            /// 动作列（UI显示用）
            /// </summary>
            public ObservableCollection<ProcessActionViewModel> ActionColumns { get; set; }

            /// <summary>
            /// 执行队列（流程执行器使用的线性步骤列表）
            /// </summary>
            public List<ProcessStateViewModel> ExecutionQueue { get; set; }

            /// <summary>
            /// 所有用到的相机设备ID集合
            /// </summary>
            public HashSet<string> CameraIds { get; set; }

            /// <summary>
            /// AI模型路径
            /// </summary>
            public string ModelPath { get; set; }

            /// <summary>
            /// 模型输入尺寸（如640、512等）
            /// 用于自适应图像预处理，匹配模型的实际输入要求
            /// </summary>
            public int ModelInputSize { get; set; } = 640;

            /// <summary>
            /// 主相机ID（默认使用的第一个相机）
            /// </summary>
            public string PrimaryCameraId { get; set; }

            /// <summary>
            /// 主相机索引（如果ID映射到索引成功）
            /// </summary>
            public int? PrimaryCameraIndex { get; set; }
        }

        /// <summary>
        /// 从JSON文件加载工作流配置
        /// </summary>
        /// <param name="filepath">JSON文件路径</param>
        /// <returns>加载结果（包含UI模型和执行队列）</returns>
        /// <exception cref="ArgumentException">文件路径为空</exception>
        /// <exception cref="FileNotFoundException">文件不存在</exception>
        /// <exception cref="InvalidOperationException">JSON格式无效</exception>
        public LoadResult LoadFromFile(string filepath)
        {
            // 参数验证
            if (string.IsNullOrEmpty(filepath))
                throw new ArgumentException("文件路径不能为空", nameof(filepath));

            if (!File.Exists(filepath))
                throw new FileNotFoundException($"文件不存在: {filepath}");

            UILogManager.Instance.LogInfo($"开始加载流程文件: {filepath}");

            // 读取并反序列化JSON
            string json = File.ReadAllText(filepath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var workflow = JsonSerializer.Deserialize<WorkflowStructure>(json, options);

            // 验证JSON结构
            if (workflow == null || workflow.Actions == null)
                throw new InvalidOperationException("流程文件格式无效");

            return ParseWorkflow(workflow);
        }

        /// <summary>
        /// 解析工作流结构为UI模型
        /// </summary>
        /// <param name="workflow">反序列化后的工作流对象</param>
        /// <returns>加载结果</returns>
        private LoadResult ParseWorkflow(WorkflowStructure workflow)
        {
            var result = new LoadResult
            {
                ActionColumns = new ObservableCollection<ProcessActionViewModel>(),
                ExecutionQueue = new List<ProcessStateViewModel>(),
                CameraIds = new HashSet<string>(),
                ModelPath = workflow.ModelPath,
                ModelInputSize = workflow.ModelInputSize  // 从JSON配置读取模型输入尺寸
            };

            // 遍历所有动作（Actions）
            foreach (var actionData in workflow.Actions)
            {
                var columnVM = new ProcessActionViewModel { Name = actionData.Name };

                // 遍历该动作下的所有步骤（States）
                if (actionData.States != null)
                {
                    foreach (var stateData in actionData.States)
                    {
                        // 创建步骤视图模型
                        var stateVM = CreateStateViewModel(actionData, stateData);

                        // 收集相机ID（去重）
                        if (!string.IsNullOrEmpty(stateVM.CameraId))
                        {
                            result.CameraIds.Add(stateVM.CameraId);
                        }

                        // 添加到列视图（UI显示）
                        columnVM.States.Add(stateVM);

                        // 添加到执行队列（流程执行器使用）
                        result.ExecutionQueue.Add(stateVM);
                    }
                }

                result.ActionColumns.Add(columnVM);
            }

            // 确定主相机（使用第一个相机作为默认）
            if (result.CameraIds.Count > 0)
            {
                foreach (var id in result.CameraIds)
                {
                    result.PrimaryCameraId = id;

                    // 尝试将相机ID映射为索引
                    int mappedIndex = CameraHelper.GetCameraIndexByName(id);
                    if (mappedIndex >= 0)
                    {
                        result.PrimaryCameraIndex = mappedIndex;
                    }
                    break; // 只取第一个
                }
            }

            UILogManager.Instance.LogInfo(
                $"流程加载完成: {result.ExecutionQueue.Count} 个步骤, {result.CameraIds.Count} 个设备"
            );

            return result;
        }

        /// <summary>
        /// 创建步骤视图模型
        /// </summary>
        /// <param name="actionData">动作数据（来自JSON）</param>
        /// <param name="stateData">步骤数据（来自JSON）</param>
        /// <returns>步骤视图模型</returns>
        private ProcessStateViewModel CreateStateViewModel(
            ActionStep actionData,
            StateStep stateData)
        {
            var stateVM = new ProcessStateViewModel
            {
                Id = stateData.Id,
                Name = stateData.Name,
                TargetLabel = stateData.SelectedLabel,    // AI需要识别的动作标签
                Threshold = stateData.Threshold,          // 识别置信度阈值
                Background = COLOR_PENDING,               // 初始状态为灰色（待机）
                StationName = actionData.StationName ?? "未配置",
                CameraId = stateData.CameraDevice,        // 该步骤使用的相机
                VideoPath = stateData.StandardVideoPath,  // 标准作业指导视频路径
                HasVideo = !string.IsNullOrEmpty(stateData.StandardVideoPath)
            };

            return stateVM;
        }
    }
}
