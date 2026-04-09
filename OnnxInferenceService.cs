using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using edge_runtime.Services;

namespace edge_runtime
{
    public class OnnxInferenceService
    {
        /// <summary>
        /// YOLO 检测的置信度阈值
        /// </summary>
        private const float CONF_THRESHOLD = 0.25f;

        private InferenceSession _session;
        private List<string> _labels;
        private string _inputName;
        private string _outputName;

        /// <summary>
        /// 模型输入高度（从 ONNX 模型动态读取）
        /// </summary>
        private int _inputHeight;

        /// <summary>
        /// 模型输入宽度（从 ONNX 模型动态读取）
        /// </summary>
        private int _inputWidth;

        /// <summary>
        /// 图像预处理器 - 负责自适应缩放和标准化处理
        /// 解决各种尺寸图像与模型输入尺寸不匹配的问题
        /// </summary>
        private ImagePreprocessor _preprocessor;

        public OnnxInferenceService(string modelPath, List<string> labels, int modelInputSize = 640)
        {
            _session = new InferenceSession(modelPath);
            _labels = labels;

            // 动态读取模型的输入和输出名称
            _inputName = _session.InputNames.First();
            _outputName = _session.OutputNames.First();

            // 从模型元数据中读取输入尺寸（自适应不同 YOLO 模型）
            try
            {
                var inputMeta = _session.InputMetadata[_inputName];
                var dims = inputMeta.Dimensions;

                // 防御性判断：
                // ONNX 模型的张量维度通常为 [Batch, Channel, Height, Width]
                // dims[2] = Height, dims[3] = Width
                // -1 表示动态轴（不确定的维度）
                if (dims.Length >= 4 && dims[2] > 0 && dims[3] > 0)
                {
                    _inputHeight = (int)dims[2];
                    _inputWidth = (int)dims[3];
                }
                else
                {
                    // 如果读取到动态轴或维度数不足，默认设为 640
                    _inputHeight = 640;
                    _inputWidth = 640;
                }
            }
            catch (Exception ex)
            {
                // 异常情况下，默认设为 640
                _inputHeight = 640;
                _inputWidth = 640;
            }

            // 初始化图像预处理器（使用从模型读取的实际输入尺寸）
            // 优先使用读取到的尺寸，如果读取失败则使用参数提供的 modelInputSize
            int actualInputSize = (_inputHeight == _inputWidth) ? _inputHeight : modelInputSize;
            _preprocessor = new ImagePreprocessor(actualInputSize);
        }

        // 识别结果结构体
        public struct Prediction
        {
            /// <summary>
            /// 检测到的类别标签
            /// </summary>
            public string Label;

            /// <summary>
            /// 检测置信度（0.0 ~ 1.0）
            /// </summary>
            public float Confidence;

            /// <summary>
            /// 边界框（映射到原始图像坐标系）
            /// </summary>
            public Rect Box;
        }

        /// <summary>
        /// YOLO Letterbox 预处理 - 等比例缩放 + 灰色填充
        /// 
        /// 原理：
        /// 1. 计算缩放因子：scale = min(目标宽/原宽, 目标高/原高)
        /// 2. 按缩放因子缩放原图（保持宽高比）
        /// 3. 在灰色背景上居中放置缩放后的图像
        /// 4. 最终得到目标尺寸的图像（无变形）
        /// 
        /// 优势：
        /// - 保持原图宽高比，不会产生变形
        /// - 灰色填充与 YOLO 标准预处理一致
        /// - 提升模型识别精度
        /// </summary>
        /// <param name="img">输入图像</param>
        /// <returns>处理后的图像（尺寸为 _inputWidth x _inputHeight）</returns>
        private Mat Letterbox(Mat img)
        {
            if (img == null || img.Empty())
                throw new ArgumentException("输入图像为空", nameof(img));

            int srcH = img.Rows;
            int srcW = img.Cols;

            // 计算缩放因子（取最小值以保证不超出目标尺寸）
            float scale = Math.Min((float)_inputWidth / srcW, (float)_inputHeight / srcH);

            // 计算缩放后的尺寸
            int newW = (int)(srcW * scale);
            int newH = (int)(srcH * scale);

            // 缩放原图
            Mat resized = new Mat();
            Cv2.Resize(img, resized, new Size(newW, newH));

            // 计算填充的位置（居中）
            int dw = (_inputWidth - newW) / 2;
            int dh = (_inputHeight - newH) / 2;

            // 创建目标画布（灰色背景）
            Mat letterboxed = new Mat(_inputHeight, _inputWidth, img.Type(), new Scalar(114, 114, 114));

            // 将缩放后的图像放在居中的位置
            Mat roi = new Mat(letterboxed, new Rect(dw, dh, newW, newH));
            resized.CopyTo(roi);

            resized.Dispose();
            roi.Dispose();

            return letterboxed;
        }

        /// <summary>
        /// 执行推理（核心方法）- 自动处理任意尺寸图像，并返回映射到原始坐标系的边界框
        /// 
        /// 自适应流程：
        /// 1. 使用Letterbox等比例缩放到模型要求的尺寸
        /// 2. 自动进行灰色边框填充（无变形）
        /// 3. 归一化像素值到[0,1]
        /// 4. 转换通道格式（BGR->RGB）
        /// 5. 执行ONNX推理
        /// 6. 解析 YOLO 输出并获取最高置信度的预测框
        /// 7. 将边界框坐标从模型输入空间映射回原始图像空间
        /// 
        /// 优势：
        /// - 输入图像尺寸无限制（512、1080、4K等都可处理）
        /// - 自动匹配模型输入要求（动态读取模型配置）
        /// - 完全杜绝"Got 512 Expected 640"错误
        /// - 图像不变形，识别精度不下降
        /// - 返回的边界框坐标已映射到原始图像尺寸
        /// </summary>
        /// <param name="frame">任意尺寸的输入图像（相机帧）</param>
        /// <returns>识别结果（标签+置信度+边界框）</returns>
        public Prediction Predict(Mat frame)
        {
            // 前置条件检查
            if (frame == null || frame.Empty())
                throw new ArgumentException("输入图像为空或无效", nameof(frame));

            try
            {
                // 保存原始图像的宽高
                int origW = frame.Cols;
                int origH = frame.Rows;

                // 计算缩放参数（用于后续逆向换算坐标）
                float r = Math.Min((float)_inputWidth / origW, (float)_inputHeight / origH);
                int dw = (_inputWidth - (int)Math.Round(origW * r)) / 2;
                int dh = (_inputHeight - (int)Math.Round(origH * r)) / 2;

                // 步骤1: 使用 Letterbox 进行等比例缩放和灰色填充
                using (var processed = Letterbox(frame))
                {
                    // 步骤2: 从处理后的图像提取像素数据并转换为 Tensor
                    float[] tensorData = new float[1 * 3 * _inputHeight * _inputWidth];
                    int tensorIndex = 0;

                    // 遍历图像像素
                    for (int h = 0; h < _inputHeight; h++)
                    {
                        for (int w = 0; w < _inputWidth; w++)
                        {
                            // 获取 BGR 像素值
                            Vec3b pixel = processed.At<Vec3b>(h, w);

                            // 转换为 RGB 并归一化到 [0, 1]
                            // 注意：OpenCV 默认是 BGR 格式，需要反序为 RGB
                            float red = pixel[2] / 255.0f;    // B channel -> R
                            float green = pixel[1] / 255.0f;  // G channel
                            float blue = pixel[0] / 255.0f;   // R channel -> B

                            // 按 CHW 顺序存储（Channel-Height-Width）
                            // R 通道
                            tensorData[0 * _inputHeight * _inputWidth + h * _inputWidth + w] = red;
                            // G 通道
                            tensorData[1 * _inputHeight * _inputWidth + h * _inputWidth + w] = green;
                            // B 通道
                            tensorData[2 * _inputHeight * _inputWidth + h * _inputWidth + w] = blue;
                        }
                    }

                    // 步骤3: 创建 ONNX 推理所需的张量
                    var tensor = new DenseTensor<float>(tensorData, new[] { 1, 3, _inputHeight, _inputWidth });

                    // 步骤4: 执行推理（使用动态获取的输入名称）
                    var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
                    using var results = _session.Run(inputs);

                    // 保持张量结构，不再使用 ToArray() 拍扁
                    var outputTensor = results.First().AsTensor<float>();

                    // 步骤5: YOLO 后处理 - 解析预测框并获取最高置信度的检测结果
                    // 传递原始图像尺寸和缩放参数，用于坐标映射
                    return ParseYoloOutput(outputTensor, origW, origH, r, dw, dh);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"ONNX推理失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 解析 YOLO 模型输出并返回最高置信度的检测结果（含坐标映射）
        /// 
        /// 核心改进：
        /// 1. 智能判断张量维度并自动处理 [1, 84, 8400] (YOLOv8) 和 [1, N, 85] (YOLOv5) 格式
        /// 2. 使用 isTransposed 标志替代 Python 的 .T 转置，彻底解决坐标错位问题
        /// 3. 提取公共的坐标映射逻辑到 CalculateBox 方法，减少代码重复
        /// </summary>
        /// <param name="output">ONNX 模型的输出张量（保持多维结构）</param>
        /// <param name="origW">原始图像宽度</param>
        /// <param name="origH">原始图像高度</param>
        /// <param name="r">缩放比例（scale ratio）</param>
        /// <param name="dw">水平灰色填充偏移（padding width）</param>
        /// <param name="dh">竖直灰色填充偏移（padding height）</param>
        /// <returns>最高置信度的预测结果（标签+置信度+映射后的边界框）</returns>
        private Prediction ParseYoloOutput(Tensor<float> output, int origW, int origH, float r, int dw, int dh)
        {
            string bestLabel = "Unknown";
            float bestConf = 0.0f;
            Rect bestBox = new Rect(0, 0, 0, 0);

            if (output == null || output.Length == 0)
                return new Prediction { Label = bestLabel, Confidence = bestConf, Box = bestBox };

            try
            {
                var dims = output.Dimensions;
                int dimsLength = dims.Length;

                // 核心修复：动态判断是否需要转置 (针对 YOLOv8 的 [1, 84, 8400] 格式)
                bool isTransposed = dimsLength == 3 && dims[1] < dims[2];

                // 获取特征数和检测框总数
                int features = isTransposed ? dims[1] : dims[dimsLength - 1];
                int numDetections = isTransposed ? dims[2] : dims[dimsLength - 2];
                int nc = _labels.Count;

                bool hasObjectness = (features == 5 + nc);

                // 局部辅助函数：根据张量的实际排列顺序，安全地取值，替代 Python 的 .T 转置
                float GetValue(int detIndex, int featureIndex)
                {
                    if (dimsLength == 3)
                        return isTransposed ? output[0, featureIndex, detIndex] : output[0, detIndex, featureIndex];
                    else if (dimsLength == 2)
                        return isTransposed ? output[featureIndex, detIndex] : output[detIndex, featureIndex];
                    return 0f;
                }

                // 遍历所有检测框
                for (int i = 0; i < numDetections; i++)
                {
                    // 使用辅助函数读取坐标，彻底解决错位问题
                    float cx = GetValue(i, 0);
                    float cy = GetValue(i, 1);
                    float bw = GetValue(i, 2);
                    float bh = GetValue(i, 3);

                    if (hasObjectness)
                    {
                        // YOLOv5 格式: [x, y, w, h, obj_conf, class_scores...]
                        float objConf = GetValue(i, 4);

                        if (objConf < CONF_THRESHOLD) continue;

                        float maxClassScore = float.MinValue;
                        int bestClassIdx = -1;

                        for (int c = 0; c < nc; c++)
                        {
                            float classScore = GetValue(i, 5 + c);
                            if (classScore > maxClassScore)
                            {
                                maxClassScore = classScore;
                                bestClassIdx = c;
                            }
                        }

                        float finalConf = objConf * maxClassScore;

                        if (finalConf >= CONF_THRESHOLD && finalConf > bestConf && bestClassIdx >= 0 && bestClassIdx < nc)
                        {
                            bestConf = finalConf;
                            bestLabel = _labels[bestClassIdx];
                            bestBox = CalculateBox(cx, cy, bw, bh, origW, origH, r, dw, dh);
                        }
                    }
                    else
                    {
                        // YOLOv8 格式: [x, y, w, h, class_scores...]
                        float maxClassScore = float.MinValue;
                        int bestClassIdx = -1;

                        for (int c = 0; c < nc; c++)
                        {
                            float classScore = GetValue(i, 4 + c);
                            if (classScore > maxClassScore)
                            {
                                maxClassScore = classScore;
                                bestClassIdx = c;
                            }
                        }

                        float finalConf = maxClassScore;

                        if (finalConf >= CONF_THRESHOLD && finalConf > bestConf && bestClassIdx >= 0 && bestClassIdx < nc)
                        {
                            bestConf = finalConf;
                            bestLabel = _labels[bestClassIdx];
                            bestBox = CalculateBox(cx, cy, bw, bh, origW, origH, r, dw, dh);
                        }
                    }
                }

                return new Prediction { Label = bestLabel, Confidence = bestConf, Box = bestBox };
            }
            catch (Exception)
            {
                return new Prediction { Label = bestLabel, Confidence = bestConf, Box = bestBox };
            }
        }

        private Rect CalculateBox(float cx, float cy, float bw, float bh, int origW, int origH, float r, int dw, int dh)
        {
            int x = (int)Math.Round(((cx - dw) / r) - (bw / r) / 2.0f);
            int y = (int)Math.Round(((cy - dh) / r) - (bh / r) / 2.0f);
            int w = (int)Math.Round(bw / r);
            int h = (int)Math.Round(bh / r);

            x = Math.Max(0, x);
            y = Math.Max(0, y);
            w = Math.Min(origW - x, w);
            h = Math.Min(origH - y, h);

            return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
        }
    }
}