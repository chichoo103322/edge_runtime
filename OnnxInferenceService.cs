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
        private string _inputName;
        private string _outputName;

        public OnnxInferenceService(string modelPath, List<string> labels)
        {
            _session = new InferenceSession(modelPath);
            _labels = labels;
            
            // 动态读取模型的输入和输出名称
            _inputName = _session.InputNames.First();
            _outputName = _session.OutputNames.First();
        }

        // 识别结果结构体
        public struct Prediction { public string Label; public float Confidence; }

        public Prediction Predict(Mat frame)
        {
            // 1. 图像预处理 (调整为 512x512 以匹配模型输入要求)
            using (var resized = frame.Resize(new Size(512, 512)))
            {
                var tensor = new DenseTensor<float>(new[] { 1, 3, 512, 512 });

                // 简单的像素归一化填充
                for (int y = 0; y < 512; y++)
                    for (int x = 0; x < 512; x++)
                    {
                        var color = resized.At<Vec3b>(y, x);
                        tensor[0, 0, y, x] = color.Item2 / 255.0f; // R
                        tensor[0, 1, y, x] = color.Item1 / 255.0f; // G
                        tensor[0, 2, y, x] = color.Item0 / 255.0f; // B
                    }

                // 2. 运行推理 (使用动态获取的输入名称)
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
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
}