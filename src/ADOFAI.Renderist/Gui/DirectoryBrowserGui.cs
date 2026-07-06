using System;
using System.IO;
using UnityEngine;
using ADOFAI.Renderist;

namespace ADOFAI.Renderist.Gui
{
    /// <summary>
    /// UMM IMGUI 内置目录浏览面板（Phase 2.2.2）。
    ///
    /// 无副作用契约：
    ///   - 只读枚举目录（Directory.GetLogicalDrives / Directory.GetDirectories）。
    ///   - 不创建目录（绝不调用 Directory.CreateDirectory）。
    ///   - 不写文件，不创建 session 目录。
    ///   - 异常时捕获并显示错误，不崩溃。
    ///
    /// 缓存策略：
    ///   - 子目录列表缓存，仅在 NeedsRefresh=true 时重新枚举。
    ///   - 刷新时机：打开面板 / 点击刷新 / 切换盘符 / 进入子目录 / 返回上级目录。
    ///   - 不在 OnGUI 每次调用时重新枚举。
    /// </summary>
    internal struct DirectoryBrowserState
    {
        public bool IsOpen;
        public string CurrentPath;
        public string[] Subdirectories;
        public string[] LogicalDrives;
        public string ErrorMessage;
        public Vector2 ScrollPosition;
        public bool NeedsRefresh;
    }

    internal static class DirectoryBrowserGui
    {
        private const int MaxSubdirectoriesDisplayed = 200;
        private const int DriveButtonsPerRow = 4;

        /// <summary>
        /// 绘制目录浏览面板。
        /// 返回 true 表示用户点击了「选择此目录」并已确认选择 CurrentPath。
        /// 调用方负责将 CurrentPath 写入 Settings.OutputDirectory 并关闭面板。
        /// </summary>
        public static bool Draw(ref DirectoryBrowserState state)
        {
            if (!state.IsOpen) return false;

            bool selected = false;

            GUILayout.Label(UiText.GuiDirBrowserTitle, GUI.skin.label);

            // 当前路径
            string displayPath = string.IsNullOrEmpty(state.CurrentPath)
                ? UiText.GuiNonePlaceholder
                : state.CurrentPath;
            GUILayout.Label(UiText.GuiDirBrowserCurrentPathPrefix + displayPath, GUI.skin.label);

            // 工具栏
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(UiText.GuiDirBrowserUp, GUI.skin.button))
            {
                NavigateUp(ref state);
            }
            if (GUILayout.Button(UiText.GuiDirBrowserRefresh, GUI.skin.button))
            {
                state.NeedsRefresh = true;
            }
            if (GUILayout.Button(UiText.GuiDirBrowserClose, GUI.skin.button))
            {
                state.IsOpen = false;
            }
            GUILayout.EndHorizontal();

            // 盘符列表（手动分行，避免依赖 GUILayout 自动换行）
            GUILayout.Label(UiText.GuiDirBrowserDrives, GUI.skin.label);
            if (state.LogicalDrives != null && state.LogicalDrives.Length > 0)
            {
                int rows = (state.LogicalDrives.Length + DriveButtonsPerRow - 1) / DriveButtonsPerRow;
                for (int r = 0; r < rows; r++)
                {
                    GUILayout.BeginHorizontal();
                    for (int c = 0; c < DriveButtonsPerRow; c++)
                    {
                        int idx = r * DriveButtonsPerRow + c;
                        if (idx >= state.LogicalDrives.Length) break;
                        if (GUILayout.Button(state.LogicalDrives[idx], GUI.skin.button))
                        {
                            state.CurrentPath = state.LogicalDrives[idx];
                            state.NeedsRefresh = true;
                        }
                    }
                    GUILayout.EndHorizontal();
                }
            }

            // 错误提示
            if (!string.IsNullOrEmpty(state.ErrorMessage))
            {
                GUILayout.Label(state.ErrorMessage, GUI.skin.label);
            }

            // 子目录列表（ScrollView，限制高度）
            GUILayout.Label(UiText.GuiDirBrowserSubdirs, GUI.skin.label);
            state.ScrollPosition = GUILayout.BeginScrollView(state.ScrollPosition, GUILayout.Height(200));
            if (state.Subdirectories != null && state.Subdirectories.Length > 0)
            {
                int displayCount = Math.Min(state.Subdirectories.Length, MaxSubdirectoriesDisplayed);
                for (int i = 0; i < displayCount; i++)
                {
                    string dir = state.Subdirectories[i];
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) name = dir;
                    if (GUILayout.Button(name + "/", GUI.skin.button))
                    {
                        state.CurrentPath = dir;
                        state.NeedsRefresh = true;
                    }
                }
                if (state.Subdirectories.Length > MaxSubdirectoriesDisplayed)
                {
                    GUILayout.Label(UiText.Format(UiText.GuiDirBrowserTruncatedFormat, MaxSubdirectoriesDisplayed), GUI.skin.label);
                }
            }
            else
            {
                GUILayout.Label(UiText.GuiNonePlaceholder, GUI.skin.label);
            }
            GUILayout.EndScrollView();

            // 选择此目录按钮
            if (GUILayout.Button(UiText.GuiDirBrowserSelect, GUI.skin.button))
            {
                selected = true;
                state.IsOpen = false;
            }

            // 刷新逻辑
            if (state.NeedsRefresh)
            {
                RefreshListing(ref state);
                state.NeedsRefresh = false;
            }

            return selected;
        }

        /// <summary>
        /// 打开面板并初始化状态。初始路径按 ResolveInitialPath 规则解析。
        /// </summary>
        public static void Open(ref DirectoryBrowserState state, string configuredOutputDirectory)
        {
            state.IsOpen = true;
            state.NeedsRefresh = true;
            state.ScrollPosition = Vector2.zero;
            state.ErrorMessage = null;
            state.Subdirectories = null;
            state.LogicalDrives = null;
            state.CurrentPath = ResolveInitialPath(configuredOutputDirectory);
        }

        private static void NavigateUp(ref DirectoryBrowserState state)
        {
            if (string.IsNullOrEmpty(state.CurrentPath)) return;
            try
            {
                DirectoryInfo parent = Directory.GetParent(state.CurrentPath);
                if (parent != null)
                {
                    state.CurrentPath = parent.FullName;
                    state.NeedsRefresh = true;
                }
            }
            catch
            {
                // 已经是根目录或异常，忽略
            }
        }

        private static void RefreshListing(ref DirectoryBrowserState state)
        {
            // 刷新盘符列表
            try
            {
                state.LogicalDrives = Directory.GetLogicalDrives();
            }
            catch (Exception)
            {
                state.LogicalDrives = new string[0];
            }

            // 刷新子目录列表
            if (string.IsNullOrEmpty(state.CurrentPath))
            {
                state.Subdirectories = new string[0];
                return;
            }

            try
            {
                state.Subdirectories = Directory.GetDirectories(state.CurrentPath);
                state.ErrorMessage = null;
            }
            catch (Exception ex)
            {
                state.Subdirectories = new string[0];
                state.ErrorMessage = UiText.Format(UiText.GuiDirBrowserEnumFailFormat, ex.Message);
            }
        }

        /// <summary>
        /// 解析初始路径。规则：
        /// 1. Settings.OutputDirectory 非空且存在 → 用它
        /// 2. Settings.OutputDirectory 非空但不存在 → 尝试有效父目录
        /// 3. 否则用 Application.persistentDataPath
        /// 4. 否则用第一个可用盘符
        /// </summary>
        private static string ResolveInitialPath(string configuredOutputDirectory)
        {
            // 1. Settings.OutputDirectory 非空且存在
            if (!string.IsNullOrEmpty(configuredOutputDirectory) && Directory.Exists(configuredOutputDirectory))
            {
                return configuredOutputDirectory;
            }

            // 2. Settings.OutputDirectory 非空但不存在 → 尝试有效父目录
            if (!string.IsNullOrEmpty(configuredOutputDirectory))
            {
                string path = configuredOutputDirectory;
                while (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            return path;
                        }
                        DirectoryInfo parent = Directory.GetParent(path);
                        if (parent == null) break;
                        path = parent.FullName;
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            // 3. Application.persistentDataPath
            try
            {
                string pdp = Application.persistentDataPath;
                if (!string.IsNullOrEmpty(pdp) && Directory.Exists(pdp))
                {
                    return pdp;
                }
            }
            catch
            {
                // 持续降级
            }

            // 4. 第一个可用盘符
            try
            {
                string[] drives = Directory.GetLogicalDrives();
                if (drives != null && drives.Length > 0)
                {
                    return drives[0];
                }
            }
            catch
            {
                // 全部失败
            }

            return string.Empty;
        }
    }
}
