using System;
using System.IO;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Provides tools for interacting with the external IDE/Editor.
    /// Allows the AI to open files, jump to lines, etc.
    /// </summary>
    [McpForUnityTool("open_asset", AutoRegister = true)]
    public static class ManageIDE
    {
        public static object HandleCommand(JObject @params)
        {
            string path = @params["path"]?.ToString();
            int line = @params["line"]?.ToObject<int>() ?? -1;

            if (string.IsNullOrEmpty(path))
            {
                return new ErrorResponse("Path parameter is required.");
            }

            // Normalize path separator
            path = path.Replace("\\", "/");

            // Check if path is relative to project or absolute
            // We encourage Assets/... relative paths but handle absolute if possible
            if (Path.IsPathRooted(path))
            {
                // Try to make it relative to project folder
                string projectPath = Path.GetDirectoryName(Application.dataPath).Replace("\\", "/");
                if (path.StartsWith(projectPath))
                {
                    path = path.Substring(projectPath.Length + 1);
                }
            }

            // Verify existence
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                // Try searching in Assets if not found (fuzzy convenience)
                if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
                {
                    string potentialPath = "Assets/" + path;
                    if (File.Exists(potentialPath)) path = potentialPath;
                }
                
                if (!File.Exists(path))
                {
                     return new ErrorResponse($"File not found at path: {path}");
                }
            }
            
            try
            {
                UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                
                // If it's a script/text and we have a line number, use specialized open
                if (line > 0)
                {
                     // This works for scripts and text files, opening them in External Script Editor (Antigravity/VSCode/etc)
                     // It puts the cursor at the specific line.
                     bool success = UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(path, line);
                     if (success) 
                         return new SuccessResponse($"Opened '{path}' at line {line}.");
                     else
                         return new ErrorResponse($"Failed to open '{path}' at line {line} (External editor error).");
                }
                else
                {
                    // General open (Scenes, Prefabs, Textures, or Scripts without line)
                    if (obj != null)
                    {
                        AssetDatabase.OpenAsset(obj);
                        return new SuccessResponse($"Opened asset '{path}' successfully.");
                    }
                    else
                    {
                        // Fallback: If AssetDatabase couldn't load it (maybe hidden file or weird extension), 
                        // try generic OS open (though InternalEditorUtility.OpenFileAtLineExternal handles most text)
                         bool success = UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(path, 1);
                         if(success)
                             return new SuccessResponse($"Opened '{path}' (generic).");
                         
                        return new ErrorResponse($"Could not load asset at '{path}' to open.");
                    }
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error opening asset: {e.Message}");
            }
        }
    }
}
