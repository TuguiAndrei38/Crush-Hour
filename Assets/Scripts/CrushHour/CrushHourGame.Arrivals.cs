using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    public partial class CrushHourGame
    {
        const float ArrivalFadeSeconds = 0.35f;
        const float ArrivalFilmLength = 3f * ArrivalShotSeconds;
        float arrivalFilmTime, arrivalSkipFade, arrivalHandoffFade;
        bool arrivalSkipping;

        void BeginArrivalSequence()
        {
            arrivalFilmTime = arrivalSkipFade = 0f;
            arrivalSkipping = false;
            if (InControlRoom)
            {
                controlRoom.lookEnabled = false;
                controlRoom.radioOpen = false;
                CloseRadio();
            }
            foreach (var unit in units)
            {
                unit.ring.enabled = false;
                if (unit.arrow != null) unit.arrow.enabled = false;
            }
            sim.BeginArrivals(venue);
            sim.StepArrivals(1.5f, venue); // Open on the first people approaching the threshold.
            crowdRenderer.ShowMarkers = false;
            BeginArrivalCamera();
        }

        void UpdateArrivalSequence()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.spaceKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame))
                SkipArrivals();

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (arrivalSkipping) arrivalSkipFade = Mathf.Min(1f, arrivalSkipFade + dt / ArrivalFadeSeconds);
            float previous = arrivalFilmTime;
            // Land exactly on the black frame between shots, even at a low frame rate.
            float cut = previous < ArrivalShotSeconds ? ArrivalShotSeconds :
                previous < 2f * ArrivalShotSeconds ? 2f * ArrivalShotSeconds : ArrivalFilmLength;
            arrivalFilmTime = Mathf.Min(previous + dt, cut);
            float shotDt = arrivalFilmTime - previous;

            if (previous < 2f * ArrivalShotSeconds)
            {
                sim.StepArrivals(shotDt, venue);
                if (arrivalFilmTime == ArrivalShotSeconds)
                    sim.StepArrivals(Mathf.Max(0f, sim.ArrivalDuration * 0.35f - (1.5f + arrivalFilmTime)), venue);
                if (arrivalFilmTime == 2f * ArrivalShotSeconds)
                    sim.FinishArrivals(venue);
            }
            else
            {
                // The final wide shot follows the same crowd and fixed-step movement as Planning.
                simAccumulator += shotDt;
                while (simAccumulator >= SimStep)
                {
                    sim.Step(SimStep, venue, false);
                    simAccumulator -= SimStep;
                }
            }

            UpdateArrivalCamera(arrivalFilmTime / ArrivalFilmLength);
            crowdRenderer.Render(shotDt);
            if (arrivalFilmTime >= ArrivalFilmLength || arrivalSkipFade >= 1f)
            {
                if (!sim.ArrivalsComplete) sim.FinishArrivals(venue);
                EndArrivalCamera();
                phase = Phase.Planning;
                arrivalHandoffFade = 1f;
                foreach (var unit in units)
                {
                    unit.ring.enabled = true;
                    if (unit.arrow != null) unit.arrow.enabled = true;
                }
                groundTimer = 0f;
                if (InControlRoom) UpdateControlRoomFeeds();
            }
        }

        void SkipArrivals()
        {
            if (arrivalSkipping) return;
            arrivalSkipFade = ArrivalShotFade(arrivalFilmTime / ArrivalFilmLength);
            arrivalSkipping = true;
        }

        void DrawArrivalSequence()
        {
            float band = Mathf.Clamp(vh * 0.082f, 48f, 80f);
            Fill(new Rect(0f, 0f, vw, band * 0.5f), Ink);
            Fill(new Rect(0f, vh - band, vw, band), Ink);
            float buttonWidth = Mathf.Min(138f, vw * 0.29f);
            float inset = Mathf.Min(28f, vw * 0.03f);
            GUI.Label(new Rect(inset, vh - band, vw - buttonWidth - 3f * inset, band), MapTitle, bannerTitleStyle);
            if (GUI.Button(new Rect(vw - buttonWidth - inset, vh - band * 0.5f - 17f, buttonWidth, 34f),
                "Skip intro  [Space]", buttonStyle))
                SkipArrivals();
            DrawArrivalFade(Mathf.Max(ArrivalShotFade(arrivalFilmTime / ArrivalFilmLength), arrivalSkipFade));
        }

        void DrawArrivalFade(float opacity)
        {
            if (!Repaint || opacity <= 0f) return;
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, Mathf.Clamp01(opacity));
            GUI.DrawTexture(new Rect(0f, 0f, vw, vh), Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
