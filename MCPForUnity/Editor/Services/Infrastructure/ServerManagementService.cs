using System;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Service for managing MCP server lifecycle
    /// </summary>
    public class ServerManagementService : IServerManagementService
    {
        /// <summary>
        /// Event fired when the server process outputs to stdout
        /// </summary>
        public event Action<string> OnLogReceived;

        /// <summary>
        /// Event fired when the server process outputs to stderr
        /// </summary>
        public event Action<string> OnErrorReceived;

        private static bool _cleanupRegistered = false;
        private ProcessJobObject _jobObject;
        private bool _disposed;
        
        // Log Batching
        private readonly ConcurrentQueue<(string Message, bool IsError)> _logQueue = new();
        private bool _isLogFlushScheduled = false;
        private const int MAX_LOGS_PER_BATCH = 50;

        private void EnqueueLog(string message, bool isError)
        {
            _logQueue.Enqueue((message, isError));
            
            // If already scheduled, efficient exit
            if (_isLogFlushScheduled) return;

            _isLogFlushScheduled = true;
            
            // Schedule processing on main thread
            // We throttle implicitly because delayCall executes once per frame cycle usually
            EditorApplication.delayCall += FlushLogs;
        }

        private void FlushLogs()
        {
            _isLogFlushScheduled = false;
            
            if (_logQueue.IsEmpty) return;

            int count = 0;
            while (_logQueue.TryDequeue(out var logItem) && count < MAX_LOGS_PER_BATCH)
            {
                count++;
                ProcessSingleLog(logItem.Message, logItem.IsError);
            }
            
            // If we still have logs, schedule another flush immediately
            if (!_logQueue.IsEmpty)
            {
                _isLogFlushScheduled = true;
                EditorApplication.delayCall += FlushLogs;
            }
        }

        private void ProcessSingleLog(string message, bool isError)
        {
            if (!isError)
            {
                McpLog.Debug($"[Server] {message}");
                OnLogReceived?.Invoke(message);
            }
            else
            {
                // Heuristic: Many CLI tools (including Python logging) write INFO/status to stderr.
                if (message.Contains("INFO") || message.Contains("Started server") || message.Contains("Uvicorn running"))
                {
                    McpLog.Info($"[Server] {message}");
                    OnErrorReceived?.Invoke(message); 
                }
                else
                {
                    McpLog.Error($"[Server Error] {message}");
                    OnErrorReceived?.Invoke(message);
                }
            }
        }
        
        /// <summary>
        /// Register cleanup handler for Unity exit
        /// </summary>
        private static void EnsureCleanupRegistered()
        {
            if (_cleanupRegistered) return;
            
            EditorApplication.quitting += () =>
            {
                // Try to stop the HTTP server when Unity exits
                try
                {
                    var service = new ServerManagementService();
                    service.StopLocalHttpServer();
                }
                catch (Exception ex)
                {
                    McpLog.Debug($"Cleanup on exit: {ex.Message}");
                }
            };
            
            _cleanupRegistered = true;
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _jobObject?.Dispose();
                _jobObject = null;
            }

            _disposed = true;
        }

        /// <summary>
        /// Clear the local uvx cache for the MCP server package
        /// </summary>
        /// <returns>True if successful, false otherwise</returns>
        public bool ClearUvxCache()
        {
            try
            {
                string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
                string uvCommand = BuildUvPathFromUvx(uvxPath);

                // Get the package name
                string packageName = "mcp-for-unity";

                // Run uvx cache clean command
                string args = $"cache clean {packageName}";

                bool success;
                string stdout;
                string stderr;

                success = ExecuteUvCommand(uvCommand, args, out stdout, out stderr);

                if (success)
                {
                    McpLog.Debug($"uv cache cleared successfully: {stdout}");
                    return true;
                }
                string combinedOutput = string.Join(
                    Environment.NewLine,
                    new[] { stderr, stdout }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

                string lockHint = (!string.IsNullOrEmpty(combinedOutput) &&
                                   combinedOutput.IndexOf("currently in-use", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "Another uv process may be holding the cache lock; wait a moment and try again or clear with '--force' from a terminal."
                    : string.Empty;

                if (string.IsNullOrEmpty(combinedOutput))
                {
                    combinedOutput = "Command failed with no output. Ensure uv is installed, on PATH, or set an override in Advanced Settings.";
                }

                McpLog.Warn(
                    $"Failed to clear uv cache using '{uvCommand} {args}'. " +
                    $"Details: {combinedOutput}{(string.IsNullOrEmpty(lockHint) ? string.Empty : " Hint: " + lockHint)}");
                
                // Cache clearing failure is not critical, so we can return true to proceed
                return true; 
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Error clearing uv cache: {ex.Message}");
                return true; // Proceed anyway
            }
        }

        private bool ExecuteUvCommand(string uvCommand, string args, out string stdout, out string stderr)
        {
            stdout = null;
            stderr = null;

            string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
            string uvPath = BuildUvPathFromUvx(uvxPath);

            string extraPathPrepend = GetPlatformSpecificPathPrepend();

            if (!string.Equals(uvCommand, uvPath, StringComparison.OrdinalIgnoreCase))
            {
                // Timeout reduced to 2 seconds to prevent UI freezing
                return ExecPath.TryRun(uvCommand, args, Application.dataPath, out stdout, out stderr, 2000, extraPathPrepend);
            }

            string command = $"{uvPath} {args}";

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                return ExecPath.TryRun("cmd.exe", $"/c {command}", Application.dataPath, out stdout, out stderr, 2000, extraPathPrepend);
            }

            string shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

            if (!string.IsNullOrEmpty(shell) && File.Exists(shell))
            {
                string escaped = command.Replace("\"", "\\\"");
                return ExecPath.TryRun(shell, $"-lc \"{escaped}\"", Application.dataPath, out stdout, out stderr, 2000, extraPathPrepend);
            }

            return ExecPath.TryRun(uvPath, args, Application.dataPath, out stdout, out stderr, 2000, extraPathPrepend);
        }

        private static string BuildUvPathFromUvx(string uvxPath)
        {
            if (string.IsNullOrWhiteSpace(uvxPath))
            {
                return uvxPath;
            }

            string directory = Path.GetDirectoryName(uvxPath);
            string extension = Path.GetExtension(uvxPath);
            string uvFileName = "uv" + extension;

            return string.IsNullOrEmpty(directory)
                ? uvFileName
                : Path.Combine(directory, uvFileName);
        }

        private string GetPlatformSpecificPathPrepend()
        {
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                return string.Join(Path.PathSeparator.ToString(), new[]
                {
                    "/opt/homebrew/bin",
                    "/usr/local/bin",
                    "/usr/bin",
                    "/bin"
                });
            }

            if (Application.platform == RuntimePlatform.LinuxEditor)
            {
                return string.Join(Path.PathSeparator.ToString(), new[]
                {
                    "/usr/local/bin",
                    "/usr/bin",
                    "/bin"
                });
            }

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                return string.Join(Path.PathSeparator.ToString(), new[]
                {
                    !string.IsNullOrEmpty(localAppData) ? Path.Combine(localAppData, "Programs", "uv") : null,
                    !string.IsNullOrEmpty(programFiles) ? Path.Combine(programFiles, "uv") : null
                }.Where(p => !string.IsNullOrEmpty(p)).ToArray());
            }

            return null;
        }

        /// <summary>
        /// Attempts to get the command that will be executed when starting the local server (HTTP or Stdio)
        /// </summary>
        public bool TryGetLocalHttpServerCommand(out string command, out string error)
        {
            // Check transport preference
            bool useHttp = EditorPrefs.GetBool(MCPForUnity.Editor.Constants.EditorPrefKeys.UseHttpTransport, true);
            
            var info = McpServerCommandLoader.GenerateCommand(useHttp);
            
            if (!string.IsNullOrEmpty(info.Error))
            {
                command = null;
                error = info.Error;
                return false;
            }

            command = info.FullCommand;
            error = null;
            return true;
        }

        /// <summary>
        /// Check if the configured HTTP URL is a local address
        /// </summary>
        public bool IsLocalUrl()
        {
            return IsLocalUrl(HttpEndpointUtility.GetBaseUrl());
        }

        private bool IsLocalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                var uri = new Uri(url);
                return uri.Host == "localhost" || uri.Host == "127.0.0.1";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if the local HTTP server can be started
        /// </summary>
        public bool CanStartLocalServer()
        {
            // Must have HTTP transport enabled
            bool useHttp = EditorPrefs.GetBool(MCPForUnity.Editor.Constants.EditorPrefKeys.UseHttpTransport, true);
            if (!useHttp) return false;

            // Must be configured for local URL
            return IsLocalUrl();
        }

        /// <summary>
        /// Start the local HTTP server in a new terminal window.
        /// Stops any existing server on the port and clears the uvx cache first.
        /// </summary>
        public bool StartLocalHttpServer()
        {
            if (!TryGetLocalHttpServerCommand(out var command, out var error))
            {
                EditorUtility.DisplayDialog(
                    "Cannot Start HTTP Server",
                    error ?? "The server command could not be constructed with the current settings.",
                    "OK");
                return false;
            }

            // First, try to stop any existing server
            StopLocalHttpServer();

            // Clear the cache to ensure we get a fresh version, but only if using remote package (uvx)
            // Local source execution (detected by 'src.main') handles its own dependencies via uv sync/run
            if (!command.Contains("src.main"))
            {
                try
                {
                    ClearUvxCache();
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Failed to clear cache before starting server: {ex.Message}");
                }
            }

            if (EditorUtility.DisplayDialog(
                "Start Local HTTP Server",
                $"This will start the MCP server in HTTP mode:\n\n{command}\n\n" +
                "The server will run in a separate terminal window. " +
                "Use 'Stop Server' button or close Unity to stop the server.\n\n" +
                "Continue?",
                "Start Server",
                "Cancel"))
            {
                try
                {
                    // Register cleanup handler for Unity exit
                    EnsureCleanupRegistered();

                    System.Diagnostics.ProcessStartInfo startInfo;

                    if (Application.platform == RuntimePlatform.WindowsEditor)
                    {
                        // Windows: Execute directly via cmd.exe /c to allow Job Object attachment
                        // We use cmd /c because 'command' might be a complex string (uv run ...)
                        startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c \"{command}\"", // Execute the command directly
                            UseShellExecute = false,
                            CreateNoWindow = true, // Hide window (Job Object manages it)
                            RedirectStandardOutput = true, // Capture output for future Log Viewer
                            RedirectStandardError = true
                        };

                        // Set PYTHONUNBUFFERED=1 environment variable
                        startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                    }
                    else
                    {
                        // Start the server in a new terminal window (cross-platform for Mac/Linux)
                         startInfo = ExecPath.CreateTerminalProcessStartInfo(command);
                    }

                    var process = System.Diagnostics.Process.Start(startInfo);

                    if (process != null && Application.platform == RuntimePlatform.WindowsEditor)
                    {
                        // Attach to Job Object for guaranteed termination
                        _jobObject?.Dispose();
                        _jobObject = new ProcessJobObject();
                        _jobObject.AssignProcess(process);

                        // Capture logs (since window is hidden)
                        process.OutputDataReceived += (s, e) => 
                        { 
                            if (!string.IsNullOrEmpty(e.Data)) 
                            {
                                EnqueueLog(e.Data, false);
                            }
                        };
                        process.ErrorDataReceived += (s, e) => 
                        { 
                            if (!string.IsNullOrEmpty(e.Data)) 
                            {
                                EnqueueLog(e.Data, true);
                            }
                        };
                        
                        try 
                        {
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                        }
                        catch (Exception startEx)
                        {
                             McpLog.Warn($"Failed to start log redirection: {startEx.Message}");
                        }

                        McpLog.Info($"Started local HTTP server (PID: {process.Id}) attached to Job Object.");
                    }
                    else
                    {
                        McpLog.Info($"Started local HTTP server: {command}");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    McpLog.Error($"Failed to start server: {ex.Message}");
                    EditorUtility.DisplayDialog(
                        "Error",
                        $"Failed to start server: {ex.Message}",
                        "OK");
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Stop the local HTTP server by finding the process listening on the configured port
        /// </summary>
        public bool StopLocalHttpServer()
        {
            string httpUrl = HttpEndpointUtility.GetBaseUrl();
            if (!IsLocalUrl(httpUrl))
            {
                McpLog.Warn("Cannot stop server: URL is not local.");
                return false;
            }

            try
            {
                var uri = new Uri(httpUrl);
                int port = uri.Port;

                if (port <= 0)
                {
                    McpLog.Warn("Cannot stop server: Invalid port.");
                    return false;
                }

                McpLog.Info($"Attempting to stop any process listening on local port {port}. This will terminate the owning process even if it is not the MCP server.");

                int pid = NetworkHelper.GetProcessIdForPort(port);
                if (pid > 0)
                {
                    NetworkHelper.KillProcess(pid);
                    McpLog.Info($"Stopped local HTTP server on port {port} (PID: {pid})");
                    return true;
                }
                else
                {
                    McpLog.Info($"No process found listening on port {port}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to stop server: {ex.Message}");
                return false;
            }
        }
    }
}
