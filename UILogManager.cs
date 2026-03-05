using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace edge_runtime
{
    /// <summary>
    /// UI 日志项
    /// </summary>
    public class LogEntry : INotifyPropertyChanged
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } // INFO, WARNING, ERROR
        public string Message { get; set; }

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] [{Level}] {Message}";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// UI 日志管理器
    /// 管理实时日志显示
    /// </summary>
    public class UILogManager : INotifyPropertyChanged
    {
        private static UILogManager _instance;
        private static readonly object _lock = new object();

        public ObservableCollection<LogEntry> Logs { get; } = new ObservableCollection<LogEntry>();

        private int _maxLogLines = 1000;

        public static UILogManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new UILogManager();
                        }
                    }
                }
                return _instance;
            }
        }

        private UILogManager()
        {
            _maxLogLines = ConfigManager.Instance.GetMaxLogLines();
        }

        /// <summary>
        /// 添加信息日志
        /// </summary>
        public void LogInfo(string message)
        {
            AddLog("INFO", message);
        }

        /// <summary>
        /// 添加警告日志
        /// </summary>
        public void LogWarning(string message)
        {
            AddLog("WARNING", message);
        }

        /// <summary>
        /// 添加错误日志
        /// </summary>
        public void LogError(string message)
        {
            AddLog("ERROR", message);
        }

        private void AddLog(string level, string message)
        {
            try
            {
                var entry = new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = level,
                    Message = message
                };

                // 在 UI 线程上添加日志
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    Logs.Add(entry);

                    // 限制日志行数，防止占用过多内存
                    while (Logs.Count > _maxLogLines)
                    {
                        Logs.RemoveAt(0);
                    }
                });
            }
            catch { }
        }

        /// <summary>
        /// 清空所有日志
        /// </summary>
        public void ClearLogs()
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Logs.Clear();
            });
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
