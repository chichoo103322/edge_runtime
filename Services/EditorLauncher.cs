using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;

namespace edge_runtime.Services
{
    /// <summary>
    /// 外部编辑器启动器 - 负责启动外部编辑器应用程序
    /// </summary>
    public class EditorLauncher
    {
        /// <summary>
        /// 打开外部编辑器
        /// </summary>
        public void OpenEditor(Window owner)
        {
            try
            {
                string editorPath = ConfigManager.Instance.GetEditorPath();

                // 如果未配置，提示用户选择
                if (string.IsNullOrEmpty(editorPath) || (!File.Exists(editorPath) && !Directory.Exists(editorPath)))
                {
                    editorPath = PromptForEditorPath();
                    if (string.IsNullOrEmpty(editorPath))
                    {
                        UILogManager.Instance.LogWarning("未选择编辑器可执行文件或目录");
                        return;
                    }
                }

                // 根据路径类型启动编辑器
                if (File.Exists(editorPath))
                {
                    if (editorPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        LaunchExecutable(editorPath);
                    }
                    else if (editorPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        LoadAndShowWindowFromAssembly(editorPath, owner);
                    }
                    else
                    {
                        throw new Exception("不支持的文件类型");
                    }
                }
                else if (Directory.Exists(editorPath))
                {
                    SearchAndLoadEditorFromDirectory(editorPath, owner);
                }
                else
                {
                    throw new Exception("提供的编辑器路径无效");
                }
            }
            catch (Exception ex)
            {
                string msg = $"启动编辑器失败: {ex.Message}";
                MessageBox.Show(msg);
                UILogManager.Instance.LogError(msg);
            }
        }

        /// <summary>
        /// 提示用户选择编辑器路径
        /// </summary>
        private string PromptForEditorPath()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|库文件 (*.dll)|*.dll|项目目录|*.*",
                Title = "请选择动作树编辑器的可执行文件、DLL 或 edge 项目目录"
            };

            if (dlg.ShowDialog() == true)
            {
                string selectedPath = dlg.FileName;
                ConfigManager.Instance.SetEditorPath(selectedPath);
                UILogManager.Instance.LogInfo($"编辑器路径已保存: {selectedPath}");
                return selectedPath;
            }

            return null;
        }

        /// <summary>
        /// 启动可执行文件
        /// </summary>
        private void LaunchExecutable(string exePath)
        {
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true
            };
            Process.Start(startInfo);
            UILogManager.Instance.LogInfo($"已启动外部编辑器进程: {exePath}");
        }

        /// <summary>
        /// 从程序集加载并显示窗口
        /// </summary>
        private void LoadAndShowWindowFromAssembly(string assemblyPath, Window owner)
        {
            if (!TryLoadAndShowWindow(assemblyPath, owner))
            {
                throw new Exception("未在 DLL 中找到可用的 Window 类型");
            }

            UILogManager.Instance.LogInfo($"已在当前进程中加载编辑器: {assemblyPath}");
        }

        /// <summary>
        /// 在目录中搜索并加载编辑器
        /// </summary>
        private void SearchAndLoadEditorFromDirectory(string directory, Window owner)
        {
            var candidates = new[]
            {
                Path.Combine(directory, "bin", "Debug"),
                Path.Combine(directory, "bin", "Debug", "net8.0"),
                Path.Combine(directory, "bin", "Debug", "net8.0-windows"),
                Path.Combine(directory, "out"),
                Path.Combine(directory, "build")
            };

            foreach (var dir in candidates)
            {
                if (!Directory.Exists(dir))
                    continue;

                var dlls = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
                foreach (var dll in dlls)
                {
                    try
                    {
                        if (TryLoadAndShowWindow(dll, owner))
                        {
                            UILogManager.Instance.LogInfo($"已在当前进程中加载编辑器 DLL: {dll}");
                            return;
                        }
                    }
                    catch { /* ignore and try next */ }
                }
            }

            throw new Exception("在指定目录中未找到可加载的编辑器输出 (DLL/EXE)。请先编译 edge 项目或手动选择编辑器的可执行文件。");
        }

        /// <summary>
        /// 尝试加载程序集并显示窗口
        /// </summary>
        private bool TryLoadAndShowWindow(string assemblyPath, Window owner)
        {
            try
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

                foreach (var type in asm.GetExportedTypes())
                {
                    if (typeof(Window).IsAssignableFrom(type) && !type.IsAbstract)
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            var wnd = (Window)Activator.CreateInstance(type);
                            wnd.Owner = owner;
                            wnd.Show();
                        });

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                UILogManager.Instance.LogError($"加载程序集失败: {assemblyPath} -> {ex.Message}");
            }

            return false;
        }
    }
}
