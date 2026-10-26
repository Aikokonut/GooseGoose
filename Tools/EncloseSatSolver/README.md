# EncloseSatSolver (OR-Tools CP-SAT)

External exact solver for GooseGoose, matching [enclose.horse](https://enclose.horse/edit) scoring:

- Grass +1, cherry +3, apple +10, bee −5
- Walls only on grass; edge grass allowed
- 4-neighbour + portals; escape = reach map edge

## Build (once)

```bash
cd Tools/EncloseSatSolver/EncloseSatSolver
dotnet build -c Release
dotnet publish -c Release -o ../publish
```

Requires [.NET 8+](https://dotnet.microsoft.com/download) runtime (SDK 8/9 is fine).

Unity `MapScoreSolver` looks for:

- `Tools/EncloseSatSolver/publish/EncloseSatSolver.exe`
- or `.../bin/Release/net8.0/EncloseSatSolver.exe`

## Manual test

```bash
EncloseSatSolver.exe --file map.json --time-sec 10
```

Input JSON: `width`, `height`, `budget`, `horseX`, `horseY`, `cells` (row-major enum ints), `portals` as `[ax,ay,bx,by],...`.
