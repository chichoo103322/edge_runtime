using System.Collections.Generic;

namespace edge_runtime
{
    // 对应 JSON 的根节点
    public class WorkflowStructure
    {
        public List<ActionStep> Actions { get; set; } = new List<ActionStep>();
    }

    // 对应 Actions 列表里的每一项
    public class ActionStep
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string StationName { get; set; }
        public List<StateStep> States { get; set; } = new List<StateStep>();
    }

    // 对应 States 列表里的每一项
    public class StateStep
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string SelectedLabel { get; set; }
        public double Threshold { get; set; }
        public string CameraDevice { get; set; }
    }
}