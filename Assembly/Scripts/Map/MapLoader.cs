﻿using ApplicationManagers;
using Events;
using System.Collections;
using System.Collections.Generic;
using UI;
using UnityEngine;
using Utility;

namespace Map
{
    class MapLoader: MonoBehaviour
    {
        public static Dictionary<int, MapObject> IdToMapObject = new Dictionary<int, MapObject>();
        public static Dictionary<int, HashSet<int>> IdToChildren = new Dictionary<int, HashSet<int>>();
        public static Dictionary<GameObject, MapObject> GoToMapObject = new Dictionary<GameObject, MapObject>();
        public static Dictionary<string, List<MapObject>> Tags = new Dictionary<string, List<MapObject>>();
        public static List<Light> Daylight = new List<Light>();
        private static Dictionary<string, Object> _assetCache = new Dictionary<string, Object>();
        private static Dictionary<string, List<Material>> _assetMaterialCache = new Dictionary<string, List<Material>>();
        private static Dictionary<string, List<Material>> _defaultMaterialCache = new Dictionary<string, List<Material>>();
        private static Dictionary<string, Material> _customMaterialCache = new Dictionary<string, Material>();
        private static MapLoader _instance;
        public static int HighestObjectId;
        private static MapScriptBasicMaterial _invisibleMaterial;

        public static void Init()
        {
            _instance = SingletonFactory.CreateSingleton(_instance);
            EventManager.OnPreLoadScene += OnPreLoadScene;
            _invisibleMaterial = new MapScriptBasicMaterial
            {
                Shader = "Transparent",
                Color = new Color255(126, 255, 255, 80)
            };
        }

        private static void OnPreLoadScene(SceneName sceneName)
        {
            _instance.StopAllCoroutines();
        }

        public static int GetNextObjectId()
        {
            HighestObjectId++;
            return HighestObjectId;
        }

        public static void StartLoadObjects(List<MapScriptBaseObject> objects, bool editor = false)
        {
            _customMaterialCache.Clear();
            _defaultMaterialCache.Clear();
            _assetMaterialCache.Clear();
            IdToChildren.Clear();
            IdToMapObject.Clear();
            GoToMapObject.Clear();
            Daylight.Clear();
            _assetCache.Clear();
            Tags.Clear();
            HighestObjectId = 1;
            _instance.StartCoroutine(_instance.LoadObjectsCoroutine(objects, editor));
        }

        public static MapObject FindObjectFromCollider(Collider collider)
        {
            Transform current = collider.transform;
            while (true)
            {
                var go = current.gameObject;
                if (GoToMapObject.ContainsKey(go))
                    return GoToMapObject[go];
                if (current.parent == null)
                    return null;
                current = current.parent;
            }
        }

        public static MapObject LoadObject(MapScriptBaseObject scriptObject, bool editor)
        {
            GameObject go = null;
            if (scriptObject is MapScriptSceneObject)
                go = LoadSceneObject((MapScriptSceneObject)scriptObject, editor);
            MapObject mapObject = new MapObject(scriptObject.Parent, go, scriptObject);
            HighestObjectId = Mathf.Max(mapObject.ScriptObject.Id, HighestObjectId);
            if (IdToMapObject.ContainsKey(scriptObject.Id))
            {
                DebugConsole.Log("Map load error: map object with duplicate ID found (" + scriptObject.Id.ToString() + ")", true);
                return mapObject;
            }
            IdToMapObject.Add(scriptObject.Id, mapObject);
            GoToMapObject.Add(go, mapObject);
            if (scriptObject.Parent > 0)
            {
                if (IdToChildren.ContainsKey(scriptObject.Parent))
                    IdToChildren[scriptObject.Parent].Add(scriptObject.Id);
                else
                    IdToChildren.Add(scriptObject.Parent, new HashSet<int>() { scriptObject.Id });
            }
            SetTransform(mapObject);
            if (!editor && !scriptObject.Active)
            {
                mapObject.GameObject.SetActive(false);
            }
            if (!editor && scriptObject.Asset.StartsWith("Interact/Supply"))
                MinimapHandler.CreateMinimapIcon(mapObject.GameObject.transform, "Supply");
            return mapObject;
        }

        public static void SetParent(MapObject obj, MapObject parent)
        {
            if (IdToChildren.ContainsKey(obj.Parent) && IdToChildren[obj.Parent].Contains(obj.ScriptObject.Id))
                IdToChildren[obj.Parent].Remove(obj.ScriptObject.Id);
            if (parent == null)
            {
                obj.Parent = 0;
                obj.GameObject.transform.SetParent(null);
            }
            else if (obj != parent && parent.Parent != obj.ScriptObject.Id)
            {
                obj.Parent = parent.ScriptObject.Id;
                obj.GameObject.transform.SetParent(parent.GameObject.transform);
                if (IdToChildren.ContainsKey(obj.Parent))
                    IdToChildren[obj.Parent].Add(obj.ScriptObject.Id);
                else
                    IdToChildren.Add(obj.Parent, new HashSet<int>() { obj.ScriptObject.Id });
            }
        }

        public static void DeleteObject(MapObject obj)
        {
            int id = obj.ScriptObject.Id;
            DeleteObject(id);
        }

        public static void DeleteObject(int id)
        {
            var mapObject = IdToMapObject[id];
            IdToMapObject.Remove(id);
            GoToMapObject.Remove(mapObject.GameObject);
            if (IdToChildren.ContainsKey(id))
            {
                foreach (int child in new List<int>(IdToChildren[id]))
                {
                    if (IdToMapObject.ContainsKey(child))
                    {
                        SetParent(IdToMapObject[child], null);
                    }
                }
            }
            Destroy(mapObject.GameObject);
        }

        private IEnumerator LoadObjectsCoroutine(List<MapScriptBaseObject> objects, bool editor)
        {
            int count = 0;
            foreach (MapScriptBaseObject obj in objects)
            {
                LoadObject(obj, editor);
                if (count % 100 == 0 && SceneLoader.SceneName == SceneName.InGame)
                {
                    UIManager.LoadingMenu.UpdateLoading(0.9f + 0.1f * ((float)count / (float)objects.Count));
                    yield return new WaitForEndOfFrame();
                }
                count++;
            }
            if (!editor)
            {
                foreach (int id in IdToMapObject.Keys)
                {
                    var mapObject = IdToMapObject[id];
                    SetParent(mapObject);
                }
            }
            if (!editor)
                Batch();
            MapManager.MapLoaded = true;
        }

        private void Batch()
        {
            Dictionary<string, GameObject> roots = new Dictionary<string, GameObject>();
            Dictionary<string, List<GameObject>> shared = new Dictionary<string, List<GameObject>>();
            Dictionary<GameObject, Transform> oldParents = new Dictionary<GameObject, Transform>();
            Dictionary<string, int> hashCounts = new Dictionary<string, int>();
            foreach (int id in IdToMapObject.Keys)
            {
                var mapObject = IdToMapObject[id];
                if (mapObject.ScriptObject.Parent > 0 || !mapObject.ScriptObject.Static)
                    continue;
                var shader = ((MapScriptSceneObject)mapObject.ScriptObject).Material.Shader;
                if (MapObjectShader.IsLegacyShader(shader) || shader == MapObjectShader.Transparent)
                    continue;
                var position = mapObject.GameObject.transform.position;
                string positionHash = ((int)(position.x / 1000f)).ToString() + "-" + ((int)(position.y / 1000f)).ToString() + "-" + ((int)(position.z / 1000f)).ToString();
                foreach (MeshFilter filter in mapObject.GameObject.GetComponentsInChildren<MeshFilter>())
                {
                    if (filter.renderer.sharedMaterials.Length > 1)
                        continue;
                    string hash = filter.sharedMesh.GetHashCode().ToString();
                    hash += positionHash;
                    if (filter.renderer.enabled)
                        hash += filter.renderer.sharedMaterial.GetHashCode().ToString();
                    else
                        hash += "disabled";
                    if (!hashCounts.ContainsKey(hash))
                        hashCounts.Add(hash, 0);
                    int meshPerHash = 65000 / filter.sharedMesh.vertexCount;
                    hashCounts[hash] += 1;
                    hash += (hashCounts[hash] / meshPerHash).ToString();
                    if (!roots.ContainsKey(hash))
                    {
                        var go = new GameObject();
                        go.layer = PhysicsLayer.MapObjectEntities;
                        roots.Add(hash, go);
                        shared.Add(hash, new List<GameObject>());
                    }
                    shared[hash].Add(filter.gameObject);
                    oldParents.Add(filter.gameObject, filter.transform.parent);
                    filter.transform.SetParent(roots[hash].transform);
                }
            }
            foreach (string hash in roots.Keys)
                CombineMeshes(roots[hash]);
            foreach (string hash in shared.Keys)
            {
                foreach (GameObject go in shared[hash])
                    go.transform.SetParent(oldParents[go]);
            }
        }

        void CombineMeshes(GameObject obj)
        {
            MeshFilter[] meshFilters = obj.GetComponentsInChildren<MeshFilter>();
            CombineInstance[] combine = new CombineInstance[meshFilters.Length];
            if (meshFilters.Length == 0)
                return;
            bool rendererEnabled = meshFilters[0].renderer.enabled;
            for (int i = 0; i < meshFilters.Length; i++)
            {
                combine[i].mesh = meshFilters[i].sharedMesh;
                combine[i].transform = meshFilters[i].transform.localToWorldMatrix;
                meshFilters[i].renderer.enabled = false;
            }
            var meshFilter = obj.AddComponent<MeshFilter>();
            obj.AddComponent<MeshRenderer>();
            meshFilter.mesh = new Mesh();
            meshFilter.mesh.CombineMeshes(combine, true, true);
            if (rendererEnabled)
                obj.GetComponent<Renderer>().material = meshFilters[0].renderer.sharedMaterial;
            else
                obj.GetComponent<Renderer>().enabled = false;
        }

        public static void RegisterTag(string tag, MapObject obj)
        {
            if (!Tags.ContainsKey(tag))
                Tags.Add(tag, new List<MapObject>());
            Tags[tag].Add(obj);
        }

        private static GameObject LoadSceneObject(MapScriptSceneObject obj, bool editor)
        {
            GameObject go;
            if (obj.Asset == "None")
            {
                go = new GameObject();
            }
            else
                go = (GameObject)Instantiate(LoadAssetCached(obj.Asset.Split('/')[1]));
            if (editor)
                SetPhysics(go, MapObjectCollideMode.Physical, MapObjectCollideWith.MapEditor, obj.PhysicsMaterial);
            else
                SetPhysics(go, obj.CollideMode, obj.CollideWith, obj.PhysicsMaterial);
            SetMaterial(go, obj.Asset, obj.Material, obj.Visible, editor);
            return go;
        }

        private static void SetTransform(MapObject mapObject)
        {
            var go = mapObject.GameObject;
            var obj = mapObject.ScriptObject;
            Transform t = go.transform;
            go.isStatic = obj.Static;
            go.name = obj.Name;
            t.position = new Vector3(obj.PositionX, obj.PositionY, obj.PositionZ);
            t.rotation = Quaternion.Euler(obj.RotationX, obj.RotationY, obj.RotationZ);
            var localScale = t.localScale;
            mapObject.BaseScale = new Vector3(localScale.x, localScale.y, localScale.z);
            t.localScale = new Vector3(mapObject.BaseScale.x * obj.ScaleX, mapObject.BaseScale.y * obj.ScaleY, mapObject.BaseScale.z * obj.ScaleZ);
        }

        public static void SetParent(MapObject mapObject)
        {
            var go = mapObject.GameObject;
            var obj = mapObject.ScriptObject;
            Transform t = go.transform;
            if (obj.Parent > 0)
            {
                if (IdToMapObject.ContainsKey(obj.Parent))
                    t.SetParent(IdToMapObject[obj.Parent].GameObject.transform);
                else
                    DebugConsole.Log("Map load error: object parent id not found (" + obj.Parent.ToString() + ")", true);
            }
        }

        public static void SetMaterial(GameObject go, string asset, MapScriptBaseMaterial material, bool visible, bool editor)
        {
            if (asset == "None")
                return;
            var allRenderers = go.GetComponentsInChildren<Renderer>();
            var renderers = new List<Renderer>();
            if (!visible && editor)
            {
                visible = true;
                material = _invisibleMaterial;
            }
            foreach (var r in allRenderers)
            {
                if (r.name != "OutlineGizmo")
                    renderers.Add(r);
            }
            if (!_assetMaterialCache.ContainsKey(asset))
            {
                var assetMats = new List<Material>();
                foreach (Renderer renderer in renderers)
                {
                    renderer.material.color = Color.white;
                    assetMats.Add(renderer.material);
                }
                _assetMaterialCache.Add(asset, assetMats);
            }
            if (material.Shader == MapObjectShader.Default.ToString())
            {
                string materialHash = asset + material.Serialize();
                if (!_defaultMaterialCache.ContainsKey(materialHash))
                {
                    var assetMats = _assetMaterialCache[asset];
                    var defaultMats = new List<Material>();
                    foreach (var assetMat in assetMats)
                    {
                        var mat = new Material(assetMat);
                        mat.color = material.Color.ToColor();
                        defaultMats.Add(mat);
                    }
                    _defaultMaterialCache.Add(materialHash, defaultMats);
                }
                var mats = _defaultMaterialCache[materialHash];
                for (int i = 0; i < renderers.Count; i++)
                {
                    renderers[i].material = mats[i];
                    renderers[i].enabled = visible;
                }
            }
            else
            {
                Material mat = null;
                string materialHash = material.Serialize();
                if (_customMaterialCache.ContainsKey(materialHash))
                    mat = _customMaterialCache[materialHash];
                else
                {
                    if (MapObjectShader.IsLegacyShader(material.Shader))
                    {
                        var legacy = (MapScriptLegacyMaterial)material;
                        mat = (Material)Instantiate(LoadAssetCached(legacy.Shader));
                        mat.SetColor("_TintColor", legacy.Color.ToColor());
                        mat.mainTextureScale = new Vector2(legacy.Tiling.x, legacy.Tiling.y);
                    }
                    else if (typeof(MapScriptBasicMaterial).IsAssignableFrom(material.GetType()))
                    {
                        var basic = (MapScriptBasicMaterial)material;
                        mat = (Material)Instantiate(LoadAssetCached(basic.Shader + "Material"));
                        if (basic.Texture != "Misc/None" && basic.Texture != "None")
                        {
                            string[] textureArr = basic.Texture.Split('/');
                            string category = textureArr[0];
                            string textureName = textureArr[1];
                            bool legacy = category == "Legacy";
                            if (BuiltinMapTextures.AllTextures.ContainsKey(textureName) && (!legacy || BuiltinMapTextures.LegacyTexturePaths.ContainsKey(textureName)))
                            {
                                var reference = BuiltinMapTextures.AllTextures[textureName];
                                Texture2D texture;
                                if (legacy)
                                {
                                    string texturePath = BuiltinMapTextures.LegacyTexturePaths[textureName];
                                    string[] pathArr = texturePath.Split('/');
                                    texture = (Texture2D)LoadAssetCached(pathArr[pathArr.Length - 1]);
                                }
                                else
                                    texture = (Texture2D)LoadAssetCached(textureName + "Texture");
                                mat.mainTexture = texture;
                                mat.mainTextureScale = new Vector2(basic.Tiling.x * reference.Tiling.x, basic.Tiling.y * reference.Tiling.y);
                                mat.mainTextureOffset = basic.Offset;

                            }
                        }
                        mat.color = basic.Color.ToColor();
                        if (material is MapScriptReflectiveMaterial)
                        {
                            var reflective = (MapScriptReflectiveMaterial)material;
                            mat.SetColor("_ReflectColor", reflective.ReflectColor.ToColor());
                        }
                    }
                    _customMaterialCache.Add(materialHash, mat);
                }
                foreach (Renderer renderer in renderers)
                {
                    if (mat != null)
                        renderer.material = mat;
                    renderer.enabled = visible;
                }
            }
        }

        private static void SetPhysics(GameObject go, string collideMode, string collideWith, string physicsMaterial)
        {
            PhysicMaterial material = null;
            if (physicsMaterial != "Default")
                material = (PhysicMaterial)LoadAssetCached(physicsMaterial);
            int layer = GetColliderLayer(collideWith);
            foreach (Collider c in go.GetComponentsInChildren<Collider>())
            {
                c.isTrigger = collideMode == MapObjectCollideMode.Region;
                c.enabled = collideMode != MapObjectCollideMode.None;
                if (material != null)
                    c.material = material;
                c.gameObject.layer = layer;
            }
            go.layer = layer;
        }

        public static void SetCollider(Collider c, string collideMode, string collideWith)
        {
            c.isTrigger = collideMode == MapObjectCollideMode.Region;
            c.enabled = collideMode != MapObjectCollideMode.None;
            c.gameObject.layer = GetColliderLayer(collideWith);
        }

        private static int GetColliderLayer(string collideWith)
        {
            int layer = 0;
            if (collideWith == MapObjectCollideWith.All)
                layer = PhysicsLayer.MapObjectAll;
            else if (collideWith == MapObjectCollideWith.MapObjects)
                layer = PhysicsLayer.MapObjectMapObjects;
            else if (collideWith == MapObjectCollideWith.Characters)
                layer = PhysicsLayer.MapObjectCharacters;
            else if (collideWith == MapObjectCollideWith.Projectiles)
                layer = PhysicsLayer.MapObjectProjectiles;
            else if (collideWith == MapObjectCollideWith.Entities)
                layer = PhysicsLayer.MapObjectEntities;
            else if (collideWith == MapObjectCollideWith.MapEditor)
                layer = PhysicsLayer.MapEditorObject;
            else if (collideWith == MapObjectCollideWith.Hitboxes)
                layer = PhysicsLayer.Hurtbox;
            return layer;
        }

        private static Object LoadAssetCached(string asset)
        {
            if (!_assetCache.ContainsKey(asset))
                _assetCache.Add(asset, AssetBundleManager.LoadAsset(asset));
            return _assetCache[asset];
        }

       
    }

    static class MapObjectShader
    {
        public static string Default = "Default";
        public static string Basic = "Basic";
        public static string Transparent = "Transparent";
        public static string Reflective = "Reflective";
        public static string OldBombExplode = "OldBombExplodeMat";
        public static string CannonRegionMat = "CannonRegionMat";
        public static string BombTexMat = "BombTexMat";
        public static string Smoke1Mat = "Smoke1Mat";

        public static bool IsLegacyShader(string shader)
        {
            return shader == OldBombExplode || shader == CannonRegionMat || shader == BombTexMat || shader == Smoke1Mat;
        }
    }

    static class MapObjectCollideMode
    {
        public static string Physical = "Physical";
        public static string Region = "Region";
        public static string None = "None";
    }

    static class MapObjectCollideWith
    {
        public static string All = "All";
        public static string MapObjects = "MapObjects";
        public static string Characters = "Characters";
        public static string Projectiles = "Projectiles";
        public static string Entities = "Entities";
        public static string Hitboxes = "Hitboxes";
        public static string MapEditor = "MapEditor";
    }

    static class MapObjectPhysicsMaterial
    {
        public static string Default = "Default";
        public static string Ice = "IceMaterial";
    }
}
