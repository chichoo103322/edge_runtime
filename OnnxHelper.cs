using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime; // 确保安装了 NuGet 包
using edge_runtime;

namespace edge_runtime
{
    public static class OnnxHelper
    {
        public static List<string> ReadLabelsFromModel(string modelPath)
        {
            var labels = new List<string>();
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)) return labels;

            try
            {
                // 读取 ONNX 元数据
                using var session = new InferenceSession(modelPath);
                var metadata = session.ModelMetadata.CustomMetadataMap;

                // 1. 尝试读取 YOLO 格式的 names (通常是 JSON 字符串)
                if (metadata.ContainsKey("names"))
                {
                    string namesJson = metadata["names"];
                    // 简单正则提取 'label' 或 "label"
                    var matches = Regex.Matches(namesJson, "['\"](.*?)['\"]");
                    foreach (Match match in matches)
                    {
                        // 过滤掉 key (如 "0":) 只保留 value
                        if (!match.Value.Contains(":"))
                            labels.Add(match.Groups[1].Value);
                    }
                }

                // 2. 如果没读到，尝试读取输出节点名作为保底
                if (labels.Count == 0)
                {
                    foreach (var output in session.OutputMetadata) labels.Add(output.Key);
                }
            }
            catch (Exception ex)
            {
                labels.Add("Error: " + ex.Message);
            }
            return labels;
        }
    }
}