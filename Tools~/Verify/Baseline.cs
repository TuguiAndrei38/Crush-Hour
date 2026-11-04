// Standalone entry point for the headless balance runs, built by verify.ps1 with FestivalVenue.cs,
// CrowdSimulation.cs and Assets/Scripts/Editor/BalanceBaselineRunner.cs. The same runs are available inside Unity
// as Tools > Crush Hour > Run Balance Baseline (use that if Windows blocks this program).
using System;
using System.IO;
using System.Linq;
using CrushHour;

static class Baseline
{
    static int Main(string[] args)
    {
        int[] seeds = args.Length > 0 ? args[0].Split(',').Select(int.Parse).ToArray() : new[] { 11, 21, 33 };
        string table = BalanceBaselineRunner.RunAll(seeds, Console.WriteLine, () => false);
        Console.WriteLine();
        Console.Write(table);
        if (args.Length > 1) File.WriteAllText(args[1], table);
        return 0;
    }
}
