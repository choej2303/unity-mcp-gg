using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Helpers;

#if UNITY_6000_0_OR_NEWER
using PhysicsMaterialType = UnityEngine.PhysicsMaterial;
using PhysicsMaterialCombine = UnityEngine.PhysicsMaterialCombine;  
#else
using PhysicsMaterialType = UnityEngine.PhysicMaterial;
using PhysicsMaterialCombine = UnityEngine.PhysicMaterialCombine;
#endif

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Service layer for asset management operations.
    /// Decoupled from the routing/tool layer.
    /// This is a partial class - see also:
    /// - AssetService.Query.cs (Search, GetAssetInfo, GetComponentsFromAsset)
    /// - AssetService.Helpers.cs (Helper methods, reflection cache, type conversion)
    /// </summary>
    public static partial class AssetService
    {
        public static object ReimportAsset(string path, JObject properties)
        {
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required for reimport.");
            
            if (!AssetPathUtility.TryResolveSecure(path, out string fullPath, out string relPath))
                return new ErrorResponse($"Invalid path or security violation: {path}");

            if (!AssetExists(relPath))
                return new ErrorResponse($"Asset not found at path: {relPath}");

            try
            {
                if (properties != null && properties.HasValues)
                {
                    Debug.LogWarning(
                        "[AssetService.Reimport] Modifying importer properties before reimport is not fully implemented yet."
                    );
                }

                AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceUpdate);
                return new SuccessResponse($"Asset '{fullPath}' reimported.", GetAssetData(fullPath));
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to reimport asset '{fullPath}': {e.Message}");
            }
        }

        public static object CreateAsset(JObject @params)
        {
            string path = @params["path"]?.ToString();
            string assetType =
                @params["assetType"]?.ToString()
                ?? @params["asset_type"]?.ToString();
            JObject properties = @params["properties"] as JObject;

            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required for create.");
            if (string.IsNullOrEmpty(assetType))
                return new ErrorResponse("'assetType' is required for create.");

            if (!AssetPathUtility.TryResolveSecure(path, out string fullPath, out string relPath))
                return new ErrorResponse($"Invalid path or security violation: {path}");

            string directory = Path.GetDirectoryName(fullPath);

            if (!Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), directory)))
            {
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), directory));
                AssetDatabase.Refresh();
            }

            if (AssetExists(fullPath))
                return new ErrorResponse($"Asset already exists at path: {fullPath}");

            try
            {
                UnityEngine.Object newAsset = null;
                string lowerAssetType = assetType.ToLowerInvariant();

                if (lowerAssetType == "folder")
                {
                    return CreateFolder(path);
                }
                else if (lowerAssetType == "material")
                {
                    var requested = properties?["shader"]?.ToString();
                    Shader shader = RenderPipelineUtility.ResolveShader(requested);
                    if (shader == null)
                        return new ErrorResponse($"Could not find a project-compatible shader (requested: '{requested ?? "none"}').");

                    var mat = new Material(shader);
                    if (properties != null)
                    {
                        JObject propertiesForApply = properties;
                        if (propertiesForApply["shader"] != null)
                        {
                            propertiesForApply = (JObject)properties.DeepClone();
                            propertiesForApply.Remove("shader");
                        }

                        if (propertiesForApply.HasValues)
                        {
                            MaterialOps.ApplyProperties(mat, propertiesForApply, InputSerializer);
                        }
                    }
                    AssetDatabase.CreateAsset(mat, fullPath);
                    newAsset = mat;
                }
                else if (lowerAssetType == "physicsmaterial")
                {
                    PhysicsMaterialType pmat = new PhysicsMaterialType();
                    if (properties != null)
                        ApplyPhysicsMaterialProperties(pmat, properties);
                    AssetDatabase.CreateAsset(pmat, fullPath);
                    newAsset = pmat;
                }
                else if (lowerAssetType == "scriptableobject")
                {
                    string scriptClassName = properties?["scriptClass"]?.ToString();
                    if (string.IsNullOrEmpty(scriptClassName))
                        return new ErrorResponse("'scriptClass' property required.");

                    Type scriptType = ComponentResolver.TryResolve(scriptClassName, out var resolvedType, out var error) ? resolvedType : null;
                    if (scriptType == null || !typeof(ScriptableObject).IsAssignableFrom(scriptType))
                    {
                        var reason = scriptType == null
                            ? (string.IsNullOrEmpty(error) ? "Type not found." : error)
                            : "Type found but does not inherit from ScriptableObject.";
                        return new ErrorResponse($"Script class '{scriptClassName}' invalid: {reason}");
                    }

                    ScriptableObject so = ScriptableObject.CreateInstance(scriptType);
                    AssetDatabase.CreateAsset(so, fullPath);
                    newAsset = so;
                }
                else if (lowerAssetType == "prefab")
                {
                    return new ErrorResponse(
                        "Creating prefabs programmatically usually requires a source GameObject. Use manage_gameobject to create/configure."
                    );
                }
                else
                {
                    return new ErrorResponse(
                        $"Creation for asset type '{assetType}' is not explicitly supported yet. Supported: Folder, Material, ScriptableObject."
                    );
                }

                if (newAsset == null && !Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), fullPath)))
                {
                    return new ErrorResponse($"Failed to create asset '{assetType}' at '{fullPath}'. See logs for details.");
                }

                AssetDatabase.SaveAssets();
                return new SuccessResponse($"Asset '{fullPath}' created successfully.", GetAssetData(fullPath));
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to create asset at '{fullPath}': {e.Message}");
            }
        }

        public static object CreateFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required for create_folder.");
            
            if (!AssetPathUtility.TryResolveSecure(path, out string fullPath, out string relPath))
                return new ErrorResponse($"Invalid path or security violation: {path}");

            string parentDir = Path.GetDirectoryName(relPath);
            string folderName = Path.GetFileName(relPath);

            if (AssetExists(fullPath))
            {
                if (AssetDatabase.IsValidFolder(fullPath))
                    return new SuccessResponse($"Folder already exists at path: {fullPath}", GetAssetData(fullPath));
                else
                    return new ErrorResponse($"An asset (not a folder) already exists at path: {fullPath}");
            }

            try
            {
                if (!string.IsNullOrEmpty(parentDir) && !AssetDatabase.IsValidFolder(parentDir))
                {
                    string parentAbsPath = Path.Combine(Directory.GetCurrentDirectory(), parentDir);
                    if (!Directory.Exists(parentAbsPath)) {
                        Directory.CreateDirectory(parentAbsPath);
                        AssetDatabase.Refresh();
                    }
                }

                string guid = AssetDatabase.CreateFolder(parentDir, folderName);
                if (string.IsNullOrEmpty(guid))
                    return new ErrorResponse($"Failed to create folder '{fullPath}'. Check logs.");

                return new SuccessResponse($"Folder '{fullPath}' created successfully.", GetAssetData(fullPath));
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to create folder '{fullPath}': {e.Message}");
            }
        }

        public static object ModifyAsset(string path, JObject properties)
        {
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required for modify.");
            if (properties == null || !properties.HasValues)
                return new ErrorResponse("'properties' are required for modify.");

            if (!AssetPathUtility.TryResolveSecure(path, out string fullPath, out string relPath))
                return new ErrorResponse($"Invalid path or security violation: {path}");

            if (!AssetExists(relPath))
                return new ErrorResponse($"Asset not found at path: {relPath}");

            try
            {
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(fullPath);
                if (asset == null)
                    return new ErrorResponse($"Failed to load asset at path: {fullPath}");

                bool modified = false;

                if (asset is GameObject gameObject)
                {
                    foreach (var prop in properties.Properties())
                    {
                        string componentName = prop.Name;
                        if (prop.Value is JObject componentProperties && componentProperties.HasValues)
                        {
                            Component targetComponent = null;
                            bool resolved = ComponentResolver.TryResolve(componentName, out var compType, out var compError);
                            if (resolved)
                            {
                                targetComponent = gameObject.GetComponent(compType);
                            }

                             if (targetComponent != null)
                            {
                                modified |= ApplyObjectProperties(targetComponent, componentProperties);
                            }
                            else
                            {
                                Debug.LogWarning($"[AssetService.ModifyAsset] Component '{componentName}' not found on '{gameObject.name}'.");
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[AssetService.ModifyAsset] Property '{prop.Name}' should be a JSON object.");
                        }
                    }
                }
                else if (asset is Material material)
                {
                    modified |= MaterialOps.ApplyProperties(material, properties, InputSerializer);
                }
                else if (asset is ScriptableObject so)
                {
                    modified |= ApplyObjectProperties(so, properties);
                }
                else if (asset is Texture)
                {
                    AssetImporter importer = AssetImporter.GetAtPath(fullPath);
                    if (importer is TextureImporter textureImporter)
                    {
                        bool importerModified = ApplyObjectProperties(textureImporter, properties);
                        if (importerModified)
                        {
                            AssetDatabase.WriteImportSettingsIfDirty(fullPath);
                            AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceUpdate);
                            modified = true;
                        }
                    }
                }
                else
                {
                    modified |= ApplyObjectProperties(asset, properties);
                }

                if (modified)
                {
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssets();
                    return new SuccessResponse($"Asset '{fullPath}' modified successfully.", GetAssetData(fullPath));
                }
                else
                {
                    return new SuccessResponse($"No applicable or modifiable properties found for asset '{fullPath}'.", GetAssetData(fullPath));
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to modify asset '{fullPath}': {e.Message}");
            }
        }

        public static object DeleteAsset(string path)
        {
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required for delete.");

            if (!AssetPathUtility.TryResolveSecure(path, out string fullPath, out string relPath))
                return new ErrorResponse($"Invalid path or security violation: {path}");

            if (!AssetExists(relPath))
                return new ErrorResponse($"Asset not found at path: {relPath}");

            try
            {
                bool success = AssetDatabase.DeleteAsset(fullPath);
                if (success)
                    return new SuccessResponse($"Asset '{fullPath}' deleted successfully.");
                else
                    return new ErrorResponse($"Failed to delete asset '{fullPath}'.");
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error deleting asset '{fullPath}': {e.Message}");
            }
        }

        public static object DuplicateAsset(string path, string destinationPath)
        {
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required for duplicate.");

            if (!AssetPathUtility.TryResolveSecure(path, out string fullSource, out string relSource))
                 return new ErrorResponse($"Invalid source path: {path}");

            if (!AssetExists(relSource))
                return new ErrorResponse($"Source asset not found at path: {relSource}");

            string destRel;
            if (string.IsNullOrEmpty(destinationPath))
            {
                destRel = AssetDatabase.GenerateUniqueAssetPath(relSource);
            }
            else
            {
                if (!AssetPathUtility.TryResolveSecure(destinationPath, out string fullDest, out destRel))
                    return new ErrorResponse($"Invalid destination path: {destinationPath}");

                if (AssetExists(destRel))
                    return new ErrorResponse($"Asset already exists at destination path: {destRel}");
                EnsureDirectoryExists(Path.GetDirectoryName(destRel));
            }

            try
            {
                bool success = AssetDatabase.CopyAsset(relSource, destRel);
                if (success)
                    return new SuccessResponse($"Asset '{relSource}' duplicated to '{destRel}'.", GetAssetData(destRel));
                else
                    return new ErrorResponse($"Failed to duplicate asset.");
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error duplicating asset '{relSource}': {e.Message}");
            }
        }

        public static object MoveOrRenameAsset(string path, string destinationPath)
        {
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' is required for move/rename.");
            if (string.IsNullOrEmpty(destinationPath))
                return new ErrorResponse("'destination' is required.");

            if (!AssetPathUtility.TryResolveSecure(path, out string fullSource, out string relSource))
                 return new ErrorResponse($"Invalid source path: {path}");
            
            if (!AssetPathUtility.TryResolveSecure(destinationPath, out string fullDest, out string destRel))
                 return new ErrorResponse($"Invalid destination path: {destinationPath}");

            if (!AssetExists(relSource))
                return new ErrorResponse($"Source asset not found at path: {relSource}");
            if (AssetExists(destRel))
                return new ErrorResponse($"Asset already exists at destination: {destRel}");

            EnsureDirectoryExists(Path.GetDirectoryName(destRel));

            try
            {
                string error = AssetDatabase.ValidateMoveAsset(relSource, destRel);
                if (!string.IsNullOrEmpty(error))
                    return new ErrorResponse($"Failed to move/rename: {error}");

                string guid = AssetDatabase.MoveAsset(relSource, destRel);
                if (!string.IsNullOrEmpty(guid))
                    return new SuccessResponse($"Asset moved/renamed to '{destRel}'.", GetAssetData(destRel));
                else
                    return new ErrorResponse("MoveAsset call failed.");
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error moving/renaming asset '{relSource}': {e.Message}");
            }
        }
    }
}
