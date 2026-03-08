using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace edge_runtime
{
    /// <summary>
    /// 全局配置管理器
    /// 读取 config.json 文件并提供统一的配置访问接口
    /// </summary>
    public class ConfigManager
    {
        private static ConfigManager _instance;
        private JsonElement _config;
        private static readonly object _lock = new object();

        public static ConfigManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new ConfigManager();
                        }
                    }
                }
                return _instance;
            }
        }

        private ConfigManager()
        {
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                
                if (!File.Exists(configPath))
                {
                    System.Windows.MessageBox.Show($"配置文件不存在: {configPath}\n将使用默认配置");
                    return;
                }

                string json = File.ReadAllText(configPath);
                _config = JsonDocument.Parse(json).RootElement;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"加载配置文件失败: {ex.Message}\n将使用默认配置");
            }
        }

        // 数据库连接字符串
        public string GetDatabaseConnectionString()
        {
            try
            {
                if (_config.ValueKind == JsonValueKind.Undefined)
                    return GetDefaultConnectionString();

                var db = _config.GetProperty("Database");
                string server = GetJsonString(db, "Server", "localhost");
                int port = GetJsonInt(db, "Port", 3306);
                string database = GetJsonString(db, "Database", "edge_runtime");
                string user = GetJsonString(db, "User", "root");
                string password = GetJsonString(db, "Password", "your_password");

                return $"Server={server};Port={port};Database={database};User={user};Password={password};Charset=utf8mb4;";
            }
            catch
            {
                return GetDefaultConnectionString();
            }
        }

        private string GetDefaultConnectionString()
        {
            return "Server=localhost;Port=3306;Database=edge_runtime;User=root;Password=your_password;Charset=utf8mb4;";
        }

        // 日志设置
        public string GetLogDirectory()
        {
            try
            {
                if (_config.ValueKind == JsonValueKind.Undefined)
                    return "ErrorLogs";

                var log = _config.GetProperty("LogSettings");
                return GetJsonString(log, "LogDirectory", "ErrorLogs");
            }
            catch
            {
                return "ErrorLogs";
            }
        }

        public int GetMaxLogLines()
        {
            try
            {
                if (_config.ValueKind == JsonValueKind.Undefined)
                    return 1000;

                var log = _config.GetProperty("LogSettings");
                return GetJsonInt(log, "MaxLogLines", 1000);
            }
            catch
            {
                return 1000;
            }
        }

        public bool IsEnabledDatabaseLogging()
        {
            try
            {
                if (_config.ValueKind == JsonValueKind.Undefined)
                    return true;

                var log = _config.GetProperty("LogSettings");
                return GetJsonBool(log, "EnableDatabaseLogging", true);
            }
            catch
            {
                return true;
            }
        }

        // 相机设置
        public int GetImageResizeWidth()
        {
            try
            {
                if (_config.ValueKind == JsonValueKind.Undefined)
                    return 512;

                var camera = _config.GetProperty("CameraSettings");
                return GetJsonInt(camera, "ImageResizeWidth", 512);
            }
            catch
            {
                return 512;
            }
        }

        public int GetImageResizeHeight()
        {
            try
            {
                if (_config.ValueKind == JsonValueKind.Undefined)
                    return 512;

                var camera = _config.GetProperty("CameraSettings");
                return GetJsonInt(camera, "ImageResizeHeight", 512);
            }
            catch
            {
                return 512;
            }
        }

        // 新增：获取默认摄像头索引
        public int GetDefaultCameraIndex()
        {
            try
            {
                if (_config.ValueKind == JsonValueKind.Undefined)
                    return -1;

                var camera = _config.GetProperty("CameraSettings");
                return GetJsonInt(camera, "DefaultCameraIndex", -1);
            }
            catch
            {
                return -1;
            }
        }

        // 编辑器设置
        public string GetEditorPath()
        {
            try
            {
                if (_config.ValueKind == JsonValueKind.Undefined)
                    return string.Empty;

                if (_config.TryGetProperty("EditorSettings", out var editorSettings))
                {
                    return GetJsonString(editorSettings, "EditorPath", string.Empty);
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public bool SetEditorPath(string editorPath)
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                
                // 读取现有配置
                if (!File.Exists(configPath))
                    return false;

                string json = File.ReadAllText(configPath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 使用 JsonSerializerOptions 来处理修改
                var options = new JsonSerializerOptions { WriteIndented = true };
                
                // 将 JsonElement 转换为字典进行修改
                var configDict = JsonSerializer.Deserialize<Dictionary<string, object>>(root.GetRawText(), options);
                
                if (configDict == null)
                    configDict = new Dictionary<string, object>();

                // 确保 EditorSettings 存在
                if (!configDict.ContainsKey("EditorSettings"))
                {
                    configDict["EditorSettings"] = new Dictionary<string, object>();
                }

                var editorSettings = configDict["EditorSettings"] as Dictionary<string, object>;
                if (editorSettings == null)
                {
                    editorSettings = new Dictionary<string, object>();
                    configDict["EditorSettings"] = editorSettings;
                }

                editorSettings["EditorPath"] = editorPath;

                // 写回配置文件
                string updatedJson = JsonSerializer.Serialize(configDict, options);
                File.WriteAllText(configPath, updatedJson);

                // 重新加载配置
                LoadConfig();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 辅助方法
        private string GetJsonString(JsonElement element, string propertyName, string defaultValue)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var prop))
                    return prop.GetString() ?? defaultValue;
            }
            catch { }
            return defaultValue;
        }

        private int GetJsonInt(JsonElement element, string propertyName, int defaultValue)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var prop))
                    return prop.GetInt32();
            }
            catch { }
            return defaultValue;
        }

        private bool GetJsonBool(JsonElement element, string propertyName, bool defaultValue)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var prop))
                    return prop.GetBoolean();
            }
            catch { }
            return defaultValue;
        }
    }
}
