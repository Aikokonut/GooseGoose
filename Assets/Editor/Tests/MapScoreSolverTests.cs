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
    public void MinCutIncumbent_IsVerifiedFeasible()
    {
        MapData map = CreateBlank(7, 7);
        map.horse = new Vector2Int(3, 3);
        map.Set(map.horse, CellType.Horse);

        MapSolution sol = MapScoreSolver.Solve(map, 8);
        Assert.IsTrue(sol.found, sol.message);
        Assert.That(sol.status, Is.EqualTo(SolveStatus.Optimal).Or.EqualTo(SolveStatus.Timeout));

        MapData verify = MapScoring.ApplyWalls(map.Clone(), sol.walls);
        Assert.IsFalse(MapPathfinder.CanEscape(verify));
        Assert.IsTrue(MapScoring.Recalculate(verify, 8, sol.walls).valid);
    }

    [Test]
    public void BonusCells_ContributeExtraPoints()
    {
        MapData map = CreateBlank(7, 7);
        map.horse = new Vector2Int(3, 3);
        map.Set(map.horse, CellType.Horse);
        map.Set(3, 2, CellType.BonusA);
        map.Set(3, 4, CellType.BonusB);
        map.wallBudget = 12;

        MapSolution sol = MapScoreSolver.Solve(map, 12);
        Assert.IsTrue(sol.found, sol.message);
        Assert.AreEqual(SolveStatus.Optimal, sol.status);

        if (sol.breakdown.bonusACount > 0)
            Assert.AreEqual(sol.breakdown.bonusACount * MapScoring.BonusAExtra, sol.breakdown.bonusAScore);
        if (sol.breakdown.bonusBCount > 0)
            Assert.AreEqual(sol.breakdown.bonusBCount * MapScoring.BonusBExtra, sol.breakdown.bonusBScore);
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
        Assert.AreEqual(SolveStatus.Optimal, sol.status);

        MapData verify = MapScoring.ApplyWalls(map.Clone(), sol.walls);
        Assert.IsFalse(MapPathfinder.CanEscape(verify), "Portal escape must be sealed");
    }

    [Test]
    public void InsufficientBudget_IsInfeasibleNotTimeout()
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
        StringAssert.DoesNotContain("exhausted", sol.message);
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
    public void Timeout_PreservesFeasibleOrReportsTimeoutClearly()
    {
        MapData map = CreateBlank(5, 5);
        map.horse = new Vector2Int(2, 2);
        map.Set(map.horse, CellType.Horse);

        long prev = MapScoreSolver.TimeLimitMilliseconds;
        try
        {
            // Min-cut incumbent should often succeed even with a tiny limit.
            MapScoreSolver.TimeLimitMilliseconds = 1;
            MapSolution sol = MapScoreSolver.Solve(map, 8);
            if (sol.found)
            {
                Assert.That(sol.status, Is.EqualTo(SolveStatus.Optimal).Or.EqualTo(SolveStatus.Timeout));
                MapData verify = MapScoring.ApplyWalls(map.Clone(), sol.walls);
                Assert.IsFalse(MapPathfinder.CanEscape(verify));
                if (sol.status == SolveStatus.Timeout)
                    Assert.IsFalse(sol.optimalityProven);
            }
            else
            {
                Assert.AreEqual(SolveStatus.Timeout, sol.status, sol.message);
                Assert.IsTrue(sol.message.StartsWith("TIMEOUT"), sol.message);
            }
        }
        finally
        {
            MapScoreSolver.TimeLimitMilliseconds = prev;
        }
    }

    [Test]
    public void Documentation_DoesNotClaimCpSatEquivalence()
    {
        // Sanity: status messages for timeout mention BnB is not CP-SAT when timed out with solution.
        MapData map = CreateBlank(11, 11);
        map.horse = new Vector2Int(5, 5);
        map.Set(map.horse, CellType.Horse);
        // Add bees so search continues after first seal.
        map.Set(5, 4, CellType.Bee);
        map.Set(4, 5, CellType.Bee);
        map.Set(6, 5, CellType.Bee);

        long prev = MapScoreSolver.TimeLimitMilliseconds;
        try
        {
            MapScoreSolver.TimeLimitMilliseconds = 2;
            MapSolution sol = MapScoreSolver.Solve(map, 20);
            if (sol.found && sol.status == SolveStatus.Timeout)
                StringAssert.Contains("not a CP-SAT", sol.message);
        }
        finally
        {
            MapScoreSolver.TimeLimitMilliseconds = prev;
        }
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
