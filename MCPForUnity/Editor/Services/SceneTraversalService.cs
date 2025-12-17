using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Service for traversing and querying scene hierarchy data.
    /// </summary>
    public static class SceneTraversalService
    {
        [System.Serializable]
        public class HierarchyNode
        {
            public string name;
            public bool activeSelf;
            public bool activeInHierarchy;
            public string tag;
            public int layer;
            public bool isStatic;
            public int instanceID;
            public TransformData transform;
            public List<HierarchyNode> children;
        }

        [System.Serializable]
        public class TransformData
        {
            public Vector3Data position;
            public Vector3Data rotation;
            public Vector3Data scale;
        }

        [System.Serializable]
        public struct Vector3Data
        {
            public float x, y, z;
            public Vector3Data(Vector3 v) { x = v.x; y = v.y; z = v.z; }
        }

        public static List<HierarchyNode> GetSceneHierarchyData(Scene scene, int maxDepth = 20)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return new List<HierarchyNode>();
            }

            GameObject[] rootObjects = scene.GetRootGameObjects();
            // Use initial capacity to avoid resizing
            var result = new List<HierarchyNode>(rootObjects.Length);
            
            foreach (var go in rootObjects)
            {
                result.Add(GetGameObjectDataRecursive(go, 0, maxDepth));
            }
            return result;
        }

        /// <summary>
        /// Recursively builds a data representation of a GameObject and its children.
        /// Uses DTOs to minimize GC overhead.
        /// </summary>
        public static HierarchyNode GetGameObjectDataRecursive(GameObject go, int currentDepth, int maxDepth)
        {
            if (go == null) return null;

            var node = new HierarchyNode
            {
                name = go.name,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = go.layer,
                isStatic = go.isStatic,
                instanceID = go.GetInstanceID(),
                transform = new TransformData
                {
                    position = new Vector3Data(go.transform.localPosition),
                    rotation = new Vector3Data(go.transform.localRotation.eulerAngles),
                    scale = new Vector3Data(go.transform.localScale)
                }
            };

            if (currentDepth < maxDepth && go.transform.childCount > 0)
            {
                node.children = new List<HierarchyNode>(go.transform.childCount);
                foreach (Transform child in go.transform)
                {
                    node.children.Add(GetGameObjectDataRecursive(child.gameObject, currentDepth + 1, maxDepth));
                }
            }
            
            return node;
        }
    }
}
