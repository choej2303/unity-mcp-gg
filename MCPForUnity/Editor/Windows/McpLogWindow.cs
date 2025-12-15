using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using MCPForUnity.Editor.Windows.Components.Logs;

namespace MCPForUnity.Editor.Windows
{
    public class McpLogWindow : EditorWindow
    {
        private McpLogViewer _logViewer;

        public static void ShowWindow()
        {
            var window = GetWindow<McpLogWindow>("MCP Logs");
            window.minSize = new Vector2(400, 300);
        }

        private void CreateGUI()
        {
            _logViewer = new McpLogViewer();
            _logViewer.SetWindowMode();
            _logViewer.style.flexGrow = 1;
            rootVisualElement.Add(_logViewer);
        }
    }
}
