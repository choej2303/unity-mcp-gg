using System;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Windows.Components.Logs
{
    [UxmlElement]
    public partial class McpLogViewer : VisualElement
    {
        private Button _toggleBtn;
        private Button _clearBtn;
        private Button _copyBtn;
        private ScrollView _scrollView;
        private VisualElement _logViewerRoot;
        private bool _isCollapsed = true;



        public McpLogViewer()
        {
            // Load UXML and USS
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Assets/MCPForUnity/Editor/Windows/Components/Logs/McpLogViewer.uxml");
            
            // Fallback strategy: Search by name if fixed path fails
            if (visualTree == null)
            {
                var guids = AssetDatabase.FindAssets("McpLogViewer t:VisualTreeAsset");
                if (guids.Length > 0)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
                }
            }

            if (visualTree != null)
            {
                visualTree.CloneTree(this);
            }
            else
            {
                // Create minimal UI if UXML fails to load
                _logViewerRoot = new VisualElement { name = "log-viewer-root" };
                _toggleBtn = new Button { name = "toggle-btn", text = "▼" };
                _logViewerRoot.Add(_toggleBtn);
                Add(_logViewerRoot);
                Debug.LogWarning("[McpLogViewer] Could not find UXML. Using fallback UI.");
            }

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/MCPForUnity/Editor/Windows/Components/Common.uss");
            
            if (styleSheet == null)
            {
                 var guids = AssetDatabase.FindAssets("Common t:StyleSheet");
                 if (guids.Length > 0)
                 {
                     styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
                 }
            }

            if (styleSheet != null)
            {
                this.styleSheets.Add(styleSheet);
            }

            // Bind Elements
            _logViewerRoot = this.Q<VisualElement>("log-viewer-root");
            _toggleBtn = this.Q<Button>("toggle-btn");
            _clearBtn = this.Q<Button>("clear-btn");
            _copyBtn = this.Q<Button>("copy-btn");
            _scrollView = this.Q<ScrollView>("log-scroll-view");
            var header = this.Q<VisualElement>("log-header");

            // Event Listeners
            _toggleBtn?.RegisterCallback<ClickEvent>(OnToggleClicked);
            header?.RegisterCallback<ClickEvent>(OnToggleClicked); // Header click also toggles
            _clearBtn?.RegisterCallback<ClickEvent>(OnClearClicked);
            _copyBtn?.RegisterCallback<ClickEvent>(OnCopyClicked);

            // Initial State
            SetCollapsed(true);

            // Subscribe to Service
            if (MCPServiceLocator.Server != null)
            {
                MCPServiceLocator.Server.OnLogReceived += OnLogReceived;
                MCPServiceLocator.Server.OnErrorReceived += OnErrorReceived;
            }

            // Cleanup on detach
            RegisterCallback<DetachFromPanelEvent>(OnDetach);
        }

        private void OnDetach(DetachFromPanelEvent evt)
        {
             if (MCPServiceLocator.Server != null)
            {
                MCPServiceLocator.Server.OnLogReceived -= OnLogReceived;
                MCPServiceLocator.Server.OnErrorReceived -= OnErrorReceived;
            }
        }

        private void OnToggleClicked(ClickEvent evt)
        {
            SetCollapsed(!_isCollapsed);
        }

        public void SetWindowMode()
        {
            _isCollapsed = false;
            // Hide toggle button
            if (_toggleBtn != null) _toggleBtn.style.display = DisplayStyle.None;
            // Unregister toggle event references if needed or just let them be harmless
            
            // Adjust Root styles for full window
            if (_logViewerRoot != null)
            {
                 _logViewerRoot.style.height = StyleKeyword.Auto; // Reset height if set
                 _logViewerRoot.style.flexGrow = 1; 
                 _logViewerRoot.RemoveFromClassList("collapsed");
            }
            
            // Adjust ScrollView to grow
            if (_scrollView != null)
            {
                _scrollView.style.height = StyleKeyword.Auto; // Override fixed 200px from USS
                _scrollView.style.flexGrow = 1;
            }

            // Disable collapsing
            _isCollapsed = false;
        }

        private void SetCollapsed(bool collapsed)
        {
            // If in window mode (implicit by toggle button missing?), prevent collapse?
            // But for now strict logic:
            _isCollapsed = collapsed;
            if (_toggleBtn != null) _toggleBtn.text = _isCollapsed ? "▼" : "▲";
            
            if (_logViewerRoot != null)
            {
                if (_isCollapsed)
                {
                    _logViewerRoot.AddToClassList("collapsed");
                }
                else
                {
                    _logViewerRoot.RemoveFromClassList("collapsed");
                }
            }
        }

        private void OnClearClicked(ClickEvent evt)
        {
            _scrollView?.Clear();
            evt.StopPropagation(); // Prevent toggling
        }

        private void OnCopyClicked(ClickEvent evt)
        {
            evt.StopPropagation();
            if (_scrollView == null) return;
            
            var sb = new System.Text.StringBuilder();
            foreach (var child in _scrollView.Children())
            {
                if (child is Label l) sb.AppendLine(l.text);
            }
            
            EditorGUIUtility.systemCopyBuffer = sb.ToString();
            Debug.Log("[McpLogViewer] All logs copied to clipboard.");
        }

        private void OnLogReceived(string message)
        {
            // Ensure UI update is on main thread (Service usually dispatches on main thread but safety first)
            // VisualElement scheduling API is preferred over EditorApplication.delayCall for UI
            this.schedule.Execute(() => AppendLog(message, false));
        }

        private void OnErrorReceived(string message)
        {
            this.schedule.Execute(() => AppendLog(message, true));
        }

        private void AppendLog(string message, bool isError)
        {
            if (_scrollView == null) return;

            var label = new Label(message);
            label.AddToClassList("log-entry");
            if (isError)
            {
                label.AddToClassList("error");
                // If error occurs, maybe auto-expand or show indicator?
                // For now, let's keep it simple.
            }

            _scrollView.Add(label);
            
            // Auto-scroll
            _scrollView.ScrollTo(label);
        }
    }
}
