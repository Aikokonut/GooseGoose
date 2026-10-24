using System;
using UnityEngine;

[Serializable]
public struct PortalLink
{
    public Vector2Int a;
    public Vector2Int b;

    public PortalLink(Vector2Int a, Vector2Int b)
    {
        this.a = a;
        this.b = b;
    }

    public bool TryGetExit(Vector2Int from, out Vector2Int to)
    {
        if (from == a)
        {
            to = b;
            return true;
        }

        if (from == b)
        {
            to = a;
            return true;
        }

        to = default;
        return false;
    }
}

[Serializable]
public class MapData
{
    public int width;
    public int height;
    public int seed;
    public int wallBudget;
    public Vector2Int horse;
    public CellType[] cells;
    public PortalLink[] portals;

    public MapData(int width, int height, int seed)
    {
        this.width = width;
        this.height = height;
        this.seed = seed;
        wallBudget = 0;
        horse = new Vector2Int(-1, -1);
        cells = new CellType[width * height];
        portals = Array.Empty<PortalLink>();
    }

    public MapData Clone()
    {
        MapData copy = new MapData(width, height, seed);
        copy.wallBudget = wallBudget;
        copy.horse = horse;
        if (cells != null)
        {
            copy.cells = new CellType[cells.Length];
            Array.Copy(cells, copy.cells, cells.Length);
        }

        if (portals != null)
        {
            copy.portals = new PortalLink[portals.Length];
            Array.Copy(portals, copy.portals, portals.Length);
        }
        else
        {
            copy.portals = Array.Empty<PortalLink>();
        }

        return copy;
    }

    public int Index(int x, int y)
    {
        return y * width + x;
    }

    public bool InBounds(int x, int y)
    {
        return x >= 0 && y >= 0 && x < width && y < height;
    }

    public bool InBounds(Vector2Int p)
    {
        return InBounds(p.x, p.y);
    }

    public CellType Get(int x, int y)
    {
        return cells[Index(x, y)];
    }

    public CellType Get(Vector2Int p)
    {
        return Get(p.x, p.y);
    }

    public void Set(int x, int y, CellType type)
    {
        cells[Index(x, y)] = type;
    }

    public void Set(Vector2Int p, CellType type)
    {
        Set(p.x, p.y, type);
    }

    public bool IsEdge(int x, int y)
    {
        return x == 0 || y == 0 || x == width - 1 || y == height - 1;
    }

    public bool IsEdge(Vector2Int p)
    {
        return IsEdge(p.x, p.y);
    }

    public bool TryGetPortalExit(Vector2Int from, out Vector2Int to)
    {
        if (portals == null)
        {
            to = default;
            return false;
        }

        for (int i = 0; i < portals.Length; i++)
        {
            if (portals[i].TryGetExit(from, out to))
                return true;
        }

        to = default;
        return false;
    }

    public bool IsPassable(CellType type)
    {
        return type != CellType.Water && type != CellType.Wall;
    }
}
