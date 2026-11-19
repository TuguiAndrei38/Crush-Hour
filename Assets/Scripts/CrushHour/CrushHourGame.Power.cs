using UnityEngine;

namespace CrushHour
{
    public partial class CrushHourGame
    {
        [Header("Control-room power")]
        [Tooltip("Random breaker trips during an emergency. Repair the outside panel to restore cameras and room lights.")]
        public bool powerOutagesEnabled = true;
        PowerOutageCycle powerCycle;

        void BindControlRoomPower()
        {
            controlRoom.PowerRestored -= OnControlRoomPowerRestored;
            controlRoom.PowerRestored += OnControlRoomPowerRestored;
        }

        void ResetControlRoomPower()
        {
            if (!InControlRoom) return;
            powerCycle = new PowerOutageCycle(seed ^ 0x4f555441, (int)difficulty);
            controlRoom.ResetPowerTraversal();
        }

        void UpdateControlRoomPower()
        {
            if (!InControlRoom || powerCycle == null) return;
            if (!powerOutagesEnabled)
            {
                powerCycle.Cancel();
                controlRoom.SetPowerOut(false);
                return;
            }
            bool active = phase == Phase.Live && incidentStarted && !paused;
            switch (powerCycle.Step(Mathf.Min(Time.unscaledDeltaTime, 0.25f), active))
            {
                case PowerTransition.Warning:
                    Log("Electrical supply unstable. A breaker trip is imminent.", Severity.Warning);
                    ShowBanner("POWER SURGE", "Prepare for a blackout. The breaker is outside the rear door.", Severity.Warning);
                    PlayRadioClick();
                    break;
                case PowerTransition.Blackout:
                    speedIndex = 0; // Give the physical repair trip normal-speed evacuation time.
                    CancelDrag();
                    controlRoom.SetPowerOut(true);
                    Log("Power failure: cameras and room lights are offline. Reset the outside breaker. Radio remains on battery.", Severity.Danger);
                    ShowBanner("POWER OUT", "C: leave chair · E: open rear door · hold E at the outside breaker.", Severity.Danger);
                    break;
            }
        }

        void OnControlRoomPowerRestored()
        {
            if (powerCycle == null || !powerCycle.Restore()) return;
            Log("Breaker reset. Cameras and room lights restored.", Severity.Good);
            ShowBanner("POWER RESTORED", "Cameras are back online. Return to the desk; the radio remained available.", Severity.Good);
            PlayRadioClick();
        }

        void FinishControlRoomPower()
        {
            powerCycle?.Cancel();
            if (InControlRoom) controlRoom.ResetPowerTraversal();
        }
    }
}
