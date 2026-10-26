using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Enclosure gameplay using the same passability / portal / escape rules as MapPathfinder.
/// The horse stays put. Click grass/bonus to place a wall; click a wall to remove it (refund).
/// Win when the horse can no longer reach the map edge.
/// </summary>
public class EnclosureGameController : MonoBehaviour
{
    BoardRenderer _board;
    MapData _map;
    int _wallsLeft;
    bool _escaped;
    bool _trapped;
    string _status = string.Empty;

    public string Status { get { return _status; } }
    public int WallsLeft { get { return _wallsLeft; } }
    public bool GameOver { get { return _escaped || _trapped; } }
    public MapData Map { get { return _map; } }

    public void Begin(MapData source, BoardRenderer board)
    {
        _board = board;
        _map = source.Clone();
        _wallsLeft = Mathf.Max(0, _map.wallBudget);
        _escaped = false;
        _trapped = false;
        _status = "Click grass to place wall, click wall again to remove. Budget: " + _wallsLeft;

        if (_map.InBounds(_map.horse))
            _map.Set(_map.horse, CellType.Horse);

        _board.Build(_map);
        EvaluateImmediateState();
        RefreshEnclosureVisual();
    }

    void Update()
    {
        if (_map == null || _escaped)
            return;

        if (!WasPrimaryClick(out Vector2 screenPos))
            return;

        Camera cam = Camera.main;
        if (cam == null)
            return;

        Vector2Int cell;
        if (!_board.ScreenToCell(screenPos, cam, out cell))
            return;

        if (_map.Get(cell) == CellType.Wall)
            TryRemoveWall(cell);
        else
            TryPlaceWall(cell);
    }

    static bool WasPrimaryClick(out Vector2 screenPos)
    {
        screenPos = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse == null)
            return false;
        if (!mouse.leftButton.wasPressedThisFrame)
            return false;
        screenPos = mouse.position.ReadValue();
        return true;
#else
        if (!Input.GetMouseButtonDown(0))
            return false;
        screenPos = Input.mousePosition;
        return true;
#endif
    }

    void OnGUI()
    {
        if (_map == null)
            return;

        const float pad = 10f;
        GUI.Box(new Rect(pad, pad, 460f, 70f), string.Empty);
        GUI.Label(new Rect(pad + 8f, pad + 8f, 440f, 54f),
            "Walls: " + _wallsLeft + "\n" + _status);
    }

    void TryPlaceWall(Vector2Int cell)
    {
        if (_wallsLeft <= 0)
        {
            _status = "No walls left. Click a wall to remove one.";
            return;
        }

        if (!_map.InBounds(cell))
            return;

        if (cell == _map.horse)
        {
            _status = "Cannot place wall on the horse.";
            return;
        }

        CellType t = _map.Get(cell);
        if (!MapScoring.IsWallPlaceable(t))
        {
            _status = "Walls only on grass/bonus cells.";
            return;
        }

        bool collectedBonus = t == CellType.BonusA || t == CellType.BonusB;
        _map.Set(cell, CellType.Wall);
        _wallsLeft--;
        if (collectedBonus)
        {
            _wallsLeft++;
            _status = "Bonus! +1 wall.";
        }
        else
        {
            _status = "Wall placed. Walls left: " + _wallsLeft;
        }

        EvaluateAfterEdit();
        RefreshEnclosureVisual();
    }

    void TryRemoveWall(Vector2Int cell)
    {
        if (!_map.InBounds(cell) || _map.Get(cell) != CellType.Wall)
            return;

        _map.Set(cell, CellType.Grass);
        _wallsLeft++;
        _status = "Wall removed (+1).";

        if (_escaped)
            return;

        EvaluateAfterEdit();
        RefreshEnclosureVisual();
    }

    void EvaluateAfterEdit()
    {
        if (_map.IsEdge(_map.horse))
        {
            _escaped = true;
            _trapped = false;
            _status = "Horse is on the boundary.";
            return;
        }

        if (!MapPathfinder.CanEscape(_map))
        {
            _trapped = true;
            _escaped = false;
            _status = "Enclosed! Horse is trapped. Click a wall to open a door.";
            return;
        }

        _trapped = false;
        _status = "Walls left: " + _wallsLeft + ". Click grass to place, wall to remove.";
    }

    void EvaluateImmediateState()
    {
        if (_map.IsEdge(_map.horse))
        {
            _escaped = true;
            _status = "Horse is already on the boundary.";
            return;
        }

        if (!MapPathfinder.CanEscape(_map))
        {
            _trapped = true;
            _status = "Enclosed! Horse is trapped. Click a wall to open a door.";
        }
    }

    void RefreshEnclosureVisual()
    {
        if (_board == null || _map == null)
            return;

        if (_trapped && !_escaped)
        {
            _board.SetEnclosedHighlight(MapScoring.BuildPenMask(_map));
            MapScoreBreakdown score = MapScoring.Recalculate(_map, _map.wallBudget);
            if (score.valid)
                _status = "Enclosed! Score " + score.totalScore
                          + " (cells " + score.enclosedCellCount
                          + ", A " + score.bonusAScore
                          + ", B " + score.bonusBScore
                          + ", bees " + score.beePenalty
                          + "). Click a wall to open a door.";
        }
        else
            _board.ClearEnclosedHighlight();
    }
}
