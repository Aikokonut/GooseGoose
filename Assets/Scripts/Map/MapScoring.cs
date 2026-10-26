using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Canonical puzzle scoring (enclose.horse rules).
/// Enclosed pen = cells reachable from the horse when the horse cannot reach any edge.
/// Per enclosed cell base +1. Cherry (BonusA) +3 extra. Golden apple (BonusB) +10 extra.
/// Bee −5 extra. Totals: grass 1, cherry 4, apple 11, bee −4. Horse cell: +1 only.
/// </summary>
public struct MapScoreBreakdown
{
    public bool valid;
    public string message;
    public int totalScore;
    public int enclosedCellCount;
    public int bonusACount;
    public int bonusAScore;
    public int bonusBCount;
    public int bonusBScore;
    public int beeCount;
    public int beePenalty;
    public Vector2Int[] wallPositions;
    public bool[] enclosedMask;
}

public static class MapScoring
{
    public const int PointsPerEnclosedCell = 1;
    /// <summary>Cherry extra (tile total 4 = 1+3).</summary>
    public const int BonusAExtra = 3;
    /// <summary>Golden apple extra (tile total 11 = 1+10).</summary>
    public const int BonusBExtra = 10;
    /// <summary>Bee extra (tile total −4 = 1−5).</summary>
    public const int BeePenalty = -5;

    static readonly Vector2Int[] Dirs =
    {
        new Vector2Int(0, 1),
        new Vector2Int(1, 0),
        new Vector2Int(0, -1),
        new Vector2Int(-1, 0)
    };

    /// <summary>
    /// Walls may only be placed on ordinary grass (edge allowed). Bonuses, bees, portals,
    /// water, and the horse cannot be walled — matches enclose.horse.
    /// </summary>
    public static bool IsWallPlaceable(CellType t)
    {
        return t == CellType.Grass;
    }

    public static bool CountsAsEnclosedCell(CellType t)
    {
        return t != CellType.Water && t != CellType.Wall;
    }

    /// <summary>Point weight of a terrain cell when enclosed (base + extra).</summary>
    public static int CellWeight(CellType t, bool isHorseCell)
    {
        if (isHorseCell)
            return PointsPerEnclosedCell;
        if (!CountsAsEnclosedCell(t))
            return 0;
        int v = PointsPerEnclosedCell;
        if (t == CellType.BonusA) v += BonusAExtra;
        else if (t == CellType.BonusB) v += BonusBExtra;
        else if (t == CellType.Bee) v += BeePenalty;
        return v;
    }

    /// <summary>
    /// Score a map that already has walls applied. Verifies horse is trapped.
    /// If solutionWalls is provided, budget is checked against that list only
    /// (editor-painted terrain walls do not consume the player budget).
    /// </summary>
    public static MapScoreBreakdown Recalculate(MapData map, int wallBudget, Vector2Int[] solutionWalls = null)
    {
        MapScoreBreakdown result = new MapScoreBreakdown
        {
            valid = false,
            wallPositions = System.Array.Empty<Vector2Int>(),
            enclosedMask = null,
            message = string.Empty
        };

        if (map == null || map.cells == null)
        {
            result.message = "Map is null.";
            return result;
        }

        List<Vector2Int> walls = new List<Vector2Int>();
        if (solutionWalls != null)
        {
            for (int i = 0; i < solutionWalls.Length; i++)
                walls.Add(solutionWalls[i]);
        }
        else
        {
            for (int y = 0; y < map.height; y++)
            {
                for (int x = 0; x < map.width; x++)
                {
                    if (map.Get(x, y) == CellType.Wall)
                        walls.Add(new Vector2Int(x, y));
                }
            }
        }

        result.wallPositions = walls.ToArray();
        if (walls.Count > wallBudget)
        {
            result.message = "Wall budget exceeded (" + walls.Count + " > " + wallBudget + ").";
            return result;
        }

        if (MapPathfinder.CanEscape(map))
        {
            result.message = "Horse can still escape — not a valid enclosure.";
            return result;
        }

        bool[] mask = BuildPenMask(map);
        result.enclosedMask = mask;

        int enclosed = 0;
        int bonusA = 0;
        int bonusB = 0;
        int bees = 0;
        int total = 0;
        int horseIdx = map.Index(map.horse.x, map.horse.y);

        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i])
                continue;

            bool isHorse = i == horseIdx;
            CellType t = isHorse ? CellType.Horse : map.cells[i];
            if (!CountsAsEnclosedCell(t) && t != CellType.Horse)
                continue;

            enclosed++;
            total += CellWeight(t, isHorse);
            if (t == CellType.BonusA) bonusA++;
            else if (t == CellType.BonusB) bonusB++;
            else if (t == CellType.Bee) bees++;
        }

        result.enclosedCellCount = enclosed;
        result.bonusACount = bonusA;
        result.bonusAScore = bonusA * BonusAExtra;
        result.bonusBCount = bonusB;
        result.bonusBScore = bonusB * BonusBExtra;
        result.beeCount = bees;
        result.beePenalty = bees * BeePenalty;
        result.totalScore = total;
        result.valid = true;
        result.message = "OK";
        return result;
    }

    /// <summary>
    /// Apply wall positions onto a clone of baseMap (base terrain must not already rely on those walls).
    /// </summary>
    public static MapData ApplyWalls(MapData baseMap, Vector2Int[] walls)
    {
        MapData copy = baseMap.Clone();
        if (walls == null)
            return copy;

        for (int i = 0; i < walls.Length; i++)
        {
            Vector2Int p = walls[i];
            if (!copy.InBounds(p))
                continue;
            if (p == copy.horse)
                continue;
            CellType t = copy.Get(p);
            if (!IsWallPlaceable(t) && t != CellType.Wall)
                continue;
            copy.Set(p, CellType.Wall);
        }

        return copy;
    }

    public static bool[] BuildPenMask(MapData map)
    {
        int n = map.width * map.height;
        bool[] mask = new bool[n];
        if (!map.InBounds(map.horse))
            return mask;

        bool[] visited = new bool[n];
        Queue<Vector2Int> q = new Queue<Vector2Int>();
        int start = map.Index(map.horse.x, map.horse.y);
        visited[start] = true;
        q.Enqueue(map.horse);

        while (q.Count > 0)
        {
            Vector2Int cur = q.Dequeue();
            mask[map.Index(cur.x, cur.y)] = true;

            if (map.TryGetPortalExit(cur, out Vector2Int exit)
                && map.InBounds(exit)
                && map.IsPassable(map.Get(exit)))
            {
                int exitIdx = map.Index(exit.x, exit.y);
                if (!visited[exitIdx])
                {
                    visited[exitIdx] = true;
                    q.Enqueue(exit);
                }
            }

            for (int d = 0; d < 4; d++)
            {
                Vector2Int next = cur + Dirs[d];
                if (!map.InBounds(next) || !map.IsPassable(map.Get(next)))
                    continue;
                int nextIdx = map.Index(next.x, next.y);
                if (visited[nextIdx])
                    continue;
                visited[nextIdx] = true;
                q.Enqueue(next);
            }
        }

        return mask;
    }

    public static int ScoreFromMask(MapData map, bool[] mask)
    {
        if (mask == null)
            return int.MinValue;

        int total = 0;
        int horseIdx = map.Index(map.horse.x, map.horse.y);
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i])
                continue;
            bool isHorse = i == horseIdx;
            CellType t = isHorse ? CellType.Horse : map.cells[i];
            total += CellWeight(t, isHorse);
        }
        return total;
    }
}
