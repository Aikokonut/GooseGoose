using UnityEditor;
using UnityEngine;

public static class MapPreviewDrawer
{
    const float CellSize = 22f;
    static GUIStyle _labelStyle;
    static GUIStyle _trappedStyle;

    public static float PreferredCellSize => CellSize;

    static readonly Color SolutionWall = new Color(0.15f, 0.15f, 0.15f, 0.95f);
    static readonly Color EnclosedTint = new Color(1f, 0.92f, 0.2f, 0.55f);
    static readonly Color BonusHighlight = new Color(1f, 0.4f, 0.1f, 0.65f);
    static readonly Color BeeHighlight = new Color(0.9f, 0.85f, 0.1f, 0.65f);

    static GUIStyle LabelStyle
    {
        get
        {
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold
                };
                _labelStyle.normal.textColor = Color.black;
            }
            return _labelStyle;
        }
    }

    static GUIStyle TrappedStyle
    {
        get
        {
            if (_trappedStyle == null)
            {
                _trappedStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter
                };
                _trappedStyle.normal.textColor = Color.white;
            }
            return _trappedStyle;
        }
    }

    public static Rect GetMapRect(Rect area, MapData map, out float cell)
    {
        cell = CellSize;
        if (map == null || map.width <= 0 || map.height <= 0)
            return new Rect(area.x, area.y, 0, 0);

        float w = map.width * cell;
        float h = map.height * cell;
        float x = area.x + (area.width - w) * 0.5f;
        float y = area.y + 4f;
        return new Rect(x, y, w, h);
    }

    public static bool TryGetCellAt(Rect mapRect, MapData map, Vector2 mouse, out Vector2Int cell)
    {
        cell = default;
        if (map == null || mapRect.width <= 0f)
            return false;

        float cellSize = mapRect.width / map.width;
        if (!mapRect.Contains(mouse))
            return false;

        int x = Mathf.FloorToInt((mouse.x - mapRect.x) / cellSize);
        int y = Mathf.FloorToInt((mouse.y - mapRect.y) / cellSize);
        if (!map.InBounds(x, y))
            return false;

        cell = new Vector2Int(x, y);
        return true;
    }

    public static void Draw(
        Rect mapRect,
        MapData map,
        EscapePath path,
        bool showTrapped,
        MapSolution solution,
        bool showSolution,
        Vector2Int? pendingPortal)
    {
        if (map == null || map.cells == null)
            return;

        float cell = mapRect.width / map.width;

        for (int y = 0; y < map.height; y++)
        {
            for (int x = 0; x < map.width; x++)
            {
                Rect r = new Rect(mapRect.x + x * cell, mapRect.y + y * cell, cell - 1f, cell - 1f);
                EditorGUI.DrawRect(r, ColorFor(map.Get(x, y)));

                string label = LabelFor(map.Get(x, y));
                if (!string.IsNullOrEmpty(label))
                    GUI.Label(r, label, LabelStyle);
            }
        }

        if (showSolution && solution != null && solution.found && solution.breakdown.enclosedMask != null)
        {
            bool[] mask = solution.breakdown.enclosedMask;
            for (int i = 0; i < mask.Length && i < map.cells.Length; i++)
            {
                if (!mask[i])
                    continue;
                int x = i % map.width;
                int y = i / map.width;
                Rect r = new Rect(mapRect.x + x * cell, mapRect.y + y * cell, cell - 1f, cell - 1f);
                CellType t = map.Get(x, y);
                if (t == CellType.BonusA || t == CellType.BonusB)
                    EditorGUI.DrawRect(r, BonusHighlight);
                else if (t == CellType.Bee)
                    EditorGUI.DrawRect(r, BeeHighlight);
                else if (t != CellType.Water && t != CellType.Wall)
                    EditorGUI.DrawRect(r, EnclosedTint);
            }

            if (solution.walls != null)
            {
                for (int i = 0; i < solution.walls.Length; i++)
                {
                    Vector2Int w = solution.walls[i];
                    Rect r = new Rect(mapRect.x + w.x * cell, mapRect.y + w.y * cell, cell - 1f, cell - 1f);
                    EditorGUI.DrawRect(r, SolutionWall);
                    GUI.Label(r, "W", LabelStyle);
                }
            }
        }

        if (map.portals != null)
        {
            Handles.BeginGUI();
            for (int i = 0; i < map.portals.Length; i++)
            {
                PortalLink p = map.portals[i];
                Vector2 a = CellCenter(mapRect, cell, p.a);
                Vector2 b = CellCenter(mapRect, cell, p.b);
                Handles.color = new Color(0.7f, 0.2f, 0.9f, 0.45f);
                Handles.DrawAAPolyLine(2f, a, b);
            }
            Handles.EndGUI();
        }

        if (pendingPortal.HasValue)
        {
            Vector2Int p = pendingPortal.Value;
            Rect r = new Rect(mapRect.x + p.x * cell, mapRect.y + p.y * cell, cell - 1f, cell - 1f);
            EditorGUI.DrawRect(r, new Color(1f, 1f, 1f, 0.45f));
            GUI.Label(r, "P?", LabelStyle);
        }

        if (path.found && path.cells != null && path.cells.Count > 0)
        {
            Handles.BeginGUI();
            for (int i = 0; i < path.cells.Count; i++)
            {
                Vector2Int c = path.cells[i];
                Rect r = new Rect(mapRect.x + c.x * cell, mapRect.y + c.y * cell, cell - 1f, cell - 1f);
                EditorGUI.DrawRect(r, new Color(1f, 0.85f, 0.1f, 0.35f));

                if (i > 0)
                {
                    Vector2 from = CellCenter(mapRect, cell, path.cells[i - 1]);
                    Vector2 to = CellCenter(mapRect, cell, c);
                    bool portalStep = path.isTeleport != null && i < path.isTeleport.Count && path.isTeleport[i];
                    Handles.color = portalStep
                        ? new Color(0.85f, 0.2f, 1f, 0.95f)
                        : new Color(1f, 0.55f, 0f, 0.95f);
                    Handles.DrawAAPolyLine(portalStep ? 4f : 3f, from, to);
                }
            }
            Handles.EndGUI();
        }
        else if (showTrapped)
        {
            Rect badge = new Rect(mapRect.x + 4f, mapRect.y + 4f, 72f, 22f);
            EditorGUI.DrawRect(badge, new Color(0.75f, 0.1f, 0.1f, 0.9f));
            GUI.Label(badge, "Trapped", TrappedStyle);
        }
    }

    static Vector2 CellCenter(Rect mapRect, float cell, Vector2Int p)
    {
        return new Vector2(mapRect.x + (p.x + 0.5f) * cell, mapRect.y + (p.y + 0.5f) * cell);
    }

    public static Color ColorFor(CellType t)
    {
        switch (t)
        {
            case CellType.Grass: return new Color(0.45f, 0.75f, 0.35f);
            case CellType.Water: return new Color(0.25f, 0.45f, 0.9f);
            case CellType.Wall: return new Color(0.35f, 0.28f, 0.22f);
            case CellType.Horse: return new Color(0.95f, 0.75f, 0.25f);
            case CellType.BonusA: return new Color(0.95f, 0.55f, 0.2f);
            case CellType.BonusB: return new Color(1f, 0.35f, 0.15f);
            case CellType.Bee: return new Color(0.95f, 0.9f, 0.25f);
            case CellType.Portal: return new Color(0.75f, 0.4f, 0.95f);
            default: return Color.gray;
        }
    }

    public static string LabelFor(CellType t)
    {
        switch (t)
        {
            case CellType.Horse: return "H";
            case CellType.BonusA: return "A";
            case CellType.BonusB: return "B";
            case CellType.Bee: return "Z";
            case CellType.Portal: return "P";
            case CellType.Wall: return "#";
            default: return string.Empty;
        }
    }
}
