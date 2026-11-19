using System;

namespace CrushHour
{
    public enum PowerSupplyState { Online, Warning, Offline }
    public enum PowerTransition { None, Warning, Blackout }

    /// <summary>
    /// Operator-time power failures, separate from the crowd RNG and simulation speed.
    /// Offline power stays latched until the physical breaker is reset.
    /// </summary>
    public sealed class PowerOutageCycle
    {
        public const float WarningSeconds = 2.5f;
        readonly Random random;
        readonly int difficulty;
        public PowerSupplyState State { get; private set; }
        public int OutageCount { get; private set; }
        public int MaxOutages => difficulty + 1;
        public float SecondsRemaining { get; private set; }

        public PowerOutageCycle(int seed, int difficulty)
        {
            random = new Random(seed);
            this.difficulty = Math.Max(0, Math.Min(2, difficulty));
            State = PowerSupplyState.Online;
            SecondsRemaining = Between(25f - this.difficulty * 5f, 40f - this.difficulty * 5f);
        }

        /// <param name="active">Only true during an unpaused, live evacuation after the incident is known.</param>
        public PowerTransition Step(float seconds, bool active)
        {
            if (!active || seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds) ||
                State == PowerSupplyState.Offline || OutageCount >= MaxOutages)
                return PowerTransition.None;

            SecondsRemaining -= seconds;
            if (SecondsRemaining > 0f) return PowerTransition.None;
            if (State == PowerSupplyState.Online)
            {
                State = PowerSupplyState.Warning;
                // A stalled frame must not skip the player's warning window.
                SecondsRemaining = WarningSeconds;
                return PowerTransition.Warning;
            }
            State = PowerSupplyState.Offline;
            OutageCount++;
            SecondsRemaining = 0f;
            return PowerTransition.Blackout;
        }

        public bool Restore()
        {
            if (State != PowerSupplyState.Offline) return false;
            State = PowerSupplyState.Online;
            SecondsRemaining = OutageCount >= MaxOutages ? float.PositiveInfinity :
                Between(difficulty == 0 ? 65f : difficulty == 1 ? 50f : 40f,
                        difficulty == 0 ? 90f : difficulty == 1 ? 75f : 60f);
            return true;
        }

        /// <summary>End the round or disable this mechanic without scheduling another outage.</summary>
        public void Cancel()
        {
            State = PowerSupplyState.Online;
            SecondsRemaining = float.PositiveInfinity;
        }

        float Between(float minimum, float maximum) => minimum + (float)random.NextDouble() * (maximum - minimum);
    }
}
