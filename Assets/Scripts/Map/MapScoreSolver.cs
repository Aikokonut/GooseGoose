using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
/// Enclosure maximizer. Prefers the external OR-Tools CP-SAT CLI
/// (Tools/EncloseSatSolver) — same model family as enclose.horse solvers.
/// Falls back to a verified min-cut incumbent if the CLI is missing.
/// Every returned solution is checked with <see cref="MapScoring.Recalculate"/>.
/// </summary>
public static class MapScoreSolver
{
    public static long TimeLimitMilliseconds = 30000;

    /// <summary>Optional override for the CP-SAT executable path.</summary>
    public static string SatSolverExePath;

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

        if (!MapPathfinder.CanEscape(source))
        {
            var zero = MapScoring.Recalculate(source, wallBudget, Array.Empty<Vector2Int>());
            if (zero.valid)
                return Finish(Array.Empty<Vector2Int>(), zero, true, false,
                    "OPTIMAL: Already enclosed. Score " + zero.totalScore + ".");
            fail.message = "Horse cannot escape but score invalid: " + zero.message;
            fail.status = SolveStatus.Infeasible;
            return fail;
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

        // Primary: OR-Tools CP-SAT via CLI.
        if (TrySolveWithSatCli(source, wallBudget, limitMs, out MapSolution sat))
            return sat;

        // Fallback: verified min-cut seeds (not claimed optimal).
        int[] bestIdx = null;
        int bestScore = int.MinValue;
        var sw = Stopwatch.StartNew();
        SeedMinCutIncumbent(source, wallBudget, sw, limitMs, ref bestIdx, ref bestScore);
        if (bestIdx == null)
        {
            fail.status = SolveStatus.Timeout;
            fail.message = "TIMEOUT: CP-SAT CLI not found/failed and min-cut found no enclosure. " +
                           "Build Tools/EncloseSatSolver (dotnet build -c Release).";
            return fail;
        }

        Vector2Int[] walls = ToPos(bestIdx, source.width);
        MapScoreBreakdown bd = MapScoring.Recalculate(
            MapScoring.ApplyWalls(source.Clone(), walls), wallBudget, walls);
        if (!bd.valid)
        {
            fail.message = "Min-cut candidate failed verify: " + bd.message;
            return fail;
        }

        return Finish(walls, bd, false, sw.ElapsedMilliseconds >= limitMs,
            "FEASIBLE (min-cut fallback, not CP-SAT): Score " + bd.totalScore +
            ", " + walls.Length + " walls. Build EncloseSatSolver for optimal.");
    }

    static bool TrySolveWithSatCli(MapData source, int budget, long limitMs, out MapSolution solution)
    {
        solution = null;
        string exe = ResolveSatExe();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return false;

        string tmpDir = Path.Combine(Application.temporaryCachePath, "EncloseSat");
        Directory.CreateDirectory(tmpDir);
        string inFile = Path.Combine(tmpDir, "map_" + Process.GetCurrentProcess().Id + ".json");
        string outFile = Path.Combine(tmpDir, "sol_" + Process.GetCurrentProcess().Id + ".json");

        try
        {
            File.WriteAllText(inFile, BuildRequestJson(source, budget), Encoding.UTF8);
            double timeSec = Math.Max(0.5, limitMs / 1000.0);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--file \"" + inFile + "\" --time-sec " +
                            timeSec.ToString(System.Globalization.CultureInfo.InvariantCulture),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? tmpDir
            };

            string stdout;
            string stderr;
            int exitCode;
            using (var p = Process.Start(psi))
            {
                if (p == null)
                    return false;
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit((int)limitMs + 5000))
                {
                    try { p.Kill(); } catch { /* ignore */ }
                    return false;
                }
                exitCode = p.ExitCode;
            }

            if (!string.IsNullOrEmpty(stderr))
                UnityEngine.Debug.LogWarning("[MapScoreSolver] CP-SAT stderr: " + stderr);

            if (string.IsNullOrWhiteSpace(stdout))
                return false;

            if (!TryParseCliResponse(stdout, out bool ok, out string status, out string message,
                    out bool optimal, out int cliScore, out Vector2Int[] walls))
                return false;

            if (!ok || walls == null)
            {
                // Still a structured failure from CP-SAT (infeasible / timeout with no sol).
                if (status == "INFEASIBLE" || status == "Infeasible" || status == "MODEL_INVALID")
                {
                    solution = Blank();
                    solution.status = SolveStatus.Infeasible;
                    solution.message = "CP-SAT: " + message;
                    return true;
                }
                return false;
            }

            MapData applied = MapScoring.ApplyWalls(source.Clone(), walls);
            MapScoreBreakdown bd = MapScoring.Recalculate(applied, budget, walls);
            if (!bd.valid || MapPathfinder.CanEscape(applied))
                return false;

            // Prefer Unity-verified score.
            bool timedOut = status.IndexOf("Feasible", StringComparison.OrdinalIgnoreCase) >= 0 && !optimal;
            string msg = message;
            if (string.IsNullOrEmpty(msg))
                msg = (optimal ? "OPTIMAL" : "FEASIBLE") + " CP-SAT score " + bd.totalScore +
                      " (cli " + cliScore + "), " + walls.Length + " walls.";

            solution = Finish(walls, bd, optimal, timedOut && !optimal, msg);
            if (!optimal && timedOut)
                solution.status = SolveStatus.Timeout;
            else if (!optimal)
                solution.status = SolveStatus.FeasibleNotProvenOptimal;
            return true;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("[MapScoreSolver] CP-SAT CLI failed: " + ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(inFile)) File.Delete(inFile); } catch { /* ignore */ }
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* ignore */ }
        }
    }

    static string ResolveSatExe()
    {
        if (!string.IsNullOrEmpty(SatSolverExePath) && File.Exists(SatSolverExePath))
            return SatSolverExePath;

        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string[] candidates =
        {
            Path.Combine(root, "Tools", "EncloseSatSolver", "publish", "EncloseSatSolver.exe"),
            Path.Combine(root, "Tools", "EncloseSatSolver", "publish", "EncloseSatSolver"),
            Path.Combine(root, "Tools", "EncloseSatSolver", "EncloseSatSolver", "bin", "Release", "net8.0", "win-x64", "EncloseSatSolver.exe"),
            Path.Combine(root, "Tools", "EncloseSatSolver", "EncloseSatSolver", "bin", "Release", "net8.0", "EncloseSatSolver.exe"),
            Path.Combine(root, "Tools", "EncloseSatSolver", "EncloseSatSolver", "bin", "Release", "net8.0", "EncloseSatSolver"),
            Path.Combine(root, "Tools", "EncloseSatSolver", "EncloseSatSolver", "bin", "Debug", "net8.0", "EncloseSatSolver.exe"),
        };
        for (int i = 0; i < candidates.Length; i++)
            if (File.Exists(candidates[i]))
                return candidates[i];
        return null;
    }

    static string BuildRequestJson(MapData map, int budget)
    {
        var sb = new StringBuilder(256 + map.cells.Length * 2);
        sb.Append("{\"width\":").Append(map.width)
          .Append(",\"height\":").Append(map.height)
          .Append(",\"budget\":").Append(budget)
          .Append(",\"horseX\":").Append(map.horse.x)
          .Append(",\"horseY\":").Append(map.horse.y)
          .Append(",\"cells\":[");
        for (int i = 0; i < map.cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append((int)map.cells[i]);
        }
        sb.Append("],\"portals\":[");
        if (map.portals != null)
        {
            for (int i = 0; i < map.portals.Length; i++)
            {
                if (i > 0) sb.Append(',');
                PortalLink p = map.portals[i];
                sb.Append('[').Append(p.a.x).Append(',').Append(p.a.y).Append(',')
                  .Append(p.b.x).Append(',').Append(p.b.y).Append(']');
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static bool TryParseCliResponse(string json, out bool ok, out string status, out string message,
        out bool optimal, out int score, out Vector2Int[] walls)
    {
        ok = false;
        status = "";
        message = "";
        optimal = false;
        score = 0;
        walls = null;

        try
        {
            // Minimal JSON scrape (avoid JsonUtility needing [Serializable] wrappers for jagged arrays).
            ok = ExtractBool(json, "ok");
            status = ExtractString(json, "status") ?? "";
            message = ExtractString(json, "message") ?? "";
            optimal = ExtractBool(json, "optimal");
            score = ExtractInt(json, "score");

            int wallsIdx = json.IndexOf("\"walls\"", StringComparison.OrdinalIgnoreCase);
            if (wallsIdx < 0)
                return ok;

            int arrStart = json.IndexOf('[', wallsIdx);
            if (arrStart < 0)
                return ok;

            var list = new List<Vector2Int>();
            int i = arrStart + 1;
            while (i < json.Length)
            {
                while (i < json.Length && (json[i] == ' ' || json[i] == ',' || json[i] == '\n' || json[i] == '\r'))
                    i++;
                if (i >= json.Length || json[i] == ']')
                    break;
                if (json[i] != '[')
                    break;
                i++;
                int x = ReadInt(json, ref i);
                while (i < json.Length && json[i] != ',' && json[i] != ']') i++;
                if (i < json.Length && json[i] == ',') i++;
                int y = ReadInt(json, ref i);
                while (i < json.Length && json[i] != ']') i++;
                if (i < json.Length && json[i] == ']') i++;
                list.Add(new Vector2Int(x, y));
            }

            walls = list.ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool ExtractBool(string json, string key)
    {
        string v = ExtractRaw(json, key);
        return v != null && v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    static int ExtractInt(string json, string key)
    {
        string v = ExtractRaw(json, key);
        return int.TryParse(v, out int n) ? n : 0;
    }

    static string ExtractString(string json, string key)
    {
        int k = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
        if (k < 0) return null;
        int colon = json.IndexOf(':', k);
        if (colon < 0) return null;
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    static string ExtractRaw(string json, string key)
    {
        int k = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
        if (k < 0) return null;
        int colon = json.IndexOf(':', k);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        int start = i;
        while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']')
            i++;
        return json.Substring(start, i - start).Trim().Trim('"');
    }

    static int ReadInt(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        int sign = 1;
        if (i < s.Length && s[i] == '-') { sign = -1; i++; }
        int v = 0;
        while (i < s.Length && char.IsDigit(s[i]))
        {
            v = v * 10 + (s[i] - '0');
            i++;
        }
        return v * sign;
    }

    // --- min-cut fallback ---------------------------------------------------

    const int Inf = 1 << 20;

    static void SeedMinCutIncumbent(MapData src, int budget, Stopwatch sw, long limitMs,
        ref int[] bestWallIdx, ref int bestScore)
    {
        int maxDist = src.width + src.height;
        for (int d = 0; d <= maxDist && sw.ElapsedMilliseconds < limitMs; d++)
        {
            TryMinCutSeed(src, budget, false, d, ref bestWallIdx, ref bestScore);
            TryMinCutSeed(src, budget, true, d, ref bestWallIdx, ref bestScore);
        }
    }

    static void TryMinCutSeed(MapData src, int budget, bool sinkSide, int minWallDist,
        ref int[] bestWallIdx, ref int bestScore)
    {
        if (!TryVertexCut(src, sinkSide, minWallDist, out int[] walls))
            return;
        if (walls.Length == 0 || walls.Length > budget)
            return;
        Vector2Int[] pos = ToPos(walls, src.width);
        MapScoreBreakdown bd = MapScoring.Recalculate(
            MapScoring.ApplyWalls(src.Clone(), pos), budget, pos);
        if (!bd.valid)
            return;
        if (bd.totalScore > bestScore ||
            (bd.totalScore == bestScore && (bestWallIdx == null || walls.Length < bestWallIdx.Length)))
        {
            bestScore = bd.totalScore;
            bestWallIdx = walls;
        }
    }

    static bool TryVertexCut(MapData map, bool sinkSide, int minWallDist, out int[] wallIndices)
    {
        wallIndices = null;
        int n = map.width * map.height;
        int srcN = n * 2;
        int snk = srcN + 1;
        var flow = new Flow(n * 2 + 2);
        int horse = map.Index(map.horse.x, map.horse.y);
        int hx = map.horse.x;
        int hy = map.horse.y;

        for (int y = 0; y < map.height; y++)
        {
            for (int x = 0; x < map.width; x++)
            {
                Vector2Int p = new Vector2Int(x, y);
                int idx = map.Index(x, y);
                CellType type = map.Get(p);
                if (!map.IsPassable(type))
                    continue;

                int vin = idx * 2;
                int vout = vin + 1;
                bool placeable = MapScoring.IsWallPlaceable(type) && idx != horse;
                if (placeable && minWallDist > 0)
                {
                    int dist = Mathf.Abs(x - hx) + Mathf.Abs(y - hy);
                    if (dist < minWallDist)
                        placeable = false;
                }
                flow.Add(vin, vout, placeable ? 1 : Inf);
                if (map.IsEdge(p))
                    flow.Add(vout, snk, Inf);

                for (int d = 0; d < 4; d++)
                {
                    Vector2Int q = p + Dirs[d];
                    if (!map.InBounds(q) || !map.IsPassable(map.Get(q)))
                        continue;
                    flow.Add(vout, map.Index(q.x, q.y) * 2, Inf);
                }
            }
        }

        flow.Add(srcN, horse * 2, Inf);
        if (map.portals != null)
        {
            for (int i = 0; i < map.portals.Length; i++)
            {
                PortalLink link = map.portals[i];
                if (!map.InBounds(link.a) || !map.InBounds(link.b))
                    continue;
                if (!map.IsPassable(map.Get(link.a)) || !map.IsPassable(map.Get(link.b)))
                    continue;
                int a = map.Index(link.a.x, link.a.y);
                int b = map.Index(link.b.x, link.b.y);
                flow.Add(a * 2 + 1, b * 2, Inf);
                flow.Add(b * 2 + 1, a * 2, Inf);
            }
        }

        if (flow.MaxFlow(srcN, snk) >= Inf)
            return false;

        bool[] mark = sinkSide ? flow.CanReachSink(snk) : flow.ReachableFrom(srcN);
        var walls = new List<int>();
        for (int idx = 0; idx < n; idx++)
        {
            if (idx == horse || !MapScoring.IsWallPlaceable(map.Get(
                    new Vector2Int(idx % map.width, idx / map.width))))
                continue;
            int vin = idx * 2, vout = vin + 1;
            bool inCut = sinkSide ? (!mark[vin] && mark[vout]) : (mark[vin] && !mark[vout]);
            if (inCut)
                walls.Add(idx);
        }

        wallIndices = walls.ToArray();
        Array.Sort(wallIndices);
        Vector2Int[] pos = ToPos(wallIndices, map.width);
        return MapScoring.Recalculate(MapScoring.ApplyWalls(map.Clone(), pos),
            Math.Max(n, wallIndices.Length), pos).valid;
    }

    sealed class Flow
    {
        struct Edge { public int to, rev, cap; }
        readonly List<Edge>[] _g;
        readonly int _n;

        public Flow(int n)
        {
            _n = n;
            _g = new List<Edge>[n];
            for (int i = 0; i < n; i++)
                _g[i] = new List<Edge>();
        }

        public void Add(int a, int b, int cap)
        {
            _g[a].Add(new Edge { to = b, rev = _g[b].Count, cap = cap });
            _g[b].Add(new Edge { to = a, rev = _g[a].Count - 1, cap = 0 });
        }

        public int MaxFlow(int s, int t)
        {
            int flow = 0;
            var level = new int[_n];
            var iter = new int[_n];
            while (Bfs(s, t, level))
            {
                Array.Clear(iter, 0, _n);
                int f;
                while ((f = Dfs(s, t, Inf, level, iter)) > 0)
                    flow += f;
            }
            return flow;
        }

        bool Bfs(int s, int t, int[] level)
        {
            for (int i = 0; i < _n; i++) level[i] = -1;
            var q = new Queue<int>();
            level[s] = 0;
            q.Enqueue(s);
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                for (int i = 0; i < _g[v].Count; i++)
                {
                    Edge e = _g[v][i];
                    if (e.cap > 0 && level[e.to] < 0)
                    {
                        level[e.to] = level[v] + 1;
                        q.Enqueue(e.to);
                    }
                }
            }
            return level[t] >= 0;
        }

        int Dfs(int v, int t, int f, int[] level, int[] iter)
        {
            if (v == t) return f;
            for (; iter[v] < _g[v].Count; iter[v]++)
            {
                Edge e = _g[v][iter[v]];
                if (e.cap <= 0 || level[v] >= level[e.to]) continue;
                int d = Dfs(e.to, t, Math.Min(f, e.cap), level, iter);
                if (d <= 0) continue;
                Edge e2 = e; e2.cap -= d; _g[v][iter[v]] = e2;
                Edge rev = _g[e.to][e.rev]; rev.cap += d; _g[e.to][e.rev] = rev;
                return d;
            }
            return 0;
        }

        public bool[] ReachableFrom(int s)
        {
            var seen = new bool[_n];
            var q = new Queue<int>();
            q.Enqueue(s); seen[s] = true;
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                for (int i = 0; i < _g[v].Count; i++)
                {
                    Edge e = _g[v][i];
                    if (e.cap > 0 && !seen[e.to]) { seen[e.to] = true; q.Enqueue(e.to); }
                }
            }
            return seen;
        }

        public bool[] CanReachSink(int t)
        {
            var seen = new bool[_n];
            var q = new Queue<int>();
            q.Enqueue(t); seen[t] = true;
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                for (int u = 0; u < _n; u++)
                {
                    if (seen[u]) continue;
                    for (int i = 0; i < _g[u].Count; i++)
                    {
                        if (_g[u][i].to == v && _g[u][i].cap > 0)
                        {
                            seen[u] = true;
                            q.Enqueue(u);
                            break;
                        }
                    }
                }
            }
            return seen;
        }
    }

    static Vector2Int[] ToPos(int[] idx, int width)
    {
        var a = new Vector2Int[idx.Length];
        for (int i = 0; i < idx.Length; i++)
            a[i] = new Vector2Int(idx[i] % width, idx[i] / width);
        return a;
    }

    static MapSolution Finish(Vector2Int[] walls, MapScoreBreakdown bd, bool proven, bool timedOut, string message)
    {
        var r = Blank();
        r.found = true;
        r.walls = walls;
        r.breakdown = bd;
        r.optimalityProven = proven;
        r.status = proven ? SolveStatus.Optimal :
            (timedOut ? SolveStatus.Timeout : SolveStatus.FeasibleNotProvenOptimal);
        r.message = message;
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
