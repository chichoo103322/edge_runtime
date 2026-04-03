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
    /// </summary>
    public class WorkflowLoader
    {
        private readonly Brush COLOR_PENDING = new SolidColorBrush(Color.FromRgb(80, 80, 80));

        public class LoadResult
        {
            public ObservableCollection<ProcessActionViewModel> ActionColumns { get; set; }
            public List<ProcessStateViewModel> ExecutionQueue { get; set; }
            public HashSet<string> CameraIds { get; set; }
            public string ModelPath { get; set; }
            public string PrimaryCameraId { get; set; }
            public int? PrimaryCameraIndex { get; set; }
        }

        /// <summary>
        /// 从JSON文件加载工作流配置
        /// </summary>
        public LoadResult LoadFromFile(string filepath)
        {
            if (string.IsNullOrEmpty(filepath))
                throw new ArgumentException("文件路径不能为空", nameof(filepath));

            if (!File.Exists(filepath))
                throw new FileNotFoundException($"文件不存在: {filepath}");

            UILogManager.Instance.LogInfo($"开始加载流程文件: {filepath}");

            string json = File.ReadAllText(filepath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var workflow = JsonSerializer.Deserialize<WorkflowStructure>(json, options);

            if (workflow == null || workflow.Actions == null)
                throw new InvalidOperationException("流程文件格式无效");

            return ParseWorkflow(workflow);
        }

        /// <summary>
        /// 解析工作流结构
        /// </summary>
        private LoadResult ParseWorkflow(WorkflowStructure workflow)
        {
            var result = new LoadResult
            {
                ActionColumns = new ObservableCollection<ProcessActionViewModel>(),
                ExecutionQueue = new List<ProcessStateViewModel>(),
                CameraIds = new HashSet<string>(),
                ModelPath = workflow.ModelPath
            };

            foreach (var actionData in workflow.Actions)
            {
                var columnVM = new ProcessActionViewModel { Name = actionData.Name };

                if (actionData.States != null)
                {
                    foreach (var stateData in actionData.States)
                    {
                        var stateVM = CreateStateViewModel(actionData, stateData);

                        // 收集相机ID
                        if (!string.IsNullOrEmpty(stateVM.CameraId))
                        {
                            result.CameraIds.Add(stateVM.CameraId);
                        }

                        columnVM.States.Add(stateVM);
                        result.ExecutionQueue.Add(stateVM);
                    }
                }

                result.ActionColumns.Add(columnVM);
            }

            // 选择首个相机作为默认主相机
            if (result.CameraIds.Count > 0)
            {
                foreach (var id in result.CameraIds)
                {
                    result.PrimaryCameraId = id;
                    int mappedIndex = CameraHelper.GetCameraIndexByName(id);
                    if (mappedIndex >= 0)
                    {
                        result.PrimaryCameraIndex = mappedIndex;
                    }
                    break;
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
        private ProcessStateViewModel CreateStateViewModel(
            ActionStep actionData,
            StateStep stateData)
        {
            var stateVM = new ProcessStateViewModel
            {
                Id = stateData.Id,
                Name = stateData.Name,
                TargetLabel = stateData.SelectedLabel,
                Threshold = stateData.Threshold,
                Background = COLOR_PENDING,
                StationName = actionData.StationName ?? "未配置",
                CameraId = stateData.CameraDevice,
                VideoPath = stateData.StandardVideoPath,
                HasVideo = !string.IsNullOrEmpty(stateData.StandardVideoPath)
            };

            return stateVM;
        }
    }
}
