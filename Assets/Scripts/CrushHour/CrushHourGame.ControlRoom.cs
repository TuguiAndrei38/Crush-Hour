using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>Control-room side of the game: zone status for the CCTV monitors (step 2), minimap markers (step 3).</summary>
    public partial class CrushHourGame
    {
        /// <summary>A zone's monitor flashes red this long after an incident starts there, then stays red while it lasts.</summary>
        const float NewIncidentFlash = 20f;
        /// <summary>Pressure (fraction of the crush threshold) that turns a zone's monitor yellow.</summary>
        const float WarnPressure = 0.6f;
        const float GasWarnLevel = 0.05f;

        int[] zoneAlarm = System.Array.Empty<int>();
        bool[] zoneWarning = System.Array.Empty<bool>();
        float zoneScanTimer;

        void UpdateControlRoomFeeds()
        {
            int n = venue.regions.Count;
            if (zoneAlarm.Length != n)
            {
                zoneAlarm = new int[n];
                zoneWarning = new bool[n];
            }

            UpdateMinimapMarkers();
            System.Array.Clear(zoneAlarm, 0, n);
            if (phase == Phase.Live || phase == Phase.Results)
                foreach (var incident in schedule.incidents)
                {
                    if (!incident.Started || incident.Silent && !(incident is GasLeakIncident g && g.Detected)) continue;
                    int region = RegionOf(incident);
                    if (region < 0 || region >= n) continue;
                    int level = incident.Age < NewIncidentFlash ? 2 : incident.Active ? 1 : 0;
                    zoneAlarm[region] = Mathf.Max(zoneAlarm[region], level);
                }

            // Pressure and gas: a full scan of the grid four times a second is plenty.
            zoneScanTimer -= Time.deltaTime;
            if (zoneScanTimer <= 0f)
            {
                zoneScanTimer = 0.25f;
                System.Array.Clear(zoneWarning, 0, n);
                bool gasKnown = false;
                foreach (var incident in schedule.incidents)
                    if (incident is GasLeakIncident leak && leak.Detected) gasKnown = true;
                float warn = crowd.crushThreshold * WarnPressure;
                for (int i = 0; i < FestivalVenue.CellCount; i++)
                {
                    int region = venue.regionOf[i];
                    if (region < 0 || region >= n || zoneWarning[region]) continue;
                    if (sim.pressure[i] > warn || (gasKnown && venue.gas[i] > GasWarnLevel)) zoneWarning[region] = true;
                }
            }

            controlRoom.alarm = incidentStarted && phase == Phase.Live;
            controlRoom.UpdateCctv(feedClock, zoneAlarm, zoneWarning);
            UpdateBoard();
            UpdateInfoScreens();
        }

        // ---------------------------------------------------------------- Info screens and wall clock (step 7)

        /// <summary>In the control room the top strip is off by default (U shows it); the wall clock replaces it.</summary>
        bool controlRoomTopBar;
        float infoTimer;
        const int LogLines = 11, LogWidth = 52;

        void UpdateInfoScreens()
        {
            infoTimer -= Time.unscaledDeltaTime;
            if (infoTimer > 0f) return;
            infoTimer = 0.25f;

            if (controlRoom.LogText != null)
            {
                var sb = new System.Text.StringBuilder();
                int shown = 0;
                for (int i = log.Count - 1; i >= 0 && shown < LogLines; i--, shown++)
                {
                    var (text, severity, time, _) = log[i];
                    string color = severity switch
                    {
                        Severity.Danger => "#ff5a4a",
                        Severity.Warning => "#ffc840",
                        Severity.Good => "#6ee08a",
                        _ => "#d8e0ea",
                    };
                    string line = string.IsNullOrEmpty(time) ? text : $"{time}  {text}";
                    if (line.Length > LogWidth) line = line.Substring(0, LogWidth - 1) + "…";
                    sb.Append("<color=").Append(color).Append('>').Append(line).Append("</color>\n");
                }
                if (shown == 0) sb.Append("<color=#8090a0>All quiet.</color>");
                controlRoom.LogText.text = sb.ToString();
            }

            if (controlRoom.TeamText != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var u in units)
                {
                    string where = u.role == Role.Megaphone ? "Police" : u.homeRegion >= 0 ? venue.regions[u.homeRegion].name : "Patrol";
                    var c = u.role == Role.Megaphone ? new Color(0.45f, 0.65f, 1f) : UnitColor(u);
                    string name = $"{Callsign(u),-9} {where}";
                    if (name.Length > 24) name = name.Substring(0, 24);
                    sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>').Append(name.PadRight(25)).Append("</color>")
                      .Append(UnitStatus(u)).Append('\n');
                }
                controlRoom.TeamText.text = sb.ToString();
                controlRoom.TeamTitle.text = $"<b>TEAM</b>   <color=#9fb0c0>{FreeGateCrews}/{difficultySettings.gateCrews} gate crews free</color>";
            }

            if (controlRoom.ClockText != null)
            {
                string state = phase == Phase.Planning ? "PLANNING" : paused ? "PAUSED" : phase == Phase.Results ? "ENDED" : Speeds[speedIndex] > 1f ? $"LIVE x{Speeds[speedIndex]:0}" : "LIVE";
                string clock = incidentStarted ? FormatTime(evacuationTime) : FormatTime(feedClock);
                controlRoom.ClockText.text = $"<b>{clock}</b>\n<size=28>{state}  ·  {sim.ActiveCount} INSIDE</size>";
            }
        }

        // ---------------------------------------------------------------- Exits board (step 5)

        ControlRoom.ExitInfo[] boardExits = System.Array.Empty<ControlRoom.ExitInfo>();

        /// <summary>A switch on the board was flicked: toggle that exit's gate (entrances have no switch).</summary>
        bool BoardSwitch(int door)
        {
            if (door < 0 || door >= venue.doors.Count || venue.doors[door].gate == null || phase == Phase.Results) return false;
            return ToggleGate(venue.doors[door].gate);
        }

        void UpdateBoard()
        {
            int n = venue.doors.Count;
            if (boardExits.Length != n) boardExits = new ControlRoom.ExitInfo[n];
            for (int i = 0; i < n; i++)
            {
                var d = venue.doors[i];
                var state = d.blocked ? ControlRoom.ExitState.Blocked
                          : d.gate == null || d.gate.Passable ? ControlRoom.ExitState.Open
                          : d.gate.open ? ControlRoom.ExitState.Opening
                          : ControlRoom.ExitState.Closed;
                boardExits[i] = new ControlRoom.ExitInfo
                {
                    name = d.name,
                    entrance = d.gate == null,
                    state = state,
                    blockReason = d.blockReason,
                    perMinute = i < exitRate.Length ? exitRate[i] : 0f,
                };
            }
            controlRoom.UpdateBoard(boardExits, FreeGateCrews, difficultySettings.gateCrews, phase == Phase.Planning);
        }

        /// <summary>The zone an incident is in: its own region, else the region of its focus cell (-1 outside every zone).</summary>
        int RegionOf(Incident incident)
        {
            if (incident.region >= 0) return incident.region;
            var c = incident.FocusCell;
            return c.x >= 0 && c.y >= 0 && c.x < W && c.y < H ? venue.regionOf[FestivalVenue.Index(c.x, c.y)] : -1;
        }

        // ---------------------------------------------------------------- Minimap markers (layer ControlRoom.MinimapLayer)

        static readonly Color MinimapPolice = new(0.3f, 0.55f, 1f);
        static readonly Color MinimapOpen = new(0.15f, 0.9f, 0.3f), MinimapClosed = new(0.95f, 0.15f, 0.1f);
        static readonly Color MinimapIncident = new(1f, 0.1f, 0.05f, 0.55f), MinimapWarning = new(1f, 0.8f, 0.1f, 0.5f);
        Transform minimapRoot;
        Texture2D minimapZoneTexture;
        readonly List<Transform> unitMarkers = new();
        readonly List<Renderer> unitMarkerRenderers = new();
        readonly List<Renderer> doorMarkers = new();
        readonly List<Renderer> incidentMarkers = new();
        readonly Dictionary<Color, Material> markerMaterials = new();

        /// <summary>Zones (tint + number), one marker per unit, per exit and per incident; under the world, so NewGame clears them.</summary>
        void SetupMinimapMarkers()
        {
            unitMarkers.Clear();
            unitMarkerRenderers.Clear();
            doorMarkers.Clear();
            incidentMarkers.Clear();
            minimapRoot = new GameObject("Minimap Markers").transform;
            minimapRoot.SetParent(world, false);

            // zone tint: one pixel per cell
            if (minimapZoneTexture != null) Destroy(minimapZoneTexture);
            minimapZoneTexture = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int r = venue.regionOf[FestivalVenue.Index(x, y)];
                    pixels[y * W + x] = r >= 0 ? new Color(venue.regions[r].color.r, venue.regions[r].color.g, venue.regions[r].color.b, 0.3f) : Color.clear;
                }
            minimapZoneTexture.SetPixels(pixels);
            minimapZoneTexture.Apply();
            var tint = MinimapMarker(PrimitiveType.Quad, new Color(1f, 1f, 1f, 1f), new Vector3(W, H, 1f));
            tint.transform.SetPositionAndRotation(new Vector3(W * 0.5f, 0.4f, H * 0.5f), Quaternion.Euler(90f, 0f, 0f));
            var tintMaterial = new Material(litMaterial) { name = "Minimap zones" };
            tintMaterial.SetTexture("_BaseMap", minimapZoneTexture);
            tintMaterial.SetColor("_BaseColor", Color.white);
            tintMaterial.EnableKeyword("_EMISSION");
            tintMaterial.SetTexture("_EmissionMap", minimapZoneTexture);
            tintMaterial.SetColor("_EmissionColor", new Color(0.6f, 0.6f, 0.6f));
            ControlRoom.MakeTransparent(tintMaterial);
            tint.GetComponent<Renderer>().sharedMaterial = tintMaterial;
            tint.name = "Zones";

            // zone numbers (same as the CAM numbers)
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            for (int r = 0; r < venue.regions.Count; r++)
            {
                var text = new GameObject($"Zone {r + 1}").AddComponent<TextMesh>();
                text.transform.SetParent(minimapRoot, false);
                var c = venue.regions[r].Center;
                text.transform.SetPositionAndRotation(new Vector3(c.x, 6f, c.y), Quaternion.Euler(90f, 0f, 0f));
                text.font = font;
                text.fontSize = 64;
                text.characterSize = 0.7f;
                text.anchor = TextAnchor.MiddleCenter;
                text.text = (r + 1).ToString();
                text.color = Color.white;
                text.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                text.gameObject.layer = ControlRoom.MinimapLayer;
            }

            foreach (var door in venue.doors)
            {
                var marker = MinimapMarker(PrimitiveType.Cube, MinimapOpen, new Vector3(3.5f, 0.3f, 3.5f));
                marker.transform.position = new Vector3(door.Center.x, 4.5f, door.Center.y);
                marker.name = door.name;
                doorMarkers.Add(marker.GetComponent<Renderer>());
            }
        }

        GameObject MinimapMarker(PrimitiveType type, Color color, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.layer = ControlRoom.MinimapLayer;
            go.transform.SetParent(minimapRoot, false);
            go.transform.localScale = scale;
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = MarkerMaterial(color);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        /// <summary>Flat, glowing colour (cached per colour; transparent when alpha &lt; 1).</summary>
        Material MarkerMaterial(Color color)
        {
            if (markerMaterials.TryGetValue(color, out var cached) && cached != null) return cached;
            var m = new Material(litMaterial) { name = $"Minimap marker {color}" };
            m.SetColor("_BaseColor", color);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", new Color(color.r, color.g, color.b) * 0.8f);
            if (color.a < 0.99f) ControlRoom.MakeTransparent(m);
            markerMaterials[color] = m;
            return m;
        }

        void UpdateMinimapMarkers()
        {
            if (minimapRoot == null) return;
            bool blink = Mathf.Repeat(Time.unscaledTime, 0.6f) < 0.35f;

            // units: patrols in their zone's colour, police blue
            while (unitMarkers.Count < units.Count)
            {
                var m = MinimapMarker(PrimitiveType.Cylinder, Color.white, new Vector3(2.4f, 0.15f, 2.4f));
                m.name = "Unit";
                unitMarkers.Add(m.transform);
                unitMarkerRenderers.Add(m.GetComponent<Renderer>());
            }
            for (int i = 0; i < unitMarkers.Count; i++)
            {
                bool used = i < units.Count;
                unitMarkers[i].gameObject.SetActive(used);
                if (!used) continue;
                var u = units[i];
                unitMarkers[i].position = new Vector3(u.position.x, 5f, u.position.y);
                var color = u.role == Role.Megaphone ? MinimapPolice
                          : u.homeRegion >= 0 && u.homeRegion < venue.regions.Count ? venue.regions[u.homeRegion].color : PatrolColor;
                color.a = 1f;
                unitMarkerRenderers[i].sharedMaterial = MarkerMaterial(color);
            }

            // exits: green open, red closed, blinking red blocked
            for (int i = 0; i < doorMarkers.Count && i < venue.doors.Count; i++)
            {
                var door = venue.doors[i];
                var color = door.IsOpen ? MinimapOpen : MinimapClosed;
                doorMarkers[i].enabled = !door.blocked || blink;
                doorMarkers[i].sharedMaterial = MarkerMaterial(color);
            }

            // incidents (known ones) and warning signs
            int k = 0;
            foreach (var incident in schedule.incidents)
            {
                if (!incident.Started || !incident.Active) continue;
                if (incident.Silent && !(incident is GasLeakIncident g && g.Detected)) continue;
                PlaceIncidentMarker(k++, incident.FocusCell, MinimapIncident, 9f, blink);
            }
            foreach (var (incident, cell) in warningMarkers)
                if (!incident.Started) PlaceIncidentMarker(k++, cell, MinimapWarning, 7f, true);
            for (int i = k; i < incidentMarkers.Count; i++) incidentMarkers[i].gameObject.SetActive(false);
        }

        void PlaceIncidentMarker(int index, Vector2Int cell, Color color, float size, bool visible)
        {
            while (incidentMarkers.Count <= index)
            {
                var m = MinimapMarker(PrimitiveType.Cylinder, color, Vector3.one);
                m.name = "Incident";
                incidentMarkers.Add(m.GetComponent<Renderer>());
            }
            var r = incidentMarkers[index];
            r.gameObject.SetActive(true);
            r.enabled = visible;
            r.sharedMaterial = MarkerMaterial(color);
            r.transform.localScale = new Vector3(size, 0.1f, size);
            r.transform.position = new Vector3(cell.x + 0.5f, 3.5f, cell.y + 0.5f);
        }
    }
}
