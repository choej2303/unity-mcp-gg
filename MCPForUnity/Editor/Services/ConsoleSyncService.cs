using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Listens to Unity Console logs and forwards them to the MCP Server
    /// using standard 'logging/message' notifications.
    /// This allows external MCP Clients (like IDEs) to see Unity logs in real-time.
    /// </summary>
    public class ConsoleSyncService : IDisposable
    {
        private readonly TransportManager _transportManager;
        private bool _isListening;
        
        // Configuration
        private bool _syncErrors = true;
        private bool _syncWarnings = true;
        private bool _syncLogs = false; // Too verbose by default

        public ConsoleSyncService(TransportManager transportManager)
        {
            _transportManager = transportManager;
            StartListening();
        }

        public void StartListening()
        {
            if (_isListening) return;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            _isListening = true;
            McpLog.Info("[ConsoleSync] Started syncing Unity logs to MCP.");
        }

        public void StopListening()
        {
            if (!_isListening) return;
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            _isListening = false;
        }

        public void Dispose()
        {
            StopListening();
        }

        private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            // Filter out internal sync logs to avoid loops
            if (condition.StartsWith("[ConsoleSync]") || condition.StartsWith("[MCP]"))
            {
                 // Allow errors to be visible if critical, but for now suppress to avoid recursion risk
                 // if logging itself causes an MCP error which logs...
                 if (condition.StartsWith("[ConsoleSync]")) return;
            }

            string mcpLevel = null;
            bool shouldSync = false;

            switch (type)
            {
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    shouldSync = _syncErrors;
                    mcpLevel = type == LogType.Exception ? "critical" : "error";
                    break;
                case LogType.Warning:
                    shouldSync = _syncWarnings;
                    mcpLevel = "warning";
                    break;
                case LogType.Log:
                    shouldSync = _syncLogs;
                    mcpLevel = "info";
                    break;
            }

            if (!shouldSync || mcpLevel == null) return;

            // Attempt to parse file and line from stack trace or condition
            // Unity stack traces often look like:
            // "at Class.Method () [0x00000] in C:\Path\To\File.cs:123"
            // Or just appended to condition.
            
            string file = null;
            int line = 0;
            
            // Simple parsing
            if (!string.IsNullOrEmpty(stackTrace))
            {
                var match = Regex.Match(stackTrace, @"in (.*?):(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    file = match.Groups[1].Value;
                    int.TryParse(match.Groups[2].Value, out line);
                }
            }

            // Construct payload
            // logging/message params: level, data (string or object), logger (string)
            var dataObj = new JObject
            {
                ["message"] = condition,
                ["stackTrace"] = stackTrace
            };
            
            if (!string.IsNullOrEmpty(file))
            {
                dataObj["file"] = file;
                dataObj["line"] = line;
            }

            var parameters = new JObject
            {
                ["level"] = mcpLevel,
                ["data"] = dataObj,
                ["logger"] = "unity"
            };

            // Dispatch
            // We use Task.Run to offload to thread pool and avoid blocking Unity log callback
            // But we must catch exceptions
            Task.Run(async () =>
            {
                try
                {
                    // Prefer HTTP transport (SSE)
                    if (_transportManager.IsRunning(TransportMode.Http))
                    {
                        await _transportManager.SendNotificationAsync(TransportMode.Http, "logging/message", parameters);
                    }
                    // Could add Stdio fallback later
                }
                catch (Exception)
                {
                    // Swallow to prevent log loops
                }
            });
        }
    }
}
