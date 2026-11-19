using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    /// <summary>The service balcony, physical breaker and operator's short repair walk.</summary>
    public partial class ControlRoom
    {
        [Header("Power service area")]
        public float walkingSpeed = 2.7f;
        public float breakerHoldSeconds = 1.25f;
        public bool PowerOut { get; private set; }
        public bool OnFoot { get; private set; }
        public bool PowerInteractionConsumed { get; private set; }
        public event Action PowerRestored;

        const float StandingEye = 1.65f, DoorCenterX = -4.1f;
        static readonly Vector3 DoorTarget = new(DoorCenterX, 1.1f, 9.1f);
        static readonly Vector3 BreakerTarget = new(DoorCenterX, 1.45f, 13.28f);
        Transform serviceDoor, breakerLever;
        Light powerGuideLight;
        Material breakerLamp;
        AudioSource breakerAudio;
        AudioClip breakerClick;
        Vector3 parkedSeat = EyePos;
        bool serviceDoorOpen;
        float serviceDoorAngle, breakerProgress, breakerAngle;
        string powerPrompt;
        GUIStyle powerPromptStyle;
        readonly List<Material> powerMaterials = new();

        void BuildPowerArea()
        {
            var area = new GameObject("Exterior electrical service balcony").transform;
            area.SetParent(Root, false);
            var steel = RoomMaterial("M_Room_Panel");
            var rail = RoomMaterial("M_Door_Metal");
            var amber = PowerMaterial("Battery service path", new Color(0.55f, 0.32f, 0.07f));
            PowerPart(area, "Service floor", steel, new Vector3(-4.1f, -0.08f, 11.55f), new Vector3(3f, 0.16f, 4.9f));
            PowerPart(area, "Panel backing", steel, new Vector3(-4.1f, 1.4f, 13.85f), new Vector3(3f, 2.8f, 0.12f));
            foreach (float x in new[] { -5.55f, -2.65f })
            {
                PowerPart(area, "Balcony top rail", rail, new Vector3(x, 1.05f, 11.55f), new Vector3(0.07f, 0.07f, 4.8f));
                foreach (float z in new[] { 9.3f, 11.5f, 13.7f })
                    PowerPart(area, "Balcony rail post", rail, new Vector3(x, 0.52f, z), new Vector3(0.07f, 1.05f, 0.07f));
            }
            PowerPart(area, "Electrical cabinet", rail, new Vector3(DoorCenterX, 1.45f, 13.55f), new Vector3(0.8f, 1.2f, 0.4f));
            PowerPart(area, "Breaker front", steel, new Vector3(DoorCenterX, 1.45f, 13.335f), new Vector3(0.68f, 1.05f, 0.06f));
            breakerLever = new GameObject("Main breaker lever").transform;
            breakerLever.SetParent(area, false);
            breakerLever.localPosition = BreakerTarget;
            PowerPart(breakerLever, "Breaker handle", rail, new Vector3(0f, 0.16f, -0.08f), new Vector3(0.08f, 0.35f, 0.1f));
            PowerPart(breakerLever, "Breaker grip", steel, new Vector3(0f, 0.31f, -0.11f), new Vector3(0.27f, 0.11f, 0.15f));
            breakerLamp = PowerMaterial("Battery breaker status", new Color(0.1f, 0.55f, 0.2f));
            PowerPart(area, "Breaker status lamp", breakerLamp, new Vector3(DoorCenterX + 0.24f, 1.86f, 13.28f), new Vector3(0.09f, 0.09f, 0.035f));
            for (int i = 0; i < 4; i++)
                PowerPart(area, "Battery floor guide", amber, new Vector3(DoorCenterX, 0.012f, 8.3f + i * 1.3f), new Vector3(0.3f, 0.022f, 0.09f));
            PowerSign(area, "MAIN POWER", new Vector3(DoorCenterX, 2.18f, 13.28f), 0.12f);
            PowerSign(area, "ON  /  RESET", new Vector3(DoorCenterX, 1.98f, 13.28f), 0.065f);
            PowerSign(area, "SERVICE ACCESS", new Vector3(DoorCenterX, 2.53f, 8.93f), 0.09f);
            powerGuideLight = new GameObject("Battery service guide").AddComponent<Light>();
            powerGuideLight.transform.SetParent(area, false);
            powerGuideLight.transform.localPosition = new Vector3(DoorCenterX, 2.65f, 10.45f);
            powerGuideLight.type = LightType.Point;
            powerGuideLight.color = new Color(1f, 0.63f, 0.3f);
            powerGuideLight.intensity = 2.5f;
            powerGuideLight.range = 5.8f;
            powerGuideLight.shadows = LightShadows.None;
            powerGuideLight.cullingMask = 1 << RoomLayer;
            powerGuideLight.gameObject.layer = RoomLayer;
            breakerAudio = area.gameObject.AddComponent<AudioSource>();
            breakerAudio.playOnAwake = false;
            breakerAudio.spatialBlend = 0f;
            breakerAudio.volume = 0.35f;
            const int rate = 22050, samples = 1800;
            var audio = new float[samples];
            for (int i = 0; i < samples; i++)
                audio[i] = Mathf.Sin(i * 0.79f) * Mathf.Exp(-i / 170f) * 0.7f;
            breakerClick = AudioClip.Create("Breaker switch click", samples, 1, rate, false);
            breakerClick.SetData(audio, 0);
        }

        Material PowerMaterial(string name, Color color)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = name, color = color };
            powerMaterials.Add(material);
            return material;
        }

        void PowerPart(Transform parent, string name, Material material, Vector3 position, Vector3 size)
        {
            var part = Prim(parent, PrimitiveType.Cube, material, position, size, Quaternion.identity);
            part.name = name;
        }

        void PowerSign(Transform parent, string text, Vector3 position, float height)
        {
            if (boardFont == null) boardFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var label = new GameObject(text).AddComponent<TextMesh>();
            label.transform.SetParent(parent, false);
            label.transform.localPosition = position;
            label.font = boardFont;
            label.fontSize = 64;
            label.characterSize = height / 6.4f;
            label.anchor = TextAnchor.MiddleCenter;
            label.color = new Color(0.85f, 0.79f, 0.6f);
            label.text = text;
            label.GetComponent<MeshRenderer>().sharedMaterial = boardFont.material;
            label.gameObject.layer = RoomLayer;
        }

        public void SetPowerOut(bool outOfPower)
        {
            bool changed = PowerOut != outOfPower;
            PowerOut = outOfPower;
            breakerProgress = 0f;
            if (breakerLamp != null) breakerLamp.color = PowerOut ? new Color(0.65f, 0.07f, 0.03f) : new Color(0.1f, 0.55f, 0.2f);
            if (changed) ApplyElectricalPowerState(!outOfPower);
        }

        public void ResetPowerTraversal()
        {
            SetPowerOut(false);
            OnFoot = false;
            seat = parkedSeat = EyePos;
            yaw = pitch = 0f;
            serviceDoorOpen = false;
            serviceDoorAngle = breakerAngle = breakerProgress = 0f;
            powerPrompt = null;
            ExitFocus();
            focusBlend = 0f;
            if (serviceDoor != null) serviceDoor.localRotation = Quaternion.identity;
            if (breakerLever != null) breakerLever.localRotation = Quaternion.identity;
            if (chair != null) { chair.localPosition = ChairPos; chair.localRotation = Quaternion.identity; }
            if (OperatorCamera != null && Root != null) ApplyView();
        }

        void UpdatePowerTraversal(bool canLook)
        {
            serviceDoorAngle = Mathf.MoveTowards(serviceDoorAngle, serviceDoorOpen ? 95f : 0f, Time.unscaledDeltaTime * 150f);
            if (serviceDoor != null) serviceDoor.localRotation = Quaternion.Euler(0f, serviceDoorAngle, 0f);
            float target = PowerOut ? 180f * (1f - breakerProgress) : 0f;
            breakerAngle = Mathf.MoveTowards(breakerAngle, target, Time.unscaledDeltaTime * 420f);
            if (breakerLever != null) breakerLever.localRotation = Quaternion.Euler(0f, 0f, breakerAngle);
            var keyboard = Keyboard.current;
            if (!canLook || radioOpen || keyboard == null) { breakerProgress = 0f; return; }
            if (keyboard.cKey.wasPressedThisFrame)
            {
                if (!OnFoot) { parkedSeat = seat; OnFoot = true; }
                else if (Vector2.Distance(new Vector2(seat.x, seat.z), new Vector2(parkedSeat.x, parkedSeat.z)) < 1.3f)
                { OnFoot = false; seat = parkedSeat; }
            }
            seat.y = Mathf.MoveTowards(seat.y, OnFoot ? StandingEye : EyePos.y, Time.unscaledDeltaTime * 3f);
            if (!OnFoot) return;
            var input = new Vector2((keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));
            if (input.sqrMagnitude < 0.01f) return;
            float a = (180f + yaw) * Mathf.Deg2Rad;
            var forward = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            var right = new Vector3(forward.z, 0f, -forward.x);
            var move = (forward * input.y + right * input.x).normalized * walkingSpeed * Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            var next = seat + new Vector3(move.x, 0f, 0f);
            if (PowerRouteWalkable(next)) seat = next;
            next = seat + new Vector3(0f, 0f, move.z);
            if (PowerRouteWalkable(next)) seat = next;
        }

        internal bool PowerRouteWalkable(Vector3 point)
        {
            // The shell has a real 0.95 m door opening. No path passes through the window or a closed leaf.
            if (point.z >= 8.8f && point.z <= 9.45f)
                return serviceDoorAngle >= 70f && point.x > -4.42f && point.x < -3.78f;
            if (point.z > 9.45f)
                return point.z <= 13.12f && point.x >= -5.28f && point.x <= -2.92f;
            float deskFront = 3.05f + 0.46f + Mathf.Max(0f, Mathf.Abs(point.x) - 0.86f) * Mathf.Tan(30f * Mathf.Deg2Rad);
            return point.x >= -5.55f && point.x <= 5.55f && point.z >= deskFront && point.z < 8.8f;
        }

        bool AimingAt(Vector3 point, Vector3 size, float reach)
        {
            var localEye = Root.InverseTransformPoint(OperatorCamera.transform.position);
            if (Vector3.Distance(localEye, point) > reach) return false;
            var ray = new Ray(localEye, Root.InverseTransformDirection(OperatorCamera.transform.forward));
            return new Bounds(point, size).IntersectRay(ray);
        }

        void ProcessPowerInteraction()
        {
            PowerInteractionConsumed = false;
            powerPrompt = null;
            if (!lookEnabled || Focused || radioOpen || !Application.isFocused || OperatorCamera == null || Root == null)
            { breakerProgress = 0f; return; }
            var keyboard = Keyboard.current;
            if (!OnFoot)
            { powerPrompt = PowerOut ? "POWER OUT  —  C: Stand up. Walk through the service door to reset the breaker." : "C — Stand up"; return; }
            bool nearChair = Vector2.Distance(new Vector2(seat.x, seat.z), new Vector2(parkedSeat.x, parkedSeat.z)) < 1.3f;
            powerPrompt = nearChair ? "C — Sit down   |   WASD — Walk" : "WASD — Walk   |   Return to your chair and press C to sit";
            if (seat.z > 9.45f && AimingAt(BreakerTarget, new Vector3(0.8f, 1.1f, 0.3f), 1.85f))
            {
                PowerInteractionConsumed = true;
                powerPrompt = PowerOut ? "HOLD E — Raise the main breaker" : "MAIN POWER — ON";
                if (PowerOut && keyboard != null && keyboard.eKey.isPressed)
                {
                    breakerProgress = Mathf.Clamp01(breakerProgress + Time.unscaledDeltaTime / Mathf.Max(0.25f, breakerHoldSeconds));
                    if (breakerProgress >= 1f)
                    {
                        SetPowerOut(false);
                        if (breakerAudio != null) breakerAudio.PlayOneShot(breakerClick);
                        PowerRestored?.Invoke();
                    }
                    return;
                }
            }
            else if (AimingAt(DoorTarget, new Vector3(0.95f, 2.2f, 0.3f), 2.1f))
            {
                PowerInteractionConsumed = true;
                bool clearOfDoor = seat.z < 8.72f || seat.z > 9.52f;
                powerPrompt = serviceDoorOpen
                    ? clearOfDoor ? "E — Close service door" : "Step clear of the doorway to close the door"
                    : "E — Open service door";
                if (keyboard != null && keyboard.eKey.wasPressedThisFrame &&
                    (!serviceDoorOpen || clearOfDoor))
                { serviceDoorOpen = !serviceDoorOpen; if (breakerAudio != null) breakerAudio.PlayOneShot(breakerClick); }
            }
            breakerProgress = 0f;
        }

        void DrawPowerPrompt()
        {
            if (string.IsNullOrEmpty(powerPrompt)) return;
            if (powerPromptStyle == null)
                powerPromptStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontSize = 16, normal = { textColor = new Color(0.96f, 0.92f, 0.81f) } };
            float scale = Mathf.Max(0.8f, Screen.height / 900f);
            powerPromptStyle.fontSize = Mathf.RoundToInt(16f * scale);
            float width = Mathf.Min(Screen.width - 36f, 800f * scale), height = 58f * scale;
            var rect = new Rect((Screen.width - width) * 0.5f, Screen.height - height - 24f * scale, width, height);
            Color old = GUI.color;
            GUI.color = new Color(0.035f, 0.045f, 0.065f, 0.94f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(rect, powerPrompt, powerPromptStyle);
            if (breakerProgress > 0f)
            {
                GUI.color = new Color(0.9f, 0.6f, 0.17f);
                GUI.DrawTexture(new Rect(rect.x + 12f, rect.yMax - 8f, (rect.width - 24f) * breakerProgress, 3f), Texture2D.whiteTexture);
            }
            GUI.color = old;
        }

        void DisposePowerArea()
        {
            foreach (var material in powerMaterials) if (material != null) Destroy(material);
            if (breakerClick != null) Destroy(breakerClick);
        }
    }
}
