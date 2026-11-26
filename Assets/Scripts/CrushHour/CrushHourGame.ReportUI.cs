using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    public partial class CrushHourGame
    {
        FieldReport selectedReport;

        FieldReport CurrentReport()
        {
            if (phase != Phase.Live || fieldReports.Count == 0) return null;
            if (fieldReports.Contains(selectedReport)) return selectedReport;
            selectedReport = fieldReports.Find(r => !r.resolved) ?? fieldReports[0];
            return selectedReport;
        }

        void NextReport(FieldReport report)
        {
            if (fieldReports.Count > 1 && fieldReports.Contains(report))
                selectedReport = fieldReports[(fieldReports.IndexOf(report) + 1) % fieldReports.Count];
        }

        bool TryViewReport(int region, Vector2 target)
        {
            if (phase != Phase.Live || paused || region < 0 || region >= venue.regions.Count) return false;
            if (InControlRoom) return controlRoom.FocusReportFeed(region);
            cameraRig.Focus(new Vector3(target.x, 0f, target.y), 32f);
            return true;
        }

        bool CanViewReports => !InControlRoom || (!controlRoom.PowerOut && !controlRoom.OnFoot);

        void HandleReportKeys(Keyboard keyboard)
        {
            if (radioBusy || paused || !Application.isFocused) return;
            var report = CurrentReport();
            if (report == null) return;
            if (keyboard.nKey.wasPressedThisFrame) NextReport(report);
            else if (keyboard.jKey.wasPressedThisFrame && CanViewReports && !report.resolved) ObserveReport(report);
            else if (keyboard.kKey.wasPressedThisFrame && CanDispatchReport(report)) DispatchReport(report);
        }

        void DrawFieldReports()
        {
            var report = CurrentReport();
            if (report == null || radioBusy) return;

            float width = Mathf.Min(370f, vw - 24f);
            float bodyWidth = width - 24f;
            float messageHeight = labelStyle.CalcHeight(new GUIContent(report.message), bodyWidth);
            bool hasDetail = !string.IsNullOrEmpty(report.detail);
            float detailHeight = hasDetail ? mutedStyle.CalcHeight(new GUIContent(report.detail), bodyWidth) + 4f : 0f;
            float x = InControlRoom ? 12f : (vw - width) * 0.5f;
            float y = InControlRoom && !controlRoomTopBar ? 12f : TopBarHeight + 16f;
            if (bannerTime > 0f && x + width > vw * 0.5f - 290f && x < vw * 0.5f + 290f)
                y = TopBarHeight + 98f;

            var rect = Panel(new Rect(x, y, width, 53f + messageHeight + detailHeight + (report.resolved ? 0f : 33f)));
            Fill(new Rect(x, y, width, 23f), Ink);
            string title = report.resolved ? "FIELD RADIO  /  CLOSED" : "FIELD RADIO  /  INVESTIGATION";
            Label(new Rect(x + 12f, y, width - 119f, 23f), title, tabStyle, PaperLight);
            if (fieldReports.Count > 1 &&
                GUI.Button(new Rect(rect.xMax - 104f, y + 2f, 101f, 19f),
                    $"{fieldReports.IndexOf(report) + 1}/{fieldReports.Count}  Next [N]", smallButtonStyle))
                deferred.Add(() => NextReport(report));

            string area = report.region >= 0 && report.region < venue.regions.Count ? venue.regions[report.region].name : "Venue";
            GUI.Label(new Rect(x + 12f, y + 26f, bodyWidth, 18f), area.ToUpperInvariant(), headerStyle);
            float bodyY = y + 45f;
            GUI.Label(new Rect(x + 12f, bodyY, bodyWidth, messageHeight), report.message, labelStyle);
            if (hasDetail)
                GUI.Label(new Rect(x + 12f, bodyY + messageHeight + 4f, bodyWidth, detailHeight - 4f), report.detail, mutedStyle);
            if (report.resolved) return;

            float buttonWidth = (bodyWidth - 6f) * 0.5f;
            float buttonsY = rect.yMax - 34f;
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && !paused && CanViewReports;
            string view = !InControlRoom ? "Inspect area [J]" : controlRoom.PowerOut ? "Camera offline" :
                controlRoom.OnFoot ? "View from desk" : $"View CAM {report.region + 1:00} [J]";
            if (GUI.Button(new Rect(x + 12f, buttonsY, buttonWidth, 26f), view, smallButtonStyle))
                deferred.Add(() => ObserveReport(report));
            bool canDispatch = CanDispatchReport(report);
            GUI.enabled = enabled && !paused && canDispatch;
            string dispatch = report.officer >= 0 ? "Officer en route" : canDispatch ? "Dispatch officer [K]" : "No officer free";
            if (GUI.Button(new Rect(x + 18f + buttonWidth, buttonsY, buttonWidth, 26f), dispatch, smallButtonStyle))
                deferred.Add(() => DispatchReport(report));
            GUI.enabled = enabled;
        }
    }
}
