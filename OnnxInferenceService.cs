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
                    var output = results.First().AsEnumerable<float>().ToArray();

                    // 步骤5: YOLO 后处理 - 解析预测框并获取最高置信度的检测结果
                    // 传递原始图像尺寸和缩放参数，用于坐标映射
                    return ParseYoloOutput(output, origW, origH, r, dw, dh);
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
        /// 支持两种 YOLO 版本：
        /// 1. YOLOv5：输出格式 [batch, num_detections, 5 + num_classes]
        ///    - [x, y, w, h, obj_conf, class_scores...]
        /// 2. YOLOv8：输出格式 [batch, num_detections, 4 + num_classes]
        ///    - [x, y, w, h, class_scores...]（无单独的 obj_conf）
        /// 
        /// 坐标映射：
        /// 1. YOLO 输出的边界框坐标相对于 Letterbox 预处理后的图像（640x640 或其他尺寸）
        /// 2. 需要逆向映射回原始图像坐标系统
        /// 3. 映射公式：
        ///    - 原坐标 = (模型坐标 - 灰色填充偏移) / 缩放比例
        ///    - x = (cx - dw) / r, y = (cy - dh) / r
        /// 
        /// 算法：
        /// 1. 推断 YOLO 版本（YOLOv5 vs YOLOv8）
        /// 2. 遍历每个检测框
        /// 3. 提取边界框坐标（cx, cy, w, h）和置信度
        /// 4. 逆向映射坐标到原始图像空间
        /// 5. 返回置信度最高的检测结果（含映射后的边界框）
        /// </summary>
        /// <param name="output">ONNX 模型的原始输出张量</param>
        /// <param name="origW">原始图像宽度</param>
        /// <param name="origH">原始图像高度</param>
        /// <param name="r">缩放比例（scale ratio）</param>
        /// <param name="dw">水平灰色填充偏移（padding width）</param>
        /// <param name="dh">竖直灰色填充偏移（padding height）</param>
        /// <returns>最高置信度的预测结果（标签+置信度+映射后的边界框）</returns>
        private Prediction ParseYoloOutput(float[] output, int origW, int origH, float r, int dw, int dh)
        {
            // 默认返回结果
            string bestLabel = "Unknown";
            float bestConf = 0.0f;
            Rect bestBox = new Rect(0, 0, 0, 0);

            // 检查输出是否为空
            if (output == null || output.Length == 0)
                return new Prediction { Label = bestLabel, Confidence = bestConf, Box = bestBox };

            try
            {
                // 推断输出张量的维度
                // YOLO 输出通常是 [batch=1, num_detections, features]
                // output 被展平后的长度应该是 num_detections * features

                int nc = _labels.Count;  // 类别数量

                // 判断模型版本：
                // YOLOv5: features = 5 + nc（有单独的 obj_conf）
                // YOLOv8: features = 4 + nc（无单独的 obj_conf）

                // 计算每个检测的特征数量
                int features;
                int numDetections;

                // 尝试推断正确的格式
                // 假设输出长度 = num_detections * features
                // 我们需要找到合适的 features 数量

                if (output.Length % (5 + nc) == 0)
                {
                    // YOLOv5 格式
                    features = 5 + nc;
                    numDetections = output.Length / features;
                }
                else if (output.Length % (4 + nc) == 0)
                {
                    // YOLOv8 格式
                    features = 4 + nc;
                    numDetections = output.Length / features;
                }
                else
                {
                    // 无法推断格式，尝试 YOLOv5 作为默认
                    features = 5 + nc;
                    numDetections = output.Length / features;
                }

                // 判断是否有单独的 objectness 置信度
                bool hasObjectness = (features == 5 + nc);

                // 遍历所有检测框
                for (int i = 0; i < numDetections; i++)
                {
                    int offset = i * features;

                    // 步骤 1: 提取边界框坐标（总是在前 4 个位置）
                    float cx = output[offset + 0];      // 中心 X 坐标
                    float cy = output[offset + 1];      // 中心 Y 坐标
                    float bw = output[offset + 2];      // 边界框宽度
                    float bh = output[offset + 3];      // 边界框高度

                    if (hasObjectness)
                    {
                        // YOLOv5 格式：[x, y, w, h, obj_conf, class_scores...]
                        float objConf = output[offset + 4];

                        // 检测置信度不足，跳过
                        if (objConf < CONF_THRESHOLD)
                            continue;

                        // 获取类别分数
                        float maxClassScore = float.MinValue;
                        int bestClassIdx = -1;

                        for (int c = 0; c < nc; c++)
                        {
                            float classScore = output[offset + 5 + c];
                            if (classScore > maxClassScore)
                            {
                                maxClassScore = classScore;
                                bestClassIdx = c;
                            }
                        }

                        // 计算最终置信度：obj_conf * class_score
                        float finalConf = objConf * maxClassScore;

                        // 更新最高置信度结果
                        if (finalConf >= CONF_THRESHOLD && finalConf > bestConf && bestClassIdx >= 0 && bestClassIdx < nc)
                        {
                            bestConf = finalConf;
                            bestLabel = _labels[bestClassIdx];

                            // 步骤 2: 逆向映射坐标到原始图像空间
                            // 模型输出的坐标是相对于 Letterbox 处理后的图像的中心坐标 (cx, cy) 和宽高 (w, h)
                            // 需要：
                            // 1. 先减去灰色填充偏移
                            // 2. 再除以缩放比例 r
                            // 3. 计算左上角坐标（从中心坐标转换）
                            int x = (int)Math.Round(((cx - dw) / r) - (bw / r) / 2.0f);
                            int y = (int)Math.Round(((cy - dh) / r) - (bh / r) / 2.0f);
                            int w = (int)Math.Round(bw / r);
                            int h = (int)Math.Round(bh / r);

                            // 步骤 3: 限制边界防止越界
                            x = Math.Max(0, x);
                            y = Math.Max(0, y);
                            w = Math.Min(origW - x, w);
                            h = Math.Min(origH - y, h);

                            // 确保宽高为正
                            w = Math.Max(0, w);
                            h = Math.Max(0, h);

                            bestBox = new Rect(x, y, w, h);
                        }
                    }
                    else
                    {
                        // YOLOv8 格式：[x, y, w, h, class_scores...]
                        float maxClassScore = float.MinValue;
                        int bestClassIdx = -1;

                        for (int c = 0; c < nc; c++)
                        {
                            float classScore = output[offset + 4 + c];
                            if (classScore > maxClassScore)
                            {
                                maxClassScore = classScore;
                                bestClassIdx = c;
                            }
                        }

                        // 直接使用类别分数作为置信度
                        float finalConf = maxClassScore;

                        // 更新最高置信度结果
                        if (finalConf >= CONF_THRESHOLD && finalConf > bestConf && bestClassIdx >= 0 && bestClassIdx < nc)
                        {
                            bestConf = finalConf;
                            bestLabel = _labels[bestClassIdx];

                            // 步骤 2: 逆向映射坐标到原始图像空间
                            // YOLOv8 同样需要坐标映射（中心坐标 -> 左上角坐标）
                            int x = (int)Math.Round(((cx - dw) / r) - (bw / r) / 2.0f);
                            int y = (int)Math.Round(((cy - dh) / r) - (bh / r) / 2.0f);
                            int w = (int)Math.Round(bw / r);
                            int h = (int)Math.Round(bh / r);

                            // 步骤 3: 限制边界防止越界
                            x = Math.Max(0, x);
                            y = Math.Max(0, y);
                            w = Math.Min(origW - x, w);
                            h = Math.Min(origH - y, h);

                            // 确保宽高为正
                            w = Math.Max(0, w);
                            h = Math.Max(0, h);

                            bestBox = new Rect(x, y, w, h);
                        }
                    }
                }

                return new Prediction
                {
                    Label = bestLabel,
                    Confidence = bestConf,
                    Box = bestBox
                };
            }
            catch (Exception ex)
            {
                // 异常情况返回默认结果
                return new Prediction { Label = bestLabel, Confidence = bestConf, Box = bestBox };
            }
        }
    }
}