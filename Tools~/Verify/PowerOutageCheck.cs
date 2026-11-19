// Headless behavior checks for the breaker cycle. No Unity assemblies or scene state are loaded.
using System;
using CrushHour;

static class PowerOutageCheck
{
    static int checks;

    static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Near(float actual, float expected, string message) =>
        Require(Math.Abs(actual - expected) < 0.0001f, message + $" ({actual} versus {expected})");

    static void Trip(PowerOutageCycle cycle)
    {
        Require(cycle.Step(100000f, true) == PowerTransition.Warning, "An online supply must warn before it trips.");
        Require(cycle.State == PowerSupplyState.Warning, "Warning transition must expose the warning state.");
        Require(cycle.Step(PowerOutageCycle.WarningSeconds, true) == PowerTransition.Blackout, "A completed warning must trip the breaker.");
        Require(cycle.State == PowerSupplyState.Offline, "Blackout transition must leave the supply offline.");
    }

    static void InitialGraceAndRandomness()
    {
        for (int difficulty = 0; difficulty < 3; difficulty++)
        {
            float minimum = difficulty == 0 ? 25f : difficulty == 1 ? 20f : 15f;
            float maximum = difficulty == 0 ? 40f : difficulty == 1 ? 35f : 30f;
            float first = new PowerOutageCycle(0, difficulty).SecondsRemaining;
            bool varied = false;
            for (int seed = 0; seed < 128; seed++)
            {
                var cycle = new PowerOutageCycle(seed, difficulty);
                Require(cycle.State == PowerSupplyState.Online && cycle.OutageCount == 0, "New rounds must begin with working power.");
                Require(cycle.SecondsRemaining >= minimum && cycle.SecondsRemaining <= maximum, "The first warning must respect the difficulty's grace window.");
                Require(cycle.MaxOutages == difficulty + 1, "Difficulty must cap the number of interruptions.");
                varied |= cycle.SecondsRemaining != first;
            }
            Require(varied, "Different round seeds must produce varied outage timing.");
        }
        Require(new PowerOutageCycle(2, -10).MaxOutages == 1, "Invalid low difficulty must use the easy cap.");
        Require(new PowerOutageCycle(2, 10).MaxOutages == 3, "Invalid high difficulty must use the hard cap.");
    }

    static void GatingAndWarningWindow()
    {
        var cycle = new PowerOutageCycle(17, 1);
        float grace = cycle.SecondsRemaining;
        foreach (float seconds in new[] { -1f, 0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Require(cycle.Step(seconds, true) == PowerTransition.None, "Invalid elapsed time must not advance the cycle.");
        Require(cycle.Step(100000f, false) == PowerTransition.None, "Planning, menus and pause must not consume the grace period.");
        Near(cycle.SecondsRemaining, grace, "Inactive time must leave the initial timer unchanged");
        Require(cycle.Step(grace - 0.25f, true) == PowerTransition.None, "The supply must stay online throughout its grace period.");
        Require(cycle.State == PowerSupplyState.Online, "No warning may happen before the chosen delay.");
        Require(cycle.Step(cycle.SecondsRemaining, true) == PowerTransition.Warning, "The grace boundary must start one warning.");
        Near(cycle.SecondsRemaining, 2.5f, "The player must receive a full warning window");
        Require(!cycle.Restore(), "An online/warning breaker cannot be repaired preemptively.");
        Require(cycle.Step(100000f, false) == PowerTransition.None, "Pausing during a warning must freeze it.");
        Near(cycle.SecondsRemaining, 2.5f, "Inactive time must not shorten the warning");
        Require(cycle.Step(1.25f, true) == PowerTransition.None, "Half the warning must not cause a blackout.");
        Require(cycle.Step(1.25f, true) == PowerTransition.Blackout, "The warning must end in one blackout transition.");
        Require(cycle.OutageCount == 1, "The outage counter increments when power fails, not when a warning appears.");

        var stalledFrame = new PowerOutageCycle(17, 2);
        Require(stalledFrame.Step(100000f, true) == PowerTransition.Warning, "A large frame must not skip the warning.");
        Near(stalledFrame.SecondsRemaining, 2.5f, "A large frame must preserve the entire warning duration");
    }

    static void ManualRepairAndCooldown()
    {
        var cycle = new PowerOutageCycle(42, 1);
        Trip(cycle);
        Require(cycle.Step(100000f, true) == PowerTransition.None, "Offline power must stay latched indefinitely.");
        Require(cycle.Step(100000f, false) == PowerTransition.None, "Pausing must not restore power.");
        Require(cycle.State == PowerSupplyState.Offline && cycle.OutageCount == 1, "An unreset breaker cannot schedule extra outages.");
        Require(cycle.Restore(), "A manual breaker reset must restore offline power.");
        float cooldown = cycle.SecondsRemaining;
        Require(cooldown >= 50f && cooldown <= 75f, "The medium cooldown starts after repair and lasts at least 50 seconds.");
        Require(!cycle.Restore(), "Repeated reset calls must not reroll or extend the cooldown.");
        Near(cycle.SecondsRemaining, cooldown, "An invalid reset must preserve the chosen cooldown");
        Require(cycle.Step(100000f, false) == PowerTransition.None, "Pause must freeze the restored-power cooldown.");
        Near(cycle.SecondsRemaining, cooldown, "Inactive time must preserve the cooldown");
        Require(cycle.Step(cooldown - 0.25f, true) == PowerTransition.None, "A repaired breaker must not immediately retrip.");
        Require(cycle.Step(cycle.SecondsRemaining, true) == PowerTransition.Warning, "A second outage can warn only after its repair cooldown.");
    }

    static void CapsAndCancellation()
    {
        for (int difficulty = 0; difficulty < 3; difficulty++)
        {
            var cycle = new PowerOutageCycle(99, difficulty);
            for (int outage = 1; outage <= difficulty + 1; outage++)
            {
                Trip(cycle);
                Require(cycle.OutageCount == outage, "Each latched interruption must count once.");
                Require(cycle.Restore(), "Every offline breaker must accept one reset.");
                if (outage < cycle.MaxOutages)
                {
                    float minimum = difficulty == 1 ? 50f : 40f;
                    float maximum = difficulty == 1 ? 75f : 60f;
                    Require(cycle.SecondsRemaining >= minimum && cycle.SecondsRemaining <= maximum, "Restoration must respect the difficulty's recurrence window.");
                }
            }
            Require(float.IsPositiveInfinity(cycle.SecondsRemaining), "The final repaired outage must disable recurrence.");
            Require(cycle.Step(float.MaxValue, true) == PowerTransition.None, "The outage cap must hold even in a long round.");
            Require(cycle.State == PowerSupplyState.Online, "Power must remain on after the final allowed repair.");
        }
        foreach (int phase in new[] { 0, 1, 2 })
        {
            var cycle = new PowerOutageCycle(4, 2);
            if (phase >= 1) cycle.Step(100000f, true);
            if (phase == 2) cycle.Step(2.5f, true);
            cycle.Cancel();
            Require(cycle.State == PowerSupplyState.Online, "Ending a round must restore power from every cycle phase.");
            Require(cycle.Step(float.MaxValue, true) == PowerTransition.None, "A cancelled round must not schedule another warning.");
        }
    }

    static void ReproducibleRounds()
    {
        var first = new PowerOutageCycle(123456, 2);
        var second = new PowerOutageCycle(123456, 2);
        for (int outage = 0; outage < 3; outage++)
        {
            Near(first.SecondsRemaining, second.SecondsRemaining, "Equal round seeds must generate identical outage windows");
            Trip(first);
            Trip(second);
            first.Restore();
            second.Restore();
        }
        var restarted = new PowerOutageCycle(123456, 2);
        Require(restarted.OutageCount == 0 && restarted.State == PowerSupplyState.Online, "A new round must clear outage history.");
        Require(restarted.SecondsRemaining >= 15f && restarted.SecondsRemaining <= 30f, "A new round must restart its first grace period.");
    }

    static int Main()
    {
        try
        {
            InitialGraceAndRandomness();
            GatingAndWarningWindow();
            ManualRepairAndCooldown();
            CapsAndCancellation();
            ReproducibleRounds();
            Console.WriteLine($"POWER CHECK PASS — {checks} assertions; grace, pause, warning, repair, cooldown, caps and round reset.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("POWER CHECK FAILED: " + exception.Message);
            return 1;
        }
    }
}
