using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Windows.Components.Settings;
using MCPForUnity.Editor.Windows.Components.Connection;
using MCPForUnity.Editor.Windows.Components.ClientConfig;
using MCPForUnity.Editor.Windows.Components.Setup;
using MCPForUnity.Editor.Dependencies;
using MCPForUnity.Editor.Constants;

namespace MCPForUnity.Editor.Windows
{
    public class MCPForUnityEditorWindow : EditorWindow
    {
        // Section controllers
        private McpSetupSection setupSection;
        private McpSettingsSection settingsSection;
        private McpConnectionSection connectionSection;
        private McpClientConfigSection clientConfigSection;
        private MCPForUnity.Editor.Windows.Components.Tools.McpToolsSection toolsSection;

        // UI Elements
        private VisualElement statusHeader;
        private VisualElement statusIndicator;
        private Label statusText;
        private Label setupHint;
        private Button toggleSetupBtn;
        private Button refreshStatusBtn;
        private VisualElement setupContainer;
        private VisualElement mainWorkspace;

        // Tab Elements
        private UnityEditor.UIElements.ToolbarToggle settingsTabToggle;
        private UnityEditor.UIElements.ToolbarToggle toolsTabToggle;
        private VisualElement settingsPanel;
        private VisualElement toolsPanel;

        private enum ActivePanel
        {
            Settings,
            Tools
        }

        private static readonly HashSet<MCPForUnityEditorWindow> OpenWindows = new();
        private bool isSetupOpen = false;
        private double _lastFocusRefreshTime = 0;
        private bool _toolsLoaded = false;

        public static void ShowWindow()
        {
            var window = GetWindow<MCPForUnityEditorWindow>("MCP For Unity");
            window.minSize = new Vector2(500, 700);
        }

        public void CreateGUI()
        {
            string basePath = AssetPathUtility.GetMcpPackageRootPath();

            // Load main window UXML
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                $"{basePath}/Editor/Windows/MCPForUnityEditorWindow.uxml"
            );

            if (visualTree == null)
            {
                McpLog.Error($"Failed to load UXML at: {basePath}/Editor/Windows/MCPForUnityEditorWindow.uxml");
                return;
            }

            visualTree.CloneTree(rootVisualElement);

            // Load main window USS
            var mainStyleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                $"{basePath}/Editor/Windows/MCPForUnityEditorWindow.uss"
            );
            if (mainStyleSheet != null)
            {
                rootVisualElement.styleSheets.Add(mainStyleSheet);
            }

            // Load common USS
            var commonStyleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                $"{basePath}/Editor/Windows/Components/Common.uss"
            );
            if (commonStyleSheet != null)
            {
                rootVisualElement.styleSheets.Add(commonStyleSheet);
            }

            // --- Query Main Elements ---
            statusHeader = rootVisualElement.Q<VisualElement>("status-header");
            statusIndicator = rootVisualElement.Q<VisualElement>("status-indicator");
            statusText = rootVisualElement.Q<Label>("status-text");
            setupHint = rootVisualElement.Q<Label>("setup-hint");
            toggleSetupBtn = rootVisualElement.Q<Button>("toggle-setup-btn");
            var openLogsBtn = rootVisualElement.Q<Button>("open-logs-btn");
            refreshStatusBtn = rootVisualElement.Q<Button>("refresh-status-btn");
            setupContainer = rootVisualElement.Q<VisualElement>("setup-container");
            mainWorkspace = rootVisualElement.Q<VisualElement>("main-workspace");

            settingsPanel = rootVisualElement.Q<VisualElement>("settings-panel");
            toolsPanel = rootVisualElement.Q<VisualElement>("tools-panel");

            // --- Initialize Tabs ---
            SetupTabs();

            // --- Initialize Sections ---

            // 1. Setup Section
            var setupPlaceholder = rootVisualElement.Q<VisualElement>("setup-section-placeholder");
            var setupTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                $"{basePath}/Editor/Windows/Components/Setup/McpSetupSection.uxml"
            );
            if (setupTree != null && setupPlaceholder != null)
            {
                var setupRoot = setupTree.Instantiate();
                setupPlaceholder.Add(setupRoot);
                setupSection = new McpSetupSection(setupRoot);
                setupSection.OnSetupComplete += CheckSystemStatus; // Re-check when setup changes
            }

            // 2. Settings Section
            var settingsPlaceholder = rootVisualElement.Q<VisualElement>("settings-section-placeholder");
            var settingsTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                $"{basePath}/Editor/Windows/Components/Settings/McpSettingsSection.uxml"
            );
            if (settingsTree != null && settingsPlaceholder != null)
            {
                var settingsRoot = settingsTree.Instantiate();
                settingsPlaceholder.Add(settingsRoot);
                settingsSection = new McpSettingsSection(settingsRoot);
            }

            // 3. Connection Section
            var connectionPlaceholder = rootVisualElement.Q<VisualElement>("connection-section-placeholder");
            var connectionTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                $"{basePath}/Editor/Windows/Components/Connection/McpConnectionSection.uxml"
            );
            if (connectionTree != null && connectionPlaceholder != null)
            {
                var connectionRoot = connectionTree.Instantiate();
                connectionPlaceholder.Add(connectionRoot);
                connectionSection = new McpConnectionSection(connectionRoot);
                connectionSection.OnManualConfigUpdateRequested += () => clientConfigSection?.RefreshSelectedClient();
            }

            // 4. Client Config Section
            var clientConfigPlaceholder = rootVisualElement.Q<VisualElement>("client-config-section-placeholder");
            var clientConfigTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                $"{basePath}/Editor/Windows/Components/ClientConfig/McpClientConfigSection.uxml"
            );
            if (clientConfigTree != null && clientConfigPlaceholder != null)
            {
                var clientConfigRoot = clientConfigTree.Instantiate();
                clientConfigPlaceholder.Add(clientConfigRoot);
                clientConfigSection = new McpClientConfigSection(clientConfigRoot);
            }

            // 5. Tools Section
            var toolsPlaceholder = rootVisualElement.Q<VisualElement>("tools-section-placeholder");
            var toolsTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                $"{basePath}/Editor/Windows/Components/Tools/McpToolsSection.uxml"
            );
            if (toolsTree != null && toolsPlaceholder != null)
            {
                var toolsRoot = toolsTree.Instantiate();
                toolsPlaceholder.Add(toolsRoot);
                toolsSection = new MCPForUnity.Editor.Windows.Components.Tools.McpToolsSection(toolsRoot);
                
                // Add separator or spacing if needed
                toolsRoot.style.marginBottom = 20;
            }

            // --- Bind Events ---
            if (toggleSetupBtn != null)
            {
                toggleSetupBtn.clicked += ToggleSetup;
            }
            if (refreshStatusBtn != null)
            {
                refreshStatusBtn.clicked += () => CheckSystemStatus();
            }
            if (openLogsBtn != null)
            {
                openLogsBtn.clicked += () => McpLogWindow.ShowWindow();
            }

            // Initial updates - Use delayCall to prevent freezing during window creation
            EditorApplication.delayCall += () => 
            {
                CheckSystemStatus(); // Single initial check after window loads
                RefreshAllData();    // Ensure client config and other data are also refreshed immediately
            };
        }

        private void SetupTabs()
        {
            settingsTabToggle = rootVisualElement.Q<UnityEditor.UIElements.ToolbarToggle>("settings-tab");
            toolsTabToggle = rootVisualElement.Q<UnityEditor.UIElements.ToolbarToggle>("tools-tab");

            if (settingsTabToggle != null)
            {
                settingsTabToggle.RegisterValueChangedCallback(evt =>
                {
                    if (!evt.newValue)
                    {
                        // Prevent unchecking all tabs
                        if (toolsTabToggle != null && !toolsTabToggle.value)
                        {
                            settingsTabToggle.SetValueWithoutNotify(true);
                        }
                        return;
                    }

                    SwitchPanel(ActivePanel.Settings);
                });
            }

            if (toolsTabToggle != null)
            {
                toolsTabToggle.RegisterValueChangedCallback(evt =>
                {
                    if (!evt.newValue)
                    {
                         // Prevent unchecking all tabs
                        if (settingsTabToggle != null && !settingsTabToggle.value)
                        {
                            toolsTabToggle.SetValueWithoutNotify(true);
                        }
                        return;
                    }

                    SwitchPanel(ActivePanel.Tools);
                });
            }

            // Restore last active panel
            var savedPanel = EditorPrefs.GetString(EditorPrefKeys.EditorWindowActivePanel, ActivePanel.Settings.ToString());
            if (!Enum.TryParse(savedPanel, out ActivePanel initialPanel))
            {
                initialPanel = ActivePanel.Settings;
            }

            SwitchPanel(initialPanel);
        }

        private void SwitchPanel(ActivePanel panel)
        {
            bool showSettings = panel == ActivePanel.Settings;

            if (settingsPanel != null)
            {
                settingsPanel.style.display = showSettings ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (toolsPanel != null)
            {
                toolsPanel.style.display = showSettings ? DisplayStyle.None : DisplayStyle.Flex;
                
                // Lazy load tools when Tools tab is first opened
                if (!showSettings && !_toolsLoaded)
                {
                    _toolsLoaded = true;
                    EditorApplication.delayCall += () => toolsSection?.Refresh();
                }
            }

            // Sync toggles
            settingsTabToggle?.SetValueWithoutNotify(showSettings);
            toolsTabToggle?.SetValueWithoutNotify(!showSettings);

            EditorPrefs.SetString(EditorPrefKeys.EditorWindowActivePanel, panel.ToString());
        }

        private void OnEnable()
        {
            // EditorApplication.update += OnEditorUpdate; // Removed for optimization: UI Toolkit scheduler handles updates
            OpenWindows.Add(this);
            
            // Ensure ConsoleSync is running
            // var _ = MCPServiceLocator.ConsoleSync; // Disabled per user request
            
             // Initial updates - Use delayCall to prevent freezing during window creation
            EditorApplication.delayCall += () => 
            {
                CheckSystemStatus(); // Single initial check after window loads
                RefreshAllData();    // Ensure client config and other data are also refreshed immediately
            };
        }

        private void OnDisable()
        {
            // EditorApplication.update -= OnEditorUpdate; // Removed for optimization
            OpenWindows.Remove(this);
            
            // Dispose of services that need cleanup
            // if (MCPServiceLocator.IsInitialized)
            // {
            //     // We don't necessarily want to kill the server when the window closes,
            //     // but we might want to stop listening to events
            // }
        }

        // Removed OnEditorUpdate() as it was redundantly calling UpdateConnectionStatus()
        // McpConnectionSection now handles its own scheduling via schedule.Execute().

        private void OnFocus()
        {
            // Only refresh lightweight data if UI is built
            if (rootVisualElement == null || rootVisualElement.childCount == 0)
                return;

            // Throttle RefreshAllData to prevent excessive calls
            // Only refresh if more than 2 seconds have passed since last refresh
            double currentTime = EditorApplication.timeSinceStartup;
            if (currentTime - _lastFocusRefreshTime > 2.0)
            {
                _lastFocusRefreshTime = currentTime;
                RefreshAllData();
            }
        }

        private void ToggleSetup()
        {
            isSetupOpen = !isSetupOpen;
            setupContainer.style.display = isSetupOpen ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private int _isChecking = 0;

        private void CheckSystemStatus()
        {
            if (this == null) return; 
            if (System.Threading.Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0) return;
            
            // Show checking state
            if (statusText != null) statusText.text = "Checking...";
            if (statusIndicator != null)
            {
                statusIndicator.RemoveFromClassList("ready");
                statusIndicator.RemoveFromClassList("error");
            }

            try
            {
                // Synchronous check (Main Thread)
                var dependencyResult = DependencyManager.CheckAllDependencies();
                
                // Safety check
                if (rootVisualElement == null) return;

                bool isReady = dependencyResult.IsSystemReady;

                if (statusIndicator != null)
                {
                    statusIndicator.RemoveFromClassList("ready");
                    statusIndicator.RemoveFromClassList("error");
                    statusIndicator.AddToClassList(isReady ? "ready" : "error");
                }

                if (statusText != null)
                {
                    statusText.RemoveFromClassList("ready");
                    statusText.RemoveFromClassList("error");

                    if (isReady)
                    {
                        statusText.text = "System Ready";
                        statusText.AddToClassList("ready");
                    }
                    else
                    {
                        statusText.text = "Setup Required";
                        statusText.AddToClassList("error");
                    }
                }

                if (setupHint != null)
                {
                    setupHint.text = isReady ? "Configure Settings" : "Click arrow to configure environment";
                }

                if (settingsPanel != null)
                {
                    if (isReady)
                    {
                        settingsPanel.RemoveFromClassList("disabled");
                        settingsPanel.SetEnabled(true);
                    }
                    else
                    {
                        settingsPanel.AddToClassList("disabled");
                        settingsPanel.SetEnabled(false);
                    }
                }

                toolsPanel?.RemoveFromClassList("disabled");
                toolsPanel?.SetEnabled(true);
                if (!isReady)
                {
                    toolsPanel?.AddToClassList("disabled");
                    toolsPanel?.SetEnabled(false);
                }

                // Update Setup Section UI with the result we already have
                setupSection?.RefreshStatus(dependencyResult);
            }
            catch (Exception ex)
            {
                McpLog.Error($"System check failed: {ex.Message}");
                if (statusText != null)
                {
                    statusText.text = "Error Checking";
                    statusText.RemoveFromClassList("ready");
                    statusText.AddToClassList("error");
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _isChecking, 0);
            }
        }

        private void RefreshAllData()
        {
            connectionSection?.UpdateConnectionStatus();

            if (MCPServiceLocator.Bridge.IsRunning)
            {
                connectionSection?.VerifyBridgeConnectionAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted) McpLog.Warn($"Bridge verification failed: {t.Exception?.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }

            clientConfigSection?.RefreshSelectedClient();
            // Removed toolsSection?.Refresh() - tools are now loaded lazily when tab is opened
        }

        internal static void RequestHealthVerification()
        {
            foreach (var window in OpenWindows)
            {
                window?.ScheduleHealthCheck();
            }
        }

        private void ScheduleHealthCheck()
        {
            EditorApplication.delayCall += () =>
            {
                var section = connectionSection;
                if (this == null || section == null)
                {
                    return;
                }
                section.VerifyBridgeConnectionAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted) McpLog.Warn($"Health check failed: {t.Exception?.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            };
        }

        public static bool HasAnyOpenWindow()
        {
            return OpenWindows.Count > 0;
        }

        public static void CloseAllOpenWindows()
        {
            // Create a copy to modify collection while iterating
            var windows = new List<MCPForUnityEditorWindow>(OpenWindows);
            foreach (var win in windows)
            {
                if (win != null) win.Close();
            }
        }
    }
}
