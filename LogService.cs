using OpenCvSharp;
using System;
using System.IO;
using MySql.Data.MySqlClient;

namespace edge_runtime
{
    public class LogService
    {
        private readonly string _connectionString;
        private readonly string _logDirectory;

        public LogService(string connectionString, string logDirectory = "ErrorLogs")
        {
            _connectionString = connectionString;
            _logDirectory = logDirectory;

            // 创建日志目录（如果不存在）
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }
        }

        /// <summary>
        /// 保存错误帧图片
        /// </summary>
        /// <param name="frame">OpenCvSharp Mat 对象</param>
        /// <param name="stepName">步骤名称</param>
        /// <returns>保存的图片文件路径</returns>
        public string SaveErrorImage(Mat frame, string stepName)
        {
            if (frame == null || frame.Empty())
            {
                return null;
            }

            try
            {
                // 生成文件名
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string sanitizedStepName = SanitizeFileName(stepName);
                string fileName = $"NG_{timestamp}_{sanitizedStepName}.jpg";
                string filePath = Path.Combine(_logDirectory, fileName);

                // 设置JPEG压缩参数
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
        /// 记录流程结果到数据库
        /// </summary>
        /// <param name="stepName">步骤名称</param>
        /// <param name="isSuccess">是否成功</param>
        /// <param name="msg">消息/备注</param>
        /// <param name="imagePath">图片路径（可选）</param>
        public void LogResult(string stepName, bool isSuccess, string msg, string imagePath = null)
        {
            try
            {
                using (var connection = new MySqlConnection(_connectionString))
                {
                    connection.Open();

                    string query = @"INSERT INTO workflow_logs (created_at, step_name, status, image_path, error_msg) 
                                     VALUES (@created_at, @step_name, @status, @image_path, @error_msg)";

                    using (var command = new MySqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@created_at", DateTime.Now);
                        command.Parameters.AddWithValue("@step_name", stepName ?? string.Empty);
                        command.Parameters.AddWithValue("@status", isSuccess ? "success" : "failed");
                        command.Parameters.AddWithValue("@image_path", imagePath ?? string.Empty);
                        command.Parameters.AddWithValue("@error_msg", msg ?? string.Empty);

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
        /// 清理非法文件名字符
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
