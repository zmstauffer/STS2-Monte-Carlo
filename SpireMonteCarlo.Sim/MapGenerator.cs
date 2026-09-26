using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Sim;

/// <summary>
/// Builds a random act map the way the game's <c>StandardActMap</c> does: seven random paths up a 7-column grid, then rest sites, shops,
/// elites and "?" rooms dropped on random nodes under the game's placement rules. The game's pruning and layout passes are left out, so
/// these maps have slightly more nodes, but the mix of rooms a path meets is about the same. Used to calibrate against real runs on many maps
/// instead of one fixture.
/// </summary>
public static class MapGenerator
{
    private const int Width = 7;
    private const int Rooms = 15;

    private sealed class Node
    {
        public int Col, Row;
        public string Type = "";
        public List<Node> Children = new(), Parents = new();
    }

    public static MapSnapshot Generate(ulong seed)
    {
        var rng = new SimRng(seed);
        int mapLength = Rooms + 1;
        var grid = new Node?[Width, mapLength + 1];
        var start = new Node { Col = Width / 2, Row = 0, Type = "Ancient" };
        var boss = new Node { Col = Width / 2, Row = mapLength, Type = "Boss" };

        Node Get(int col, int row) => grid[col, row] ??= new Node { Col = col, Row = row };
        void Link(Node parent, Node child)
        {
            if (!parent.Children.Contains(child)) { parent.Children.Add(child); child.Parents.Add(parent); }
        }

        var starts = new List<Node>();
        for (int i = 0; i < 7; i++)
        {
            Node first = Get(rng.Next(Width), 1);
            if (i == 1) while (starts.Contains(first)) first = Get(rng.Next(Width), 1);
            starts.Add(first);
            Node at = first;
            while (at.Row < mapLength - 1)
            {
                var moves = new List<int> { -1, 0, 1 };
                rng.Shuffle(moves);
                Node? next = null;
                foreach (int move in moves)
                {
                    int col = Math.Clamp(at.Col + move, 0, Width - 1);
                    if (HasInvalidCrossover(grid, at, col)) continue;
                    next = Get(col, at.Row + 1);
                    break;
                }
                next ??= Get(at.Col, at.Row + 1);
                Link(at, next);
                at = next;
            }
        }
        foreach (Node n in Row(grid, mapLength - 1)) Link(n, boss);
        foreach (Node n in Row(grid, 1)) Link(start, n);

        // Fixed rows first: pre-boss rest sites, the treasure row, and the opening fights.
        foreach (Node n in Row(grid, mapLength - 1)) n.Type = "RestSite";
        foreach (Node n in Row(grid, mapLength - 7)) n.Type = "Treasure";
        foreach (Node n in Row(grid, 1)) n.Type = "Monster";

        int rests = Math.Clamp((int)Math.Round(7 + Gaussian(rng)), 6, 7);
        int unknowns = Math.Clamp((int)Math.Round(12 + Gaussian(rng)), 10, 14);
        var queue = new Queue<string>();
        for (int i = 0; i < rests; i++) queue.Enqueue("RestSite");
        for (int i = 0; i < 3; i++) queue.Enqueue("Shop");
        for (int i = 0; i < 5; i++) queue.Enqueue("Elite");
        for (int i = 0; i < unknowns; i++) queue.Enqueue("Unknown");

        var all = new List<Node>();
        for (int r = 1; r < mapLength; r++) all.AddRange(Row(grid, r));
        for (int pass = 0; pass < 3 && queue.Count > 0; pass++)
        {
            var open = all.Where(n => n.Type == "").ToList();
            rng.Shuffle(open);
            foreach (Node n in open)
            {
                if (queue.Count == 0) break;
                for (int k = 0, count = queue.Count; k < count; k++)
                {
                    string type = queue.Dequeue();
                    if (IsValid(type, n, mapLength)) { n.Type = type; break; }
                    queue.Enqueue(type);
                }
            }
        }
        foreach (Node n in all.Where(n => n.Type == "")) n.Type = "Monster";

        var snapshot = new MapSnapshot { Columns = Width, Rows = mapLength, Boss = new MapCoordinate(boss.Col, boss.Row) };
        foreach (Node n in new[] { start }.Concat(all))
            snapshot.Points.Add(new MapPointSnapshot { Col = n.Col, Row = n.Row, Type = n.Type, Children = n.Children.Select(c => new MapCoordinate(c.Col, c.Row)).ToList() });
        snapshot.Points.Add(new MapPointSnapshot { Col = boss.Col, Row = boss.Row, Type = "Boss" });
        return snapshot;
    }

    private static double Gaussian(SimRng rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static IEnumerable<Node> Row(Node?[,] grid, int row)
    {
        for (int c = 0; c < Width; c++)
            if (grid[c, row] is Node n) yield return n;
    }

    /// <summary>Two paths may not cross each other on a diagonal.</summary>
    private static bool HasInvalidCrossover(Node?[,] grid, Node current, int targetCol)
    {
        int delta = targetCol - current.Col;
        if (delta == 0) return false;
        Node? beside = grid[targetCol, current.Row];
        return beside != null && beside.Children.Any(c => c.Col - beside.Col == -delta);
    }

    private static bool IsValid(string type, Node n, int mapLength)
    {
        if (n.Row >= mapLength - 3 && type == "RestSite") return false;
        if (n.Row < 6 && type is "RestSite" or "Elite") return false;
        if (type is "Elite" or "RestSite" or "Treasure" or "Shop")
        {
            if (n.Parents.Concat(n.Children).Any(p => p.Type == type)) return false;
        }
        if (type is "RestSite" or "Monster" or "Unknown" or "Elite" or "Shop")
        {
            if (n.Parents.SelectMany(p => p.Children).Any(s => s != n && s.Type == type)) return false;
        }
        return true;
    }
}
