using Google.OrTools.Sat;

namespace EncloseSatSolver;

/// <summary>
/// CP-SAT enclosure maximizer matching enclose.horse / Dynomight-style models:
/// wall + enclosed vars, edge not enclosed, separation, tree connectivity via parents/dist,
/// objective = base 1 + cherry 3 + apple 10 + bee -5.
/// </summary>
public static class SatEnclosureSolver
{
    const int Grass = 0, Water = 1, WallTerrain = 2, Horse = 3, BonusA = 4, Portal = 5, BonusB = 6, Bee = 7;

    static readonly (int dx, int dy)[] Dirs = { (0, 1), (1, 0), (0, -1), (-1, 0) };

    public static SolveResponse Solve(MapRequest req, double timeSec)
    {
        int w = req.Width;
        int h = req.Height;
        int n = w * h;
        int[] cells = req.Cells ?? Array.Empty<int>();
        if (cells.Length != n)
            return Fail("cells length mismatch");

        int horse = req.HorseY * w + req.HorseX;
        if (horse < 0 || horse >= n)
            return Fail("invalid horse");

        int budget = Math.Max(0, req.Budget);

        var adj = BuildAdjacency(w, h, cells, req.Portals);

        // Already sealed?
        if (!CanEscape(cells, w, h, horse, adj, new HashSet<int>()))
        {
            int score = ScoreReachable(cells, w, h, horse, adj, new HashSet<int>());
            return new SolveResponse
            {
                Ok = true,
                Status = "Optimal",
                Optimal = true,
                Score = score,
                Walls = Array.Empty<int[]>(),
                Message = "Already enclosed."
            };
        }

        var model = new CpModel();

        var toggleable = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (i == horse) continue;
            int t = cells[i];
            if (t == Water || t == WallTerrain) continue;
            if (t == BonusA || t == BonusB || t == Bee || t == Portal) continue;
            // Grass (or Horse cell elsewhere) — only grass is wall-placeable.
            if (t == Grass)
                toggleable.Add(i);
        }
        var toggleSet = new HashSet<int>(toggleable);

        var wall = new Dictionary<int, BoolVar>();
        foreach (int i in toggleable)
            wall[i] = model.NewBoolVar($"w{i}");

        if (toggleable.Count > 0)
            model.Add(LinearExpr.Sum(wall.Values.ToArray()) <= budget);
        else if (budget < 0)
            return Fail("no placeable cells");

        var nonWater = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (cells[i] == Water || cells[i] == WallTerrain) continue;
            nonWater.Add(i);
        }

        var enclosed = new Dictionary<int, BoolVar>();
        foreach (int i in nonWater)
            enclosed[i] = model.NewBoolVar($"e{i}");

        model.Add(enclosed[horse] == 1);

        foreach (int i in nonWater)
        {
            if (toggleSet.Contains(i))
                model.Add(enclosed[i] == 0).OnlyEnforceIf(wall[i]);

            if (IsEdge(i, w, h))
            {
                if (toggleSet.Contains(i))
                    model.Add(enclosed[i] == 0).OnlyEnforceIf(wall[i].Not());
                else if (i != horse)
                    model.Add(enclosed[i] == 0);
            }
        }

        // Reachability / no-cut between non-walled neighbors: enclosed must agree across open edges.
        foreach (int i in nonWater)
        {
            if (!adj.TryGetValue(i, out var neighbors)) continue;
            foreach (int j in neighbors)
            {
                if (j <= i || !enclosed.ContainsKey(j)) continue;
                bool wi = wall.ContainsKey(i);
                bool wj = wall.ContainsKey(j);

                if (wi && wj)
                {
                    model.Add(enclosed[j] >= enclosed[i] - wall[j]).OnlyEnforceIf(wall[i].Not());
                    model.Add(enclosed[i] >= enclosed[j] - wall[i]).OnlyEnforceIf(wall[j].Not());
                }
                else if (wi)
                {
                    model.Add(enclosed[j] >= enclosed[i]).OnlyEnforceIf(wall[i].Not());
                    model.Add(enclosed[i] >= enclosed[j] - wall[i]);
                }
                else if (wj)
                {
                    model.Add(enclosed[j] >= enclosed[i] - wall[j]);
                    model.Add(enclosed[i] >= enclosed[j]).OnlyEnforceIf(wall[j].Not());
                }
                else
                {
                    model.Add(enclosed[j] >= enclosed[i]);
                    model.Add(enclosed[i] >= enclosed[j]);
                }
            }
        }

        AddConnectivity(model, horse, n, enclosed, wall, toggleSet, nonWater, adj);

        var obj = new List<LinearExpr>();
        foreach (int i in nonWater)
            obj.Add(enclosed[i] * CellWeight(cells[i], i == horse));
        model.Maximize(LinearExpr.Sum(obj.ToArray()));

        var solver = new CpSolver();
        timeSec = Math.Max(0.5, timeSec);
        solver.StringParameters = $"max_time_in_seconds:{timeSec.ToString(System.Globalization.CultureInfo.InvariantCulture)},num_search_workers:8";

        CpSolverStatus status = solver.Solve(model);

        if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
        {
            return new SolveResponse
            {
                Ok = false,
                Status = status.ToString(),
                Optimal = false,
                Message = "CP-SAT: " + status,
                Walls = Array.Empty<int[]>(),
                Score = 0
            };
        }

        var placed = new List<int>();
        foreach (int i in toggleable)
        {
            if (solver.Value(wall[i]) == 1)
                placed.Add(i);
        }
        var wallSet = new HashSet<int>(placed);

        // Independent BFS verify (same rules as Unity MapPathfinder / MapScoring).
        if (CanEscape(cells, w, h, horse, adj, wallSet))
        {
            return new SolveResponse
            {
                Ok = false,
                Status = "VerifyFailed",
                Message = "CP-SAT walls still allow escape.",
                Walls = ToPairs(placed, w),
                Score = 0
            };
        }

        int verified = ScoreReachable(cells, w, h, horse, adj, wallSet);
        bool optimal = status == CpSolverStatus.Optimal;

        return new SolveResponse
        {
            Ok = true,
            Status = optimal ? "Optimal" : "Feasible",
            Optimal = optimal,
            Score = verified,
            Walls = ToPairs(placed, w),
            Message = (optimal ? "OPTIMAL" : "FEASIBLE") + " CP-SAT score " + verified + ", " + placed.Count + " walls."
        };
    }

    static void AddConnectivity(
        CpModel model, int horse, int n,
        Dictionary<int, BoolVar> enclosed,
        Dictionary<int, BoolVar> wall,
        HashSet<int> toggleSet,
        List<int> nonWater,
        Dictionary<int, List<int>> adj)
    {
        var dist = new Dictionary<int, IntVar>();
        foreach (int i in nonWater)
            dist[i] = model.NewIntVar(0, n, $"d{i}");

        model.Add(dist[horse] == 0);

        foreach (int i in nonWater)
        {
            if (i == horse) continue;
            if (!adj.TryGetValue(i, out var neighbors))
            {
                model.Add(enclosed[i] == 0);
                continue;
            }

            var parents = neighbors.Where(j => dist.ContainsKey(j)).ToList();
            if (parents.Count == 0)
            {
                model.Add(enclosed[i] == 0);
                continue;
            }

            var parentVars = new List<BoolVar>();
            foreach (int j in parents)
            {
                var p = model.NewBoolVar($"p{i}_{j}");
                parentVars.Add(p);
                model.Add(enclosed[j] == 1).OnlyEnforceIf(p);
                model.Add(dist[i] == dist[j] + 1).OnlyEnforceIf(p);
                if (toggleSet.Contains(j))
                    model.Add(wall[j] == 0).OnlyEnforceIf(p);
                if (toggleSet.Contains(i))
                    model.Add(wall[i] == 0).OnlyEnforceIf(p);
            }

            model.Add(LinearExpr.Sum(parentVars.ToArray()) == 1).OnlyEnforceIf(enclosed[i]);
            model.Add(LinearExpr.Sum(parentVars.ToArray()) == 0).OnlyEnforceIf(enclosed[i].Not());
        }
    }

    static Dictionary<int, List<int>> BuildAdjacency(int w, int h, int[] cells, int[][]? portals)
    {
        int n = w * h;
        var adj = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (cells[i] == Water || cells[i] == WallTerrain) continue;
            var list = new List<int>(6);
            int x = i % w, y = i / w;
            foreach (var (dx, dy) in Dirs)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (cells[j] == Water || cells[j] == WallTerrain) continue;
                list.Add(j);
            }
            adj[i] = list;
        }

        if (portals != null)
        {
            foreach (var pair in portals)
            {
                if (pair == null || pair.Length < 4) continue;
                int ax = pair[0], ay = pair[1], bx = pair[2], by = pair[3];
                if (ax < 0 || ay < 0 || bx < 0 || by < 0 || ax >= w || ay >= h || bx >= w || by >= h)
                    continue;
                int a = ay * w + ax;
                int b = by * w + bx;
                if (!adj.ContainsKey(a) || !adj.ContainsKey(b)) continue;
                if (!adj[a].Contains(b)) adj[a].Add(b);
                if (!adj[b].Contains(a)) adj[b].Add(a);
            }
        }

        return adj;
    }

    static bool IsEdge(int i, int w, int h)
    {
        int x = i % w, y = i / w;
        return x == 0 || y == 0 || x == w - 1 || y == h - 1;
    }

    static int CellWeight(int cellType, bool isHorse)
    {
        if (isHorse) return 1;
        return cellType switch
        {
            BonusA => 1 + 3,
            BonusB => 1 + 10,
            Bee => 1 - 5,
            Water => 0,
            WallTerrain => 0,
            _ => 1 // grass, portal, horse-as-type
        };
    }

    static bool CanEscape(int[] cells, int w, int h, int horse,
        Dictionary<int, List<int>> adj, HashSet<int> walls)
    {
        var seen = new HashSet<int> { horse };
        var q = new Queue<int>();
        q.Enqueue(horse);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            if (IsEdge(cur, w, h))
                return true;
            if (!adj.TryGetValue(cur, out var ns)) continue;
            foreach (int n in ns)
            {
                if (walls.Contains(n) || seen.Contains(n)) continue;
                if (cells[n] == Water || cells[n] == WallTerrain) continue;
                seen.Add(n);
                q.Enqueue(n);
            }
        }
        return false;
    }

    static int ScoreReachable(int[] cells, int w, int h, int horse,
        Dictionary<int, List<int>> adj, HashSet<int> walls)
    {
        var seen = new HashSet<int> { horse };
        var q = new Queue<int>();
        q.Enqueue(horse);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            if (!adj.TryGetValue(cur, out var ns)) continue;
            foreach (int n in ns)
            {
                if (walls.Contains(n) || seen.Contains(n)) continue;
                if (cells[n] == Water || cells[n] == WallTerrain) continue;
                seen.Add(n);
                q.Enqueue(n);
            }
        }

        int score = 0;
        foreach (int i in seen)
            score += CellWeight(cells[i], i == horse);
        return score;
    }

    static int[][] ToPairs(List<int> idx, int w)
    {
        var a = new int[idx.Count][];
        for (int i = 0; i < idx.Count; i++)
            a[i] = new[] { idx[i] % w, idx[i] / w };
        return a;
    }

    static SolveResponse Fail(string msg) => new()
    {
        Ok = false,
        Status = "Error",
        Message = msg,
        Walls = Array.Empty<int[]>(),
        Score = 0
    };
}
