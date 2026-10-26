using System.Text.Json;
using System.Text.Json.Serialization;

namespace EncloseSatSolver;

/// <summary>
/// CLI entry: reads map JSON from stdin or --file, writes solution JSON to stdout.
/// Usage: EncloseSatSolver [--file map.json] [--time-sec 30]
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string? file = null;
        double timeSec = 30;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--file" && i + 1 < args.Length)
                file = args[++i];
            else if (args[i] == "--time-sec" && i + 1 < args.Length)
                double.TryParse(args[++i], out timeSec);
        }

        string json;
        try
        {
            json = file != null ? File.ReadAllText(file) : Console.In.ReadToEnd();
        }
        catch (Exception ex)
        {
            WriteError("Failed to read input: " + ex.Message);
            return 2;
        }

        MapRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<MapRequest>(json, JsonOpts());
        }
        catch (Exception ex)
        {
            WriteError("Invalid JSON: " + ex.Message);
            return 2;
        }

        if (req == null || req.Cells == null || req.Width <= 0 || req.Height <= 0)
        {
            WriteError("Missing width/height/cells.");
            return 2;
        }

        var result = SatEnclosureSolver.Solve(req, timeSec);
        Console.Out.Write(JsonSerializer.Serialize(result, JsonOpts()));
        return result.Ok ? 0 : 1;
    }

    static void WriteError(string msg)
    {
        var err = new SolveResponse
        {
            Ok = false,
            Status = "Error",
            Message = msg,
            Walls = Array.Empty<int[]>(),
            Score = 0
        };
        Console.Out.Write(JsonSerializer.Serialize(err, JsonOpts()));
    }

    static JsonSerializerOptions JsonOpts() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

public sealed class MapRequest
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Budget { get; set; }
    public int HorseX { get; set; }
    public int HorseY { get; set; }
    /// <summary>Row-major cell types: 0 Grass, 1 Water, 2 Wall, 3 Horse, 4 BonusA, 5 Portal, 6 BonusB, 7 Bee.</summary>
    public int[]? Cells { get; set; }
    public int[][]? Portals { get; set; }
}

public sealed class SolveResponse
{
    public bool Ok { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public int Score { get; set; }
    public bool Optimal { get; set; }
    /// <summary>Wall positions as [x,y] pairs.</summary>
    public int[][] Walls { get; set; } = Array.Empty<int[]>();
}
