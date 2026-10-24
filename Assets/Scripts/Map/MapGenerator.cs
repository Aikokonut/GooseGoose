using System;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class MapGenSettings
{
    public int width = 16;
    public int height = 16;
    public int seed = 1;
    public int wallBudget = 12;
    [Range(0f, 0.45f)] public float waterDensity = 0.24f;
    [Range(2, 8)] public int waterBlobMin = 3;
    [Range(3, 14)] public int waterBlobMax = 8;
    public int bonusCount = 3;
    public int portalPairs = 1;
}

/// <summary>
/// Generates enclosure-puzzle terrain, not a maze:
/// - water is made from thin connected orthogonal formations, never filled blobs;
/// - walls are NOT pre-spawned (wallBudget is the player's available budget);
/// - portal pairs are spread apart and bonuses are spatially distributed;
/// - only returns maps the player can enclose with a non-trivial wall cut within budget.
/// </summary>
public static class MapGenerator
{
    private const int CandidateAttempts = 128;
    private const int MinUsefulWallCut = 3;
    private const int FlowInfinity = 1000000;

    private static readonly Vector2Int[] Directions =
    {
        new Vector2Int(0, 1),
        new Vector2Int(1, 0),
        new Vector2Int(0, -1),
        new Vector2Int(-1, 0)
    };

    public static MapData Generate(MapGenSettings settings)
    {
        if (settings == null)
            settings = new MapGenSettings();

        int width = Mathf.Clamp(settings.width, 3, 32);
        int height = Mathf.Clamp(settings.height, 3, 32);
        int minLength = Mathf.Clamp(settings.waterBlobMin, 2, 14);
        int maxLength = Mathf.Clamp(settings.waterBlobMax, minLength, 14);
        float density = Mathf.Clamp(settings.waterDensity, 0f, 0.45f);
        int wallBudget = Mathf.Max(0, settings.wallBudget);
        int bonusCount = Mathf.Max(0, settings.bonusCount);
        int portalPairs = Mathf.Max(0, settings.portalPairs);

        // One RNG per generation makes a seed fully reproducible.
        System.Random rng = new System.Random(settings.seed);
        MapData bestFeasible = null;
        float bestFeasibleScore = float.MinValue;

        for (int attempt = 0; attempt < CandidateAttempts; attempt++)
        {
            MapData candidate = CreateEmptyMap(width, height, settings.seed);
            int targetWater = GetTargetWaterCount(candidate, density);
            PlaceWaterFormations(candidate, targetWater, minLength, maxLength, rng);

            if (!PlaceHorseOnEscapableGrass(candidate, rng))
                continue;

            // Portals are added only while the enclosure cut stays in the playable band.
            PlacePortals(candidate, portalPairs, wallBudget, rng);
            PlaceBonuses(candidate, bonusCount, rng);

            MapValidationResult validation = MapValidator.Validate(candidate);
            if (!validation.ok)
                continue;

            int minimumWalls = GetMinimumWallsToEnclose(candidate);
            if (!IsPlayableEnclosureCut(minimumWalls, wallBudget))
                continue;

            float score = EvaluateCandidate(candidate, targetWater, minimumWalls, wallBudget);
            if (score > bestFeasibleScore)
            {
                bestFeasible = candidate;
                bestFeasibleScore = score;
            }
        }

        if (bestFeasible == null)
            return null;

        bestFeasible.wallBudget = wallBudget;
        return bestFeasible;
    }

    /// <summary>
    /// Non-trivial cut the player can afford: at least MinUsefulWallCut walls,
    /// and no more than the configured budget.
    /// </summary>
    public static bool IsPlayableEnclosureCut(int minimumWalls, int wallBudget)
    {
        if (minimumWalls == int.MaxValue || minimumWalls <= 0)
            return false;
        if (wallBudget < MinUsefulWallCut)
            return false;
        return minimumWalls >= MinUsefulWallCut && minimumWalls <= wallBudget;
    }

    private static MapData CreateEmptyMap(int width, int height, int seed)
    {
        MapData map = new MapData(width, height, seed);
        for (int i = 0; i < map.cells.Length; i++)
            map.cells[i] = CellType.Grass;

        map.portals = Array.Empty<PortalLink>();
        return map;
    }

    private static int GetTargetWaterCount(MapData map, float density)
    {
        int areaTarget = Mathf.RoundToInt(map.width * map.height * density);
        int interiorArea = Mathf.Max(0, (map.width - 2) * (map.height - 2));

        // Prevent high slider values from turning the whole map into water.
        int structuredLimit = Mathf.RoundToInt(interiorArea * 0.30f);
        return Mathf.Clamp(areaTarget, 0, structuredLimit);
    }

    private static void PlaceWaterFormations(MapData map, int target, int minLength, int maxLength, System.Random rng)
    {
        if (target <= 0 || map.width < 5 || map.height < 5)
            return;

        int hardLimit = Mathf.Min(
            (map.width - 2) * (map.height - 2),
            target + Mathf.Max(2, Mathf.CeilToInt(target * 0.10f)));
        int maxFormations = Mathf.Clamp(Mathf.CeilToInt(target / 10f), 2, 16);
        int formations = 0;
        int failedAttempts = 0;

        while (CountWater(map) < target && formations < maxFormations && failedAttempts < maxFormations * 3)
        {
            int before = CountWater(map);
            int kind = rng.Next(4);

            if (target - before <= Mathf.Max(5, minLength * 3))
                DrawShortBand(map, minLength, maxLength, hardLimit, rng);
            else if (kind == 0)
                DrawUFormation(map, minLength, maxLength, hardLimit, rng);
            else if (kind == 1)
                DrawLFormation(map, minLength, maxLength, hardLimit, rng);
            else
                DrawBentFormation(map, minLength, maxLength, hardLimit, rng);

            int after = CountWater(map);
            if (after > before)
            {
                formations++;
                failedAttempts = 0;
            }
            else
            {
                failedAttempts++;
            }
        }
    }

    private static void DrawUFormation(MapData map, int minLength, int maxLength, int hardLimit, System.Random rng)
    {
        int maxSpanX = Mathf.Min(maxLength, map.width - 3);
        int maxSpanY = Mathf.Min(maxLength, map.height - 3);
        if (maxSpanX < 2 || maxSpanY < 2)
        {
            DrawBentFormation(map, minLength, maxLength, hardLimit, rng);
            return;
        }

        int spanX = RandomLength(rng, Mathf.Min(minLength, maxSpanX), maxSpanX);
        int spanY = RandomLength(rng, Mathf.Min(minLength, maxSpanY), maxSpanY);
        int x = rng.Next(1, map.width - spanX - 1);
        int y = rng.Next(1, map.height - spanY - 1);
        bool baseAtBottom = rng.Next(2) == 0;
        int baseY = baseAtBottom ? y : y + spanY;
        int openY = baseAtBottom ? y + spanY : y;
        int before = CountWater(map);

        DrawLine(map, new Vector2Int(x, baseY), new Vector2Int(x + spanX, baseY), hardLimit);
        DrawLine(map, new Vector2Int(x, baseY), new Vector2Int(x, openY), hardLimit);
        DrawLine(map, new Vector2Int(x + spanX, baseY), new Vector2Int(x + spanX, openY), hardLimit);

        // A failed/overlapping U is harmless; later attempts choose another formation.
        if (CountWater(map) == before)
            DrawBentFormation(map, minLength, maxLength, hardLimit, rng);
    }

    private static void DrawLFormation(MapData map, int minLength, int maxLength, int hardLimit, System.Random rng)
    {
        Vector2Int start = FindOpenStart(map, rng);
        if (start.x < 0)
            return;

        bool horizontalFirst = rng.Next(2) == 0;
        int firstLength = RandomLength(rng, minLength, maxLength);
        int secondLength = RandomLength(rng, minLength, maxLength);
        Vector2Int cursor = start;

        AddWater(map, cursor, hardLimit);
        if (!DrawSegment(map, ref cursor, horizontalFirst, firstLength, hardLimit, rng))
            return;

        DrawSegment(map, ref cursor, !horizontalFirst, secondLength, hardLimit, rng);
    }

    private static void DrawBentFormation(MapData map, int minLength, int maxLength, int hardLimit, System.Random rng)
    {
        Vector2Int start = FindOpenStart(map, rng);
        if (start.x < 0)
            return;

        Vector2Int cursor = start;
        AddWater(map, cursor, hardLimit);
        bool horizontal = rng.Next(2) == 0;
        int segments = rng.Next(3, 5);

        for (int i = 0; i < segments; i++)
        {
            int length = RandomLength(rng, minLength, maxLength);
            if (!DrawSegment(map, ref cursor, horizontal, length, hardLimit, rng))
            {
                horizontal = !horizontal;
                if (!DrawSegment(map, ref cursor, horizontal, length, hardLimit, rng))
                    break;
            }
            horizontal = !horizontal;
        }
    }

    private static void DrawShortBand(MapData map, int minLength, int maxLength, int hardLimit, System.Random rng)
    {
        Vector2Int start = FindOpenStart(map, rng);
        if (start.x < 0)
            return;

        Vector2Int cursor = start;
        AddWater(map, cursor, hardLimit);
        DrawSegment(map, ref cursor, rng.Next(2) == 0, RandomLength(rng, 2, Mathf.Max(2, Mathf.Min(maxLength, 5))), hardLimit, rng);
    }

    private static bool DrawSegment(MapData map, ref Vector2Int cursor, bool horizontal, int requestedLength, int hardLimit, System.Random rng)
    {
        Vector2Int positive = horizontal ? Vector2Int.right : Vector2Int.up;
        Vector2Int negative = -positive;
        int positiveRoom = AvailableSteps(map, cursor, positive);
        int negativeRoom = AvailableSteps(map, cursor, negative);
        if (positiveRoom < 1 && negativeRoom < 1)
            return false;

        Vector2Int direction;
        int room;
        if (positiveRoom < 1)
        {
            direction = negative;
            room = negativeRoom;
        }
        else if (negativeRoom < 1)
        {
            direction = positive;
            room = positiveRoom;
        }
        else if (rng.Next(2) == 0)
        {
            direction = positive;
            room = positiveRoom;
        }
        else
        {
            direction = negative;
            room = negativeRoom;
        }

        int length = Mathf.Min(requestedLength, room);
        if (length < 1)
            return false;

        bool moved = false;
        for (int i = 0; i < length; i++)
        {
            Vector2Int next = cursor + direction;
            if (!IsInterior(map, next))
                break;

            CellType type = map.Get(next);
            if (type != CellType.Grass && type != CellType.Water)
                break;

            if (type == CellType.Grass && !AddWater(map, next, hardLimit))
                break;

            cursor = next;
            moved = true;
        }
        return moved;
    }

    private static int AvailableSteps(MapData map, Vector2Int start, Vector2Int direction)
    {
        int steps = 0;
        Vector2Int p = start + direction;
        while (IsInterior(map, p))
        {
            steps++;
            p += direction;
        }
        return steps;
    }

    private static void DrawLine(MapData map, Vector2Int a, Vector2Int b, int hardLimit)
    {
        if (a.x != b.x && a.y != b.y)
            return;

        Vector2Int direction = new Vector2Int(Math.Sign(b.x - a.x), Math.Sign(b.y - a.y));
        Vector2Int p = a;
        while (true)
        {
            if (!IsInterior(map, p))
                return;

            CellType type = map.Get(p);
            if (type != CellType.Grass && type != CellType.Water)
                return;
            if (type == CellType.Grass && !AddWater(map, p, hardLimit))
                return;
            if (p == b)
                return;
            p += direction;
        }
    }

    private static bool AddWater(MapData map, Vector2Int p, int hardLimit)
    {
        if (!IsInterior(map, p))
            return false;
        CellType type = map.Get(p);
        if (type == CellType.Water)
            return true;
        if (type != CellType.Grass || CountWater(map) >= hardLimit)
            return false;

        map.Set(p, CellType.Water);
        return true;
    }

    private static Vector2Int FindOpenStart(MapData map, System.Random rng)
    {
        Vector2Int best = new Vector2Int(-1, -1);
        int bestDistance = int.MinValue;
        int samples = Mathf.Min(40, Mathf.Max(12, map.width * map.height / 8));

        for (int i = 0; i < samples; i++)
        {
            int x = rng.Next(1, map.width - 1);
            int y = rng.Next(1, map.height - 1);
            Vector2Int p = new Vector2Int(x, y);
            if (map.Get(p) != CellType.Grass)
                continue;

            int nearestWater = DistanceToNearestWater(map, p);
            int score = nearestWater * 4 + rng.Next(0, 4);
            if (score > bestDistance)
            {
                bestDistance = score;
                best = p;
            }
        }

        if (best.x >= 0)
            return best;

        for (int y = 1; y < map.height - 1; y++)
        {
            for (int x = 1; x < map.width - 1; x++)
            {
                if (map.Get(x, y) == CellType.Grass)
                    return new Vector2Int(x, y);
            }
        }
        return best;
    }

    private static int DistanceToNearestWater(MapData map, Vector2Int p)
    {
        int best = map.width + map.height;
        for (int y = 1; y < map.height - 1; y++)
        {
            for (int x = 1; x < map.width - 1; x++)
            {
                if (map.Get(x, y) != CellType.Water)
                    continue;
                int distance = Mathf.Abs(p.x - x) + Mathf.Abs(p.y - y);
                if (distance < best)
                    best = distance;
            }
        }
        return best;
    }

    private static bool IsInterior(MapData map, Vector2Int p)
    {
        return p.x > 0 && p.y > 0 && p.x < map.width - 1 && p.y < map.height - 1;
    }

    private static int RandomLength(System.Random rng, int min, int max)
    {
        min = Mathf.Max(2, min);
        max = Mathf.Max(min, max);
        return rng.Next(min, max + 1);
    }

    private static bool PlaceHorseOnEscapableGrass(MapData map, System.Random rng)
    {
        bool[] fromBoundary = GetBoundaryReachableCells(map);
        List<Vector2Int> candidates = new List<Vector2Int>();
        float bestScore = float.MinValue;
        List<Vector2Int> bestCandidates = new List<Vector2Int>();

        for (int y = 1; y < map.height - 1; y++)
        {
            for (int x = 1; x < map.width - 1; x++)
            {
                Vector2Int p = new Vector2Int(x, y);
                if (map.Get(p) != CellType.Grass || !fromBoundary[map.Index(x, y)])
                    continue;

                int edgeDistance = Mathf.Min(Mathf.Min(x, map.width - 1 - x), Mathf.Min(y, map.height - 1 - y));
                int openNeighbours = 0;
                int adjacentWater = 0;
                for (int d = 0; d < 4; d++)
                {
                    Vector2Int n = p + Directions[d];
                    if (!map.InBounds(n))
                        continue;
                    CellType type = map.Get(n);
                    if (type == CellType.Grass)
                        openNeighbours++;
                    else if (type == CellType.Water)
                        adjacentWater++;
                }

                float score = edgeDistance * 1.5f + openNeighbours * 0.75f - adjacentWater * 2f + (float)rng.NextDouble();
                if (score > bestScore + 1.5f)
                {
                    bestScore = score;
                    bestCandidates.Clear();
                    bestCandidates.Add(p);
                }
                else if (score >= bestScore - 1.5f)
                {
                    bestCandidates.Add(p);
                }
                candidates.Add(p);
            }
        }

        if (bestCandidates.Count == 0)
        {
            if (candidates.Count == 0)
                return false;
            Vector2Int fallback = candidates[rng.Next(candidates.Count)];
            map.horse = fallback;
            map.Set(fallback, CellType.Horse);
            return true;
        }

        Vector2Int horse = bestCandidates[rng.Next(bestCandidates.Count)];
        map.horse = horse;
        map.Set(horse, CellType.Horse);
        return true;
    }

    private static bool[] GetBoundaryReachableCells(MapData map)
    {
        int count = map.width * map.height;
        bool[] visited = new bool[count];
        Queue<Vector2Int> queue = new Queue<Vector2Int>();

        for (int x = 0; x < map.width; x++)
        {
            AddBoundaryCell(map, new Vector2Int(x, 0), visited, queue);
            AddBoundaryCell(map, new Vector2Int(x, map.height - 1), visited, queue);
        }
        for (int y = 1; y < map.height - 1; y++)
        {
            AddBoundaryCell(map, new Vector2Int(0, y), visited, queue);
            AddBoundaryCell(map, new Vector2Int(map.width - 1, y), visited, queue);
        }

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            for (int d = 0; d < 4; d++)
            {
                Vector2Int next = current + Directions[d];
                if (!map.InBounds(next))
                    continue;
                int index = map.Index(next.x, next.y);
                if (visited[index] || !map.IsPassable(map.Get(next)))
                    continue;
                visited[index] = true;
                queue.Enqueue(next);
            }
        }
        return visited;
    }

    private static void AddBoundaryCell(MapData map, Vector2Int p, bool[] visited, Queue<Vector2Int> queue)
    {
        int index = map.Index(p.x, p.y);
        if (visited[index] || !map.IsPassable(map.Get(p)))
            return;
        visited[index] = true;
        queue.Enqueue(p);
    }

    private static void PlacePortals(MapData map, int pairs, int wallBudget, System.Random rng)
    {
        List<Vector2Int> candidates = CollectGrassInterior(map);
        List<PortalLink> links = new List<PortalLink>();
        List<Vector2Int> anchors = new List<Vector2Int>();
        anchors.Add(map.horse);

        // Portals add graph edges, so they can only keep or lower the wall cut.
        // Skip when the pre-portal cut is already outside the playable band.
        int cutBeforePortals = GetMinimumWallsToEnclose(map);
        if (!IsPlayableEnclosureCut(cutBeforePortals, wallBudget))
        {
            map.portals = Array.Empty<PortalLink>();
            return;
        }

        for (int pair = 0; pair < pairs && candidates.Count >= 2; pair++)
        {
            int bestA = -1;
            int bestB = -1;
            float bestScore = float.MinValue;
            int trials = Mathf.Min(160, candidates.Count * 2);

            for (int trial = 0; trial < trials; trial++)
            {
                int ia = rng.Next(candidates.Count);
                int ib = rng.Next(candidates.Count);
                if (ia == ib)
                    continue;

                Vector2Int a = candidates[ia];
                Vector2Int b = candidates[ib];
                int pairDistance = ManhattanDistance(a, b);
                if (pairDistance < Mathf.Max(4, (map.width + map.height) / 5))
                    continue;

                int anchorA = MinimumDistance(a, anchors);
                int anchorB = MinimumDistance(b, anchors);
                float score = pairDistance * 1.5f + (anchorA + anchorB) * 0.35f + (float)rng.NextDouble();
                if (score > bestScore)
                {
                    bestScore = score;
                    bestA = ia;
                    bestB = ib;
                }
            }

            if (bestA < 0 || bestB < 0)
            {
                // Relax the distance rule only if the map is too small or crowded.
                bestA = rng.Next(candidates.Count);
                bestB = (bestA + 1 + rng.Next(candidates.Count - 1)) % candidates.Count;
            }

            Vector2Int portalA = candidates[bestA];
            Vector2Int portalB = candidates[bestB];
            if (bestA > bestB)
            {
                int temp = bestA;
                bestA = bestB;
                bestB = temp;
            }

            // Tentatively place; keep only if escape + enclosure cut stay playable.
            map.Set(portalA, CellType.Portal);
            map.Set(portalB, CellType.Portal);
            PortalLink tentative = new PortalLink(portalA, portalB);
            PortalLink[] previous = links.ToArray();
            links.Add(tentative);
            map.portals = links.ToArray();

            bool keep = MapPathfinder.CanEscape(map)
                        && IsPlayableEnclosureCut(GetMinimumWallsToEnclose(map), wallBudget);

            candidates.RemoveAt(bestB);
            candidates.RemoveAt(bestA);

            if (!keep)
            {
                map.Set(portalA, CellType.Grass);
                map.Set(portalB, CellType.Grass);
                links.Clear();
                for (int i = 0; i < previous.Length; i++)
                    links.Add(previous[i]);
                map.portals = links.ToArray();
                continue;
            }

            anchors.Add(portalA);
            anchors.Add(portalB);
        }

        map.portals = links.ToArray();
    }

    private static void PlaceBonuses(MapData map, int count, System.Random rng)
    {
        List<Vector2Int> candidates = CollectGrassInterior(map);
        List<Vector2Int> anchors = new List<Vector2Int>();
        anchors.Add(map.horse);
        if (map.portals != null)
        {
            for (int i = 0; i < map.portals.Length; i++)
            {
                anchors.Add(map.portals[i].a);
                anchors.Add(map.portals[i].b);
            }
        }

        for (int i = 0; i < count && candidates.Count > 0; i++)
        {
            int bestIndex = -1;
            float bestScore = float.MinValue;
            int trials = Mathf.Min(80, candidates.Count);

            for (int trial = 0; trial < trials; trial++)
            {
                int index = rng.Next(candidates.Count);
                Vector2Int p = candidates[index];
                int anchorDistance = MinimumDistance(p, anchors);
                int waterDistance = DistanceToNearestWater(map, p);
                float waterPreference = waterDistance >= 1 && waterDistance <= 3
                    ? 2f
                    : -Mathf.Min(6f, Mathf.Abs(waterDistance - 2) * 1.25f);
                float score = anchorDistance * 1.4f + waterPreference + (float)rng.NextDouble();

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = index;
                }
            }

            if (bestIndex < 0)
                bestIndex = rng.Next(candidates.Count);

            Vector2Int bonus = candidates[bestIndex];
            candidates.RemoveAt(bestIndex);
            map.Set(bonus, CellType.BonusA);
            anchors.Add(bonus);
        }
    }

    private static List<Vector2Int> CollectGrassInterior(MapData map)
    {
        List<Vector2Int> result = new List<Vector2Int>();
        for (int y = 1; y < map.height - 1; y++)
        {
            for (int x = 1; x < map.width - 1; x++)
            {
                if (map.Get(x, y) == CellType.Grass)
                    result.Add(new Vector2Int(x, y));
            }
        }
        return result;
    }

    private static int MinimumDistance(Vector2Int point, List<Vector2Int> anchors)
    {
        if (anchors == null || anchors.Count == 0)
            return 0;

        int min = int.MaxValue;
        for (int i = 0; i < anchors.Count; i++)
        {
            int distance = ManhattanDistance(point, anchors[i]);
            if (distance < min)
                min = distance;
        }
        return min;
    }

    private static int ManhattanDistance(Vector2Int a, Vector2Int b)
    {
        return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
    }

    private static float EvaluateCandidate(MapData map, int targetWater, int minimumWalls, int wallBudget)
    {
        int waterCount = CountWater(map);
        float score = -Mathf.Abs(waterCount - targetWater) * 1.5f;
        int squareBlocks = CountFullWaterSquares(map);
        score -= squareBlocks * 8f;

        WaterStats waterStats = GetWaterStats(map);
        int desiredComponents = Mathf.Clamp(Mathf.RoundToInt(targetWater / 12f), 1, 4);
        score -= Mathf.Abs(waterStats.components - desiredComponents) * 2f;
        score -= waterStats.singleCellComponents * 5f;

        float spanX = map.width > 2 ? waterStats.spanX / (float)(map.width - 2) : 0f;
        float spanY = map.height > 2 ? waterStats.spanY / (float)(map.height - 2) : 0f;
        score += (spanX + spanY) * 4f;

        // Mild preference for some open space — heavy reachable bonus pushed junk open maps.
        int reachable = CountReachableFromHorse(map);
        score += reachable / (float)(map.width * map.height) * 3f;

        if (IsPlayableEnclosureCut(minimumWalls, wallBudget))
        {
            score += 40f;

            // Prefer cuts near ~65% of budget (meaningful, not trivial, not maxed-out).
            float desiredWalls = Mathf.Max(MinUsefulWallCut, wallBudget * 0.65f);
            score -= Mathf.Abs(minimumWalls - desiredWalls) * 4f;
        }
        else
        {
            score -= 80f + Mathf.Max(0, minimumWalls - wallBudget) * 5f;
        }

        // Penalize giant open regions with very few water boundaries around them.
        int grassCells = 0;
        int waterBoundaryEdges = 0;
        for (int y = 1; y < map.height - 1; y++)
        {
            for (int x = 1; x < map.width - 1; x++)
            {
                if (map.Get(x, y) == CellType.Water)
                    continue;

                grassCells++;
                for (int d = 0; d < 4; d++)
                {
                    Vector2Int n = new Vector2Int(x, y) + Directions[d];
                    if (map.InBounds(n) && map.Get(n) == CellType.Water)
                        waterBoundaryEdges++;
                }
            }
        }

        float boundaryRatio = grassCells > 0 ? waterBoundaryEdges / (float)grassCells : 0f;
        score += Mathf.Min(boundaryRatio, 2.5f) * 3f;
        return score;
    }

    private struct WaterStats
    {
        public int components;
        public int singleCellComponents;
        public int spanX;
        public int spanY;
    }

    private static WaterStats GetWaterStats(MapData map)
    {
        WaterStats result = new WaterStats();
        bool[] visited = new bool[map.cells.Length];
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        int minX = map.width;
        int minY = map.height;
        int maxX = -1;
        int maxY = -1;

        for (int y = 1; y < map.height - 1; y++)
        {
            for (int x = 1; x < map.width - 1; x++)
            {
                if (map.Get(x, y) != CellType.Water)
                    continue;

                minX = Mathf.Min(minX, x);
                minY = Mathf.Min(minY, y);
                maxX = Mathf.Max(maxX, x);
                maxY = Mathf.Max(maxY, y);
                int startIndex = map.Index(x, y);
                if (visited[startIndex])
                    continue;

                result.components++;
                int componentSize = 0;
                visited[startIndex] = true;
                queue.Enqueue(new Vector2Int(x, y));
                while (queue.Count > 0)
                {
                    Vector2Int current = queue.Dequeue();
                    componentSize++;
                    for (int d = 0; d < 4; d++)
                    {
                        Vector2Int next = current + Directions[d];
                        if (!map.InBounds(next) || map.Get(next) != CellType.Water)
                            continue;
                        int index = map.Index(next.x, next.y);
                        if (visited[index])
                            continue;
                        visited[index] = true;
                        queue.Enqueue(next);
                    }
                }

                if (componentSize == 1)
                    result.singleCellComponents++;
            }
        }

        result.spanX = maxX >= minX ? maxX - minX + 1 : 0;
        result.spanY = maxY >= minY ? maxY - minY + 1 : 0;
        return result;
    }

    private static int CountFullWaterSquares(MapData map)
    {
        int count = 0;
        for (int y = 1; y < map.height - 2; y++)
        {
            for (int x = 1; x < map.width - 2; x++)
            {
                if (map.Get(x, y) == CellType.Water &&
                    map.Get(x + 1, y) == CellType.Water &&
                    map.Get(x, y + 1) == CellType.Water &&
                    map.Get(x + 1, y + 1) == CellType.Water)
                    count++;
            }
        }
        return count;
    }

    private static int CountWater(MapData map)
    {
        int count = 0;
        for (int i = 0; i < map.cells.Length; i++)
        {
            if (map.cells[i] == CellType.Water)
                count++;
        }
        return count;
    }

    private static int CountReachableFromHorse(MapData map)
    {
        if (!map.InBounds(map.horse) || !map.IsPassable(map.Get(map.horse)))
            return 0;

        bool[] visited = new bool[map.cells.Length];
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        int startIndex = map.Index(map.horse.x, map.horse.y);
        visited[startIndex] = true;
        queue.Enqueue(map.horse);
        int count = 0;

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            count++;

            if (map.TryGetPortalExit(current, out Vector2Int exit))
                EnqueuePassable(map, exit, visited, queue);

            for (int d = 0; d < 4; d++)
                EnqueuePassable(map, current + Directions[d], visited, queue);
        }
        return count;
    }

    private static void EnqueuePassable(MapData map, Vector2Int p, bool[] visited, Queue<Vector2Int> queue)
    {
        if (!map.InBounds(p) || !map.IsPassable(map.Get(p)))
            return;
        int index = map.Index(p.x, p.y);
        if (visited[index])
            return;
        visited[index] = true;
        queue.Enqueue(p);
    }

    /// <summary>
    /// Returns the minimum number of interior Grass cells that must become walls
    /// to disconnect the horse from every map-edge cell. Water and existing walls
    /// are blocked; portals are real graph links. int.MaxValue means no finite
    /// legal wall cut was found (for example, an escape path cannot be cut using
    /// placeable Grass cells alone).
    /// </summary>
    public static int GetMinimumWallsToEnclose(MapData map)
    {
        if (map == null || map.cells == null || !map.InBounds(map.horse))
            return int.MaxValue;

        int cellCount = map.width * map.height;
        int source = cellCount * 2;
        int sink = source + 1;
        FlowNetwork flow = new FlowNetwork(cellCount * 2 + 2);
        int horseIndex = map.Index(map.horse.x, map.horse.y);

        for (int y = 0; y < map.height; y++)
        {
            for (int x = 0; x < map.width; x++)
            {
                Vector2Int p = new Vector2Int(x, y);
                CellType type = map.Get(p);
                if (!map.IsPassable(type))
                    continue;

                int index = map.Index(x, y);
                int input = index * 2;
                int output = input + 1;
                // Edge cells are placeable; only out-of-bounds is illegal.
                bool canPlaceWall = MapScoring.IsWallPlaceable(type) && index != horseIndex;
                flow.AddEdge(input, output, canPlaceWall ? 1 : FlowInfinity);

                if (map.IsEdge(p))
                    flow.AddEdge(output, sink, FlowInfinity);

                for (int d = 0; d < 4; d++)
                {
                    Vector2Int next = p + Directions[d];
                    if (!map.InBounds(next) || !map.IsPassable(map.Get(next)))
                        continue;
                    int nextIndex = map.Index(next.x, next.y);
                    flow.AddEdge(output, nextIndex * 2, FlowInfinity);
                }
            }
        }

        if (!map.IsPassable(map.Get(map.horse)))
            return int.MaxValue;
        flow.AddEdge(source, horseIndex * 2, FlowInfinity);

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
                flow.AddEdge(a * 2 + 1, b * 2, FlowInfinity);
                flow.AddEdge(b * 2 + 1, a * 2, FlowInfinity);
            }
        }

        int cut = flow.MaxFlow(source, sink, FlowInfinity);
        return cut >= FlowInfinity ? int.MaxValue : cut;
    }

    private sealed class FlowNetwork
    {
        private sealed class Edge
        {
            public int to;
            public int reverseIndex;
            public int capacity;

            public Edge(int to, int reverseIndex, int capacity)
            {
                this.to = to;
                this.reverseIndex = reverseIndex;
                this.capacity = capacity;
            }
        }

        private readonly List<Edge>[] _graph;
        private readonly int[] _level;
        private readonly int[] _nextEdge;
        private readonly Queue<int> _queue;

        public FlowNetwork(int nodeCount)
        {
            _graph = new List<Edge>[nodeCount];
            for (int i = 0; i < nodeCount; i++)
                _graph[i] = new List<Edge>();
            _level = new int[nodeCount];
            _nextEdge = new int[nodeCount];
            _queue = new Queue<int>(nodeCount);
        }

        public void AddEdge(int from, int to, int capacity)
        {
            Edge forward = new Edge(to, _graph[to].Count, capacity);
            Edge reverse = new Edge(from, _graph[from].Count, 0);
            _graph[from].Add(forward);
            _graph[to].Add(reverse);
        }

        public int MaxFlow(int source, int sink, int limit)
        {
            int total = 0;
            while (total < limit && BuildLevels(source, sink))
            {
                Array.Clear(_nextEdge, 0, _nextEdge.Length);
                int pushed;
                while (total < limit && (pushed = SendFlow(source, sink, limit - total)) > 0)
                    total += pushed;
            }
            return total;
        }

        private bool BuildLevels(int source, int sink)
        {
            for (int i = 0; i < _level.Length; i++)
                _level[i] = -1;
            _queue.Clear();
            _level[source] = 0;
            _queue.Enqueue(source);

            while (_queue.Count > 0)
            {
                int current = _queue.Dequeue();
                List<Edge> edges = _graph[current];
                for (int i = 0; i < edges.Count; i++)
                {
                    Edge edge = edges[i];
                    if (edge.capacity <= 0 || _level[edge.to] >= 0)
                        continue;
                    _level[edge.to] = _level[current] + 1;
                    _queue.Enqueue(edge.to);
                }
            }
            return _level[sink] >= 0;
        }

        private int SendFlow(int current, int sink, int available)
        {
            if (current == sink)
                return available;

            List<Edge> edges = _graph[current];
            for (; _nextEdge[current] < edges.Count; _nextEdge[current]++)
            {
                Edge edge = edges[_nextEdge[current]];
                if (edge.capacity <= 0 || _level[edge.to] != _level[current] + 1)
                    continue;

                int pushed = SendFlow(edge.to, sink, Mathf.Min(available, edge.capacity));
                if (pushed <= 0)
                    continue;

                edge.capacity -= pushed;
                _graph[edge.to][edge.reverseIndex].capacity += pushed;
                return pushed;
            }
            return 0;
        }
    }
}
