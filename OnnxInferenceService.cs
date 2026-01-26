using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace edge_runtime
{
    public class OnnxInferenceService
    {
        private InferenceSession _session;
        private List<string> _labels;

        public OnnxInferenceService(string modelPath, List<string> labels)
        {
            _session = new InferenceSession(modelPath);
            _labels = labels;
        }

        // 识别结果结构体
        public struct Prediction { public string Label; public float Confidence; }

        public Prediction Predict(Mat frame)
        {
            // 1. 图像预处理 (根据你的模型需求调整，这里以 224x224 为例)
            var resized = frame.Resize(new Size(224, 224));
            var tensor = new DenseTensor<float>(new[] { 1, 3, 224, 224 });

            // 简单的像素归一化填充
            for (int y = 0; y < 224; y++)
                for (int x = 0; x < 224; x++)
                {
                    var color = resized.At<Vec3b>(y, x);
                    tensor[0, 0, y, x] = color.Item2 / 255.0f; // R
                    tensor[0, 1, y, x] = color.Item1 / 255.0f; // G
                    tensor[0, 2, y, x] = color.Item0 / 255.0f; // B
                }

            // 2. 运行推理
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", tensor) };
            using var results = _session.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();

            // 3. 获取置信度最高的标签
            int maxIndex = Array.IndexOf(output, output.Max());
            return new Prediction
            {
                Label = _labels.Count > maxIndex ? _labels[maxIndex] : "Unknown",
                Confidence = output[maxIndex]
            };
        }
    }
}