using MCPForUnity.Editor.Windows;
using UnityEditor;

namespace MCPForUnity.Editor.MenuItems
{
    public static class MCPForUnityMenu
    {
        [MenuItem("Window/MCP For Unity %#m", priority = 1)]
        public static void ShowMCPWindow()
        {
            MCPForUnityEditorWindow.ShowWindow();
        }
        [MenuItem("Tools/MCP For Unity/Open Server Logs", priority = 20)]
        public static void ShowLogWindow()
        {
            McpLogWindow.ShowWindow();
        }
    }
}

