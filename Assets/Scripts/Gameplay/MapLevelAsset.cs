using System;
using UnityEngine;

/// <summary>
/// Serializable map payload used by the level pipeline and editor Play Test.
/// Survives domain reload because it lives as a project asset.
/// </summary>
[CreateAssetMenu(fileName = "MapLevel", menuName = "GooseGoose/Map Level")]
public class MapLevelAsset : ScriptableObject
{
    public int width;
    public int height;
    public int seed;
    public int wallBudget;
    public int horseX;
    public int horseY;
    public int[] cells;
    public int[] portalFlat;

    [Tooltip("When true, LevelBootstrap prefers this asset over normal levels for one Play Mode session.")]
    public bool playTestActive;

    public void CopyFrom(MapData map, int budget)
    {
        if (map == null)
            throw new ArgumentNullException("map");

        width = map.width;
        height = map.height;
        seed = map.seed;
        wallBudget = budget;
        horseX = map.horse.x;
        horseY = map.horse.y;

        cells = new int[map.cells.Length];
        for (int i = 0; i < map.cells.Length; i++)
            cells[i] = (int)map.cells[i];

        int n = map.portals != null ? map.portals.Length : 0;
        portalFlat = new int[n * 4];
        for (int i = 0; i < n; i++)
        {
            portalFlat[i * 4] = map.portals[i].a.x;
            portalFlat[i * 4 + 1] = map.portals[i].a.y;
            portalFlat[i * 4 + 2] = map.portals[i].b.x;
            portalFlat[i * 4 + 3] = map.portals[i].b.y;
        }
    }

    public MapData ToMapData()
    {
        if (width < 1 || height < 1 || cells == null || cells.Length != width * height)
            return null;

        MapData map = new MapData(width, height, seed);
        map.wallBudget = wallBudget;
        map.horse = new Vector2Int(horseX, horseY);

        for (int i = 0; i < cells.Length; i++)
            map.cells[i] = (CellType)cells[i];

        if (portalFlat == null || portalFlat.Length < 4)
        {
            map.portals = Array.Empty<PortalLink>();
            return map;
        }

        int pairs = portalFlat.Length / 4;
        map.portals = new PortalLink[pairs];
        for (int i = 0; i < pairs; i++)
        {
            map.portals[i] = new PortalLink(
                new Vector2Int(portalFlat[i * 4], portalFlat[i * 4 + 1]),
                new Vector2Int(portalFlat[i * 4 + 2], portalFlat[i * 4 + 3]));
        }

        return map;
    }
}
