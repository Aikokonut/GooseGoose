using System.Collections.Generic;
using UnityEngine;

public struct EscapePath
{
    public bool found;
    public List<Vector2Int> cells;
    public List<bool> isTeleport;

    static readonly EscapePath Empty = new EscapePath
    {
        found = false,
        cells = null,
        isTeleport = null
    };

    public static EscapePath None()
    {
        return Empty;
    }
}

/// <summary>
/// BFS escape path: 4-dir moves, water/walls blocked, portals teleport to linked cell.
/// Escape = reach any map-edge cell. Does not mutate MapData.
/// </summary>
public static class MapPathfinder
{
    static readonly Vector2Int[] Dirs =
    {
        new Vector2Int(0, 1),
        new Vector2Int(1, 0),
        new Vector2Int(0, -1),
        new Vector2Int(-1, 0)
    };

    public static EscapePath FindEscape(MapData map)
    {
        EscapePath result = EscapePath.None();
        if (map == null || map.cells == null || map.width <= 0 || map.height <= 0)
            return result;

        Vector2Int start = map.horse;
        if (!map.InBounds(start) || !map.IsPassable(map.Get(start)))
            return result;

        int n = map.width * map.height;
        int[] parent = new int[n];
        bool[] cameByPortal = new bool[n];
        bool[] visited = new bool[n];
        for (int i = 0; i < n; i++)
            parent[i] = -1;

        Queue<Vector2Int> q = new Queue<Vector2Int>();
        int startIdx = map.Index(start.x, start.y);
        visited[startIdx] = true;
        q.Enqueue(start);

        Vector2Int end = new Vector2Int(-1, -1);
        bool found = false;

        while (q.Count > 0)
        {
            Vector2Int cur = q.Dequeue();

            if (map.IsEdge(cur))
            {
                end = cur;
                found = true;
                break;
            }

            // Portal teleport by link coordinates (works even if horse marker overwrites the cell type)
            if (map.TryGetPortalExit(cur, out Vector2Int exit))
            {
                if (map.InBounds(exit) && map.IsPassable(map.Get(exit)))
                {
                    int exitIdx = map.Index(exit.x, exit.y);
                    if (!visited[exitIdx])
                    {
                        visited[exitIdx] = true;
                        parent[exitIdx] = map.Index(cur.x, cur.y);
                        cameByPortal[exitIdx] = true;
                        q.Enqueue(exit);
                    }
                }
            }

            for (int d = 0; d < 4; d++)
            {
                Vector2Int next = cur + Dirs[d];
                if (!map.InBounds(next))
                    continue;

                CellType t = map.Get(next);
                if (!map.IsPassable(t))
                    continue;

                int nextIdx = map.Index(next.x, next.y);
                if (visited[nextIdx])
                    continue;

                visited[nextIdx] = true;
                parent[nextIdx] = map.Index(cur.x, cur.y);
                cameByPortal[nextIdx] = false;
                q.Enqueue(next);
            }
        }

        if (!found)
            return result;

        // Reconstruct path (reverse, then flip)
        List<Vector2Int> path = new List<Vector2Int>();
        List<bool> teleports = new List<bool>();
        int idx = map.Index(end.x, end.y);
        while (idx >= 0)
        {
            int x = idx % map.width;
            int y = idx / map.width;
            path.Add(new Vector2Int(x, y));
            teleports.Add(cameByPortal[idx]);
            idx = parent[idx];
        }

        path.Reverse();
        teleports.Reverse();
        // First cell is start — never a teleport step into start
        if (teleports.Count > 0)
            teleports[0] = false;

        result.found = true;
        result.cells = path;
        result.isTeleport = teleports;
        return result;
    }

    public static bool CanEscape(MapData map)
    {
        return FindEscape(map).found;
    }
}
