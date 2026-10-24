public struct MapValidationResult
{
    public bool ok;
    public string message;
}

public static class MapValidator
{
    /// <summary>
    /// Structural + escape checks (does not require a playable wall cut).
    /// </summary>
    public static MapValidationResult Validate(MapData map)
    {
        if (map == null || map.cells == null)
            return Fail("Map is null.");

        if (map.width < 3 || map.height < 3)
            return Fail("Map too small (min 3x3).");

        if (map.cells.Length != map.width * map.height)
            return Fail("Cell array size mismatch.");

        if (!map.InBounds(map.horse))
            return Fail("Horse position out of bounds.");

        CellType horseCell = map.Get(map.horse);
        if (horseCell != CellType.Horse && horseCell != CellType.Grass
            && horseCell != CellType.BonusA && horseCell != CellType.BonusB)
            return Fail("Horse sits on impassable cell.");

        if (map.portals != null)
        {
            for (int i = 0; i < map.portals.Length; i++)
            {
                PortalLink p = map.portals[i];
                if (!map.InBounds(p.a) || !map.InBounds(p.b))
                    return Fail("Portal pair out of bounds.");
                if (p.a == p.b)
                    return Fail("Portal pair points to itself.");
                if (map.Get(p.a) != CellType.Portal || map.Get(p.b) != CellType.Portal)
                    return Fail("Portal cells missing.");
            }
        }

        if (!MapPathfinder.CanEscape(map))
            return Fail("Trapped: horse has no escape path to the boundary.");

        return new MapValidationResult { ok = true, message = "OK — horse can escape." };
    }

    /// <summary>
    /// Full playability: escape + non-trivial enclosure cut within wall budget.
    /// </summary>
    public static MapValidationResult ValidateForPlay(MapData map, int wallBudget)
    {
        MapValidationResult basic = Validate(map);
        if (!basic.ok)
            return basic;

        int minimumWalls = MapGenerator.GetMinimumWallsToEnclose(map);
        if (!MapGenerator.IsPlayableEnclosureCut(minimumWalls, wallBudget))
        {
            if (minimumWalls == int.MaxValue)
                return Fail("No legal enclosure cut using placeable grass cells.");
            if (wallBudget < 3)
                return Fail("Wall budget too low for a useful puzzle (need at least 3).");
            if (minimumWalls < 3)
                return Fail("Too easy: minimum walls " + minimumWalls + " (need at least 3).");
            return Fail("Cannot enclose within budget. Minimum walls: " + minimumWalls + "; budget: " + wallBudget + ".");
        }

        return new MapValidationResult
        {
            ok = true,
            message = "OK — playable. Minimum walls: " + minimumWalls + "; budget: " + wallBudget + "."
        };
    }

    static MapValidationResult Fail(string msg)
    {
        return new MapValidationResult { ok = false, message = msg };
    }
}
