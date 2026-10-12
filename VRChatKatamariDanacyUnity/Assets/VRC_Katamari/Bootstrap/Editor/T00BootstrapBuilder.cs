using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Components;

public static class T00BootstrapBuilder
{
    [MenuItem("VRC Katamari/T00/Create Bootstrap Scene")]
    public static void Create()
    {
        const string folder = "Assets/VRC_Katamari/Bootstrap";
        const string path = folder + "/T00_Bootstrap.unity";
        Directory.CreateDirectory(folder);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var world = new GameObject("VRCWorld");
        var descriptor = world.AddComponent<VRCSceneDescriptor>();
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Ground";
        floor.transform.position = Vector3.zero;
        floor.transform.localScale = new Vector3(10f, 1f, 10f);
        var spawn = new GameObject("Spawn");
        spawn.transform.position = new Vector3(0f, 1f, -3f);
        descriptor.spawns = new Transform[] { spawn.transform };
        var camera = new GameObject("Reference Camera");
        camera.transform.position = new Vector3(0f, 4f, -8f);
        camera.transform.rotation = Quaternion.Euler(20f, 0f, 0f);
        camera.AddComponent<Camera>();
        descriptor.ReferenceCamera = camera;
        if (!EditorSceneManager.SaveScene(scene, path)) throw new System.Exception("Failed to save T00 scene");
        AssetDatabase.SaveAssets();
        Debug.Log("T00_BOOTSTRAP_SCENE_CREATED " + path);
    }
}