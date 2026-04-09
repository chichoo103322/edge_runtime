using System.Collections.Generic;

namespace edge_runtime
{
    // 对应 JSON 的根节点
    public class WorkflowStructure
    {
        public List<ActionStep> Actions { get; set; } = new List<ActionStep>();
        public string ModelPath { get; set; } = string.Empty;  // AI 模型路径
        /// <summary>
        /// 模型输入尺寸（如640、512等）
        /// 用于自适应图像预处理
        /// 如果未指定，默认使用640
        /// </summary>
        public int ModelInputSize { get; set; } = 640;
    }

    // 对应 Actions 列表里的每一项
    public class ActionStep
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string StationName { get; set; }
        public List<StateStep> States { get; set; } = new List<StateStep>();
    }

    // 对应每个 State 的定义（JSON 中的元素）
    public class StateStep
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string SelectedLabel { get; set; }
        public double Threshold { get; set; }

        // 新增：从 JSON 中读取相机字段（例如 "cameraDevice"）
        public string CameraDevice { get; set; }

        // 新增：标准作业指导视频路径
        public string StandardVideoPath { get; set; } = string.Empty;
    }
}