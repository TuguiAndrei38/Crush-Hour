using UnityEngine;

namespace CrushHour
{
    public partial class CrushHourGame
    {
        Texture2D replayHeatTexture;
        readonly Color32[] replayHeat = new Color32[TurningPointReplay.Columns * TurningPointReplay.Rows];
        int replayTextureFrame = -1;
        float replayTime, replayHold;
        bool replayPlaying, replayPassedMoment;

        void StartDebriefPlayback()
        {
            replayTextureFrame = -1;
            replayTime = turningPoint.GetFrame(0).Time;
            replayHold = 0f;
            replayPassedMoment = false;
            replayPlaying = turningPoint.FrameCount > 1;
        }

        void AdvanceDebriefPlayback(float dt)
        {
            if (!replayPlaying) return;
            if (replayHold > 0f)
            {
                replayHold -= dt;
                return;
            }
            float end = turningPoint.GetFrame(turningPoint.FrameCount - 1).Time;
            replayTime = Mathf.Min(replayTime + dt, end);
            if (!replayPassedMoment && replayTime >= turningPoint.MomentTime)
            {
                replayTime = turningPoint.MomentTime;
                replayHold = 1.4f;
                replayPassedMoment = true;
            }
            else if (replayTime >= end) replayPlaying = false;
        }

        void DrawDebriefReplay()
        {
            if (turningPoint == null || turningPoint.FrameCount == 0) return;
            float first = turningPoint.GetFrame(0).Time;
            float last = turningPoint.GetFrame(turningPoint.FrameCount - 1).Time;
            int index = 0;
            while (index + 1 < turningPoint.FrameCount && turningPoint.GetFrame(index + 1).Time <= replayTime) index++;
            var frame = turningPoint.GetFrame(index);

            GUILayout.Label($"TURNING POINT   /   {FormatTime(turningPoint.MomentTime)}", headerStyle);
            if (replayTitleStyle == null) replayTitleStyle = Txt(fontHeavy, 20, Ink, wrap: true);
            GUILayout.Label(turningPoint.Title, replayTitleStyle);
            var space = GUILayoutUtility.GetRect(10f, 216f, GUILayout.ExpandWidth(true));
            if (Repaint) DrawReplayMap(space, frame, index);

            GUILayout.BeginHorizontal();
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && last > first;
            if (GUILayout.Button(replayPlaying ? "Pause" : replayTime >= last ? "Replay" : "Play",
                smallButtonStyle, GUILayout.Width(58f), GUILayout.Height(24f)))
            {
                if (!replayPlaying && replayTime >= last) StartDebriefPlayback();
                else replayPlaying = !replayPlaying;
            }
            float seek = GUILayout.HorizontalSlider(replayTime, first, Mathf.Max(first + 0.001f, last));
            if (Mathf.Abs(seek - replayTime) > 0.001f)
            {
                replayTime = seek;
                replayHold = 0f;
                replayPassedMoment = seek >= turningPoint.MomentTime;
                replayPlaying = false;
            }
            GUI.enabled = wasEnabled;
            GUILayout.Label(FormatTime(frame.Time), headerRightStyle, GUILayout.Width(42f));
            GUILayout.EndHorizontal();

            int door = turningPoint.FocusDoor;
            string counts = door >= 0 ? $"{frame.Approaching[door]} approaching   /   {frame.Exited[door]} out through this exit" :
                $"{frame.Safe} out safely   /   {frame.Injured} injured across venue";
            GUILayout.Label(counts, labelStyle);
            GUILayout.Label(turningPoint.Caption, labelStyle);
        }

        GUIStyle replayTitleStyle;

        void DrawReplayMap(Rect space, TurningPointReplay.Frame frame, int index)
        {
            Fill(space, Ink);
            const float legendHeight = 23f;
            float scale = Mathf.Min((space.width - 12f) / W, (space.height - legendHeight - 8f) / H);
            if (scale <= 0f) return;
            var map = new Rect(space.center.x - W * scale * 0.5f, space.y + 4f, W * scale, H * scale);
            GUI.DrawTexture(map, menuPreviews[(int)venue.map]);
            if (replayHeatTexture == null)
            {
                replayHeatTexture = new Texture2D(TurningPointReplay.Columns, TurningPointReplay.Rows, TextureFormat.RGBA32, false)
                    { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Debrief crowd density" };
            }
            if (index != replayTextureFrame)
            {
                for (int i = 0; i < replayHeat.Length; i++)
                {
                    int people = frame.Density[i];
                    replayHeat[i] = people == 0 ? new Color32(0, 0, 0, 0) :
                        people < 8 ? new Color32(255, 202, 70, 115) :
                        people < 20 ? new Color32(242, 130, 29, 195) : new Color32(192, 41, 32, 225);
                }
                replayHeatTexture.SetPixels32(replayHeat);
                replayHeatTexture.Apply(false);
                replayTextureFrame = index;
            }
            GUI.DrawTexture(map, replayHeatTexture);
            if (frame.Injured > 0)
                for (int i = 0; i < frame.Hurt.Length; i++)
                {
                    if (frame.Hurt[i] == 0) continue;
                    float x = map.x + (i % TurningPointReplay.Columns + 0.5f) * TurningPointReplay.CellSize * scale;
                    float y = map.yMax - (i / TurningPointReplay.Columns + 0.5f) * TurningPointReplay.CellSize * scale;
                    Fill(new Rect(x - 2f, y - 2f, 4f, 4f), PanicInk);
                }
            for (int d = 0; d < venue.doors.Count; d++)
            {
                var exit = venue.doors[d].rect;
                var marker = new Rect(map.x + exit.x * scale, map.yMax - exit.yMax * scale,
                    Mathf.Max(3f, exit.width * scale), Mathf.Max(3f, exit.height * scale));
                Fill(new Rect(marker.x - 1f, marker.y - 1f, marker.width + 2f, marker.height + 2f), Ink);
                Fill(marker, (frame.OpenMask & (1 << d)) != 0 ? ExitGreen : Paper);
            }
            // Keep the focus hollow so the recorded crowd remains visible inside it.
            Vector2 focus = new(map.x + turningPoint.Focus.x * scale, map.yMax - turningPoint.Focus.y * scale);
            const float r = 7f;
            Fill(new Rect(focus.x - r, focus.y - r, 2f * r, 2f), Ink);
            Fill(new Rect(focus.x - r, focus.y + r - 2f, 2f * r, 2f), Ink);
            Fill(new Rect(focus.x - r, focus.y - r, 2f, 2f * r), Ink);
            Fill(new Rect(focus.x + r - 2f, focus.y - r, 2f, 2f * r), Ink);
            if (turningPoint.FocusDoor >= 0)
                DrawReplayExitLabel(map, focus, venue.doors[turningPoint.FocusDoor].name, Ink);
            if (turningPoint.AlternateDoor >= 0)
            {
                var alternate = venue.doors[turningPoint.AlternateDoor];
                var point = new Vector2(map.x + alternate.Center.x * scale, map.yMax - alternate.Center.y * scale);
                DrawReplayExitLabel(map, point, alternate.name, new Color(0.1f, 0.32f, 0.23f));
            }

            var legend = new Rect(space.x + 8, space.yMax - legendHeight, space.width - 16f, legendHeight);
            Label(legend, "Orange–red  Crowd     Green  Open exit     Purple  Injured", landmarkStyle, PaperLight);
        }

        void DrawReplayExitLabel(Rect map, Vector2 doorPoint, string name, Color fill)
        {
            float width = Mathf.Min(map.width, TextWidth(name, signNameStyle) + 8f);
            float top = doorPoint.y < map.center.y ? doorPoint.y + 9f : doorPoint.y - 24f;
            var rect = new Rect(Mathf.Clamp(doorPoint.x - width * 0.5f, map.x, map.xMax - width),
                Mathf.Clamp(top, map.y, map.yMax - 15f), width, 15f);
            Fill(rect, fill);
            Label(rect, name, signNameStyle, PaperLight);
        }
    }
}
