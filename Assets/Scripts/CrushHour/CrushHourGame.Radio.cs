using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    /// <summary>
    /// Step 6: orders over the radio in the control room, CS2-buy-menu style. Hold V: a wheel of targets (each zone's
    /// patrol in its zone colour, the police officers); move the mouse towards a slice and release V or click. Then a
    /// wheel of orders for that unit (zone evacuation per side with the door it would pick, escort, clear a blocked
    /// exit → which exit, go to a zone → which zone, back to post). Greyed slices say why they can't be used. The unit
    /// answers in the log like on the radio ("P3: Copy, evacuating via East Gate A.").
    /// </summary>
    public partial class CrushHourGame
    {
        enum RadioStage { Closed, Target, Command, Pick }

        class RadioItem
        {
            public string label, sub, reason;
            public Color color;
            public bool enabled = true;
            public Action action;
        }

        RadioStage radioStage;
        Vector2 radioPointer;
        int radioUnit = -1;
        string radioTitle;
        readonly List<RadioItem> radioItems = new();
        /// <summary>The radio used the mouse / Esc this frame (so the room and the map don't also get it).</summary>
        bool radioBusy, radioTookEscape;
        AudioSource radioAudio;
        AudioClip radioClick;
        const float RadioPointerRadius = 160f, RadioDeadZone = 22f;

        /// <summary>Every frame in the control room, before the room's and the map's input.</summary>
        void UpdateRadio()
        {
            radioBusy = radioStage != RadioStage.Closed;
            radioTookEscape = false;
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard == null || mouse == null || !controlRoom.lookEnabled || controlRoom.Focused || phase == Phase.Results)
            {
                if (radioStage != RadioStage.Closed) CloseRadio();
                controlRoom.radioOpen = false;
                return;
            }

            if (radioStage == RadioStage.Closed)
            {
                if (keyboard.vKey.wasPressedThisFrame) OpenTargets();
            }
            else
            {
                radioPointer += mouse.delta.ReadValue();
                if (radioPointer.magnitude > RadioPointerRadius) radioPointer = radioPointer.normalized * RadioPointerRadius;

                bool cancel = mouse.rightButton.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame
                              || (radioStage != RadioStage.Target && keyboard.vKey.wasPressedThisFrame);
                bool releaseV = radioStage == RadioStage.Target && keyboard.vKey.wasReleasedThisFrame;
                bool confirm = mouse.leftButton.wasPressedThisFrame || releaseV;
                if (cancel)
                {
                    radioTookEscape = keyboard.escapeKey.wasPressedThisFrame;
                    CloseRadio();
                }
                else if (confirm)
                {
                    int i = RadioSelection();
                    if (i < 0) { if (releaseV) CloseRadio(); }     // let go without pointing anywhere: nothing
                    else if (radioItems[i].enabled)
                    {
                        radioPointer = Vector2.zero;
                        PlayRadioClick();
                        radioItems[i].action?.Invoke();
                    }
                }
                radioBusy = true;
            }
            controlRoom.radioOpen = radioStage != RadioStage.Closed || radioBusy;
        }

        void CloseRadio()
        {
            radioStage = RadioStage.Closed;
            radioItems.Clear();
            radioUnit = -1;
            radioPointer = Vector2.zero;
        }

        int RadioSelection()
        {
            int n = radioItems.Count;
            if (n == 0 || radioPointer.magnitude < RadioDeadZone) return -1;
            // Slice 0 at the top, then clockwise. The pointer is in mouse space (y up).
            float angle = Mathf.Atan2(radioPointer.x, radioPointer.y) * Mathf.Rad2Deg;   // 0 = up, 90 = right
            if (angle < 0f) angle += 360f;
            return Mathf.RoundToInt(angle / (360f / n)) % n;
        }

        string Callsign(Unit u) => u.role == Role.Megaphone ? $"Police {u.number}" : $"P{u.number}";

        void RadioReply(Unit u, string text) => Log($"{Callsign(u)}: Copy, {text}", Severity.Info);

        // ---------------------------------------------------------------- Wheels

        void OpenTargets()
        {
            PlayRadioClick();
            radioStage = RadioStage.Target;
            radioTitle = "RADIO\nwho?";
            radioItems.Clear();
            radioPointer = Vector2.zero;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                int index = i;
                bool police = u.role == Role.Megaphone;
                radioItems.Add(new RadioItem
                {
                    label = Callsign(u),
                    sub = police ? "Police" : u.homeRegion >= 0 ? venue.regions[u.homeRegion].name : "Patrol",
                    color = police ? new Color(0.3f, 0.5f, 1f) : UnitColor(u),
                    action = () => OpenCommands(index),
                });
            }
        }

        void OpenCommands(int unitIndex)
        {
            if (unitIndex < 0 || unitIndex >= units.Count) { CloseRadio(); return; }
            var u = units[unitIndex];
            radioUnit = unitIndex;
            selectedUnit = unitIndex;
            radioStage = RadioStage.Command;
            radioTitle = $"{Callsign(u)}\n{(u.homeRegion >= 0 ? venue.regions[u.homeRegion].name : RoleName(u.role))}";
            radioItems.Clear();

            if (u.role == Role.Patrolman && u.homeRegion >= 0)
            {
                string why = phase != Phase.Live ? "show not started"
                           : !incidentStarted ? "before the incident"
                           : regionOrderCooldown[u.homeRegion] > 0f ? $"cooldown {Mathf.CeilToInt(regionOrderCooldown[u.homeRegion])} s"
                           : null;
                foreach (OrderSide side in new[] { OrderSide.Front, OrderSide.Right, OrderSide.Back, OrderSide.Left })
                {
                    int door = sim.PickOrderDoor(u.homeRegion, SideDirection(u, side), venue);
                    var s = side;
                    radioItems.Add(new RadioItem
                    {
                        label = $"EVACUATE {side.ToString().ToUpperInvariant()}",
                        sub = door >= 0 ? $"via {venue.doors[door].name}" : "no open exit",
                        color = new Color(0.95f, 0.75f, 0.2f),
                        enabled = why == null,
                        reason = why,
                        action = () =>
                        {
                            OrderZone(u, s);
                            RadioReply(u, u.orderDoor >= 0 ? $"evacuating the zone via {venue.doors[u.orderDoor].name}." : "telling everyone to get out.");
                            CloseRadio();
                        },
                    });
                }
            }

            bool escorting = u.escortPhase == EscortPhase.Escorting;
            radioItems.Add(new RadioItem
            {
                label = "ESCORT DRUNK",
                sub = "nearest one",
                color = new Color(0.6f, 0.85f, 1f),
                enabled = !escorting && sim.DrunkInsideCount > 0,
                reason = escorting ? "already escorting" : sim.DrunkInsideCount == 0 ? "no drunks left" : null,
                action = () =>
                {
                    EscortNearestDrunk(unitIndex);
                    RadioReply(u, "walking a drunk fan out.");
                    CloseRadio();
                },
            });

            int blocked = 0;
            foreach (var d in venue.doors) if (d.blocked) blocked++;
            radioItems.Add(new RadioItem
            {
                label = "CLEAR EXIT",
                sub = blocked > 0 ? $"{blocked} blocked" : "",
                color = new Color(0.95f, 0.45f, 0.3f),
                enabled = blocked > 0,
                reason = blocked > 0 ? null : "no blocked exit",
                action = () => OpenPickDoor(unitIndex),
            });
            radioItems.Add(new RadioItem
            {
                label = "GO TO ZONE",
                sub = "choose",
                color = new Color(0.7f, 0.9f, 0.6f),
                action = () => OpenPickZone(unitIndex),
            });
            radioItems.Add(new RadioItem
            {
                label = "BACK TO POST",
                sub = "",
                color = new Color(0.85f, 0.85f, 0.85f),
                action = () =>
                {
                    if (u.escortPhase != EscortPhase.None) CancelEscort(u);
                    ReturnToPost(u);
                    RadioReply(u, "heading back to my post.");
                    CloseRadio();
                },
            });
        }

        void OpenPickDoor(int unitIndex)
        {
            var u = units[unitIndex];
            radioStage = RadioStage.Pick;
            radioTitle = $"{Callsign(u)}\nclear which exit?";
            radioItems.Clear();
            foreach (var d in venue.doors)
            {
                if (!d.blocked) continue;
                var door = d;
                radioItems.Add(new RadioItem
                {
                    label = d.name.ToUpperInvariant(),
                    sub = d.blockReason ?? "blocked",
                    color = new Color(0.95f, 0.45f, 0.3f),
                    action = () =>
                    {
                        SendToClear(unitIndex, door);
                        RadioReply(u, $"on my way to clear {door.name}.");
                        CloseRadio();
                    },
                });
            }
            if (radioItems.Count == 0) CloseRadio();
        }

        void OpenPickZone(int unitIndex)
        {
            var u = units[unitIndex];
            radioStage = RadioStage.Pick;
            radioTitle = $"{Callsign(u)}\ngo to which zone?";
            radioItems.Clear();
            for (int r = 0; r < venue.regions.Count; r++)
            {
                var region = venue.regions[r];
                radioItems.Add(new RadioItem
                {
                    label = $"{r + 1}. {region.name.ToUpperInvariant()}",
                    sub = $"{sim.CountInRegion(r, venue)} people",
                    color = region.color,
                    action = () =>
                    {
                        if (u.escortPhase != EscortPhase.None)
                        {
                            CancelEscort(u);
                            u.escortPhase = EscortPhase.None;
                        }
                        var path = venue.FindPath(u.position, FestivalVenue.CellCenter(FestivalVenue.Index(region.post.x, region.post.y)));
                        if (path == null) Log($"{u.Name} can't get to {region.name}.", Severity.Warning);
                        else
                        {
                            u.path.Clear();
                            u.path.AddRange(path);
                            RadioReply(u, $"heading to {region.name}.");
                        }
                        CloseRadio();
                    },
                });
            }
        }

        // ---------------------------------------------------------------- Drawing (OnGUI, scaled GUI space)

        void DrawRadio()
        {
            if (radioStage == RadioStage.Closed || radioItems.Count == 0) return;
            var center = new Vector2(vw * 0.5f, vh * 0.5f);
            int n = radioItems.Count;
            float radius = n > 8 ? 230f : 190f;
            int selected = RadioSelection();

            // dim the view a little and draw the hub
            Fill(new Rect(0, 0, vw, vh), new Color(0f, 0f, 0f, 0.25f));
            var hub = new Rect(center.x - 80, center.y - 34, 160, 68);
            Frame(hub, Ink, Yellow);
            Label(hub, radioTitle, orderSubStyle, Paper);

            for (int i = 0; i < n; i++)
            {
                var item = radioItems[i];
                float a = (90f - i * 360f / n) * Mathf.Deg2Rad;
                var p = center + new Vector2(Mathf.Cos(a), -Mathf.Sin(a)) * radius;
                var rect = new Rect(p.x - 82, p.y - 27, 164, 54);
                bool on = i == selected;
                Color fill = !item.enabled ? new Color(0.45f, 0.45f, 0.47f, 0.92f) : on ? PaperLight : new Color(Paper.r, Paper.g, Paper.b, 0.92f);
                Frame(rect, fill, on && item.enabled ? Yellow : Ink);
                Fill(new Rect(rect.x, rect.y, 6, rect.height), item.enabled ? item.color : Color.gray);
                Label(new Rect(rect.x + 6, rect.y + 3, rect.width - 6, 24), item.label, orderTitleStyle, item.enabled ? Ink : new Color(0.25f, 0.25f, 0.27f));
                string sub = item.enabled ? item.sub : item.reason ?? item.sub;
                if (!string.IsNullOrEmpty(sub))
                    Label(new Rect(rect.x + 6, rect.y + 27, rect.width - 6, 22), sub, orderSubStyle, item.enabled ? InkSoft : new Color(0.15f, 0.15f, 0.17f));
            }

            // where the mouse points
            var dot = center + new Vector2(radioPointer.x, -radioPointer.y) * (radius / RadioPointerRadius) * 0.6f;
            Fill(new Rect(dot.x - 5, dot.y - 5, 10, 10), Yellow);
            string hint = radioStage == RadioStage.Target ? "Point and release V (or click). Right click / Esc: cancel."
                        : "Point and click. Right click / Esc / V: cancel.";
            Label(new Rect(center.x - 250, center.y + radius + 40, 500, 22), hint, orderSubStyle, Paper);
        }

        // ---------------------------------------------------------------- Sound

        /// <summary>A short burst of radio static (made in code), on opening the radio and on each choice.</summary>
        void PlayRadioClick()
        {
            if (radioAudio == null)
            {
                radioAudio = cam.gameObject.AddComponent<AudioSource>();
                radioAudio.playOnAwake = false;
                radioAudio.spatialBlend = 0f;
                radioAudio.volume = 0.35f;
            }
            if (radioClick == null)
            {
                const int rate = 22050;
                int length = rate / 8;
                var data = new float[length];
                var r = new System.Random(7);
                for (int i = 0; i < length; i++)
                {
                    float t = i / (float)length;
                    float envelope = t < 0.05f ? t / 0.05f : Mathf.Exp(-(t - 0.05f) * 9f);
                    data[i] = ((float)r.NextDouble() * 2f - 1f) * envelope * 0.8f + Mathf.Sin(i * 2f * Mathf.PI * 1400f / rate) * envelope * 0.2f;
                }
                radioClick = AudioClip.Create("Radio static", length, 1, rate, false);
                radioClick.SetData(data, 0);
            }
            radioAudio.PlayOneShot(radioClick);
        }
    }
}
