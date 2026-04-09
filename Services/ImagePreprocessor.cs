using OpenCvSharp;
using System;

namespace edge_runtime.Services
{
    /// <summary>
    /// 图像预处理器 - 通用图像自适应处理模块
    /// 
    /// 核心功能：
    /// 1. Letterbox等比例缩放：保证不变形，自动适应任意输入尺寸
    /// 2. 黑边灰色填充：使用128灰色填充边界，保持图像信息完整
    /// 3. 自动尺寸匹配：自动适配模型输入要求（默认640x640）
    /// 4. 一次性处理：输入任意尺寸，输出标准化张量
    /// 
    /// 使用场景：解决"InputArgument_Error: Got 512 Expected 640"类尺寸不匹配错误
    /// </summary>
    public class ImagePreprocessor
    {
        /// <summary>
        /// 模型输入要求的标准尺寸
        /// </summary>
        private int _targetSize = 640;

        /// <summary>
        /// 灰色填充颜色（用于Letterbox填充的像素值）
        /// </summary>
        private const int PADDING_COLOR = 128;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="targetSize">模型要求的输入尺寸（默认640）</param>
        public ImagePreprocessor(int targetSize = 640)
        {
            if (targetSize <= 0)
                throw new ArgumentException("目标尺寸必须大于0", nameof(targetSize));
            _targetSize = targetSize;
        }

        /// <summary>
        /// Letterbox图像预处理 - 核心算法
        /// 
        /// 算法说明：
        /// 1. 计算等比例缩放因子（使图像完全适配目标框）
        /// 2. 按比例缩放原图
        /// 3. 在缩放后的图像周围填充灰色边框
        /// 4. 最终产生目标尺寸的方形图像
        /// 
        /// 优点：
        /// - 保持原始宽高比，不会拉伸变形
        /// - 完整保留图像信息
        /// - 边框填充灰色（中性值），不干扰模型识别
        /// </summary>
        /// <param name="inputImage">输入的OpenCV Mat图像</param>
        /// <returns>预处理后的标准尺寸图像（640x640）</returns>
        public Mat LetterboxResize(Mat inputImage)
        {
            if (inputImage == null || inputImage.Empty())
                throw new ArgumentException("输入图像为空或无效", nameof(inputImage));

            int origWidth = inputImage.Width;
            int origHeight = inputImage.Height;

            // 步骤1: 计算等比例缩放因子（取较小值，保证完整缩放到目标框内）
            float scale = Math.Min((float)_targetSize / origWidth, (float)_targetSize / origHeight);

            // 步骤2: 计算缩放后的实际尺寸
            int scaledWidth = (int)(origWidth * scale);
            int scaledHeight = (int)(origHeight * scale);

            // 步骤3: 缩放图像（使用双线性插值保持质量）
            Mat scaled = new Mat();
            Cv2.Resize(inputImage, scaled, new Size(scaledWidth, scaledHeight), 0, 0, InterpolationFlags.Linear);

            // 步骤4: 计算填充偏移（将缩放后的图像居中放置）
            int padLeft = (_targetSize - scaledWidth) / 2;
            int padTop = (_targetSize - scaledHeight) / 2;
            int padRight = _targetSize - scaledWidth - padLeft;
            int padBottom = _targetSize - scaledHeight - padTop;

            // 步骤5: 创建最终输出图像（先填充灰色背景）
            Mat result = new Mat(_targetSize, _targetSize, MatType.CV_8UC3, new Scalar(PADDING_COLOR, PADDING_COLOR, PADDING_COLOR));

            // 步骤6: 将缩放后的图像复制到中心位置
            Rect roi = new Rect(padLeft, padTop, scaledWidth, scaledHeight);
            scaled.CopyTo(result[roi]);

            // 步骤7: 清理临时资源
            scaled.Dispose();

            return result;
        }

        /// <summary>
        /// 将预处理后的Mat图像转换为ONNX推理所需的归一化张量
        /// 
        /// 处理流程：
        /// 1. BGR到RGB的通道转换（OpenCV默认是BGR格式）
        /// 2. 将像素值从[0,255]归一化到[0,1]
        /// 3. 组织为ONNX张量格式：(Batch=1, Channel=3, Height=640, Width=640)
        /// </summary>
        /// <param name="mat">预处理后的Mat图像</param>
        /// <returns>归一化后的浮点数组（长度1x3x640x640）</returns>
        public float[] MatToNormalizedArray(Mat mat)
        {
            if (mat == null || mat.Empty())
                throw new ArgumentException("输入Mat为空或无效", nameof(mat));

            int height = mat.Height;
            int width = mat.Width;
            int channels = mat.Channels();

            if (channels != 3)
                throw new ArgumentException("输入图像必须是3通道（BGR）格式", nameof(mat));

            // 分配张量空间：1(batch) x 3(channels) x height x width
            float[] tensorData = new float[1 * 3 * height * width];
            int idx = 0;

            // 按照ONNX模型的期望格式填充数据
            // 格式：[B, C, H, W] 其中 C=3(R, G, B)
            // 遍历顺序：先按行列遍历空间维度，再按通道组织

            for (int c = 0; c < 3; c++)  // 遍历每个通道（R, G, B）
            {
                for (int y = 0; y < height; y++)  // 遍历行
                {
                    for (int x = 0; x < width; x++)  // 遍历列
                    {
                        // 获取当前像素的BGR值
                        Vec3b pixel = mat.At<Vec3b>(y, x);

                        // OpenCV是BGR格式，需要转换为RGB
                        // 然后归一化到[0,1]范围
                        float value;
                        if (c == 0)  // R通道
                            value = pixel.Item2 / 255.0f;
                        else if (c == 1)  // G通道
                            value = pixel.Item1 / 255.0f;
                        else  // B通道
                            value = pixel.Item0 / 255.0f;

                        tensorData[idx++] = value;
                    }
                }
            }

            return tensorData;
        }

        /// <summary>
        /// 一体化预处理函数 - 从任意尺寸图像到ONNX张量
        /// 
        /// 这是最常用的接口，一次调用完成全部预处理流程：
        /// 输入：任意尺寸的相机帧 -> 输出：ONNX模型所需的归一化张量
        /// </summary>
        /// <param name="inputMat">输入图像（任意尺寸）</param>
        /// <returns>预处理后的张量数组</returns>
        public float[] Preprocess(Mat inputMat)
        {
            if (inputMat == null || inputMat.Empty())
                throw new ArgumentException("输入图像为空或无效", nameof(inputMat));

            // 步骤1: Letterbox缩放（等比例缩放 + 灰色填充）
            Mat letterboxMat = LetterboxResize(inputMat);

            try
            {
                // 步骤2: 转换为ONNX张量（通道转换 + 像素归一化）
                float[] tensorData = MatToNormalizedArray(letterboxMat);
                return tensorData;
            }
            finally
            {
                // 清理临时资源
                letterboxMat?.Dispose();
            }
        }

        /// <summary>
        /// 设置模型目标尺寸（支持动态调整）
        /// </summary>
        /// <param name="newSize">新的目标尺寸</param>
        public void SetTargetSize(int newSize)
        {
            if (newSize <= 0)
                throw new ArgumentException("目标尺寸必须大于0", nameof(newSize));
            _targetSize = newSize;
        }

        /// <summary>
        /// 获取当前配置的模型目标尺寸
        /// </summary>
        /// <returns>目标尺寸（如640）</returns>
        public int GetTargetSize() => _targetSize;
    }
}
