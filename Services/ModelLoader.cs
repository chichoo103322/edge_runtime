using System;
using System.IO;
using System.Windows;

namespace edge_runtime.Services
{
    /// <summary>
    /// AI模型加载器 - 负责加载和初始化ONNX模型
    /// </summary>
    public class ModelLoader
    {
        /// <summary>
        /// 加载AI模型
        /// </summary>
        /// <returns>加载成功的推理服务实例，失败返回null</returns>
        public OnnxInferenceService LoadModel(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
            {
                string msg = "模型路径为空，将跳过 AI 模型加载";
                MessageBox.Show(msg);
                UILogManager.Instance.LogWarning(msg);
                return null;
            }

            // 模型路径容错：如果绝对路径不存在，在 BaseDirectory 下寻找同名文件
            string finalModelPath = ResolveModelPath(modelPath);

            if (!File.Exists(finalModelPath))
            {
                string msg = $"模型文件不存在: {modelPath}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogError(msg);
                return null;
            }

            try
            {
                UILogManager.Instance.LogInfo($"正在加载 AI 模型: {finalModelPath}");
                var labels = OnnxHelper.ReadLabelsFromModel(finalModelPath);
                var aiService = new OnnxInferenceService(finalModelPath, labels);

                string msg = $"AI 模型已成功加载: {finalModelPath}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogInfo(msg);

                return aiService;
            }
            catch (Exception ex)
            {
                string msg = $"加载 AI 模型失败: {ex.Message}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogError(msg);
                return null;
            }
        }

        /// <summary>
        /// 解析模型路径（支持相对路径和绝对路径）
        /// </summary>
        private string ResolveModelPath(string modelPath)
        {
            if (File.Exists(modelPath))
                return modelPath;

            string fileName = Path.GetFileName(modelPath);
            string alternativePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            if (File.Exists(alternativePath))
            {
                UILogManager.Instance.LogInfo($"模型路径已自动更正: {alternativePath}");
                return alternativePath;
            }

            return modelPath;
        }
    }
}
