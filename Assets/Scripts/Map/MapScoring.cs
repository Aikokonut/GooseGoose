using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Canonical puzzle scoring. Solver and gameplay must use this only.
/// Enclosed pen = reachable passable cells from horse when horse cannot escape.
/// Per enclosed cell: +1. BonusA +5, BonusB +10, Bee -3 (extra). Horse cell: +1 only.
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
    public const int BonusAExtra = 5;
    public const int BonusBExtra = 10;
    public const int BeePenalty = -3;

    static readonly Vector2Int[] Dirs =
    {
        new Vector2Int(0, 1),
        new Vector2Int(1, 0),
        new Vector2Int(0, -1),
        new Vector2Int(-1, 0)
    };

    public static bool IsWallPlaceable(CellType t)
    {
        return t == CellType.Grass || t == CellType.BonusA || t == CellType.BonusB;
    }

    public static bool CountsAsEnclosedCell(CellType t)
    {
        return t != CellType.Water && t != CellType.Wall;
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

        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i])
                continue;

            CellType t = map.cells[i];
            // Horse marker may overwrite underlying type; still count as enclosed cell.
            if (map.Index(map.horse.x, map.horse.y) == i)
                t = CellType.Horse;

            if (!CountsAsEnclosedCell(t) && t != CellType.Horse)
                continue;

            enclosed++;
            if (t == CellType.BonusA)
                bonusA++;
            else if (t == CellType.BonusB)
                bonusB++;
            else if (t == CellType.Bee)
                bees++;
        }

        result.enclosedCellCount = enclosed;
        result.bonusACount = bonusA;
        result.bonusAScore = bonusA * BonusAExtra;
        result.bonusBCount = bonusB;
        result.bonusBScore = bonusB * BonusBExtra;
        result.beeCount = bees;
        result.beePenalty = bees * BeePenalty;
        result.totalScore = enclosed * PointsPerEnclosedCell
                            + result.bonusAScore
                            + result.bonusBScore
                            + result.beePenalty;
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

    /// <summary>
    /// Optimistic upper bound on score for a trapped pen (used for pruning).
    /// Sums +1 and extras for every currently enclosed cell — same as Recalculate when trapped.
    /// </summary>
    public static int ScoreFromMask(MapData map, bool[] mask)
    {
        if (mask == null)
            return int.MinValue;

        int total = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i])
                continue;
            CellType t = map.cells[i];
            if (map.Index(map.horse.x, map.horse.y) == i)
                t = CellType.Horse;
            if (!CountsAsEnclosedCell(t) && t != CellType.Horse)
                continue;
            total += PointsPerEnclosedCell;
            if (t == CellType.BonusA)
                total += BonusAExtra;
            else if (t == CellType.BonusB)
                total += BonusBExtra;
            else if (t == CellType.Bee)
                total += BeePenalty;
        }
        return total;
    }
}
