using UnityEngine;

/// <summary>
/// Level initialization pipeline. Loads MapData via LevelSession (Play Test override
/// or default level) and starts EnclosureGameController.
/// </summary>
public class LevelBootstrap : MonoBehaviour
{
    [SerializeField] MapLevelAsset defaultLevel;
    [SerializeField] BoardRenderer boardPrefab;
    [SerializeField] bool createBoardIfMissing = true;

    EnclosureGameController _game;
    string _bootError;

    public string BootError { get { return _bootError; } }
    public EnclosureGameController Game { get { return _game; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootPlayTest()
    {
        if (!LevelSession.IsPlayTestActive())
            return;

        LevelBootstrap existing = Object.FindFirstObjectByType<LevelBootstrap>();
        if (existing != null)
            return;

        GameObject go = new GameObject("LevelBootstrap_PlayTest");
        go.AddComponent<LevelBootstrap>();
    }

    void Start()
    {
        Boot();
    }

    public void Boot()
    {
        _bootError = null;

        MapData map;
        string error;
        if (!LevelSession.TryResolve(defaultLevel, out map, out error))
        {
            _bootError = error;
            Debug.LogError("[LevelBootstrap] " + error);
            return;
        }

        BoardRenderer board = boardPrefab;
        if (board == null)
            board = GetComponent<BoardRenderer>();
        if (board == null && createBoardIfMissing)
            board = gameObject.AddComponent<BoardRenderer>();

        if (board == null)
        {
            _bootError = "BoardRenderer is missing.";
            Debug.LogError("[LevelBootstrap] " + _bootError);
            return;
        }

        _game = GetComponent<EnclosureGameController>();
        if (_game == null)
            _game = gameObject.AddComponent<EnclosureGameController>();

        EnsureCamera();
        _game.Begin(map, board);

        if (LevelSession.IsPlayTestActive())
            Debug.Log("[LevelBootstrap] Play Test map loaded. Seed=" + map.seed +
                      " size=" + map.width + "x" + map.height +
                      " walls=" + map.wallBudget);
    }

    void OnGUI()
    {
        if (string.IsNullOrEmpty(_bootError))
            return;

        GUI.color = Color.red;
        GUI.Box(new Rect(10, 90, 520, 60), string.Empty);
        GUI.Label(new Rect(18, 98, 500, 50), "Level load failed:\n" + _bootError);
        GUI.color = Color.white;
    }

    static void EnsureCamera()
    {
        if (Camera.main != null)
            return;

        GameObject camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        Camera cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        camGo.AddComponent<AudioListener>();
    }
}
