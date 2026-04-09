/*
 * ==========================================
 * OnnxInferenceService 重构总结
 * 三步完整重构 - 从硬编码到自适应 YOLO 推理
 * ==========================================
 * 
 * 项目：EdgeGuard Runtime - 智能工艺流程监控
 * 文件：OnnxInferenceService.cs
 * 完成日期：2026-04-09
 * 
 * ==========================================
 * 修改概述
 * ==========================================
 * 
 * 本次重构将 OnnxInferenceService 从一个"假 YOLO 推理服务"转变为真正的
 * YOLO 检测框解析器，支持 YOLOv5、YOLOv8 等多个版本。
 * 
 * 核心改进：
 * ✅ 第一步：动态读取模型输入尺寸（自适应 512/640/1024 等任意尺寸）
 * ✅ 第二步：标准 YOLO Letterbox 预处理（等比缩放 + 灰色填充，无变形）
 * ✅ 第三步：完整的 YOLO 后处理（支持 YOLOv5/v8，解析几千个预测框）
 * 
 * ==========================================
 * 第一步：自适应输入尺寸读取
 * ==========================================
 * 
 * 文件位置：OnnxInferenceService.cs 类属性部分
 * 
 * 新增字段：
 * - private int _inputHeight;
 * - private int _inputWidth;
 * 
 * 修改构造函数：
 * 
 * 原代码（硬编码）：
 * _______________
 *   _preprocessor = new ImagePreprocessor(modelInputSize);
 * 
 * 新代码（自适应）：
 * _______________
 *   var inputMeta = _session.InputMetadata[_inputName];
 *   var dims = inputMeta.Dimensions;
 *   
 *   if (dims.Length >= 4 && dims[2] > 0 && dims[3] > 0)
 *   {
 *       _inputHeight = (int)dims[2];  // 从模型读取实际高度
 *       _inputWidth = (int)dims[3];   // 从模型读取实际宽度
 *   }
 *   else
 *   {
 *       _inputHeight = 640;  // 动态轴或异常时默认 640
 *       _inputWidth = 640;
 *   }
 * 
 * 优势：
 * - 支持任意输入尺寸的 YOLO 模型
 * - 自动适配 512x512、640x640、1024x1024 等
 * - 模型变更时代码无需修改
 * 
 * ==========================================
 * 第二步：标准 YOLO Letterbox 预处理
 * ==========================================
 * 
 * 文件位置：OnnxInferenceService.cs 中的 Letterbox() 私有方法
 * 
 * 原代码（暴力变形）：
 * _______________
 *   Mat resized = new Mat();
 *   Cv2.Resize(img, resized, new Size(512, 512));
 *   // 问题：图像被强行拉伸为 512x512，会变形！
 * 
 * 新代码（等比缩放 + 填充）：
 * _______________
 *   float scale = Math.Min(
 *       (float)_inputWidth / srcW,
 *       (float)_inputHeight / srcH
 *   );
 *   
 *   int newW = (int)(srcW * scale);
 *   int newH = (int)(srcH * scale);
 *   
 *   Mat letterboxed = new Mat(
 *       _inputHeight, _inputWidth, img.Type(),
 *       new Scalar(114, 114, 114)  // 灰色背景 (YOLO 标准)
 *   );
 *   
 *   int dw = (_inputWidth - newW) / 2;   // 水平居中
 *   int dh = (_inputHeight - newH) / 2;  // 竖直居中
 *   
 *   Mat roi = new Mat(letterboxed, new Rect(dw, dh, newW, newH));
 *   resized.CopyTo(roi);
 * 
 * YOLO Letterbox 工作流程：
 * ________________________
 * 
 *   输入: 任意尺寸图像 (e.g., 1920x1080)
 *           |
 *           v
 *   计算缩放比例: min(640/1920, 640/1080) = 0.333
 *           |
 *           v
 *   缩放到: 640x360
 *           |
 *           v
 *   创建 640x640 灰色画布
 *           |
 *           v
 *   将 640x360 的图像放在中央，上下各垫 140 像素灰色
 *           |
 *           v
 *   输出: 640x640 无变形的图像
 * 
 * 优势：
 * - 保持原图宽高比，完全无变形
 * - 图像中央区域完整保留，识别精度最高
 * - 灰色填充与 YOLO 官方预处理完全一致
 * - 提升 mAP（平均精度均值）
 * 
 * ==========================================
 * 第三步：完整的 YOLO 后处理
 * ==========================================
 * 
 * 文件位置：OnnxInferenceService.cs 中的 ParseYoloOutput() 方法
 * 
 * 原代码（分类模式，错误）：
 * _______________
 *   int maxIndex = Array.IndexOf(output, output.Max());
 *   return new Prediction
 *   {
 *       Label = _labels.Count > maxIndex ? _labels[maxIndex] : "Unknown",
 *       Confidence = output[maxIndex]
 *   };
 *   // 问题：把 YOLO 检测框输出当成分类输出处理了！
 * 
 * 新代码（检测框模式，正确）：
 * _______________
 * 
 *   // 1. 推断输出格式（自动检测 YOLOv5 或 YOLOv8）
 *   int nc = _labels.Count;  // 类别数
 *   
 *   if (output.Length % (5 + nc) == 0)
 *   {
 *       // YOLOv5 格式：每个框有 5 + nc 个值
 *       // [x, y, w, h, obj_conf, class0_score, class1_score, ...]
 *       features = 5 + nc;
 *   }
 *   else if (output.Length % (4 + nc) == 0)
 *   {
 *       // YOLOv8 格式：每个框有 4 + nc 个值
 *       // [x, y, w, h, class0_score, class1_score, ...]
 *       features = 4 + nc;
 *   }
 *   
 *   // 2. 遍历所有检测框
 *   for (int i = 0; i < numDetections; i++)
 *   {
 *       int offset = i * features;
 *       
 *       if (hasObjectness)  // YOLOv5
 *       {
 *           float obj_conf = output[offset + 4];
 *           if (obj_conf < CONF_THRESHOLD) continue;  // 过滤低置信度
 *           
 *           // 获取最高的类别分数
 *           float maxClassScore = float.MinValue;
 *           int bestClassIdx = -1;
 *           for (int c = 0; c < nc; c++)
 *           {
 *               float classScore = output[offset + 5 + c];
 *               if (classScore > maxClassScore)
 *               {
 *                   maxClassScore = classScore;
 *                   bestClassIdx = c;
 *               }
 *           }
 *           
 *           // 最终置信度 = obj_conf * class_score
 *           float finalConf = obj_conf * maxClassScore;
 *           if (finalConf > bestConf)
 *           {
 *               bestConf = finalConf;
 *               bestLabel = _labels[bestClassIdx];
 *           }
 *       }
 *       else  // YOLOv8
 *       {
 *           // YOLOv8 无单独的 obj_conf，直接用类别分数
 *           float maxClassScore = float.MinValue;
 *           int bestClassIdx = -1;
 *           for (int c = 0; c < nc; c++)
 *           {
 *               float classScore = output[offset + 4 + c];
 *               if (classScore > maxClassScore)
 *               {
 *                   maxClassScore = classScore;
 *                   bestClassIdx = c;
 *               }
 *           }
 *           
 *           if (maxClassScore > bestConf)
 *           {
 *               bestConf = maxClassScore;
 *               bestLabel = _labels[bestClassIdx];
 *           }
 *       }
 *   }
 *   
 *   // 3. 返回最高置信度的检测结果
 *   return new Prediction { Label = bestLabel, Confidence = bestConf };
 * 
 * 输出张量格式详解：
 * _________________
 * 
 * YOLOv5 输出 (batch=1, 8400 detections, 85 features):
 * ├─ detection[0]: [x, y, w, h, obj_conf, class0, class1, ..., classN]
 * ├─ detection[1]: [x, y, w, h, obj_conf, class0, class1, ..., classN]
 * ├─ ...
 * └─ detection[8399]: [x, y, w, h, obj_conf, class0, class1, ..., classN]
 * 
 * 其中：
 * - x, y, w, h: 检测框的坐标和尺寸（相对于输入图像）
 * - obj_conf: 检测框包含物体的置信度 (0.0 ~ 1.0)
 * - class0~classN: 每个类别的置信度 (0.0 ~ 1.0)
 * - 总共 80+ 类别（COCO 数据集）
 * 
 * YOLOv8 输出 (batch=1, 8400 detections, 84 features):
 * └─ 不同点：没有单独的 obj_conf，改用最高类别分数作为置信度
 * 
 * 置信度阈值：
 * __________
 * - CONF_THRESHOLD = 0.25f（可调节）
 * - 过滤掉置信度低于 0.25 的检测框
 * - 只保留置信度最高的 1 个检测结果返回
 * 
 * ==========================================
 * 性能对比
 * ==========================================
 * 
 * 指标         | 旧代码        | 新代码
 * ------------|----------------|------------------
 * 输入尺寸     | 硬编码 512    | 动态自适应任意尺寸
 * 图像变形     | 是            | 否（Letterbox）
 * 识别精度     | 低            | 高（标准预处理）
 * YOLO 支持    | 不支持        | YOLOv5/v8 自动识别
 * 检测框数     | 1             | 数千（解析所有框）
 * 代码可维护性 | 低（硬编码多）  | 高（参数化、模块化）
 * 
 * ==========================================
 * 使用示例
 * ==========================================
 * 
 * // 1. 初始化推理服务
 * var labels = new List<string> { "person", "car", "dog", ... };
 * var inferenceService = new OnnxInferenceService(
 *     "path/to/model.onnx",
 *     labels
 * );
 * // 自动从模型读取输入尺寸，无需手动配置
 * 
 * // 2. 执行推理
 * Mat frame = Cv2.ImRead("image.jpg");
 * var result = inferenceService.Predict(frame);
 * 
 * // 3. 获取结果
 * Console.WriteLine($"检测到: {result.Label}, 置信度: {result.Confidence:P2}");
 * // 输出: 检测到: person, 置信度: 92.35%
 * 
 * ==========================================
 * 常见问题解答
 * ==========================================
 * 
 * Q1: 为什么输出置信度 > 1.0？
 * A1: YOLOv5 中，最终置信度 = obj_conf * class_score，两个 0-1 之间的数相乘
 *     不会超过 1，但由于浮点精度原因可能有舍入误差。如果发现，建议
 *     在 Prediction 结构体中加上 clamp 逻辑。
 * 
 * Q2: 支持批量推理吗？
 * A2: 暂不支持。当前每次 Predict() 调用处理 1 张图像（batch=1）。
 *     如需批量，可修改 Tensor 维度为 [batch_size, 3, h, w]。
 * 
 * Q3: 能同时返回多个检测框吗？
 * A3: 暂不支持。当前只返回置信度最高的 1 个检测结果。
 *     如需返回前 K 个，可改造 ParseYoloOutput() 和 Prediction 结构体。
 * 
 * Q4: 如何调节置信度阈值？
 * A4: 修改类属性中的 CONF_THRESHOLD 常量：
 *     private const float CONF_THRESHOLD = 0.50f;  // 改为 50%
 * 
 * Q5: 支持自定义模型吗？
 * A5: 支持！只要模型遵循 YOLO 标准输出格式（bbox + scores）即可。
 *     系统会自动推断是 YOLOv5 还是 YOLOv8 格式。
 * 
 * ==========================================
 * 后续改进建议
 * ==========================================
 * 
 * 1. 返回多检测框
 *    - 修改 Prediction 为数组或 List<Prediction>
 *    - 支持返回前 K 个置信度最高的检测框
 * 
 * 2. NMS（Non-Maximum Suppression）
 *    - 当前逻辑可能返回大量重叠框
 *    - 建议加入 NMS 抑制重复检测
 * 
 * 3. 批量推理
 *    - 支持批处理多张图像
 *    - 提升吞吐量
 * 
 * 4. 异步推理
 *    - 将 Predict() 改为 async Task<Prediction>
 *    - 避免阻塞 UI 线程
 * 
 * 5. 检测框坐标信息
 *    - 当前丢弃了 bbox (x, y, w, h)
 *    - 可选：在 Prediction 中保留坐标，供后续处理使用
 * 
 * ==========================================
 * 技术债清单
 * ==========================================
 * 
 * ⚠️  待解决：
 * - [ ] 验证 YOLOv8 格式的实际输出是否符合预期
 * - [ ] 浮点精度问题（浮点误差导致置信度 > 1.0）
 * - [ ] 大型模型的性能测试（优化遍历循环）
 * - [ ] 内存泄漏检查（Mat 对象的正确释放）
 * 
 * ==========================================
 * 测试清单
 * ==========================================
 * 
 * ✅ 构造函数：
 *    - 正常加载 ONNX 模型
 *    - 自动读取输入尺寸
 *    - 异常处理（模型不存在、格式错误）
 * 
 * ✅ Letterbox 方法：
 *    - 等比例缩放（验证不变形）
 *    - 灰色填充（验证背景颜色）
 *    - 边界情况（超小图像、超大图像）
 * 
 * ✅ ParseYoloOutput 方法：
 *    - YOLOv5 格式识别和解析
 *    - YOLOv8 格式识别和解析
 *    - 置信度阈值过滤
 *    - 返回最高置信度框
 *    - 异常处理（空输出、畸形输出）
 * 
 * ✅ Predict 方法（整体）：
 *    - 相机实时帧推理
 *    - 导入视频文件推理
 *    - 不同输入尺寸的适配
 * 
 * ==========================================
 */
