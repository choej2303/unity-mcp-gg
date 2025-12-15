using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Menus
{
    public static class McpContextMenus
    {
        private const string MenuPathAssets = "Assets/Antigravity/";
        private const string MenuPathGameObject = "GameObject/Antigravity/";

        // --- ASSETS CONTEXT ---

        [MenuItem(MenuPathAssets + "Ask about this...", false, 20)]
        private static void AskAboutAsset()
        {
            HandleAssetAction("Explain this asset: ");
        }

        [MenuItem(MenuPathAssets + "Refactor this...", true)]
        private static bool ValidateRefactorAsset()
        {
            // Only allow refactoring for scripts
            return Selection.activeObject is MonoScript;
        }

        [MenuItem(MenuPathAssets + "Refactor this...", false, 21)]
        private static void RefactorAsset()
        {
            HandleAssetAction("Refactor this script to improve readability and performance: ");
        }

        [MenuItem(MenuPathAssets + "Analyze Logic...", true)]
        private static bool ValidateAnalyzeAsset()
        {
             return Selection.activeObject is MonoScript;
        }

        [MenuItem(MenuPathAssets + "Analyze Logic...", false, 22)]
        private static void AnalyzeAsset()
        {
            HandleAssetAction("Analyze the logic of this script and point out potential bugs: ");
        }


        // --- GAMEOBJECT CONTEXT ---

        [MenuItem(MenuPathGameObject + "Ask about selected GameObject(s)", false, 10)]
        private static void AskAboutGameObject()
        {
            var gameObjects = Selection.gameObjects;
            if (gameObjects == null || gameObjects.Length == 0) return;

            var sb = new StringBuilder();
            
            if (gameObjects.Length == 1)
            {
                sb.AppendLine($"Analyze this GameObject: '{gameObjects[0].name}'");
                AppendGameObjectDetails(sb, gameObjects[0]);
            }
            else
            {
                sb.AppendLine($"Analyze these {gameObjects.Length} GameObjects:");
                foreach (var go in gameObjects)
                {
                    sb.AppendLine($"\n[GameObject: {go.name}]");
                    AppendGameObjectDetails(sb, go);
                }
            }

            CopyToClipboardAndNotify(sb.ToString(), $"Context for {gameObjects.Length} object(s) copied to clipboard!");
        }

        private static void AppendGameObjectDetails(StringBuilder sb, GameObject go)
        {
            sb.AppendLine($"Tag: {go.tag}, Layer: {LayerMask.LayerToName(go.layer)}");
            sb.AppendLine("Components:");
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                sb.AppendLine($"- {comp.GetType().Name}");
            }

            if (go.transform.childCount > 0)
            {
                sb.AppendLine("Children:");
                foreach (Transform child in go.transform)
                {
                    sb.AppendLine($"- [Child] {child.name} (Active: {child.gameObject.activeSelf})");
                }
            }
        }

        // --- HANDLER LOGIC ---

        private static void HandleAssetAction(string prefixPrompt)
        {
            var obj = Selection.activeObject;
            if (obj == null) return;

            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return;

            string absPath = Path.GetFullPath(path);
            
            // Check if Antigravity is the external editor (Simple heuristic or pref check)
            // For now, we use the fallback to clipboard as it's safer and requested.
            // Future: Integrate with CodeEditor.CurrentEditor

            // 1. Copy context to clipboard
            string contentToCopy = $"{prefixPrompt}\nFile: {path}\nAbsolute: {absPath}";
            CopyToClipboardAndNotify(contentToCopy, $"Prompt for '{obj.name}' copied to clipboard! Paste in Antigravity.");

            // 2. Open the file in the external editor (Seamless Handoff)
            // This switches focus to the editor where the user can immediately paste.
            AssetDatabase.OpenAsset(obj);
        }

        private static void CopyToClipboardAndNotify(string content, string message)
        {
            GUIUtility.systemCopyBuffer = content;
            Debug.Log($"[Antigravity] {message}");
            
            // Show a small notification
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.ShowNotification(new GUIContent("Copied to Clipboard! Switching to Editor..."));
            }
        }
    }
}
