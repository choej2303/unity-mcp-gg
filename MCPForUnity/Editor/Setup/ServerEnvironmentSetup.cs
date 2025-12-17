using System;
using System.IO;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Constants;
using System.Threading.Tasks;

using System.Runtime.InteropServices;

namespace MCPForUnity.Editor.Setup
{
    /// <summary>
    /// Automates the setup of the Python server environment (venv, dependencies)
    /// </summary>
    public static class ServerEnvironmentSetup
    {
        public static string ServerRoot => Path.Combine(AssetPathUtility.GetPackageAbsolutePath(), "Server~");
        public static string VenvPath => Path.Combine(ServerRoot, ".venv");
        public static string RequirementsPath => Path.Combine(ServerRoot, "Server", "requirements.txt");
        
        private const string UV_VERSION = "0.5.11";

        public static bool IsEnvironmentReady(string packageRootPath = null)
        {
            string root = packageRootPath ?? AssetPathUtility.GetPackageAbsolutePath();
            if (string.IsNullOrEmpty(root)) 
            {
                McpLog.Warn("[MCP Setup] Package root is null or empty.");
                return false;
            }
            
            string serverRoot = Path.Combine(root, "Server~");
            string venvPath = Path.Combine(serverRoot, ".venv");
            
            string venvPython = Path.Combine(venvPath, "Scripts", "python.exe");
            if (!File.Exists(venvPython))
            {
                venvPython = Path.Combine(venvPath, "bin", "python");
            }
            
            bool exists = File.Exists(venvPython);
            return exists;
        }

        public static void InstallServerEnvironment()
        {
            // 0. Pre-check prerequisites (Python & Node)
            // Soft check for Python - if missing, we will attempt to bootstrap via uv
            bool hasPython = CheckPython(silent: true);
            bool hasNode = CheckNode();

            // Only block on Node.js
            if (!hasNode)
            {
                EditorUtility.DisplayDialog("Setup Requirement", "Node.js is required but not found. Please install Node.js to proceed.", "OK");
                SetupWindowService.ShowSetupWindow();
                return;
            }

            try
            {
                // 1. Check/Install uv (Bootstrap)
                EditorUtility.DisplayProgressBar("MCP Setup", "Checking 'uv' package manager...", 0.2f);
                string uvPath = GetOrInstallUv();
                
                if (string.IsNullOrEmpty(uvPath))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", "Failed to setup 'uv'. Please verify internet connection.", "OK");
                    return;
                }

                // 2. Ensure Python 3.11+ is available (uv managed)
                // If system python is missing/old, uv will fetch a managed one
                EditorUtility.DisplayProgressBar("MCP Setup", "Verifying Python environment...", 0.4f);
                if (!EnsurePythonWithUv(uvPath))
                {
                     EditorUtility.ClearProgressBar();
                     EditorUtility.DisplayDialog("Error", "Failed to install Python 3.11 via uv.", "OK");
                     return;
                }

                // 3. Create venv (Skipped if uv sync handles it, but explicit check is safe)
                // uv sync --python 3.11 will handle venv creation

                // 4. Install Dependencies
                EditorUtility.DisplayProgressBar("MCP Setup", "Syncing dependencies...", 0.6f);
                if (!InstallDependencies(uvPath))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", "Failed to verify dependencies.", "OK");
                    return;
                }

                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Success", "MCP Server environment setup complete!\n\nYou can now connect using Cursor/Claude.", "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                McpLog.Error($"[MCP Setup] Error: {ex}");
                EditorUtility.DisplayDialog("Setup Failed", 
                    $"Setup failed: {ex.Message}\n\nRunning processes might be locking files, or PATH environment variables might need a refresh.\n\nTry restarting Unity (or your computer) and run Setup again.", "OK");
            }
        }
        
        private static bool CheckPython(bool silent = false)
        {
            string pythonCmd = GetPythonCommand();
            if (RunCommand(pythonCmd, "--version", out string output))
            {
                output = output.Trim();
                if (output.StartsWith("Python "))
                {
                    string versionStr = output.Substring(7);
                    string[] parts = versionStr.Split('.');
                    if (parts.Length >= 2 && 
                        int.TryParse(parts[0], out int major) && 
                        int.TryParse(parts[1], out int minor))
                    {
                        if (major > 3 || (major == 3 && minor >= 11)) return true;
                    }
                }
            }
            if (!silent) McpLog.Warn($"[MCP Setup] System Python check failed. Will attempt to use uv-managed Python.");
            return false;
        }

        private static bool CheckNode()
        {
            return RunCommand(GetNodeCommand(), "--version", out _);
        }

        public static string InstallUvExplicitly()
        {
            return GetOrInstallUv();
        }

        private static string GetOrInstallUv()
        {
            // 1. Check local bootstrap first
            string packageRoot = AssetPathUtility.GetPackageAbsolutePath();
            string serverRoot = Path.Combine(packageRoot, "Server~");
            string uvDir = Path.Combine(serverRoot, ".uv");  
            string uvName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "uv.exe" : "uv";
            string localUvPath = Path.Combine(uvDir, uvName);
            
            if (File.Exists(localUvPath)) return localUvPath;

            // 2. Check override or system PATH as fallback
            if (RunCommand("uv", "--version", out _)) return "uv";

            // 3. Bootstrap (Download)
            if (BootstrapUv(uvDir))
            {
                 // Find the binary in the extracted folder and move it to root of .uv
                 // Re-check
                 if (File.Exists(localUvPath)) return localUvPath;
                 
                 // Fallback search in subdirectories
                 var found = Directory.GetFiles(uvDir, uvName, SearchOption.AllDirectories);
                 if (found.Length > 0)
                 {
                     string detected = found[0];
                     try 
                     {
                         // Move to root for simplicity
                         if (File.Exists(localUvPath)) File.Delete(localUvPath);
                         File.Move(detected, localUvPath);
                         
                         // Chmod +x on unix
                         if (Application.platform != RuntimePlatform.WindowsEditor)
                         {
                             RunCommand("chmod", $"+x \"{localUvPath}\"", out _);
                         }
                         
                         return localUvPath;
                     } 
                     catch(Exception ex) 
                     {
                         McpLog.Warn($"[MCP Setup] Failed to move uv binary: {ex.Message}. Using found path.");
                         return detected;
                     }
                 }
            }

            return null;
        }
        
        private static bool BootstrapUv(string uvDir)
        {
            try
            {
                if (Directory.Exists(uvDir)) Directory.Delete(uvDir, true);
                Directory.CreateDirectory(uvDir);
                
                string url = "";
                string archiveName = "";
                
                if (Application.platform == RuntimePlatform.WindowsEditor) {
                    url = $"https://github.com/astral-sh/uv/releases/download/{UV_VERSION}/uv-x86_64-pc-windows-msvc.zip";
                    archiveName = "uv.zip";
                }
                else if (Application.platform == RuntimePlatform.OSXEditor) {
                    bool isArm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64; 
                    string arch = isArm ? "aarch64" : "x86_64";
                    url = $"https://github.com/astral-sh/uv/releases/download/{UV_VERSION}/uv-{arch}-apple-darwin.tar.gz";
                    archiveName = "uv.tar.gz";
                }
                else { // Linux
                    url = $"https://github.com/astral-sh/uv/releases/download/{UV_VERSION}/uv-x86_64-unknown-linux-gnu.tar.gz";
                    archiveName = "uv.tar.gz";
                }

                string archivePath = Path.Combine(uvDir, archiveName);
                McpLog.Info($"[MCP Setup] Downloading uv from: {url}");
                
                bool downloadSuccess = false;
                
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                     // PowerShell for HTTPS download
                     string psScript = $"Invoke-WebRequest -Uri '{url}' -OutFile '{archivePath}'; Expand-Archive -Path '{archivePath}' -DestinationPath '{uvDir}' -Force";
                     downloadSuccess = RunCommand("powershell", $"-Command \"{psScript}\"", out string err);
                     if (!downloadSuccess) McpLog.Error($"[MCP Setup] Download failed: {err}");
                }
                else
                {
                    // Curl + Tar
                    string cmd = $"curl -L '{url}' -o '{archivePath}' && tar -xzf '{archivePath}' -C '{uvDir}'";
                    downloadSuccess = RunCommand("/bin/bash", $"-c \"{cmd}\"", out string err);
                    if (!downloadSuccess) McpLog.Error($"[MCP Setup] Download failed: {err}");
                }

                return downloadSuccess;
            }
            catch (Exception ex)
            {
                McpLog.Error($"[MCP Setup] Bootstrap exception: {ex.Message}");
                return false;
            }
        }
        
        private static bool EnsurePythonWithUv(string uvPath)
        {
            string workingDir = Path.GetFullPath(ServerRoot);
            // Ensure we have a python version installed via uv
            // "uv python install 3.11"
            McpLog.Info("[MCP Setup] Ensuring Python 3.11 is installed via uv...");
            bool success = RunCommand(uvPath, "python install 3.11", out string output, workingDirectory: workingDir);
            
            // "uv venv" to create environment with that python
            // If .venv exists, uv respects it or recreates it if needed
            // RunCommand(uvPath, "venv", out _, workingDirectory: workingDir); 
            // Actually 'uv sync' handles venv creation usually, but 'uv venv' is safer to ensure it exists
            
            return success; 
        }

        /// <summary>
        /// Creates a Python virtual environment manually. No-op if UV is available (UV handles venv creation).
        /// </summary>
        private static bool CreateVenv()
        {
            // Legacy method, mostly unused now as uv handles this
            if (Directory.Exists(VenvPath)) return true;
            McpLog.Info($"[MCP Setup] Creating virtual environment at: {VenvPath}");
            return RunCommand(GetPythonCommand(), $"-m venv \"{VenvPath}\"", out string output, workingDirectory: ServerRoot);
        }

        private static bool InstallDependencies(string uvPath)
        {
            string workingDir = Path.GetFullPath(ServerRoot);

            if (!string.IsNullOrEmpty(uvPath))
            {
                McpLog.Info("[MCP Setup] Using 'uv sync' to install dependencies...");
                // uv sync will create .venv if needed and install everything defined in pyproject.toml
                // Explicitly ask for python 3.11 to be used for this environment
                return RunCommand(uvPath, "sync --python 3.11", out string output, workingDirectory: workingDir);
            }
            else
            {
                // Fallback for standard pip
                string venvPython = Path.Combine(VenvPath, "Scripts", "python.exe");
                if (!File.Exists(venvPython)) venvPython = Path.Combine(VenvPath, "bin", "python");
                venvPython = Path.GetFullPath(venvPython);

                if (!File.Exists(venvPython))
                {
                    McpLog.Error($"[MCP Setup] Virtual environment python not found at: {venvPython}");
                    return false;
                }

                McpLog.Info("[MCP Setup] Using standard pip to install dependencies...");
                return RunCommand(venvPython, "-m pip install -e .", out string output, workingDirectory: workingDir);
            }
        }

        private static bool RunCommand(string fileName, string arguments, out string output, string workingDirectory = null)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                
                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    psi.WorkingDirectory = workingDirectory;
                }

                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        McpLog.Error($"[MCP Setup] Failed to start process: {fileName}");
                        return false;
                    }

                    // Read streams asynchronously
                    // Note: ReadToEndAsync is not available in .NET Standard 2.0 (Unity's default profile), using wrapper or async delegate
                    var outputTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                    var errorTask = Task.Run(() => p.StandardError.ReadToEnd());
                    
                    // 5 minute timeout for downloads/installs
                    if (!p.WaitForExit(300000)) 
                    {
                        try { p.Kill(); } catch {}
                        McpLog.Error($"[MCP Setup] Command timed out: {fileName} {arguments}");
                        return false;
                    }

                    output = outputTask.Result;
                    string error = errorTask.Result;

                    if (p.ExitCode != 0)
                    {
                        McpLog.Error($"[MCP Setup] Command failed: {fileName} {arguments}\nOutput: {output}\nError: {error}");
                        return false;
                    }
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // File not found
                return false;
            }
            catch (Exception ex)
            {
                McpLog.Error($"[MCP Setup] Exception running command '{fileName} {arguments}': {ex.Message}");
                return false;
            }
        }

        private static string GetPythonCommand()
        {
            string overridePath = EditorPrefs.GetString(EditorPrefKeys.PythonPathOverride, "");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            {
                return overridePath;
            }
            return "python";
        }

        private static string GetNodeCommand()
        {
            string overridePath = EditorPrefs.GetString(EditorPrefKeys.NodePathOverride, "");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            {
                return overridePath;
            }
            return "node";
        }
    }
}

