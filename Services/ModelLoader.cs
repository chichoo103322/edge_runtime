using System;
using System.IO;
using System.Windows;

namespace edge_runtime.Services
{
    /// <summary>
    /// AI模型加载器 - 负责加载和初始化ONNX模型
    /// 核心职责：
    /// 1. 解析模型路径（支持相对路径和绝对路径）
    /// 2. 加载模型标签（从模型元数据中读取）
    /// 3. 创建推理服务实例
    /// </summary>
    public class ModelLoader
    {
        /// <summary>
        /// 加载AI模型
        /// </summary>
        /// <param name="modelPath">模型文件路径（可以是相对路径或绝对路径）</param>
        /// <param name="modelInputSize">模型输入尺寸（默认640）</param>
        /// <returns>加载成功的推理服务实例，失败返回null</returns>
        public OnnxInferenceService LoadModel(string modelPath, int modelInputSize = 640)
        {
            // 验证：路径不能为空
            if (string.IsNullOrEmpty(modelPath))
            {
                string msg = "模型路径为空，将跳过 AI 模型加载";
                MessageBox.Show(msg);
                UILogManager.Instance.LogWarning(msg);
                return null;
            }

            // 解析路径（支持相对路径）
            string finalModelPath = ResolveModelPath(modelPath);

            // 验证：文件必须存在
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

                // 从模型元数据读取标签列表
                var labels = OnnxHelper.ReadLabelsFromModel(finalModelPath);

                // 创建推理服务（传入模型输入尺寸，支持自适应预处理）
                var aiService = new OnnxInferenceService(finalModelPath, labels, modelInputSize);

                string msg = $"AI 模型已成功加载: {finalModelPath} (输入尺寸: {modelInputSize}x{modelInputSize})";
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
        /// 如果提供的是相对路径，会在应用程序目录下查找同名文件
        /// </summary>
        /// <param name="modelPath">原始模型路径</param>
        /// <returns>解析后的绝对路径</returns>
        private string ResolveModelPath(string modelPath)
        {
            // 如果是绝对路径且文件存在，直接返回
            if (File.Exists(modelPath))
                return modelPath;

            // 尝试在应用程序目录下查找同名文件
            string fileName = Path.GetFileName(modelPath);
            string alternativePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            if (File.Exists(alternativePath))
            {
                UILogManager.Instance.LogInfo($"模型路径已自动更正: {alternativePath}");
                return alternativePath;
            }

            // 如果都找不到，返回原路径（后续会报错）
            return modelPath;
        }
    }
}
