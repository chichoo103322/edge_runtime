using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace edge_runtime
{
    public partial class ErrorLogViewerWindow : Window
    {
        private readonly string _connectionString;
        private readonly string _logDirectory;

        public ErrorLogViewerWindow()
        {
            InitializeComponent();

            _connectionString = ConfigManager.Instance.GetDatabaseConnectionString();
            _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ErrorLogs");

            // 默认查询今天的记录
            DpStartDate.SelectedDate = DateTime.Today;
            DpEndDate.SelectedDate = DateTime.Today;

            LoadLogs();
        }

        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            LoadLogs();
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Directory.Exists(_logDirectory))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _logDirectory,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show($"日志目录不存在: {_logDirectory}", "提示");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开目录失败: {ex.Message}", "错误");
            }
        }

        private void LoadLogs()
        {
            var logs = new List<ErrorLogItem>();

            try
            {
                using var connection = new MySqlConnection(_connectionString);
                connection.Open();

                // 构建查询
                string query = "SELECT time, step, status, img_path FROM workflow_logs WHERE status IN ('NG', 'TIMEOUT')";
                var parameters = new List<MySqlParameter>();

                if (DpStartDate.SelectedDate.HasValue)
                {
                    query += " AND time >= @startDate";
                    parameters.Add(new MySqlParameter("@startDate", DpStartDate.SelectedDate.Value.Date));
                }

                if (DpEndDate.SelectedDate.HasValue)
                {
                    query += " AND time < @endDate";
                    parameters.Add(new MySqlParameter("@endDate", DpEndDate.SelectedDate.Value.Date.AddDays(1)));
                }

                // 状态筛选
                if (CbStatusFilter.SelectedIndex == 1)
                {
                    query += " AND status = 'NG'";
                }
                else if (CbStatusFilter.SelectedIndex == 2)
                {
                    query += " AND status = 'TIMEOUT'";
                }

                query += " ORDER BY time DESC LIMIT 500";

                using var command = new MySqlCommand(query, connection);
                command.Parameters.AddRange(parameters.ToArray());

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    logs.Add(new ErrorLogItem
                    {
                        Time = reader.GetDateTime("time"),
                        Step = reader.IsDBNull(reader.GetOrdinal("step")) ? "" : reader.GetString("step"),
                        Status = reader.IsDBNull(reader.GetOrdinal("status")) ? "" : reader.GetString("status"),
                        ImgPath = reader.IsDBNull(reader.GetOrdinal("img_path")) ? "" : reader.GetString("img_path")
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"查询数据库失败: {ex.Message}", "错误");
            }

            LvLogs.ItemsSource = logs;
            TxtSummary.Text = $"共 {logs.Count} 条记录 (最多显示500条)";
        }

        private void LvLogs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LvLogs.SelectedItem is not ErrorLogItem item)
            {
                ImgPreview.Source = null;
                TxtNoImage.Visibility = Visibility.Visible;
                TxtImageTitle.Text = "选择一条记录查看图片";
                TxtImagePath.Text = "";
                return;
            }

            TxtImageTitle.Text = $"[{item.Status}] {item.Step} - {item.TimeDisplay}";

            if (string.IsNullOrEmpty(item.ImgPath))
            {
                ImgPreview.Source = null;
                TxtNoImage.Visibility = Visibility.Visible;
                TxtImagePath.Text = "无图片路径";
                return;
            }

            // 尝试解析图片路径（支持相对路径和绝对路径）
            string fullPath = item.ImgPath;
            if (!Path.IsPathRooted(fullPath))
            {
                fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fullPath);
            }

            TxtImagePath.Text = fullPath;

            if (File.Exists(fullPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    ImgPreview.Source = bitmap;
                    TxtNoImage.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    ImgPreview.Source = null;
                    TxtNoImage.Text = $"加载失败: {ex.Message}";
                    TxtNoImage.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ImgPreview.Source = null;
                TxtNoImage.Text = "图片文件不存在";
                TxtNoImage.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>
    /// 错误日志条目数据模型
    /// </summary>
    public class ErrorLogItem
    {
        public DateTime Time { get; set; }
        public string Step { get; set; } = "";
        public string Status { get; set; } = "";
        public string ImgPath { get; set; } = "";

        public string TimeDisplay => Time.ToString("yyyy-MM-dd HH:mm:ss");

        public Brush StatusColor => Status switch
        {
            "NG" => new SolidColorBrush(Color.FromRgb(231, 76, 60)),
            "TIMEOUT" => new SolidColorBrush(Color.FromRgb(243, 156, 18)),
            _ => new SolidColorBrush(Color.FromRgb(149, 165, 166))
        };
    }
}
