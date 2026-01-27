using OpenCvSharp;
using System;
using System.IO;
using MySql.Data.MySqlClient;

namespace edge_runtime
{
    public class LogService
    {
        // 数据库连接字符串（请根据实际情况修改）
        private const string CONNECTION_STRING = "Server=localhost;Database=edge_runtime;User=root;Password=your_password;Charset=utf8mb4;";
        
        private readonly string _logDirectory;

        public LogService(string logDirectory = "ErrorLogs")
        {
            _logDirectory = logDirectory;

            // 创建日志目录（如果不存在）
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }
        }

        /// <summary>
        /// 保存帧图片到磁盘
        /// </summary>
        /// <param name="frame">OpenCvSharp Mat 对象</param>
        /// <param name="stepName">步骤名称</param>
        /// <param name="status">状态标签（OK/NG）</param>
        /// <returns>保存的图片文件路径</returns>
        public string SaveFrame(Mat frame, string stepName, string status = "NG")
        {
            if (frame == null || frame.Empty())
            {
                return null;
            }

            try
            {
                // 生成文件名格式: 时间戳_步骤名_状态.jpg
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string sanitizedStepName = SanitizeFileName(stepName);
                string fileName = $"{timestamp}_{sanitizedStepName}_{status}.jpg";
                string filePath = Path.Combine(_logDirectory, fileName);

                // 设置JPEG压缩参数（质量70）
                var param = new int[] { (int)ImwriteFlags.JpegQuality, 70 };

                // 保存图片
                Cv2.ImWrite(filePath, frame, param);

                return filePath;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"保存图片失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 记录流程结果到 MySQL 数据库
        /// </summary>
        /// <param name="stepName">步骤名称</param>
        /// <param name="status">状态（OK/NG/Complete）</param>
        /// <param name="imagePath">图片路径（可选）</param>
        public void LogToDb(string stepName, string status, string imagePath = null)
        {
            try
            {
                using (var connection = new MySqlConnection(CONNECTION_STRING))
                {
                    connection.Open();

                    string query = @"INSERT INTO workflow_logs (time, step, status, img_path) 
                                     VALUES (@time, @step, @status, @img_path)";

                    using (var command = new MySqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@time", DateTime.Now);
                        command.Parameters.AddWithValue("@step", stepName ?? string.Empty);
                        command.Parameters.AddWithValue("@status", status ?? string.Empty);
                        command.Parameters.AddWithValue("@img_path", imagePath ?? string.Empty);

                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"数据库记录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理文件名中的非法字符
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "unknown";

            // 移除非法字符
            string invalidChars = System.Text.RegularExpressions.Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string pattern = $"[{invalidChars}]";
            return System.Text.RegularExpressions.Regex.Replace(fileName, pattern, "_");
        }
    }
}
