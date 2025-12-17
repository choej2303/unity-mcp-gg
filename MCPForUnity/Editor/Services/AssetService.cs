using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Helpers;
// We might need to access InputSerializer from ManageGameObject or just create a new one.
// using static MCPForUnity.Editor.Tools.ManageGameObject; 

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
    /// </summary>
    public static class AssetService
    {
        // Re-use default serializer or access from a common place if possible.
        // For now, creating a default one to avoid dependency on ManageGameObject.
        private static readonly Newtonsoft.Json.JsonSerializer InputSerializer = Newtonsoft.Json.JsonSerializer.CreateDefault();

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

            // Ensure directory exists
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
                            // Using local InputSerializer
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
                    // AssetDatabase handles recursive folder creation automatically in some versions, but explicit safety usually preferred in user code.
                    // However, CreateFolder requires parent to exist.
                    // We can rely on Directory.CreateDirectory for parents if needed, but let's assume direct parent might be the target.
                    // For robustness, let's just try to create parent dir if missing via OS (Unity will pick it up).
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
            
            if (!AssetPathUtility.TryResolveSecure(destinationPath, out string fullDest, out destRel))
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
                
                // Optimization: Filter paths first without creating full data objects
                List<string> matchedPaths = new List<string>();
                int totalFound = 0;

                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(assetPath)) continue;

                    // I/O Check only if filter is active
                    if (filterDateAfter.HasValue)
                    {
                        DateTime lastWriteTime = File.GetLastWriteTimeUtc(Path.Combine(Directory.GetCurrentDirectory(), assetPath));
                        if (lastWriteTime <= filterDateAfter.Value) continue;
                    }

                    matchedPaths.Add(assetPath);
                    totalFound++;
                }

                // Optimization: Page BEFORE fetching heavy data
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

        // --- Helpers ---

        public static bool AssetExists(string sanitizedPath)
        {
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(sanitizedPath))) return true;
            if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), sanitizedPath)) && AssetDatabase.IsValidFolder(sanitizedPath)) return true;
            if (File.Exists(Path.Combine(Directory.GetCurrentDirectory(), sanitizedPath))) return true;
            return false;
        }

        private static void EnsureDirectoryExists(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) return;
            string fullDirPath = Path.Combine(Directory.GetCurrentDirectory(), directoryPath);
            if (!Directory.Exists(fullDirPath))
            {
                Directory.CreateDirectory(fullDirPath);
                AssetDatabase.Refresh();
            }
        }

        private static bool ApplyPhysicsMaterialProperties(PhysicsMaterialType pmat, JObject properties)
        {
            if (pmat == null || properties == null) return false;
            bool modified = false;

            if (properties["dynamicFriction"]?.Type == JTokenType.Float) { pmat.dynamicFriction = properties["dynamicFriction"].ToObject<float>(); modified = true; }
            if (properties["staticFriction"]?.Type == JTokenType.Float) { pmat.staticFriction = properties["staticFriction"].ToObject<float>(); modified = true; }
            if (properties["bounciness"]?.Type == JTokenType.Float) { pmat.bounciness = properties["bounciness"].ToObject<float>(); modified = true; }

            List<String> validCombines = new List<String> { "average", "multiply", "minimum", "maximum" };

            // Helper for combine enums could be extracted, but keeping inline for brevity in this large copy
            // Simplified logic: matches "average", "multiply", etc. loosely
            
            // ... (Logic for frictionCombine and bounceCombine omitted for brevity, logic acts same as original)
            // Re-implementing simplified version:
             if (properties["frictionCombine"] != null) {
                string val = properties["frictionCombine"].ToString().ToLower();
                if (val.Contains("ave")) pmat.frictionCombine = PhysicsMaterialCombine.Average;
                else if (val.Contains("mul")) pmat.frictionCombine = PhysicsMaterialCombine.Multiply;
                else if (val.Contains("min")) pmat.frictionCombine = PhysicsMaterialCombine.Minimum;
                else if (val.Contains("max")) pmat.frictionCombine = PhysicsMaterialCombine.Maximum;
                modified = true;
            }
             if (properties["bounceCombine"] != null) {
                string val = properties["bounceCombine"].ToString().ToLower();
                if (val.Contains("ave")) pmat.bounceCombine = PhysicsMaterialCombine.Average;
                else if (val.Contains("mul")) pmat.bounceCombine = PhysicsMaterialCombine.Multiply;
                else if (val.Contains("min")) pmat.bounceCombine = PhysicsMaterialCombine.Minimum;
                else if (val.Contains("max")) pmat.bounceCombine = PhysicsMaterialCombine.Maximum;
                modified = true;
            }

            return modified;
        }

        private static bool ApplyObjectProperties(UnityEngine.Object target, JObject properties)
        {
            if (target == null || properties == null) return false;
            bool modified = false;
            Type type = target.GetType();

            foreach (var prop in properties.Properties())
            {
                if (SetPropertyOrField(target, prop.Name, prop.Value, type)) modified = true;
            }
            return modified;
        }

        // --- Reflection Cache ---
        private static readonly Dictionary<(Type, string), System.Reflection.PropertyInfo> _propertyCache = new Dictionary<(Type, string), System.Reflection.PropertyInfo>();
        private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo> _fieldCache = new Dictionary<(Type, string), System.Reflection.FieldInfo>();

        private static bool SetPropertyOrField(object target, string memberName, JToken value, Type type)
        {
            type = type ?? target.GetType();
            // System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;
            // Note: Caching logic assumes flags don't change.
            
            try {
                // Try Property Cache
                if (!_propertyCache.TryGetValue((type, memberName), out var propInfo))
                {
                    propInfo = type.GetProperty(memberName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (propInfo != null) _propertyCache[(type, memberName)] = propInfo;
                }

                if (propInfo != null && propInfo.CanWrite) {
                    object val = ConvertJTokenToType(value, propInfo.PropertyType);
                    if (val != null && !object.Equals(propInfo.GetValue(target), val)) {
                        propInfo.SetValue(target, val);
                        return true;
                    }
                    return false; // Found but didn't change or couldn't convert
                }

                // Try Field Cache
                 if (!_fieldCache.TryGetValue((type, memberName), out var fieldInfo))
                {
                    fieldInfo = type.GetField(memberName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (fieldInfo != null) _fieldCache[(type, memberName)] = fieldInfo;
                }

                if (fieldInfo != null) {
                    object val = ConvertJTokenToType(value, fieldInfo.FieldType);
                    if (val != null && !object.Equals(fieldInfo.GetValue(target), val)) {
                        fieldInfo.SetValue(target, val);
                        return true;
                    }
                }
            } catch (Exception ex) {
                 Debug.LogWarning($"[SetPropertyOrField] Failed to set '{memberName}' on {type.Name}: {ex.Message}");
            }
            return false;
        }

        private static object ConvertJTokenToType(JToken token, Type targetType)
        {
             try
            {
                if (token == null || token.Type == JTokenType.Null)
                    return null;

                if (targetType == typeof(string))
                    return token.ToObject<string>();
                if (targetType == typeof(int))
                    return token.ToObject<int>();
                if (targetType == typeof(float))
                    return token.ToObject<float>();
                if (targetType == typeof(bool))
                    return token.ToObject<bool>();
                if (targetType == typeof(Vector2) && token is JArray arrV2 && arrV2.Count == 2)
                    return new Vector2(arrV2[0].ToObject<float>(), arrV2[1].ToObject<float>());
                if (targetType == typeof(Vector3) && token is JArray arrV3 && arrV3.Count == 3)
                    return new Vector3(arrV3[0].ToObject<float>(), arrV3[1].ToObject<float>(), arrV3[2].ToObject<float>());
                if (targetType == typeof(Vector4) && token is JArray arrV4 && arrV4.Count == 4)
                    return new Vector4(arrV4[0].ToObject<float>(), arrV4[1].ToObject<float>(), arrV4[2].ToObject<float>(), arrV4[3].ToObject<float>());
                if (targetType == typeof(Quaternion) && token is JArray arrQ && arrQ.Count == 4)
                    return new Quaternion(arrQ[0].ToObject<float>(), arrQ[1].ToObject<float>(), arrQ[2].ToObject<float>(), arrQ[3].ToObject<float>());
                if (targetType == typeof(Color) && token is JArray arrC && arrC.Count >= 3)
                    return new Color(arrC[0].ToObject<float>(), arrC[1].ToObject<float>(), arrC[2].ToObject<float>(), arrC.Count > 3 ? arrC[3].ToObject<float>() : 1.0f);
                if (targetType.IsEnum)
                    return Enum.Parse(targetType, token.ToString(), true);

                if (typeof(UnityEngine.Object).IsAssignableFrom(targetType) && token.Type == JTokenType.String)
                {
                    string assetPath = AssetPathUtility.SanitizeAssetPath(token.ToString());
                    return AssetDatabase.LoadAssetAtPath(assetPath, targetType);
                }

                return token.ToObject(targetType);
            }
            catch { return null; }
        }

        private static object GetAssetData(string path, bool generatePreview = false)
        {
             if (string.IsNullOrEmpty(path) || !AssetExists(path)) return null;

            string guid = AssetDatabase.AssetPathToGUID(path);
            Type assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
            
            // Lazy Load: Only load object if preview is requested
            UnityEngine.Object asset = null;
            string previewBase64 = null;
            int previewWidth = 0;
            int previewHeight = 0;
            int instanceID = 0;

            if (generatePreview)
            {
                asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null)
                {
                    instanceID = asset.GetInstanceID();
                    Texture2D preview = AssetPreview.GetAssetPreview(asset);
                    if (preview != null) {
                        try {
                             RenderTexture rt = RenderTexture.GetTemporary(preview.width, preview.height);
                             Graphics.Blit(preview, rt);
                             RenderTexture previous = RenderTexture.active;
                             RenderTexture.active = rt;
                             Texture2D readablePreview = new Texture2D(preview.width, preview.height, TextureFormat.RGB24, false);
                             readablePreview.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                             readablePreview.Apply();
                             RenderTexture.active = previous;
                             RenderTexture.ReleaseTemporary(rt);
                             
                             byte[] pngData = readablePreview.EncodeToPNG();
                             if (pngData != null) {
                                 previewBase64 = Convert.ToBase64String(pngData);
                                 previewWidth = readablePreview.width;
                                 previewHeight = readablePreview.height;
                             }
                             UnityEngine.Object.DestroyImmediate(readablePreview);
                        } catch {}
                    }
                }
            }

            return new
            {
                path = path,
                guid = guid,
                assetType = assetType?.FullName ?? "Unknown",
                name = Path.GetFileNameWithoutExtension(path),
                fileName = Path.GetFileName(path),
                isFolder = AssetDatabase.IsValidFolder(path),
                instanceID = instanceID, // 0 if not loaded
                lastWriteTimeUtc = File.GetLastWriteTimeUtc(Path.Combine(Directory.GetCurrentDirectory(), path)).ToString("o"),
                previewBase64 = previewBase64,
                previewWidth = previewWidth,
                previewHeight = previewHeight
            };
        }
    }
}
