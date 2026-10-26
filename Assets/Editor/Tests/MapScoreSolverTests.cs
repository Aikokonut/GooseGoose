using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class MapScoreSolverTests
{
    [Test]
    public void OrdinaryMap_ReturnsOptimalEnclosure()
    {
        MapData map = CreateBlank(7, 7);
        map.horse = new Vector2Int(3, 3);
        map.Set(map.horse, CellType.Horse);
        map.wallBudget = 8;

        MapSolution sol = MapScoreSolver.Solve(map, 8);
        Assert.IsTrue(sol.found, sol.message);
        Assert.AreEqual(SolveStatus.Optimal, sol.status);
        Assert.IsTrue(sol.optimalityProven);
        Assert.LessOrEqual(sol.walls.Length, 8);

        MapData verify = MapScoring.ApplyWalls(map.Clone(), sol.walls);
        Assert.IsFalse(MapPathfinder.CanEscape(verify));
        MapScoreBreakdown score = MapScoring.Recalculate(verify, 8, sol.walls);
        Assert.IsTrue(score.valid);
        Assert.AreEqual(score.totalScore, sol.breakdown.totalScore);
    }

    [Test]
    public void SmallMap_MatchesBruteForceOptimal()
    {
        MapData map = CreateBlank(5, 5);
        map.horse = new Vector2Int(2, 2);
        map.Set(map.horse, CellType.Horse);
        map.Set(1, 2, CellType.BonusA);
        map.Set(2, 1, CellType.Bee);
        int budget = 6;

        long prev = MapScoreSolver.TimeLimitMilliseconds;
        try
        {
            MapScoreSolver.TimeLimitMilliseconds = 60000;
            MapSolution sol = MapScoreSolver.Solve(map, budget);
            Assert.IsTrue(sol.found, sol.message);
            Assert.IsTrue(sol.optimalityProven, sol.message);

            int brute = BruteForceBestScore(map, budget);
            Assert.AreEqual(brute, sol.breakdown.totalScore,
                "Solver score must match exhaustive enumeration");
        }
        finally
        {
            MapScoreSolver.TimeLimitMilliseconds = prev;
        }
    }

    [Test]
    public void WallRowShift_FindsHigherScoreThanTightCut()
    {
        // Water seals left/right; a WWWW lid one row above the edge can shift onto the edge
        // for +4 grass cells. Budget 4.
        MapData map = CreateBlank(8, 6);
        map.horse = new Vector2Int(3, 2);
        map.Set(map.horse, CellType.Horse);
        for (int y = 0; y < 6; y++)
        {
            map.Set(1, y, CellType.Water);
            map.Set(6, y, CellType.Water);
        }

        long prev = MapScoreSolver.TimeLimitMilliseconds;
        try
        {
            MapScoreSolver.TimeLimitMilliseconds = 30000;
            MapSolution sol = MapScoreSolver.Solve(map, 4);
            Assert.IsTrue(sol.found, sol.message);
            Assert.GreaterOrEqual(sol.breakdown.totalScore, 8);
            Assert.IsFalse(MapPathfinder.CanEscape(MapScoring.ApplyWalls(map.Clone(), sol.walls)));
        }
        finally
        {
            MapScoreSolver.TimeLimitMilliseconds = prev;
        }
    }

    [Test]
    public void BonusCells_UseGameExtras_Cherry3_Apple10()
    {
        Assert.AreEqual(3, MapScoring.BonusAExtra);
        Assert.AreEqual(10, MapScoring.BonusBExtra);
        Assert.AreEqual(-5, MapScoring.BeePenalty);

        MapData map = CreateBlank(7, 7);
        map.horse = new Vector2Int(3, 3);
        map.Set(map.horse, CellType.Horse);
        map.Set(3, 2, CellType.BonusA);
        map.Set(3, 4, CellType.BonusB);
        map.wallBudget = 12;

        MapSolution sol = MapScoreSolver.Solve(map, 12);
        Assert.IsTrue(sol.found, sol.message);

        if (sol.breakdown.bonusACount > 0)
            Assert.AreEqual(sol.breakdown.bonusACount * 3, sol.breakdown.bonusAScore);
        if (sol.breakdown.bonusBCount > 0)
            Assert.AreEqual(sol.breakdown.bonusBCount * 10, sol.breakdown.bonusBScore);

        // Independent recalculation
        MapScoreBreakdown again = MapScoring.Recalculate(
            MapScoring.ApplyWalls(map.Clone(), sol.walls), 12, sol.walls);
        Assert.AreEqual(again.totalScore, sol.breakdown.totalScore);
    }

    [Test]
    public void BeePenalty_IsAppliedWhenBeeEnclosed()
    {
        MapData map = CreateBlank(7, 7);
        map.horse = new Vector2Int(3, 3);
        map.Set(map.horse, CellType.Horse);
        map.Set(2, 3, CellType.Bee);
        map.wallBudget = 12;

        MapSolution sol = MapScoreSolver.Solve(map, 12);
        Assert.IsTrue(sol.found, sol.message);

        if (sol.breakdown.beeCount > 0)
            Assert.AreEqual(sol.breakdown.beeCount * MapScoring.BeePenalty, sol.breakdown.beePenalty);
    }

    [Test]
    public void CannotPlaceWallOnCherry_OrBee_OrPortal()
    {
        Assert.IsFalse(MapScoring.IsWallPlaceable(CellType.BonusA));
        Assert.IsFalse(MapScoring.IsWallPlaceable(CellType.BonusB));
        Assert.IsFalse(MapScoring.IsWallPlaceable(CellType.Bee));
        Assert.IsFalse(MapScoring.IsWallPlaceable(CellType.Portal));
        Assert.IsTrue(MapScoring.IsWallPlaceable(CellType.Grass));
    }

    [Test]
    public void PortalPair_IsRespectedInConnectivity()
    {
        MapData map = CreateBlank(8, 8);
        map.horse = new Vector2Int(2, 2);
        map.Set(map.horse, CellType.Horse);

        Vector2Int a = new Vector2Int(2, 3);
        Vector2Int b = new Vector2Int(6, 6);
        map.Set(a, CellType.Portal);
        map.Set(b, CellType.Portal);
        map.portals = new[] { new PortalLink(a, b) };
        map.wallBudget = 16;

        MapSolution sol = MapScoreSolver.Solve(map, 16);
        Assert.IsTrue(sol.found, sol.message);

        MapData verify = MapScoring.ApplyWalls(map.Clone(), sol.walls);
        Assert.IsFalse(MapPathfinder.CanEscape(verify), "Portal escape must be sealed");
        Assert.IsTrue(MapScoring.Recalculate(verify, 16, sol.walls).valid);
    }

    [Test]
    public void EdgeEscape_WithoutWalls_IsInvalidScore()
    {
        MapData map = CreateBlank(5, 5);
        map.horse = new Vector2Int(2, 2);
        map.Set(map.horse, CellType.Horse);
        Assert.IsTrue(MapPathfinder.CanEscape(map));
        Assert.IsFalse(MapScoring.Recalculate(map, 10).valid);
    }

    [Test]
    public void Water_BlocksMovementAndWalls()
    {
        MapData map = CreateBlank(5, 5);
        map.horse = new Vector2Int(2, 2);
        map.Set(map.horse, CellType.Horse);
        map.Set(2, 1, CellType.Water);
        Assert.IsFalse(MapScoring.IsWallPlaceable(CellType.Water));
        Assert.IsFalse(map.IsPassable(CellType.Water));
    }

    [Test]
    public void InsufficientBudget_IsInfeasible()
    {
        MapData map = CreateBlank(9, 9);
        map.horse = new Vector2Int(4, 4);
        map.Set(map.horse, CellType.Horse);

        int minCut = MapGenerator.GetMinimumWallsToEnclose(map);
        Assume.That(minCut, Is.GreaterThan(0));
        Assume.That(minCut, Is.Not.EqualTo(int.MaxValue));

        int budget = Mathf.Max(0, minCut - 1);
        MapSolution sol = MapScoreSolver.Solve(map, budget);
        Assert.IsFalse(sol.found);
        Assert.AreEqual(SolveStatus.Infeasible, sol.status);
        Assert.IsTrue(sol.message.StartsWith("INFEASIBLE"), sol.message);
    }

    [Test]
    public void OpenMapWithZeroBudget_ReportsInfeasible()
    {
        MapData map = CreateBlank(6, 6);
        map.horse = new Vector2Int(2, 2);
        map.Set(map.horse, CellType.Horse);
        Assert.IsTrue(MapPathfinder.CanEscape(map));

        MapSolution sol = MapScoreSolver.Solve(map, 0);
        Assert.IsFalse(sol.found);
        Assert.AreEqual(SolveStatus.Infeasible, sol.status);
    }

    [Test]
    public void EscapableLayout_NeverReportedAsSolution()
    {
        MapData map = CreateBlank(6, 6);
        map.horse = new Vector2Int(2, 2);
        map.Set(map.horse, CellType.Horse);

        // Single wall cannot enclose.
        Vector2Int[] fake = { new Vector2Int(2, 1) };
        MapData applied = MapScoring.ApplyWalls(map.Clone(), fake);
        Assert.IsTrue(MapPathfinder.CanEscape(applied));
        Assert.IsFalse(MapScoring.Recalculate(applied, 1, fake).valid);
    }

    [Test]
    public void Timeout_ReturnsBestVerifiedOrTimeoutStatus()
    {
        MapData map = CreateBlank(11, 11);
        map.horse = new Vector2Int(5, 5);
        map.Set(map.horse, CellType.Horse);
        map.Set(5, 4, CellType.BonusA);
        map.Set(4, 5, CellType.BonusB);

        long prev = MapScoreSolver.TimeLimitMilliseconds;
        try
        {
            MapScoreSolver.TimeLimitMilliseconds = 5;
            MapSolution sol = MapScoreSolver.Solve(map, 20);
            if (sol.found)
            {
                Assert.That(sol.status,
                    Is.EqualTo(SolveStatus.Optimal).Or.EqualTo(SolveStatus.Timeout)
                        .Or.EqualTo(SolveStatus.FeasibleNotProvenOptimal));
                MapData verify = MapScoring.ApplyWalls(map.Clone(), sol.walls);
                Assert.IsFalse(MapPathfinder.CanEscape(verify));
                Assert.IsTrue(MapScoring.Recalculate(verify, 20, sol.walls).valid);
                if (sol.status == SolveStatus.Timeout)
                    Assert.IsFalse(sol.optimalityProven);
            }
            else
            {
                Assert.AreEqual(SolveStatus.Timeout, sol.status, sol.message);
            }
        }
        finally
        {
            MapScoreSolver.TimeLimitMilliseconds = prev;
        }
    }

    [Test]
    public void ExcessiveBudget_StillOptimalOrFeasible()
    {
        MapData map = CreateBlank(5, 5);
        map.horse = new Vector2Int(2, 2);
        map.Set(map.horse, CellType.Horse);

        MapSolution sol = MapScoreSolver.Solve(map, 50);
        Assert.IsTrue(sol.found, sol.message);
        Assert.LessOrEqual(sol.walls.Length, 50);
        Assert.IsTrue(MapScoring.Recalculate(
            MapScoring.ApplyWalls(map.Clone(), sol.walls), 50, sol.walls).valid);
    }

    /// <summary>Enumerate all wall subsets up to budget; return best verified score.</summary>
    static int BruteForceBestScore(MapData map, int budget)
    {
        var placeable = new List<int>();
        for (int i = 0; i < map.cells.Length; i++)
        {
            Vector2Int p = new Vector2Int(i % map.width, i / map.width);
            if (p == map.horse)
                continue;
            if (MapScoring.IsWallPlaceable(map.Get(p)))
                placeable.Add(i);
        }

        int best = int.MinValue;
        int m = placeable.Count;
        int maxMask = 1 << m;
        for (int mask = 0; mask < maxMask; mask++)
        {
            int bits = 0;
            for (int t = mask; t != 0; t &= t - 1)
                bits++;
            if (bits > budget)
                continue;

            var walls = new List<Vector2Int>(bits);
            for (int i = 0; i < m; i++)
            {
                if ((mask & (1 << i)) == 0)
                    continue;
                int idx = placeable[i];
                walls.Add(new Vector2Int(idx % map.width, idx / map.width));
            }

            Vector2Int[] arr = walls.ToArray();
            MapScoreBreakdown bd = MapScoring.Recalculate(
                MapScoring.ApplyWalls(map.Clone(), arr), budget, arr);
            if (bd.valid && bd.totalScore > best)
                best = bd.totalScore;
        }

        return best;
    }

    static MapData CreateBlank(int w, int h)
    {
        MapData map = new MapData(w, h, 1);
        for (int i = 0; i < map.cells.Length; i++)
            map.cells[i] = CellType.Grass;
        map.portals = System.Array.Empty<PortalLink>();
        return map;
    }
}
