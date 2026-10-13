using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;
using UdonSharp;
using UdonSharpEditor;

public static class T04PlatformLabBuilder
{
    private const string Folder = "Assets/VRC_Katamari/Prototypes/Platform";

    [MenuItem("VRC Katamari/T04/Create Platform Lab")]
    public static void Create()
    {
        Directory.CreateDirectory(Folder);
        EnsureProgramAsset<T04PlatformDriver>();
        EnsureProgramAsset<T04FollowRig>();
        EnsureProgramAsset<T04VisualRoll>();
        EnsureProgramAsset<T04StationControl>();
        UdonSharpProgramAsset.CompileAllCsPrograms();
        if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
            throw new System.InvalidOperationException("T04 UdonSharp compilation failed; scene was not created.");

        string path = Folder + "/T04_PlatformLab.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject world = new GameObject("VRCWorld");
        VRCSceneDescriptor descriptor = world.AddComponent<VRCSceneDescriptor>();

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(10f, 1f, 10f);

        GameObject spawn = new GameObject("Spawn");
        spawn.transform.position = new Vector3(0f, 1f, -4f);
        descriptor.spawns = new Transform[] { spawn.transform };

        GameObject carrier = new GameObject("StationCarrier");
        carrier.transform.position = new Vector3(0f, 0f, 1f);
        GameObject stationObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        stationObject.name = "Station";
        stationObject.transform.SetParent(carrier.transform, false);
        stationObject.transform.localPosition = new Vector3(0f, 0.5f, 0f);
        stationObject.transform.localScale = new Vector3(2f, 1f, 2f);
        VRC.SDK3.Components.VRCStation station = stationObject.AddComponent<VRC.SDK3.Components.VRCStation>();
        station.disableStationExit = true;
        station.PlayerMobility = VRC.SDKBase.VRCStation.Mobility.Mobile;

        T04PlatformDriver driver = stationObject.AddUdonSharpComponent<T04PlatformDriver>();
        driver.station = station;
        driver.carrier = carrier.transform;

        GameObject follow = new GameObject("FollowRig");
        follow.transform.position = new Vector3(0f, 2.2f, -1.5f);
        T04FollowRig followBehaviour = follow.AddUdonSharpComponent<T04FollowRig>();
        driver.followRig = follow.transform;

        GameObject visualRoll = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visualRoll.name = "VisualRoll";
        visualRoll.transform.position = new Vector3(0f, 0.8f, 3f);
        visualRoll.transform.localScale = Vector3.one;
        visualRoll.AddUdonSharpComponent<T04VisualRoll>();
        driver.visualRoll = visualRoll.transform;

        GameObject exitButton = GameObject.CreatePrimitive(PrimitiveType.Cube);
        exitButton.name = "WorldExitButton";
        exitButton.transform.position = new Vector3(2f, 1f, 1f);
        exitButton.transform.localScale = new Vector3(0.5f, 0.5f, 0.2f);
        T04StationControl exitControl = exitButton.AddUdonSharpComponent<T04StationControl>();
        exitControl.driver = driver;

        GameObject cameraObject = new GameObject("Reference Camera");
        cameraObject.transform.position = new Vector3(0f, 5f, -8f);
        cameraObject.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
        cameraObject.AddComponent<Camera>();
        descriptor.ReferenceCamera = cameraObject;

        if (!EditorSceneManager.SaveScene(scene, path))
            throw new System.Exception("T04 scene save failed");
        AssetDatabase.SaveAssets();
        Debug.Log("[T04] SCENE_CREATED " + path);
    }

    private static void EnsureProgramAsset<T>() where T : UdonSharpBehaviour
    {
        string scriptPath = Folder + "/" + typeof(T).Name + ".cs";
        string assetPath = Folder + "/" + typeof(T).Name + ".asset";
        UdonSharpProgramAsset program = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath);
        if (program == null)
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            if (script == null) throw new System.InvalidOperationException("Missing UdonSharp script: " + scriptPath);
            program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            program.sourceCsScript = script;
            AssetDatabase.CreateAsset(program, assetPath);
            Debug.Log("[T04] PROGRAM_ASSET_CREATED " + assetPath);
        }
    }
}
