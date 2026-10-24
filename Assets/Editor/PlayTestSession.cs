using UnityEditor;
using UnityEngine;

/// <summary>
/// Writes the temporary Play Test map asset and clears it when Play Mode ends.
/// Does not touch permanent level assets.
/// </summary>
[InitializeOnLoad]
public static class PlayTestSession
{
    const string StatusKey = "GooseGoose.PlayTest.Armed";

    static PlayTestSession()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    public static bool TryArm(MapData map, int wallBudget, out string error)
    {
        error = null;
        if (map == null)
        {
            error = "No map generated.";
            return false;
        }

        MapValidationResult validation = MapValidator.ValidateForPlay(map, map.wallBudget);
        if (!validation.ok)
        {
            error = "Play Test blocked: " + validation.message;
            return false;
        }

        EnsureResourcesFolder();

        MapLevelAsset asset = AssetDatabase.LoadAssetAtPath<MapLevelAsset>(LevelSession.PlayTestAssetPath);
        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance<MapLevelAsset>();
            AssetDatabase.CreateAsset(asset, LevelSession.PlayTestAssetPath);
        }

        asset.CopyFrom(map, wallBudget);
        asset.playTestActive = true;
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Confirm Resources.Load will see it after domain reload
        MapLevelAsset check = AssetDatabase.LoadAssetAtPath<MapLevelAsset>(LevelSession.PlayTestAssetPath);
        if (check == null || !check.playTestActive || check.ToMapData() == null)
        {
            error = "Failed to write Play Test asset at " + LevelSession.PlayTestAssetPath;
            return false;
        }

        EditorPrefs.SetBool(StatusKey, true);
        return true;
    }

    public static void Disarm()
    {
        MapLevelAsset asset = AssetDatabase.LoadAssetAtPath<MapLevelAsset>(LevelSession.PlayTestAssetPath);
        if (asset != null && asset.playTestActive)
        {
            asset.playTestActive = false;
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        EditorPrefs.SetBool(StatusKey, false);
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            if (EditorPrefs.GetBool(StatusKey, false))
                Disarm();
        }
    }

    static void EnsureResourcesFolder()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            AssetDatabase.CreateFolder("Assets", "Resources");
    }

    static bool _batchVerify;
    static bool _batchOk;
    static string _batchMessage = string.Empty;

    /// <summary>
    /// Arms a generated map, enters Play Mode, asserts LevelBootstrap loaded it, then exits.
    /// Unity: -batchmode -executeMethod PlayTestSession.BatchVerifyPlayTest
    /// </summary>
    public static void BatchVerifyPlayTest()
    {
        _batchVerify = true;
        _batchOk = false;
        _batchMessage = string.Empty;

        MapGenSettings settings = new MapGenSettings
        {
            width = 10,
            height = 10,
            seed = 42,
            wallBudget = 10,
            waterDensity = 0.2f,
            bonusCount = 2,
            portalPairs = 1
        };

        MapData map = MapGenerator.Generate(settings);
        map.wallBudget = settings.wallBudget;

        string error;
        if (!TryArm(map, settings.wallBudget, out error))
        {
            Debug.LogError("[PlayTestSession] BATCH FAIL arm: " + error);
            EditorApplication.Exit(1);
            return;
        }

        // Open gameplay scene before play
        string scenePath = "Assets/Scenes/SampleScene.unity";
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath);

        EditorApplication.playModeStateChanged += OnBatchPlayModeStateChanged;
        EditorApplication.EnterPlaymode();
    }

    static void OnBatchPlayModeStateChanged(PlayModeStateChange state)
    {
        if (!_batchVerify)
            return;

        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            EditorApplication.delayCall += EvaluateBatchInPlayMode;
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            EditorApplication.playModeStateChanged -= OnBatchPlayModeStateChanged;
            Disarm();
            _batchVerify = false;
            if (_batchOk)
            {
                Debug.Log("[PlayTestSession] BATCH OK: " + _batchMessage);
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError("[PlayTestSession] BATCH FAIL: " + _batchMessage);
                EditorApplication.Exit(1);
            }
        }
    }

    static void EvaluateBatchInPlayMode()
    {
        LevelBootstrap boot = Object.FindFirstObjectByType<LevelBootstrap>();
        if (boot == null)
        {
            // AutoBoot runs AfterSceneLoad; give Start one more frame
            EditorApplication.delayCall += EvaluateBatchInPlayModeSecondTry;
            return;
        }

        FinishBatchFromBoot(boot);
    }

    static void EvaluateBatchInPlayModeSecondTry()
    {
        LevelBootstrap boot = Object.FindFirstObjectByType<LevelBootstrap>();
        if (boot == null)
        {
            _batchOk = false;
            _batchMessage = "LevelBootstrap missing after EnteredPlayMode.";
            EditorApplication.isPlaying = false;
            return;
        }

        FinishBatchFromBoot(boot);
    }

    static void FinishBatchFromBoot(LevelBootstrap boot)
    {
        if (!string.IsNullOrEmpty(boot.BootError))
        {
            _batchOk = false;
            _batchMessage = boot.BootError;
            EditorApplication.isPlaying = false;
            return;
        }

        if (boot.Game == null || boot.Game.Map == null)
        {
            _batchOk = false;
            _batchMessage = "Game map was not loaded.";
            EditorApplication.isPlaying = false;
            return;
        }

        if (!LevelSession.IsPlayTestActive())
        {
            _batchOk = false;
            _batchMessage = "Play Test flag was not active during load.";
            EditorApplication.isPlaying = false;
            return;
        }

        _batchOk = true;
        _batchMessage = "seed=" + boot.Game.Map.seed +
                        " size=" + boot.Game.Map.width + "x" + boot.Game.Map.height +
                        " walls=" + boot.Game.WallsLeft +
                        " horse=" + boot.Game.Map.horse;
        EditorApplication.isPlaying = false;
    }

    [MenuItem("GooseGoose/Verify Play Test Boot")]
    public static void VerifyPlayTestBoot()
    {
        BatchVerifyPlayTest();
    }
}
