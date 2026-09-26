using System.Diagnostics;

static class Stage
{
    public static void Run(string name, Action action)
    {
        Console.WriteLine($"=== {name}");
        var sw = Stopwatch.StartNew();
        try
        {
            action();
            Console.WriteLine($"OK ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED ({sw.ElapsedMilliseconds} ms): {ex.GetType().Name}: {ex.Message}");
            for (Exception? e = ex.InnerException; e != null; e = e.InnerException)
                Console.WriteLine($"  caused by {e.GetType().Name}: {e.Message}");
            Console.WriteLine(string.Join("\n", (ex.InnerException ?? ex).StackTrace!.Split('\n').Take(8)));
            Environment.Exit(1);
        }
    }
}
