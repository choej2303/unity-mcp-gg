using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers; // For Response/McpForUnityTool attribute
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("build_project", Description = "Build the Unity project for a specified platform.")]
    public static class BuildTools
    {
        public static object HandleCommand(JObject @params)
        {
            string targetStr = @params["target"]?.ToString();
            string outputPath = @params["output_path"]?.ToString();
            bool development = @params["development"]?.ToObject<bool>() ?? false;
            var scenesArray = @params["scenes"] as JArray;

            if (string.IsNullOrEmpty(targetStr))
            {
                return new ErrorResponse("Missing required argument: 'target' (e.g. 'standalone_win64', 'android', 'ios', 'webgl').");
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                return new ErrorResponse("Missing required argument: 'output_path'.");
            }

            // Parse BuildTarget
            BuildTarget buildTarget;
            BuildTargetGroup buildGroup;
            
            if (!TryParseBuildTarget(targetStr, out buildTarget, out buildGroup))
            {
                return new ErrorResponse($"Unknown or unsupported build target: '{targetStr}'. Supported: standalone_win64, standalone_osx, android, ios, webgl.");
            }

            // Validate Output Path
            // Ensure directory exists
            try
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Invalid output_path: {ex.Message}");
            }

            // Get Scenes
            string[] scenePaths;
            if (scenesArray != null && scenesArray.Count > 0)
            {
                scenePaths = scenesArray.Select(s => s.ToString()).ToArray();
                // Validate scenes exist
                var missing = scenePaths.Where(s => !File.Exists(s) && !File.Exists(Path.Combine(Application.dataPath, "..", s))).ToList();
                if (missing.Any())
                {
                    return new ErrorResponse($"Some scenes were not found: {string.Join(", ", missing)}");
                }
            }
            else
            {
                // Use EditorBuildSettings
                scenePaths = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();
            }

            if (scenePaths.Length == 0)
            {
                return new ErrorResponse("No scenes to build. Add scenes to Build Settings or provide 'scenes' argument.");
            }

            // Setup Build Options
            BuildOptions options = BuildOptions.None;
            if (development) options |= BuildOptions.Development;

            var buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = scenePaths,
                locationPathName = outputPath,
                target = buildTarget,
                targetGroup = buildGroup, // Important for switching active build target if needed? Unity usually auto-switches or warns.
                options = options
            };

            // Perform Build
            // Note: BuildPipeline.BuildPlayer must be run on main thread? 
            // HandleCommand is usually invoked on main thread via MCP dispatcher? 
            // In typical MCP implementation here, yes.
            
            BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                return new SuccessResponse($"Build succeeded: {summary.totalSize} bytes. Time: {summary.totalTime}", new
                {
                    outputPath = summary.outputPath,
                    totalSize = summary.totalSize,
                    totalTime = summary.totalTime.ToString(),
                    warnings = summary.totalWarnings
                });
            }
            else
            {
                // Collect errors
                var errors = report.steps
                    .SelectMany(s => s.messages)
                    .Where(m => m.type == LogType.Error || m.type == LogType.Exception)
                    .Select(m => m.content)
                    .ToList();
                    
                return new ErrorResponse($"Build failed with {summary.totalErrors} errors.", new 
                {
                    result = summary.result.ToString(),
                    errors = errors
                });
            }
        }

        private static bool TryParseBuildTarget(string str, out BuildTarget target, out BuildTargetGroup group)
        {
            str = str.ToLowerInvariant();
            target = BuildTarget.NoTarget;
            group = BuildTargetGroup.Unknown;

            switch (str)
            {
                case "standalone_win64":
                case "windows":
                case "win64":
                    target = BuildTarget.StandaloneWindows64;
                    group = BuildTargetGroup.Standalone;
                    return true;
                case "standalone_osx":
                case "osx":
                case "mac":
                case "macos":
                    target = BuildTarget.StandaloneOSX;
                    group = BuildTargetGroup.Standalone;
                    return true;
                case "standalone_linux64":
                case "linux":
                    target = BuildTarget.StandaloneLinux64;
                    group = BuildTargetGroup.Standalone;
                    return true;
                case "android":
                    target = BuildTarget.Android;
                    group = BuildTargetGroup.Android;
                    return true;
                case "ios":
                case "iphone":
                    target = BuildTarget.iOS;
                    group = BuildTargetGroup.iOS;
                    return true;
                case "webgl":
                    target = BuildTarget.WebGL;
                    group = BuildTargetGroup.WebGL;
                    return true;
                default:
                    return false;
            }
        }
    }
}
