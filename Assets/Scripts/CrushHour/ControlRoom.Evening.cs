using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// Week 8, evening phases: what the room shows of them. The game (CrushHourGame.Phases) fills
    /// <see cref="TimelineText"/> (a line along the bottom of the video wall), calls <see cref="WallBanner"/> for big
    /// messages on the wall ("HEADLINER ON STAGE", "DROP IN 5", "ON FIRE x2"), <see cref="Flash"/> for fireworks (every
    /// screen whites out for a moment), sets <see cref="heatAlarm"/> (yellow beacon above 90 % HEAT) and
    /// <see cref="roomTint"/> (the lamps follow the evening: warm, dusk, night blue, party purple).
    /// </summary>
    public partial class ControlRoom
    {
        /// <summary>Bottom line of the video wall: phases, countdown, next moment, HEAT. Rich text.</summary>
        [System.NonSerialized] public string TimelineText = "";
        /// <summary>HEAT above 90 %: the beacon turns yellow (no siren). The red incident alarm wins.</summary>
        [System.NonSerialized] public bool heatAlarm;
        /// <summary>Colour of the room lamps (multiplies <see cref="lampColor"/>).</summary>
        [System.NonSerialized] public Color roomTint = Color.white;

        readonly List<Light> lampLights = new();
        TextMesh wallTimeline, wallBanner;
        float bannerStart = -99f, bannerUntil = -99f, flashUntil = -99f, flashStrength;
        Color bannerColor = Color.white, tintShown = Color.white;
        bool flashShown;

        void SetupWallEvening(Font font)
        {
            wallTimeline = MakeCaption(wallCamera, font, "Timeline", TextAnchor.LowerLeft, new Vector2(-0.95f, -0.93f), 0.042f);
            wallBanner = MakeCaption(wallCamera, font, "Banner", TextAnchor.MiddleCenter, new Vector2(0f, 0.42f), 0.11f);
            wallBanner.color = Color.clear;
            bannerUntil = -99f;
        }

        void LayoutWallEvening()
        {
            if (wallCamera == null) return;
            if (wallTimeline != null) LayoutCaption(wallTimeline, wallCamera, new Vector2(-0.95f, -0.93f), 0.042f);
            if (wallBanner != null) LayoutCaption(wallBanner, wallCamera, new Vector2(0f, 0.42f), 0.11f);
        }

        void UpdateWallEveningText()
        {
            if (wallTimeline != null && wallTimeline.text != TimelineText) wallTimeline.text = TimelineText ?? "";
        }

        /// <summary>A big message across the top of the video wall for <paramref name="seconds"/> (fades out at the end).</summary>
        public void WallBanner(string text, Color color, float seconds)
        {
            if (wallBanner == null) return;
            wallBanner.text = text;
            bannerColor = color;
            bannerStart = Time.unscaledTime;
            bannerUntil = bannerStart + seconds;
            nextWallRender = 0f;
        }

        void UpdateWallBanner(float now)
        {
            if (wallBanner == null) return;
            if (now >= bannerUntil)
            {
                if (wallBanner.color.a > 0f) wallBanner.color = Color.clear;
                return;
            }
            float fadeIn = Mathf.Clamp01((now - bannerStart) / 0.15f), fadeOut = Mathf.Clamp01((bannerUntil - now) / 0.5f);
            var c = bannerColor;
            c.a = Mathf.Min(fadeIn, fadeOut);
            wallBanner.color = c;
        }

        /// <summary>Fireworks: every screen (wall and monitors) goes white for ~0.2 s, <paramref name="strength"/> 0–1.</summary>
        public void Flash(float strength)
        {
            flashStrength = Mathf.Max(Time.unscaledTime < flashUntil ? flashStrength : 0f, Mathf.Clamp01(strength));
            flashUntil = Time.unscaledTime + 0.2f;
            nextWallRender = 0f;
        }

        float FlashAmount(float now) => now < flashUntil ? flashStrength * Mathf.Clamp01((flashUntil - now) / 0.2f) : 0f;

        /// <summary>Every frame (LateUpdate): monitor flashes and the lamp colour.</summary>
        void UpdateEveningFx()
        {
            float now = Time.unscaledTime, flash = FlashAmount(now);
            if (flash > 0f || flashShown)
            {
                var emission = Color.white * (1f + flash * 8f);
                foreach (var feed in feeds)
                    if (feed.screenMaterial != null) feed.screenMaterial.SetColor("_EmissionColor", emission);
                flashShown = flash > 0f;
            }
            if (roomTint != tintShown)
            {
                tintShown = roomTint;
                foreach (var light in lampLights)
                    if (light != null) light.color = lampColor * roomTint;
            }
        }
    }
}
