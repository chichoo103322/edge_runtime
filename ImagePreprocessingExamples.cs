using OpenCvSharp;
using System;
using System.Collections.Generic;
using edge_runtime.Services;

namespace edge_runtime.Examples
{
    /// <summary>
    /// 自适应图像预处理集成示例
    /// 
    /// 本文件演示了如何使用新的ImagePreprocessor类
    /// 以及如何与现有的ONNX推理流程集成
    /// </summary>
    public class ImagePreprocessingExamples
    {
        // =====================================================================
        // 示例1: 基础使用 - 自动处理任意尺寸相机输入
        // =====================================================================
        public static void Example_BasicUsage()
        {
            /*
             * 场景: WorkflowExecutor.ProcessFrame()中的使用
             * 
             * 现在的代码已经完全自动处理，无需手动调整
             */

            // 模拟接收相机帧（可能是任意尺寸）
            Mat cameraFrame = Cv2.ImRead("camera_frame.jpg");  // 可能是512×512, 1920×1080等

            // 直接调用Predict，系统自动处理尺寸
            // OnnxInferenceService已集成ImagePreprocessor
            // var prediction = _aiService.Predict(cameraFrame);
            // Console.WriteLine($"识别结果: {prediction.Label} ({prediction.Confidence:P})");
        }

        // =====================================================================
        // 示例2: 手动使用ImagePreprocessor - 如果需要单独的预处理
        // =====================================================================
        public static void Example_ManualPreprocessing()
        {
            /*
             * 场景: 如果你想在推理前检查或保存预处理后的图像
             */

            // 创建预处理器（指定模型要求的输入尺寸）
            var preprocessor = new ImagePreprocessor(targetSize: 640);

            // 读取输入图像（任意尺寸）
            Mat originalImage = Cv2.ImRead("input_image.jpg");
            Console.WriteLine($"原始图像尺寸: {originalImage.Width}×{originalImage.Height}");

            // 方式A: Letterbox缩放（只缩放，不转换为张量）
            Mat letterboxImage = preprocessor.LetterboxResize(originalImage);
            Console.WriteLine($"Letterbox后尺寸: {letterboxImage.Width}×{letterboxImage.Height}");
            Cv2.ImWrite("letterbox_output.jpg", letterboxImage);

            // 方式B: 转换为ONNX张量（用于推理）
            float[] tensorData = preprocessor.MatToNormalizedArray(letterboxImage);
            Console.WriteLine($"张量维度: 1×3×{letterboxImage.Height}×{letterboxImage.Width}");
            Console.WriteLine($"张量数据长度: {tensorData.Length}");

            // 方式C: 一步到位
            float[] tensorData_OneStep = preprocessor.Preprocess(originalImage);
            Console.WriteLine($"一步处理完成，张量长度: {tensorData_OneStep.Length}");
        }

        // =====================================================================
        // 示例3: 支持不同模型的不同输入尺寸
        // =====================================================================
        public static void Example_DifferentModelSizes()
        {
            /*
             * 场景: 你的项目中可能有多个模型，要求不同的输入尺寸
             * YOLO通常用640, 但某些模型可能用512、1024等
             */

            Mat inputImage = Cv2.ImRead("test_image.jpg");

            // 模型A: 640×640
            var preprocessor_640 = new ImagePreprocessor(targetSize: 640);
            float[] tensor_640 = preprocessor_640.Preprocess(inputImage);
            Console.WriteLine($"640×640模型张量大小: {tensor_640.Length}");

            // 模型B: 512×512
            var preprocessor_512 = new ImagePreprocessor(targetSize: 512);
            float[] tensor_512 = preprocessor_512.Preprocess(inputImage);
            Console.WriteLine($"512×512模型张量大小: {tensor_512.Length}");

            // 模型C: 1024×1024
            var preprocessor_1024 = new ImagePreprocessor(targetSize: 1024);
            float[] tensor_1024 = preprocessor_1024.Preprocess(inputImage);
            Console.WriteLine($"1024×1024模型张量大小: {tensor_1024.Length}");
        }

        // =====================================================================
        // 示例4: 动态调整模型尺寸（运行时修改）
        // =====================================================================
        public static void Example_DynamicSizeAdjustment()
        {
            /*
             * 场景: 应用运行时，根据配置动态改变预处理尺寸
             */

            var preprocessor = new ImagePreprocessor(targetSize: 640);
            Console.WriteLine($"初始目标尺寸: {preprocessor.GetTargetSize()}");

            // 动态修改目标尺寸
            preprocessor.SetTargetSize(512);
            Console.WriteLine($"修改后目标尺寸: {preprocessor.GetTargetSize()}");

            Mat testImage = Cv2.ImRead("test.jpg");
            float[] tensorData = preprocessor.Preprocess(testImage);
            Console.WriteLine($"预处理完成，输出张量大小: {tensorData.Length}");
        }

        // =====================================================================
        // 示例5: 处理各种输入尺寸的兼容性测试
        // =====================================================================
        public static void Example_CompatibilityTest()
        {
            /*
             * 场景: 验证系统对各种输入尺寸的处理能力
             */

            var preprocessor = new ImagePreprocessor(640);

            // 定义测试用例
            var testSizes = new List<(int width, int height, string desc)>
            {
                (640, 640, "方形 - 标准尺寸"),
                (512, 512, "方形 - 较小"),
                (1280, 720, "宽屏 - 720p"),
                (1920, 1080, "宽屏 - 1080p"),
                (3840, 2160, "宽屏 - 4K"),
                (480, 640, "竖屏"),
                (1080, 1920, "竖屏 - 1080p"),
                (1024, 768, "标准 - 4:3"),
                (800, 600, "小尺寸 - 4:3"),
            };

            foreach (var (w, h, desc) in testSizes)
            {
                try
                {
                    // 创建测试图像
                    Mat testImage = new Mat(h, w, MatType.CV_8UC3, Scalar.All(128));

                    // 执行预处理
                    float[] tensorData = preprocessor.Preprocess(testImage);

                    Console.WriteLine($"✓ {desc:20} ({w:4}×{h:4}) → 张量长度: {tensorData.Length}");
                    testImage.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ {desc:20} ({w:4}×{h:4}) → 错误: {ex.Message}");
                }
            }
        }

        // =====================================================================
        // 示例6: 处理异常情况
        // =====================================================================
        public static void Example_ErrorHandling()
        {
            /*
             * 场景: 如何处理预处理过程中的异常
             */

            var preprocessor = new ImagePreprocessor(640);

            try
            {
                // 错误1: 空图像
                Mat emptyImage = new Mat();
                float[] result1 = preprocessor.Preprocess(emptyImage);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"捕获异常: {ex.Message}");
                // 输出: 输入图像为空或无效
            }

            try
            {
                // 错误2: 无效的目标尺寸
                var invalidPreprocessor = new ImagePreprocessor(targetSize: -1);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"捕获异常: {ex.Message}");
                // 输出: 目标尺寸必须大于0
            }

            try
            {
                // 错误3: 灰度图像（需要BGR）
                Mat grayImage = Cv2.ImRead("image.jpg", ImreadModes.Grayscale);
                float[] result3 = preprocessor.Preprocess(grayImage);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"捕获异常: {ex.Message}");
                // 输出: 输入图像必须是3通道（BGR）格式
            }
        }

        // =====================================================================
        // 示例7: 性能测试
        // =====================================================================
        public static void Example_PerformanceBenchmark()
        {
            /*
             * 场景: 测试预处理的性能开销
             */

            var preprocessor = new ImagePreprocessor(640);
            Mat testImage = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(100));

            // 预热
            for (int i = 0; i < 5; i++)
                preprocessor.Preprocess(testImage);

            // 测试
            int iterations = 100;
            var startTime = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                float[] tensorData = preprocessor.Preprocess(testImage);
            }

            startTime.Stop();
            double avgTime = startTime.ElapsedMilliseconds / (double)iterations;
            Console.WriteLine($"平均预处理时间: {avgTime:F2}ms (基于{iterations}次迭代)");
            Console.WriteLine($"预计FPS: {1000.0 / avgTime:F1}fps");
        }

        // =====================================================================
        // 示例8: 与OnnxInferenceService的集成（完整流程）
        // =====================================================================
        public static void Example_CompleteInferencePipeline()
        {
            /*
             * 场景: 展示完整的从图像到推理的流程
             * 
             * 在实际应用中，以下步骤都由OnnxInferenceService自动处理
             * 你只需要调用 Predict() 方法即可
             */

            Console.WriteLine("=== 完整推理管道示例 ===\n");

            // 步骤1: 加载测试图像（模拟相机输入）
            Mat cameraInput = Cv2.ImRead("camera_frame.jpg");
            Console.WriteLine($"1. 相机输入: {cameraInput.Width}×{cameraInput.Height}");

            // 步骤2: 创建预处理器
            var preprocessor = new ImagePreprocessor(640);

            // 步骤3: Letterbox缩放
            Mat letterbox = preprocessor.LetterboxResize(cameraInput);
            Console.WriteLine($"2. Letterbox缩放: {letterbox.Width}×{letterbox.Height}");

            // 步骤4: 转换为张量
            float[] tensorData = preprocessor.MatToNormalizedArray(letterbox);
            Console.WriteLine($"3. 转换为张量: {tensorData.Length}个浮点数");

            // 步骤5: 创建ONNX张量（这部分在OnnxInferenceService.Predict中完成）
            // var tensor = new DenseTensor<float>(tensorData, new[] { 1, 3, 640, 640 });

            // 步骤6: 执行推理（在实际应用中）
            // var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor("input", tensor) });

            Console.WriteLine("\n实际应用中，你只需要:");
            Console.WriteLine("  var prediction = _aiService.Predict(cameraInput);");
            Console.WriteLine("所有这些步骤都会自动完成！");
        }
    }

    // =========================================================================
    // 高级用法：自定义预处理后处理
    // =========================================================================
    public class AdvancedPreprocessingUsage
    {
        /// <summary>
        /// 如果需要对预处理后的图像进行检查或修改，可以这样做
        /// </summary>
        public static void SavePreprocessedImageForDebug()
        {
            var preprocessor = new ImagePreprocessor(640);
            Mat originalImage = Cv2.ImRead("input.jpg");

            // 获取Letterbox处理后的图像
            Mat letterboxImage = preprocessor.LetterboxResize(originalImage);

            // 可选: 在图像上绘制有用的调试信息
            Cv2.PutText(letterboxImage, $"Preprocessed: {letterboxImage.Width}x{letterboxImage.Height}",
                new OpenCvSharp.Point(10, 30),
                HersheyFonts.HersheyPlain, 1.5,
                Scalar.All(255));

            // 保存用于调试
            Cv2.ImWrite("debug_preprocessed.jpg", letterboxImage);
            Console.WriteLine("预处理后的图像已保存为 debug_preprocessed.jpg");

            letterboxImage.Dispose();
        }

        /// <summary>
        /// 对比原始图像和预处理后的图像
        /// </summary>
        public static void CompareBeforeAndAfter()
        {
            var preprocessor = new ImagePreprocessor(640);
            Mat original = Cv2.ImRead("original.jpg");
            Mat processed = preprocessor.LetterboxResize(original);

            Console.WriteLine($"原始: {original.Width}×{original.Height}");
            Console.WriteLine($"处理后: {processed.Width}×{processed.Height}");

            // 创建对比图像（左原始，右处理后）
            Mat comparison = new Mat(640, 1280, MatType.CV_8UC3, Scalar.All(0));

            // 左边: 缩小的原始图像
            Mat originalResized = new Mat();
            Cv2.Resize(original, originalResized, new Size(640, 640));
            originalResized.CopyTo(comparison[new Rect(0, 0, 640, 640)]);

            // 右边: 处理后的图像
            processed.CopyTo(comparison[new Rect(640, 0, 640, 640)]);

            Cv2.ImWrite("comparison.jpg", comparison);
            Console.WriteLine("对比图像已保存为 comparison.jpg");
        }
    }
}
