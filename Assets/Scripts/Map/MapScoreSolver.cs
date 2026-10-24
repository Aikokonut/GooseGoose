using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public enum SolveStatus
{
    Optimal,
    Infeasible,
    FeasibleNotProvenOptimal,
    Timeout
}

public class MapSolution
{
    public bool found;
    public bool optimalityProven;
    public SolveStatus status;
    public string message;
    public Vector2Int[] walls;
    public MapScoreBreakdown breakdown;
}

/// <summary>
/// Seed with min vertex-cut (walls may sit on map edge), then improve by run translate / nudge.
/// </summary>
public static class MapScoreSolver
{
    public static long TimeLimitMilliseconds = 3500;

    const int Inf = 1 << 20;

    static readonly Vector2Int[] Dirs =
    {
        new Vector2Int(0, 1), new Vector2Int(1, 0),
        new Vector2Int(0, -1), new Vector2Int(-1, 0)
    };

    public static MapSolution Solve(MapData source, int wallBudget)
    {
        var fail = Blank();
        if (source?.cells == null || source.width <= 0 || source.height <= 0 ||
            source.cells.Length != source.width * source.height ||
            !source.InBounds(source.horse) || !source.IsPassable(source.Get(source.horse)))
        {
            fail.message = "Invalid map or horse position.";
            fail.status = SolveStatus.Infeasible;
            return fail;
        }

        wallBudget = Mathf.Max(0, wallBudget);
        long limitMs = Math.Max(100, TimeLimitMilliseconds);
        var sw = Stopwatch.StartNew();

        if (!MapPathfinder.CanEscape(source))
        {
            var zero = MapScoring.Recalculate(source, wallBudget, Array.Empty<Vector2Int>());
            if (zero.valid && zero.beeCount == 0)
                return Ok(source, wallBudget, Array.Empty<int>(), true, false, zero.totalScore, 0);
        }

        int minCut = MapGenerator.GetMinimumWallsToEnclose(source);
        if (minCut == int.MaxValue || minCut > wallBudget)
        {
            fail.found = false;
            fail.status = SolveStatus.Infeasible;
            fail.message = minCut == int.MaxValue
                ? "INFEASIBLE: No legal enclosure cut."
                : "INFEASIBLE: Need " + minCut + " walls, budget " + wallBudget + ".";
            return fail;
        }

        int[] best = null;
        int bestScore = int.MinValue;
        int steps = 0;

        // Push the cut outward: only allow walls at dist >= d from horse.
        // d=0 is classic min-cut (tiny pen). Larger d → walls farther out → bigger pen.
        int maxDist = source.width + source.height;
        for (int d = 0; d <= maxDist && sw.ElapsedMilliseconds < limitMs; d++)
        {
            TrySeed(source, wallBudget, false, d, ref best, ref bestScore, ref steps);
            TrySeed(source, wallBudget, true, d, ref best, ref bestScore, ref steps);
        }

        if (best == null)
        {
            fail.found = false;
            fail.status = SolveStatus.Infeasible;
            fail.message = "INFEASIBLE: Min-cut walls exceed budget after recovery.";
            return fail;
        }

        int scoreBefore = bestScore;
        bool moved = true;
        while (moved && sw.ElapsedMilliseconds < limitMs)
            moved = ImproveOnce(source, wallBudget, ref best, ref bestScore, ref steps, sw, limitMs);

        bool timedOut = sw.ElapsedMilliseconds >= limitMs;
        return Ok(source, wallBudget, best, false, timedOut, scoreBefore, steps);
    }

    static bool ImproveOnce(MapData src, int budget, ref int[] best, ref int bestScore, ref int steps,
        Stopwatch sw, long limitMs)
    {
        if (PruneWalls(src, budget, ref best, ref bestScore, ref steps))
            return true;

        foreach (int[] run in WallRuns(best, src.width))
        {
            if (run.Length < 2 || sw.ElapsedMilliseconds >= limitMs)
                continue;
            for (int d = 0; d < 4; d++)
            {
                for (int dist = 1; dist <= 4; dist++)
                {
                    if (!TranslateRun(src, best, run, Dirs[d], dist, out int[] moved))
                        break;
                    if (TryAcceptWalls(src, budget, moved, ref best, ref bestScore, ref steps))
                        return true;
                    if (moved.Length < budget &&
                        TrySealAndAccept(src, budget, moved, ref best, ref bestScore, ref steps))
                        return true;
                }
            }
        }

        for (int i = 0; i < best.Length && sw.ElapsedMilliseconds < limitMs; i++)
        {
            Vector2Int from = Idx(best[i], src.width);
            for (int d = 0; d < 4; d++)
            {
                Vector2Int to = from + Dirs[d];
                if (!CanPlace(src, to) || Contains(best, src.Index(to.x, to.y)))
                    continue;
                int[] moved = Replace(best, best[i], src.Index(to.x, to.y));
                if (TryAcceptWalls(src, budget, moved, ref best, ref bestScore, ref steps))
                    return true;
                if (moved.Length < budget &&
                    TrySealAndAccept(src, budget, moved, ref best, ref bestScore, ref steps))
                    return true;
            }
        }

        return false;
    }

    static bool PruneWalls(MapData src, int budget, ref int[] best, ref int bestScore, ref int steps)
    {
        for (int i = 0; i < best.Length; i++)
        {
            int[] without = RemoveAt(best, i);
            if (!Score(src, budget, without, out int sc))
                continue;
            if (sc > bestScore || (sc == bestScore && without.Length < best.Length))
            {
                best = without;
                bestScore = sc;
                steps++;
                return true;
            }
        }
        return false;
    }

    static bool TryAcceptWalls(MapData src, int budget, int[] candidate,
        ref int[] best, ref int bestScore, ref int steps)
    {
        if (!Score(src, budget, candidate, out int sc))
            return false;
        if (sc > bestScore || (sc == bestScore && candidate.Length < best.Length))
        {
            best = Unique(candidate);
            bestScore = sc;
            steps++;
            return true;
        }
        return false;
    }

    static bool TrySealAndAccept(MapData src, int budget, int[] seed,
        ref int[] best, ref int bestScore, ref int steps)
    {
        int[] cur = Unique(seed);
        for (int g = 0; g < 16; g++)
        {
            if (Score(src, budget, cur, out _))
                return TryAcceptWalls(src, budget, cur, ref best, ref bestScore, ref steps);
            if (cur.Length >= budget)
                return false;

            MapData map = MapScoring.ApplyWalls(src.Clone(), ToPos(cur, src.width));
            EscapePath esc = MapPathfinder.FindEscape(map);
            if (!esc.found || esc.cells == null)
                return false;

            int fill = -1;
            int bestDist = -1;
            for (int i = 0; i < esc.cells.Count; i++)
            {
                Vector2Int p = esc.cells[i];
                if (!CanPlace(src, p))
                    continue;
                int idx = src.Index(p.x, p.y);
                if (Contains(cur, idx))
                    continue;
                int dist = Mathf.Abs(p.x - src.horse.x) + Mathf.Abs(p.y - src.horse.y);
                if (dist > bestDist)
                {
                    bestDist = dist;
                    fill = idx;
                }
            }
            if (fill < 0)
                return false;
            cur = AddSorted(cur, fill);
        }
        return false;
    }

    static void TrySeed(MapData src, int budget, bool sinkSide, int minWallDist,
        ref int[] best, ref int bestScore, ref int steps)
    {
        if (!TryCut(src, null, null, sinkSide, minWallDist, out int[] walls))
            return;
        if (walls.Length > budget || walls.Length == 0)
            return;
        if (!Score(src, budget, walls, out int sc))
            return;

        if (best == null || sc > bestScore || (sc == bestScore && walls.Length < best.Length))
        {
            best = walls;
            bestScore = sc;
            steps++;
        }
    }

    static List<int[]> WallRuns(int[] walls, int width)
    {
        var runs = new List<int[]>();
        AddAxisRuns(walls, width, true, runs);
        AddAxisRuns(walls, width, false, runs);
        runs.Sort((a, b) => b.Length.CompareTo(a.Length));
        return runs;
    }

    static void AddAxisRuns(int[] walls, int width, bool horizontal, List<int[]> runs)
    {
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < walls.Length; i++)
        {
            int key = horizontal ? walls[i] / width : walls[i] % width;
            if (!groups.TryGetValue(key, out List<int> list))
            {
                list = new List<int>();
                groups[key] = list;
            }
            list.Add(walls[i]);
        }

        foreach (var kv in groups)
        {
            List<int> list = kv.Value;
            list.Sort((a, b) =>
            {
                int aa = horizontal ? a % width : a / width;
                int bb = horizontal ? b % width : b / width;
                return aa.CompareTo(bb);
            });
            int start = 0;
            for (int i = 1; i <= list.Count; i++)
            {
                bool split = i == list.Count;
                if (!split)
                {
                    int prev = horizontal ? list[i - 1] % width : list[i - 1] / width;
                    int cur = horizontal ? list[i] % width : list[i] / width;
                    split = cur != prev + 1;
                }
                if (!split)
                    continue;
                int len = i - start;
                if (len >= 2)
                {
                    var run = new int[len];
                    for (int k = 0; k < len; k++)
                        run[k] = list[start + k];
                    runs.Add(run);
                }
                start = i;
            }
        }
    }

    static bool TranslateRun(MapData src, int[] cur, int[] run, Vector2Int dir, int steps, out int[] result)
    {
        result = null;
        var runSet = new HashSet<int>(run);
        var neu = new int[run.Length];
        Vector2Int delta = dir * steps;
        for (int i = 0; i < run.Length; i++)
        {
            Vector2Int to = Idx(run[i], src.width) + delta;
            if (!CanPlace(src, to))
                return false;
            int ti = src.Index(to.x, to.y);
            if (Contains(cur, ti) && !runSet.Contains(ti))
                return false;
            neu[i] = ti;
        }
        Array.Sort(neu);
        for (int i = 1; i < neu.Length; i++)
            if (neu[i] == neu[i - 1])
                return false;

        var kept = new List<int>(cur.Length);
        for (int i = 0; i < cur.Length; i++)
            if (!runSet.Contains(cur[i]))
                kept.Add(cur[i]);
        kept.AddRange(neu);
        result = Unique(kept.ToArray());
        return true;
    }

    static Vector2Int Idx(int i, int w) => new Vector2Int(i % w, i / w);

    // Edge cells are placeable; only out-of-bounds is illegal.
    static bool CanPlace(MapData src, Vector2Int c) =>
        src.InBounds(c) && c != src.horse && MapScoring.IsWallPlaceable(src.Get(c));

    static bool Contains(int[] a, int v)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] == v) return true;
        return false;
    }

    static int[] Unique(int[] values)
    {
        if (values == null || values.Length == 0)
            return Array.Empty<int>();
        int[] c = (int[])values.Clone();
        Array.Sort(c);
        int w = 1;
        for (int r = 1; r < c.Length; r++)
            if (c[r] != c[w - 1])
                c[w++] = c[r];
        if (w == c.Length)
            return c;
        var u = new int[w];
        Array.Copy(c, u, w);
        return u;
    }

    static int[] AddSorted(int[] values, int value)
    {
        var o = new int[values.Length + 1];
        int i = 0;
        while (i < values.Length && values[i] < value)
        {
            o[i] = values[i];
            i++;
        }
        o[i] = value;
        for (int j = i; j < values.Length; j++)
            o[j + 1] = values[j];
        return o;
    }

    static int[] RemoveAt(int[] values, int index)
    {
        var o = new int[values.Length - 1];
        for (int i = 0, j = 0; i < values.Length; i++)
            if (i != index)
                o[j++] = values[i];
        return o;
    }

    static int[] Replace(int[] values, int oldV, int newV)
    {
        var o = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
            o[i] = values[i] == oldV ? newV : values[i];
        return Unique(o);
    }

    // --- min vertex-cut horse → edge ---

    static bool TryCut(MapData map, HashSet<int> mustKeep, HashSet<int> blocked, bool sinkSide,
        int minWallDist, out int[] wallIndices)
    {
        wallIndices = null;
        int n = map.width * map.height;
        int src = n * 2;
        int snk = src + 1;
        var flow = new Flow(n * 2 + 2);
        int horse = map.Index(map.horse.x, map.horse.y);
        int hx = map.horse.x;
        int hy = map.horse.y;

        if (mustKeep != null && mustKeep.Count > 0)
        {
            if (!KeepIsConnected(map, mustKeep, horse, blocked))
            {
                mustKeep = ExpandKeepWithPaths(map, mustKeep, blocked, horse);
                if (mustKeep == null)
                    return false;
            }
            else if (!mustKeep.Contains(horse))
                mustKeep.Add(horse);
        }

        for (int y = 0; y < map.height; y++)
        {
            for (int x = 0; x < map.width; x++)
            {
                Vector2Int p = new Vector2Int(x, y);
                int idx = map.Index(x, y);
                if (blocked != null && blocked.Contains(idx))
                    continue;
                CellType type = map.Get(p);
                if (!map.IsPassable(type))
                    continue;

                int vin = idx * 2;
                int vout = vin + 1;
                bool placeable = MapScoring.IsWallPlaceable(type) && idx != horse;
                // Push cut outward: forbid walls too close to the horse.
                if (placeable && minWallDist > 0)
                {
                    int dist = Mathf.Abs(x - hx) + Mathf.Abs(y - hy);
                    if (dist < minWallDist)
                        placeable = false;
                }
                bool forcedOpen = mustKeep != null && mustKeep.Contains(idx) && !TouchesPassableEdge(map, p);
                int cap = (!placeable || forcedOpen) ? Inf : 1;
                flow.Add(vin, vout, cap);

                if (map.IsEdge(p))
                    flow.Add(vout, snk, Inf);

                for (int d = 0; d < 4; d++)
                {
                    Vector2Int q = p + Dirs[d];
                    if (!map.InBounds(q))
                        continue;
                    int j = map.Index(q.x, q.y);
                    if (blocked != null && blocked.Contains(j))
                        continue;
                    if (!map.IsPassable(map.Get(q)))
                        continue;
                    flow.Add(vout, j * 2, Inf);
                }
            }
        }

        if (blocked != null && blocked.Contains(horse))
            return false;
        if (!map.IsPassable(map.Get(map.horse)))
            return false;
        // Only horse is a flow source. Extra source→mustKeep links caused Inf paths
        // and did not guarantee map connectivity (pen check does that instead).
        flow.Add(src, horse * 2, Inf);

        if (map.portals != null)
        {
            for (int i = 0; i < map.portals.Length; i++)
            {
                PortalLink link = map.portals[i];
                if (!map.InBounds(link.a) || !map.InBounds(link.b))
                    continue;
                int a = map.Index(link.a.x, link.a.y);
                int b = map.Index(link.b.x, link.b.y);
                if (blocked != null && (blocked.Contains(a) || blocked.Contains(b)))
                    continue;
                if (!map.IsPassable(map.Get(link.a)) || !map.IsPassable(map.Get(link.b)))
                    continue;
                flow.Add(a * 2 + 1, b * 2, Inf);
                flow.Add(b * 2 + 1, a * 2, Inf);
            }
        }

        int cut = flow.MaxFlow(src, snk);
        if (cut >= Inf)
            return false;

        // With must-keep terminals, source-side recovery matches the multi-source cut.
        bool useSink = sinkSide && mustKeep == null;
        if (!TryRecoverWalls(map, flow, horse, n, mustKeep, blocked, useSink, out wallIndices))
            if (!TryRecoverWalls(map, flow, horse, n, mustKeep, blocked, !useSink, out wallIndices))
                return false;

        // Must-keep cells must actually lie in the horse pen after walls are applied.
        if (mustKeep != null && mustKeep.Count > 0)
        {
            bool[] pen = PenMask(map, wallIndices);
            foreach (int idx in mustKeep)
            {
                if (idx >= 0 && idx < pen.Length && !pen[idx])
                    return false;
            }
        }

        return true;
    }

    static bool TryRecoverWalls(MapData map, Flow flow, int horse, int n,
        HashSet<int> mustKeep, HashSet<int> blocked, bool sinkSide, out int[] wallIndices)
    {
        int srcNode = n * 2;
        int snkNode = srcNode + 1;
        bool[] mark = sinkSide ? flow.CanReachSink(snkNode) : flow.ReachableFrom(srcNode);
        wallIndices = RecoverCutCells(map, horse, n, mustKeep, blocked, sinkSide, mark);
        int verifyBudget = Math.Max(n, wallIndices.Length);
        if (Score(map, verifyBudget, wallIndices, out _))
            return true;

        var walls = new List<int>();
        for (int idx = 0; idx < n; idx++)
        {
            Vector2Int p = new Vector2Int(idx % map.width, idx / map.width);
            if (idx == horse)
                continue;
            if (!MapScoring.IsWallPlaceable(map.Get(p)))
                continue;
            if (mustKeep != null && mustKeep.Contains(idx))
                continue;
            if (blocked != null && blocked.Contains(idx))
                continue;
            if (flow.Saturated(idx * 2, idx * 2 + 1))
                walls.Add(idx);
        }
        wallIndices = walls.ToArray();
        Array.Sort(wallIndices);
        return Score(map, Math.Max(n, wallIndices.Length), wallIndices, out _);
    }

    static int[] RecoverCutCells(MapData map, int horse, int n,
        HashSet<int> mustKeep, HashSet<int> blocked, bool sinkSide, bool[] mark)
    {
        var walls = new List<int>();
        for (int idx = 0; idx < n; idx++)
        {
            Vector2Int p = new Vector2Int(idx % map.width, idx / map.width);
            if (!map.InBounds(p) || idx == horse)
                continue;
            if (blocked != null && blocked.Contains(idx))
                continue;
            if (!MapScoring.IsWallPlaceable(map.Get(p)))
                continue;
            if (mustKeep != null && mustKeep.Contains(idx))
                continue;

            int vin = idx * 2;
            int vout = vin + 1;
            bool inCut = sinkSide
                ? (!mark[vin] && mark[vout])
                : (mark[vin] && !mark[vout]);
            if (inCut)
                walls.Add(idx);
        }
        var arr = walls.ToArray();
        Array.Sort(arr);
        return arr;
    }

    static bool TouchesPassableEdge(MapData map, Vector2Int p)
    {
        if (!map.InBounds(p))
            return false;
        for (int d = 0; d < 4; d++)
        {
            Vector2Int q = p + Dirs[d];
            if (map.InBounds(q) && map.IsEdge(q) && map.IsPassable(map.Get(q)))
                return true;
        }
        return false;
    }

    /// <summary>True if every keep cell is reachable from horse stepping only on keep cells.</summary>
    static bool KeepIsConnected(MapData map, HashSet<int> keep, int horse, HashSet<int> blocked)
    {
        if (!keep.Contains(horse))
            return false;
        var seen = new HashSet<int>();
        var q = new Queue<int>();
        q.Enqueue(horse);
        seen.Add(horse);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            Vector2Int p = new Vector2Int(cur % map.width, cur / map.width);
            for (int d = 0; d < 4; d++)
            {
                Vector2Int np = p + Dirs[d];
                if (!map.InBounds(np) || !map.IsPassable(map.Get(np)))
                    continue;
                int j = map.Index(np.x, np.y);
                if (!keep.Contains(j) || (blocked != null && blocked.Contains(j)) || !seen.Add(j))
                    continue;
                q.Enqueue(j);
            }

            if (map.Get(p) == CellType.Portal && map.TryGetPortalExit(p, out Vector2Int exit))
            {
                int j = map.Index(exit.x, exit.y);
                if (keep.Contains(j) && (blocked == null || !blocked.Contains(j)) && seen.Add(j))
                    q.Enqueue(j);
            }
        }

        foreach (int idx in keep)
        {
            if (blocked != null && blocked.Contains(idx))
                continue;
            if (!seen.Contains(idx))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Add horse→target paths into must-keep. Prefer routes that avoid edge-adjacent cells
    /// so water-bounded pockets are not poisoned by border-hugging shortest paths.
    /// </summary>
    static HashSet<int> ExpandKeepWithPaths(MapData map, HashSet<int> keep, HashSet<int> blocked, int horse)
    {
        var expanded = new HashSet<int> { horse };
        foreach (int target in keep)
        {
            if (blocked != null && blocked.Contains(target))
                continue;
            List<int> path = ShortestPath(map, horse, target, blocked, avoidEdgeAdjacent: true);
            if (path == null)
                path = ShortestPath(map, horse, target, blocked, avoidEdgeAdjacent: false);
            if (path == null)
                return null;
            for (int i = 0; i < path.Count; i++)
                expanded.Add(path[i]);
        }
        return expanded;
    }

    static List<int> ShortestPath(MapData map, int start, int goal, HashSet<int> blocked, bool avoidEdgeAdjacent)
    {
        if (start == goal)
            return new List<int> { start };
        int n = map.width * map.height;
        var prev = new int[n];
        var seen = new bool[n];
        for (int i = 0; i < n; i++)
            prev[i] = -1;
        var q = new Queue<int>();
        q.Enqueue(start);
        seen[start] = true;
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            if (cur == goal)
                break;
            Vector2Int p = new Vector2Int(cur % map.width, cur / map.width);
            for (int d = 0; d < 4; d++)
            {
                Vector2Int np = p + Dirs[d];
                if (!map.InBounds(np) || !map.IsPassable(map.Get(np)))
                    continue;
                if (map.IsEdge(np) && map.Index(np.x, np.y) != goal)
                    continue;
                if (avoidEdgeAdjacent && map.Index(np.x, np.y) != goal && TouchesPassableEdge(map, np))
                    continue;
                int j = map.Index(np.x, np.y);
                if (blocked != null && blocked.Contains(j))
                    continue;
                if (seen[j])
                    continue;
                seen[j] = true;
                prev[j] = cur;
                q.Enqueue(j);
            }

            if (map.Get(p) == CellType.Portal && map.TryGetPortalExit(p, out Vector2Int exit) &&
                map.InBounds(exit) && map.IsPassable(map.Get(exit)))
            {
                int j = map.Index(exit.x, exit.y);
                if ((blocked == null || !blocked.Contains(j)) && !seen[j] &&
                    (!map.IsEdge(exit) || j == goal) &&
                    (!avoidEdgeAdjacent || j == goal || !TouchesPassableEdge(map, exit)))
                {
                    seen[j] = true;
                    prev[j] = cur;
                    q.Enqueue(j);
                }
            }
        }

        if (!seen[goal])
            return null;
        var path = new List<int>();
        for (int c = goal; c >= 0; c = prev[c])
        {
            path.Add(c);
            if (c == start)
                break;
        }
        path.Reverse();
        return path;
    }

    sealed class Flow
    {
        struct E
        {
            public int to, rev, cap;
            public E(int to, int rev, int cap)
            {
                this.to = to;
                this.rev = rev;
                this.cap = cap;
            }
        }

        readonly List<E>[] g;
        readonly int[] level, it;
        readonly Queue<int> q;

        public Flow(int n)
        {
            g = new List<E>[n];
            for (int i = 0; i < n; i++)
                g[i] = new List<E>();
            level = new int[n];
            it = new int[n];
            q = new Queue<int>(n);
        }

        public void Add(int a, int b, int cap)
        {
            g[a].Add(new E(b, g[b].Count, cap));
            g[b].Add(new E(a, g[a].Count - 1, 0));
        }

        public int MaxFlow(int s, int t)
        {
            int flow = 0;
            while (Bfs(s, t))
            {
                Array.Clear(it, 0, it.Length);
                int f;
                while ((f = Dfs(s, t, Inf)) > 0)
                    flow += f;
            }
            return flow;
        }

        public bool[] ReachableFrom(int s)
        {
            var seen = new bool[g.Length];
            q.Clear();
            q.Enqueue(s);
            seen[s] = true;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                var es = g[u];
                for (int i = 0; i < es.Count; i++)
                {
                    E e = es[i];
                    if (e.cap <= 0 || seen[e.to])
                        continue;
                    seen[e.to] = true;
                    q.Enqueue(e.to);
                }
            }
            return seen;
        }

        /// <summary>Nodes that can reach sink in residual (for sink-side cut).</summary>
        public bool[] CanReachSink(int t)
        {
            var seen = new bool[g.Length];
            q.Clear();
            q.Enqueue(t);
            seen[t] = true;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                var es = g[u];
                for (int i = 0; i < es.Count; i++)
                {
                    E e = es[i];
                    // Walk residual backward: residual(to→u) lives on the reverse slot.
                    if (g[e.to][e.rev].cap <= 0 || seen[e.to])
                        continue;
                    seen[e.to] = true;
                    q.Enqueue(e.to);
                }
            }
            return seen;
        }

        public bool Saturated(int from, int to)
        {
            var es = g[from];
            for (int i = 0; i < es.Count; i++)
                if (es[i].to == to)
                    return es[i].cap == 0;
            return false;
        }

        bool Bfs(int s, int t)
        {
            for (int i = 0; i < level.Length; i++)
                level[i] = -1;
            q.Clear();
            level[s] = 0;
            q.Enqueue(s);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                var es = g[u];
                for (int i = 0; i < es.Count; i++)
                {
                    E e = es[i];
                    if (e.cap <= 0 || level[e.to] >= 0)
                        continue;
                    level[e.to] = level[u] + 1;
                    q.Enqueue(e.to);
                }
            }
            return level[t] >= 0;
        }

        int Dfs(int u, int t, int f)
        {
            if (u == t)
                return f;
            var es = g[u];
            for (; it[u] < es.Count; it[u]++)
            {
                E e = es[it[u]];
                if (e.cap <= 0 || level[e.to] != level[u] + 1)
                    continue;
                int pushed = Dfs(e.to, t, Math.Min(f, e.cap));
                if (pushed <= 0)
                    continue;
                e.cap -= pushed;
                es[it[u]] = e;
                E rev = g[e.to][e.rev];
                rev.cap += pushed;
                g[e.to][e.rev] = rev;
                return pushed;
            }
            return 0;
        }
    }

    // --- scoring / pen helpers ---

    static bool Score(MapData src, int budget, int[] wallIdx, out int score)
    {
        score = int.MinValue;
        if (wallIdx == null || wallIdx.Length > budget)
            return false;
        Vector2Int[] pos = ToPos(wallIdx, src.width);
        MapData map = MapScoring.ApplyWalls(src.Clone(), pos);
        if (MapPathfinder.CanEscape(map))
            return false;
        var bd = MapScoring.Recalculate(map, budget, pos);
        if (!bd.valid)
            return false;
        score = bd.totalScore;
        return true;
    }

    static bool[] PenMask(MapData src, int[] wallIdx)
    {
        MapData map = MapScoring.ApplyWalls(src.Clone(), ToPos(wallIdx, src.width));
        return MapScoring.BuildPenMask(map);
    }

    static HashSet<int> PenCells(bool[] pen)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < pen.Length; i++)
            if (pen[i])
                set.Add(i);
        return set;
    }

    static List<int> FrontierOutside(MapData src, bool[] pen)
    {
        var list = new List<int>();
        var seen = new HashSet<int>();
        for (int i = 0; i < pen.Length; i++)
        {
            if (!pen[i])
                continue;
            Vector2Int p = new Vector2Int(i % src.width, i / src.width);
            for (int d = 0; d < 4; d++)
            {
                Vector2Int q = p + Dirs[d];
                if (!src.InBounds(q) || src.IsEdge(q))
                    continue;
                if (!src.IsPassable(src.Get(q)))
                    continue;
                int j = src.Index(q.x, q.y);
                if (pen[j] || !seen.Add(j))
                    continue;
                list.Add(j);
            }
        }
        return list;
    }

    static int CellValue(MapData src, int idx)
    {
        CellType t = src.cells[idx];
        if (idx == src.Index(src.horse.x, src.horse.y))
            t = CellType.Horse;
        int v = MapScoring.PointsPerEnclosedCell;
        if (t == CellType.BonusA) v += MapScoring.BonusAExtra;
        else if (t == CellType.BonusB) v += MapScoring.BonusBExtra;
        else if (t == CellType.Bee) v += MapScoring.BeePenalty;
        return v;
    }

    static Vector2Int[] ToPos(int[] idx, int w)
    {
        var p = new Vector2Int[idx.Length];
        for (int i = 0; i < idx.Length; i++)
            p[i] = new Vector2Int(idx[i] % w, idx[i] / w);
        return p;
    }

    static MapSolution Ok(MapData src, int budget, int[] wallIdx, bool optimal, bool timedOut,
        int scoreBefore, int steps)
    {
        Vector2Int[] walls = ToPos(wallIdx, src.width);
        MapData solved = MapScoring.ApplyWalls(src.Clone(), walls);
        MapScoreBreakdown bd = MapScoring.Recalculate(solved, budget, walls);
        var r = Blank();
        if (!bd.valid || MapPathfinder.CanEscape(solved))
        {
            r.status = SolveStatus.FeasibleNotProvenOptimal;
            r.message = "Candidate failed verification.";
            return r;
        }

        r.found = true;
        r.walls = walls;
        r.breakdown = bd;
        r.optimalityProven = optimal;
        r.status = optimal ? SolveStatus.Optimal :
            (timedOut ? SolveStatus.Timeout : SolveStatus.FeasibleNotProvenOptimal);

        string note = steps > 0
            ? " Cut-grow +" + (bd.totalScore - scoreBefore) + " pts (" + steps + " steps)."
            : "";
        if (optimal)
            r.message = "OPTIMAL: Score " + bd.totalScore + ".";
        else if (timedOut)
            r.message = "TIME LIMIT: Score " + bd.totalScore + ", " + walls.Length + " walls." + note;
        else
            r.message = "MIN-CUT SEARCH: Score " + bd.totalScore + ", " + walls.Length + " walls." + note;
        return r;
    }

    static MapSolution Blank() => new MapSolution
    {
        found = false,
        optimalityProven = false,
        status = SolveStatus.Infeasible,
        message = "",
        walls = Array.Empty<Vector2Int>(),
        breakdown = default
    };
}
