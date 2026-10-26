using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class MapGeneratorWindow : EditorWindow
{
    enum PaintTool
    {
        Grass,
        Water,
        Wall,
        Horse,
        BonusA,
        BonusB,
        Bee,
        Portal
    }

    MapGenSettings _settings = new MapGenSettings();
    MapData _map;
    string _status = "Generate a map to begin.";
    Vector2 _scroll;

    bool _hoverHorse;
    EscapePath _escapePath;
    bool _pathDirty = true;
    int _cachedMapVersion;
    int _mapVersion;

    PaintTool _tool = PaintTool.Grass;
    Vector2Int? _pendingPortal;
    Vector2Int _lastPaintCell = new Vector2Int(int.MinValue, int.MinValue);

    MapSolution _solution;
    bool _showSolution = true;

    [MenuItem("GooseGoose/Map Generator")]
    public static void Open()
    {
        MapGeneratorWindow w = GetWindow<MapGeneratorWindow>("Map Generator");
        w.minSize = new Vector2(520, 640);
    }

    void OnEnable()
    {
        wantsMouseMove = true;
    }

    void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawSettings();
        DrawButtons();
        DrawPaintToolbar();
        DrawSolverControls();
        EditorGUILayout.Space(6);
        EditorGUILayout.HelpBox(_status, MessageType.Info);
        DrawSolutionPanel();
        EditorGUILayout.Space(8);
        DrawPreview();

        EditorGUILayout.EndScrollView();

        if (Event.current.type == EventType.MouseMove)
            Repaint();
    }

    void DrawSettings()
    {
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        _settings.width = EditorGUILayout.IntSlider("Width", _settings.width, 8, 32);
        _settings.height = EditorGUILayout.IntSlider("Height", _settings.height, 8, 32);
        _settings.seed = EditorGUILayout.IntField("Seed", _settings.seed);
        _settings.wallBudget = EditorGUILayout.IntSlider("Wall Budget", _settings.wallBudget, 0, 60);
        _settings.waterDensity = EditorGUILayout.Slider("Water Density", _settings.waterDensity, 0f, 0.4f);
        _settings.waterBlobMin = EditorGUILayout.IntSlider("Water Segment Min", _settings.waterBlobMin, 2, 8);
        _settings.waterBlobMax = EditorGUILayout.IntSlider("Water Segment Max", _settings.waterBlobMax, 3, 14);
        _settings.bonusCount = EditorGUILayout.IntSlider("Bonuses", _settings.bonusCount, 0, 8);
        _settings.portalPairs = EditorGUILayout.IntSlider("Portal Pairs", _settings.portalPairs, 0, 4);
        EditorGUILayout.HelpBox(
            "Paint tools edit the current map in place. Score: +1/enclosed cell, cherry +3, apple +10, bee −5 (enclose.horse). Generate only keeps playable enclosure cuts.",
            MessageType.None);
    }

    void DrawButtons()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Generate", GUILayout.Height(28)))
            DoGenerate();
        if (GUILayout.Button("Validate", GUILayout.Height(28)))
            DoValidate();
        if (GUILayout.Button("Save", GUILayout.Height(28)))
            DoSave();
        if (GUILayout.Button("Play Test", GUILayout.Height(28)))
            DoPlayTest();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Randomize Everything"))
        {
            RandomizeAllSettings();
            DoGenerate();
        }
        EditorGUILayout.EndHorizontal();
    }

    void DrawPaintToolbar()
    {
        EditorGUILayout.LabelField("Paint Tool", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawToolToggle(PaintTool.Grass, "Grass");
        DrawToolToggle(PaintTool.Water, "Water");
        DrawToolToggle(PaintTool.Wall, "Wall");
        DrawToolToggle(PaintTool.Horse, "Horse");
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        DrawToolToggle(PaintTool.BonusA, "BonusA");
        DrawToolToggle(PaintTool.BonusB, "BonusB");
        DrawToolToggle(PaintTool.Bee, "Bee");
        DrawToolToggle(PaintTool.Portal, "Portal");
        EditorGUILayout.EndHorizontal();
        if (_tool == PaintTool.Portal)
            EditorGUILayout.LabelField(_pendingPortal.HasValue
                ? "Portal: click second cell to link."
                : "Portal: click first endpoint.");
    }

    void DrawToolToggle(PaintTool tool, string label)
    {
        bool on = _tool == tool;
        if (GUILayout.Toggle(on, label, EditorStyles.miniButton) && !on)
        {
            _tool = tool;
            _pendingPortal = null;
        }
    }

    void DrawSolverControls()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Find Best Score", GUILayout.Height(28)))
            DoFindBestScore();
        if (GUILayout.Button(_showSolution ? "Hide Solution" : "Show Solution", GUILayout.Height(28)))
            _showSolution = !_showSolution;
        if (GUILayout.Button("Clear Solution", GUILayout.Height(28)))
        {
            _solution = null;
            _status = "Solution cleared.";
        }
        EditorGUILayout.EndHorizontal();
    }

    void DrawSolutionPanel()
    {
        if (_solution == null)
            return;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Solver Result", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Status", StatusLabel(_solution.status));
        if (!_solution.found)
        {
            MessageType mt = _solution.status == SolveStatus.Timeout ? MessageType.Warning : MessageType.Error;
            EditorGUILayout.HelpBox(_solution.message, mt);
            return;
        }

        MapScoreBreakdown b = _solution.breakdown;
        EditorGUILayout.LabelField("Maximum total score", b.totalScore.ToString());
        EditorGUILayout.LabelField("Enclosed score cells", b.enclosedCellCount.ToString());
        EditorGUILayout.LabelField("Cherry (BonusA)", b.bonusACount + " × " + MapScoring.BonusAExtra + " = " + b.bonusAScore);
        EditorGUILayout.LabelField("Apple (BonusB)", b.bonusBCount + " × " + MapScoring.BonusBExtra + " = " + b.bonusBScore);
        EditorGUILayout.LabelField("Bees", b.beeCount + " × " + MapScoring.BeePenalty + " = " + b.beePenalty);
        EditorGUILayout.LabelField("Walls required", (b.wallPositions != null ? b.wallPositions.Length : 0).ToString());
        if (b.wallPositions != null && b.wallPositions.Length > 0)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int show = Mathf.Min(b.wallPositions.Length, 24);
            for (int i = 0; i < show; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("(").Append(b.wallPositions[i].x).Append(",").Append(b.wallPositions[i].y).Append(")");
            }
            if (b.wallPositions.Length > show)
                sb.Append(" …");
            EditorGUILayout.LabelField("Wall positions", sb.ToString());
        }
        EditorGUILayout.LabelField("Optimality proven", _solution.optimalityProven ? "Yes" : "No");
        MessageType boxType = _solution.status == SolveStatus.Optimal ? MessageType.Info : MessageType.Warning;
        EditorGUILayout.HelpBox(_solution.message, boxType);
    }

    static string StatusLabel(SolveStatus s)
    {
        switch (s)
        {
            case SolveStatus.Optimal: return "OPTIMAL";
            case SolveStatus.Infeasible: return "INFEASIBLE";
            case SolveStatus.Timeout: return "TIMEOUT";
            case SolveStatus.FeasibleNotProvenOptimal: return "FEASIBLE (NOT PROVEN OPTIMAL)";
            default: return s.ToString();
        }
    }

    void DrawPreview()
    {
        if (_map == null)
        {
            EditorGUILayout.LabelField("No map yet.");
            return;
        }

        float cell = MapPreviewDrawer.PreferredCellSize;
        float mapW = _map.width * cell;
        float mapH = _map.height * cell;
        Rect area = GUILayoutUtility.GetRect(mapW + 16f, mapH + 16f, GUILayout.ExpandWidth(true));
        Rect mapRect = MapPreviewDrawer.GetMapRect(area, _map, out _);

        HandlePaint(mapRect);
        HandleHover(mapRect);

        if (_pathDirty || _cachedMapVersion != _mapVersion)
        {
            RecalcPath();
            _cachedMapVersion = _mapVersion;
            _pathDirty = false;
        }

        MapPreviewDrawer.Draw(
            mapRect,
            _map,
            _hoverHorse ? _escapePath : EscapePath.None(),
            _hoverHorse && !_escapePath.found,
            _solution,
            _showSolution,
            _pendingPortal);

        if (_hoverHorse)
        {
            string msg = _escapePath.found
                ? "Escape path: " + (_escapePath.cells != null ? _escapePath.cells.Count : 0) + " steps."
                : "Trapped — no route to boundary.";
            EditorGUILayout.LabelField(msg);
        }
        else
            EditorGUILayout.LabelField("Hover H for escape path. Drag with a paint tool to edit.");
    }

    void HandlePaint(Rect mapRect)
    {
        Event e = Event.current;
        if (e == null || _map == null)
            return;

        bool paintEvent = (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0;
        if (!paintEvent)
        {
            if (e.type == EventType.MouseUp)
                _lastPaintCell = new Vector2Int(int.MinValue, int.MinValue);
            return;
        }

        // Don't steal clicks from GUI controls above the map.
        if (!mapRect.Contains(e.mousePosition))
            return;

        if (!MapPreviewDrawer.TryGetCellAt(mapRect, _map, e.mousePosition, out Vector2Int cell))
            return;

        if (e.type == EventType.MouseDrag && cell == _lastPaintCell)
            return;

        if (ApplyPaint(cell))
        {
            _lastPaintCell = cell;
            e.Use();
            Repaint();
        }
    }

    bool ApplyPaint(Vector2Int cell)
    {
        if (!_map.InBounds(cell))
            return false;

        InvalidateSolution();

        if (_tool == PaintTool.Portal)
        {
            PaintPortal(cell);
            NotifyMapEdited();
            return true;
        }

        if (_tool == PaintTool.Horse)
        {
            PaintHorse(cell);
            NotifyMapEdited();
            return true;
        }

        // Painting over a portal endpoint dissolves its pair.
        DissolvePortalAt(cell);

        CellType type = ToolToCell(_tool);
        if (cell == _map.horse && type != CellType.Horse)
        {
            // Moving terrain under horse without Horse tool clears horse marker position invalidly —
            // require Horse tool to relocate.
            if (type == CellType.Water || type == CellType.Wall || type == CellType.Bee)
            {
                _status = "Cannot place impassable/bee on horse. Move horse first.";
                return false;
            }
        }

        _map.Set(cell, type);
        if (cell == _map.horse && type != CellType.Horse)
            _map.Set(cell, CellType.Horse);

        NotifyMapEdited();
        _status = "Painted " + type + " at (" + cell.x + "," + cell.y + ").";
        return true;
    }

    void PaintHorse(Vector2Int cell)
    {
        DissolvePortalAt(cell);
        if (_map.InBounds(_map.horse) && _map.horse != cell)
        {
            if (_map.Get(_map.horse) == CellType.Horse)
                _map.Set(_map.horse, CellType.Grass);
        }
        _map.horse = cell;
        _map.Set(cell, CellType.Horse);
        _status = "Horse moved to (" + cell.x + "," + cell.y + ").";
    }

    void PaintPortal(Vector2Int cell)
    {
        if (cell == _map.horse)
        {
            _status = "Portal cannot overlap horse.";
            return;
        }

        if (!_pendingPortal.HasValue)
        {
            DissolvePortalAt(cell);
            _pendingPortal = cell;
            _map.Set(cell, CellType.Portal);
            _status = "Portal endpoint A set. Click endpoint B.";
            return;
        }

        Vector2Int a = _pendingPortal.Value;
        if (a == cell)
        {
            _status = "Portal endpoints must differ.";
            return;
        }

        DissolvePortalAt(cell);
        _map.Set(a, CellType.Portal);
        _map.Set(cell, CellType.Portal);

        List<PortalLink> links = new List<PortalLink>();
        if (_map.portals != null)
        {
            for (int i = 0; i < _map.portals.Length; i++)
                links.Add(_map.portals[i]);
        }
        links.Add(new PortalLink(a, cell));
        _map.portals = links.ToArray();
        _pendingPortal = null;
        _status = "Portal linked (" + a.x + "," + a.y + ") ↔ (" + cell.x + "," + cell.y + ").";
    }

    void DissolvePortalAt(Vector2Int cell)
    {
        if (_map.portals == null || _map.portals.Length == 0)
            return;

        List<PortalLink> kept = new List<PortalLink>();
        for (int i = 0; i < _map.portals.Length; i++)
        {
            PortalLink p = _map.portals[i];
            if (p.a == cell || p.b == cell)
            {
                if (_map.InBounds(p.a) && _map.Get(p.a) == CellType.Portal && p.a != cell)
                    _map.Set(p.a, CellType.Grass);
                if (_map.InBounds(p.b) && _map.Get(p.b) == CellType.Portal && p.b != cell)
                    _map.Set(p.b, CellType.Grass);
                continue;
            }
            kept.Add(p);
        }
        _map.portals = kept.ToArray();
    }

    static CellType ToolToCell(PaintTool tool)
    {
        switch (tool)
        {
            case PaintTool.Water: return CellType.Water;
            case PaintTool.Wall: return CellType.Wall;
            case PaintTool.Horse: return CellType.Horse;
            case PaintTool.BonusA: return CellType.BonusA;
            case PaintTool.BonusB: return CellType.BonusB;
            case PaintTool.Bee: return CellType.Bee;
            case PaintTool.Portal: return CellType.Portal;
            default: return CellType.Grass;
        }
    }

    void NotifyMapEdited()
    {
        if (_map != null)
            _map.wallBudget = _settings.wallBudget;
        _mapVersion++;
        _pathDirty = true;
    }

    void InvalidateSolution()
    {
        if (_solution != null)
        {
            _solution = null;
            _status = "Map edited — previous solution cleared.";
        }
    }

    void HandleHover(Rect mapRect)
    {
        Event e = Event.current;
        if (e == null)
            return;
        if (e.type != EventType.MouseMove && e.type != EventType.MouseDrag && e.type != EventType.Repaint)
            return;

        bool wasHover = _hoverHorse;
        bool nowHover = false;
        if (MapPreviewDrawer.TryGetCellAt(mapRect, _map, e.mousePosition, out Vector2Int cell))
            nowHover = cell == _map.horse;

        if (nowHover != wasHover)
        {
            _hoverHorse = nowHover;
            _pathDirty = true;
            Repaint();
        }
    }

    void RecalcPath()
    {
        if (_map == null || !_hoverHorse)
        {
            _escapePath = EscapePath.None();
            return;
        }
        _escapePath = MapPathfinder.FindEscape(_map);
    }

    void RandomizeAllSettings()
    {
        _settings.seed = Random.Range(1, int.MaxValue);
        System.Random rng = new System.Random(_settings.seed);
        _settings.width = rng.Next(12, 25);
        _settings.height = rng.Next(12, 25);
        _settings.wallBudget = rng.Next(8, 25);
        _settings.waterDensity = (float)(0.18 + rng.NextDouble() * 0.17);
        _settings.waterBlobMin = rng.Next(2, 5);
        _settings.waterBlobMax = rng.Next(Mathf.Max(4, _settings.waterBlobMin + 2), 10);
        _settings.bonusCount = rng.Next(1, 7);
        _settings.portalPairs = rng.Next(0, 3);
    }

    void DoGenerate()
    {
        _map = MapGenerator.Generate(_settings);
        _mapVersion++;
        _pathDirty = true;
        _hoverHorse = false;
        _escapePath = EscapePath.None();
        _pendingPortal = null;
        InvalidateSolution();
        _solution = null;

        if (_map == null)
        {
            _status = "No playable map for seed " + _settings.seed +
                      ". Need a cut of 3.." + _settings.wallBudget +
                      " walls. Try Randomize, raise Wall Budget, or adjust water/size.";
            Repaint();
            return;
        }

        _map.wallBudget = _settings.wallBudget;
        MapValidationResult v = MapValidator.ValidateForPlay(_map, _settings.wallBudget);
        _status = "Generated seed " + _settings.seed + ". " + v.message;
        Repaint();
    }

    void DoValidate()
    {
        if (_map == null)
        {
            _status = "No map to validate.";
            return;
        }
        _map.wallBudget = _settings.wallBudget;
        MapValidationResult v = MapValidator.ValidateForPlay(_map, _settings.wallBudget);
        _status = v.message;
        Repaint();
    }

    void DoFindBestScore()
    {
        if (_map == null)
        {
            _status = "No map to solve.";
            return;
        }

        _map.wallBudget = _settings.wallBudget;
        MapValidationResult basic = MapValidator.Validate(_map);
        if (!basic.ok)
        {
            _status = "Solve blocked: " + basic.message;
            _solution = null;
            return;
        }

        _status = "Solving…";
        Repaint();

        long prevLimit = MapScoreSolver.TimeLimitMilliseconds;
        try
        {
            // Keep editor responsive but allow a real exact search.
            // CP-SAT CLI usually finishes well under this; acts as hard cap.
            MapScoreSolver.TimeLimitMilliseconds = 30000;
            _solution = MapScoreSolver.Solve(_map, _settings.wallBudget);
        }
        finally
        {
            MapScoreSolver.TimeLimitMilliseconds = prevLimit;
        }

        _showSolution = _solution.found;
        _status = _solution.message;
        Repaint();
    }

    void DoSave()
    {
        if (_map == null)
        {
            _status = "No map to save.";
            return;
        }

        MapValidationResult v = MapValidator.ValidateForPlay(_map, _settings.wallBudget);
        if (!v.ok)
        {
            _status = "Save blocked: " + v.message;
            return;
        }

        string dir = "Assets/Maps";
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder("Assets", "Maps");

        string path = EditorUtility.SaveFilePanelInProject(
            "Save Map",
            "Map_" + _settings.seed,
            "json",
            "Save map JSON",
            dir);

        if (string.IsNullOrEmpty(path))
            return;

        _map.wallBudget = _settings.wallBudget;
        string json = JsonUtility.ToJson(new MapSaveDto(_map), true);
        File.WriteAllText(path, json);
        AssetDatabase.Refresh();
        _status = "Saved: " + path;
    }

    void DoPlayTest()
    {
        if (_map == null)
        {
            _status = "Play Test blocked: generate a map first.";
            EditorUtility.DisplayDialog("Play Test", _status, "OK");
            return;
        }

        _map.wallBudget = _settings.wallBudget;

        MapValidationResult playCheck = MapValidator.ValidateForPlay(_map, _settings.wallBudget);
        if (!playCheck.ok)
        {
            _status = "Play Test blocked: " + playCheck.message;
            EditorUtility.DisplayDialog("Play Test", _status, "OK");
            return;
        }

        string error;
        if (!PlayTestSession.TryArm(_map, _settings.wallBudget, out error))
        {
            _status = error;
            EditorUtility.DisplayDialog("Play Test", error, "OK");
            return;
        }

        string scenePath = "Assets/Scenes/SampleScene.unity";
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
        {
            _status = "Play Test blocked: gameplay scene missing at " + scenePath;
            PlayTestSession.Disarm();
            EditorUtility.DisplayDialog("Play Test", _status, "OK");
            return;
        }

        var active = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        if (active.path != scenePath)
        {
            if (active.isDirty)
            {
                if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    _status = "Play Test cancelled: unsaved scene changes.";
                    PlayTestSession.Disarm();
                    return;
                }
            }
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath);
        }

        if (EditorApplication.isPlaying)
        {
            _status = "Exit Play Mode before starting a new Play Test.";
            EditorUtility.DisplayDialog("Play Test", _status, "OK");
            return;
        }

        _status = "Entering Play Mode with map (seed " + _map.seed + ", walls " + _map.wallBudget + ").";
        EditorApplication.isPlaying = true;
    }

    [System.Serializable]
    class MapSaveDto
    {
        public int width;
        public int height;
        public int seed;
        public int wallBudget;
        public int horseX;
        public int horseY;
        public int[] cells;
        public int[] portalFlat;

        public MapSaveDto(MapData map)
        {
            width = map.width;
            height = map.height;
            seed = map.seed;
            wallBudget = map.wallBudget;
            horseX = map.horse.x;
            horseY = map.horse.y;
            cells = new int[map.cells.Length];
            for (int i = 0; i < map.cells.Length; i++)
                cells[i] = (int)map.cells[i];

            int n = map.portals != null ? map.portals.Length : 0;
            portalFlat = new int[n * 4];
            for (int i = 0; i < n; i++)
            {
                portalFlat[i * 4] = map.portals[i].a.x;
                portalFlat[i * 4 + 1] = map.portals[i].a.y;
                portalFlat[i * 4 + 2] = map.portals[i].b.x;
                portalFlat[i * 4 + 3] = map.portals[i].b.y;
            }
        }
    }
}
