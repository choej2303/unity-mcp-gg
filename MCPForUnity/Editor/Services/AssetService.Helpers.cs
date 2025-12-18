using System;
using System.Collections.Generic;
using System.IO;
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
    /// Helper methods for AssetService.
    /// </summary>
    public static partial class AssetService
    {
        // Re-use default serializer or access from a common place if possible.
        private static readonly Newtonsoft.Json.JsonSerializer InputSerializer = Newtonsoft.Json.JsonSerializer.CreateDefault();

        // --- Reflection Cache ---
        private static readonly Dictionary<(Type, string), System.Reflection.PropertyInfo> _propertyCache = new Dictionary<(Type, string), System.Reflection.PropertyInfo>();
        private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo> _fieldCache = new Dictionary<(Type, string), System.Reflection.FieldInfo>();

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

        private static bool SetPropertyOrField(object target, string memberName, JToken value, Type type)
        {
            type = type ?? target.GetType();
            
            try {
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
                    return false;
                }

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
                instanceID = instanceID,
                lastWriteTimeUtc = File.GetLastWriteTimeUtc(Path.Combine(Directory.GetCurrentDirectory(), path)).ToString("o"),
                previewBase64 = previewBase64,
                previewWidth = previewWidth,
                previewHeight = previewHeight
            };
        }
    }
}
