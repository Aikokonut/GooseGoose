using UnityEngine;

/// <summary>
/// Runtime level selection. Play Test writes a temporary Resources asset with
/// playTestActive=true so the override survives domain reload.
/// </summary>
public static class LevelSession
{
    public const string PlayTestResourceName = "PlayTestMap";
    public const string PlayTestAssetPath = "Assets/Resources/PlayTestMap.asset";

    /// <summary>
    /// Resolves the map for this play session. Prefers an active play-test asset.
    /// </summary>
    public static bool TryResolve(MapLevelAsset defaultLevel, out MapData map, out string error)
    {
        map = null;
        error = null;

        MapLevelAsset chosen = null;

        MapLevelAsset playTest = Resources.Load<MapLevelAsset>(PlayTestResourceName);
        if (playTest != null && playTest.playTestActive)
            chosen = playTest;

        if (chosen == null)
            chosen = defaultLevel;

        if (chosen == null)
        {
            error = "No level asset assigned and no active Play Test map.";
            return false;
        }

        map = chosen.ToMapData();
        if (map == null)
        {
            error = "Level asset '" + chosen.name + "' could not be converted to MapData (corrupt size/cells).";
            return false;
        }

        int budget = map.wallBudget > 0 ? map.wallBudget : chosen.wallBudget;
        MapValidationResult validation = MapValidator.ValidateForPlay(map, budget);
        if (!validation.ok)
        {
            error = "Level failed validation: " + validation.message;
            map = null;
            return false;
        }

        return true;
    }

    public static bool IsPlayTestActive()
    {
        MapLevelAsset playTest = Resources.Load<MapLevelAsset>(PlayTestResourceName);
        return playTest != null && playTest.playTestActive;
    }
}
