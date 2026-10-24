using UnityEngine;

/// <summary>
/// Builds and refreshes a simple colored-quad board that matches editor preview colors.
/// Optional enclosed-region overlay tints the trapped horse's reachable area yellow.
/// </summary>
public class BoardRenderer : MonoBehaviour
{
    public float cellSize = 1f;

    Transform _root;
    SpriteRenderer[] _cells;
    TextMesh[] _labels;
    MapData _map;
    Texture2D _white;
    bool[] _enclosedHighlight;

    static readonly Color Grass = new Color(0.45f, 0.75f, 0.35f);
    static readonly Color Water = new Color(0.25f, 0.45f, 0.9f);
    static readonly Color Wall = new Color(0.35f, 0.28f, 0.22f);
    static readonly Color Horse = new Color(0.95f, 0.75f, 0.25f);
    static readonly Color Bonus = new Color(0.95f, 0.55f, 0.2f);
    static readonly Color Portal = new Color(0.75f, 0.4f, 0.95f);
    static readonly Color Enclosed = new Color(0.95f, 0.88f, 0.2f);

    public void Build(MapData map)
    {
        _map = map;
        _enclosedHighlight = null;
        Clear();

        if (map == null)
            return;

        EnsureWhiteSprite();

        GameObject rootGo = new GameObject("BoardCells");
        rootGo.transform.SetParent(transform, false);
        _root = rootGo.transform;

        int n = map.width * map.height;
        _cells = new SpriteRenderer[n];
        _labels = new TextMesh[n];

        Vector3 origin = GetOrigin(map);

        for (int y = 0; y < map.height; y++)
        {
            for (int x = 0; x < map.width; x++)
            {
                int idx = map.Index(x, y);
                GameObject cellGo = new GameObject("Cell_" + x + "_" + y);
                cellGo.transform.SetParent(_root, false);
                cellGo.transform.localPosition = origin + new Vector3(x * cellSize, -y * cellSize, 0f);

                SpriteRenderer sr = cellGo.AddComponent<SpriteRenderer>();
                sr.sprite = Sprite.Create(_white, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
                sr.transform.localScale = new Vector3(cellSize * 0.92f, cellSize * 0.92f, 1f);
                _cells[idx] = sr;

                GameObject labelGo = new GameObject("Label");
                labelGo.transform.SetParent(cellGo.transform, false);
                labelGo.transform.localPosition = new Vector3(0f, 0f, -0.1f);
                TextMesh tm = labelGo.AddComponent<TextMesh>();
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.characterSize = 0.12f;
                tm.fontSize = 32;
                tm.color = Color.black;
                tm.GetComponent<MeshRenderer>().sortingOrder = 2;
                _labels[idx] = tm;
            }
        }

        Refresh();
        FrameCamera(map);
    }

    public void SetEnclosedHighlight(bool[] highlight)
    {
        _enclosedHighlight = highlight;
        Refresh();
    }

    public void ClearEnclosedHighlight()
    {
        _enclosedHighlight = null;
        Refresh();
    }

    public void Refresh()
    {
        if (_map == null || _cells == null)
            return;

        for (int y = 0; y < _map.height; y++)
        {
            for (int x = 0; x < _map.width; x++)
            {
                int idx = _map.Index(x, y);
                CellType t = _map.Get(x, y);
                bool isHorse = _map.horse.x == x && _map.horse.y == y;
                if (isHorse)
                    t = CellType.Horse;

                Color color = ColorFor(t);
                if (_enclosedHighlight != null
                    && idx < _enclosedHighlight.Length
                    && _enclosedHighlight[idx]
                    && t != CellType.Water
                    && t != CellType.Wall)
                {
                    // Keep horse readable; tint the pen yellow.
                    color = isHorse ? Color.Lerp(Horse, Enclosed, 0.55f) : Enclosed;
                }

                _cells[idx].color = color;
                _labels[idx].text = LabelFor(t);
            }
        }
    }

    public bool ScreenToCell(Vector3 screenPos, Camera cam, out Vector2Int cell)
    {
        cell = default;
        if (_map == null || cam == null)
            return false;

        Ray ray = cam.ScreenPointToRay(screenPos);
        Plane plane = new Plane(Vector3.forward, transform.position);
        float enter;
        if (!plane.Raycast(ray, out enter))
            return false;

        Vector3 world = ray.GetPoint(enter);
        Vector3 local = transform.InverseTransformPoint(world);
        Vector3 origin = GetOrigin(_map);

        float lx = local.x - origin.x + cellSize * 0.5f;
        float ly = origin.y - local.y + cellSize * 0.5f;
        int x = Mathf.FloorToInt(lx / cellSize);
        int y = Mathf.FloorToInt(ly / cellSize);
        if (!_map.InBounds(x, y))
            return false;

        cell = new Vector2Int(x, y);
        return true;
    }

    void FrameCamera(MapData map)
    {
        Camera cam = Camera.main;
        if (cam == null)
            return;

        cam.orthographic = true;
        float w = map.width * cellSize;
        float h = map.height * cellSize;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.orthographicSize = Mathf.Max(h * 0.55f, w * 0.55f / Mathf.Max(0.01f, cam.aspect));
    }

    Vector3 GetOrigin(MapData map)
    {
        float w = map.width * cellSize;
        float h = map.height * cellSize;
        return new Vector3(-w * 0.5f + cellSize * 0.5f, h * 0.5f - cellSize * 0.5f, 0f);
    }

    void EnsureWhiteSprite()
    {
        if (_white != null)
            return;
        _white = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        Color[] px = new Color[16];
        for (int i = 0; i < 16; i++)
            px[i] = Color.white;
        _white.SetPixels(px);
        _white.Apply();
    }

    void Clear()
    {
        if (_root != null)
            Destroy(_root.gameObject);
        _root = null;
        _cells = null;
        _labels = null;
    }

    static Color ColorFor(CellType t)
    {
        switch (t)
        {
            case CellType.Water: return Water;
            case CellType.Wall: return Wall;
            case CellType.Horse: return Horse;
            case CellType.BonusA: return Bonus;
            case CellType.BonusB: return new Color(1f, 0.35f, 0.15f);
            case CellType.Bee: return new Color(0.95f, 0.9f, 0.2f);
            case CellType.Portal: return Portal;
            default: return Grass;
        }
    }

    static string LabelFor(CellType t)
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
