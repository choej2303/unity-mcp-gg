using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Query and search operations for AssetService.
    /// </summary>
    public static partial class AssetService
    {
        public static object SearchAssets(JObject @params)
        {
            string searchPattern = @params["searchPattern"]?.ToString();
            string filterType = @params["filterType"]?.ToString();
            string pathScope = @params["path"]?.ToString();
            string filterDateAfterStr = @params["filterDateAfter"]?.ToString();
            int pageSize = @params["pageSize"]?.ToObject<int?>() ?? 50;
            int pageNumber = @params["pageNumber"]?.ToObject<int?>() ?? 1;
            bool generatePreview = @params["generatePreview"]?.ToObject<bool>() ?? false;

            List<string> searchFilters = new List<string>();
            if (!string.IsNullOrEmpty(searchPattern))
                searchFilters.Add(searchPattern);
            if (!string.IsNullOrEmpty(filterType))
                searchFilters.Add($"t:{filterType}");

            string[] folderScope = null;
            if (!string.IsNullOrEmpty(pathScope))
            {
                if (AssetPathUtility.TryResolveSecure(pathScope, out _, out string safeScope))
                {
                    folderScope = new string[] { safeScope };
                    if (!AssetDatabase.IsValidFolder(folderScope[0]))
                    {
                        folderScope = null;
                    }
                }
            }

            DateTime? filterDateAfter = null;
            if (!string.IsNullOrEmpty(filterDateAfterStr))
            {
                if (DateTime.TryParse(filterDateAfterStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsedDate))
                    filterDateAfter = parsedDate;
            }

            try
            {
                string[] guids = AssetDatabase.FindAssets(string.Join(" ", searchFilters), folderScope);
                
                List<string> matchedPaths = new List<string>();
                int totalFound = 0;

                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(assetPath)) continue;

                    if (filterDateAfter.HasValue)
                    {
                        DateTime lastWriteTime = File.GetLastWriteTimeUtc(Path.Combine(Directory.GetCurrentDirectory(), assetPath));
                        if (lastWriteTime <= filterDateAfter.Value) continue;
                    }

                    matchedPaths.Add(assetPath);
                    totalFound++;
                }

                int startIndex = (pageNumber - 1) * pageSize;
                var pagedPaths = matchedPaths.Skip(startIndex).Take(pageSize);
                
                List<object> pagedResults = new List<object>();
                foreach (var path in pagedPaths)
                {
                   pagedResults.Add(GetAssetData(path, generatePreview));
                }

                return new SuccessResponse(
                    $"Found {totalFound} asset(s). Returning page {pageNumber} ({pagedResults.Count} assets).",
                    new { totalAssets = totalFound, pageSize = pageSize, pageNumber = pageNumber, assets = pagedResults }
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error searching assets: {e.Message}");
            }
        }

        public static object GetAssetInfo(string path, bool generatePreview)
        {
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required.");
            
            if (!AssetPathUtility.TryResolveSecure(path, out string fullPath, out string relPath))
                return new ErrorResponse($"Invalid path or security violation: {path}");

            if (!AssetExists(relPath))
                return new ErrorResponse($"Asset not found at path: {relPath}");

            return new SuccessResponse("Asset info retrieved.", GetAssetData(relPath, generatePreview));
        }

        public static object GetComponentsFromAsset(string path)
        {
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required.");

            if (!AssetPathUtility.TryResolveSecure(path, out string fullPath, out string relPath))
                return new ErrorResponse($"Invalid path or security violation: {path}");

            if (!AssetExists(relPath))
                return new ErrorResponse($"Asset not found at path: {relPath}");

            try
            {
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relPath);
                if (asset == null)
                    return new ErrorResponse($"Failed to load asset at path: {relPath}");

                GameObject gameObject = asset as GameObject;
                if (gameObject == null)
                    return new ErrorResponse($"Asset at '{relPath}' is not a GameObject (Type: {asset.GetType().FullName}).");

                Component[] components = gameObject.GetComponents<Component>();
                var componentList = components.Select(comp => new { typeName = comp.GetType().FullName, instanceID = comp.GetInstanceID() }).ToList<object>();

                return new SuccessResponse($"Found {componentList.Count} component(s).", componentList);
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error getting components: {e.Message}");
            }
        }
    }
}
