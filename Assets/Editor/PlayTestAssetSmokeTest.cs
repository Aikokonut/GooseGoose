using UnityEditor;
using UnityEngine;

/// <summary>
/// Verifies Play Test asset write/load without requiring a free Unity lock for EnterPlaymode.
/// Full Play Mode boot is covered by PlayTestSession.BatchVerifyPlayTest when the project is free.
/// </summary>
public static class PlayTestAssetSmokeTest
{
    [MenuItem("GooseGoose/Smoke Test Play Test Asset")]
    public static void Run()
    {
        MapGenSettings settings = new MapGenSettings
        {
            width = 12,
            height = 12,
            seed = 7,
            wallBudget = 14,
            waterDensity = 0.22f,
            bonusCount = 2,
            portalPairs = 1
        };

        MapData map = MapGenerator.Generate(settings);
        map.wallBudget = settings.wallBudget;

        string error;
        if (!PlayTestSession.TryArm(map, settings.wallBudget, out error))
        {
            Debug.LogError("[PlayTestAssetSmokeTest] FAIL: " + error);
            return;
        }

        MapLevelAsset asset = AssetDatabase.LoadAssetAtPath<MapLevelAsset>(LevelSession.PlayTestAssetPath);
        if (asset == null || !asset.playTestActive)
        {
            Debug.LogError("[PlayTestAssetSmokeTest] FAIL: asset missing or inactive.");
            PlayTestSession.Disarm();
            return;
        }

        MapData loaded = asset.ToMapData();
        if (loaded == null)
        {
            Debug.LogError("[PlayTestAssetSmokeTest] FAIL: ToMapData returned null.");
            PlayTestSession.Disarm();
            return;
        }

        if (loaded.width != map.width || loaded.height != map.height || loaded.wallBudget != map.wallBudget)
        {
            Debug.LogError("[PlayTestAssetSmokeTest] FAIL: size/budget mismatch.");
            PlayTestSession.Disarm();
            return;
        }

        if (loaded.horse != map.horse || loaded.cells.Length != map.cells.Length)
        {
            Debug.LogError("[PlayTestAssetSmokeTest] FAIL: horse/cells mismatch.");
            PlayTestSession.Disarm();
            return;
        }

        for (int i = 0; i < map.cells.Length; i++)
        {
            if (loaded.cells[i] != map.cells[i])
            {
                Debug.LogError("[PlayTestAssetSmokeTest] FAIL: cell " + i + " mismatch.");
                PlayTestSession.Disarm();
                return;
            }
        }

        MapData resolved;
        string resolveError;
        if (!LevelSession.TryResolve(null, out resolved, out resolveError))
        {
            Debug.LogError("[PlayTestAssetSmokeTest] FAIL resolve: " + resolveError);
            PlayTestSession.Disarm();
            return;
        }

        // Leave disarmed so normal play is restored after smoke test.
        PlayTestSession.Disarm();
        Debug.Log("[PlayTestAssetSmokeTest] OK — Play Test asset round-trip + LevelSession resolve succeeded.");
    }
}
