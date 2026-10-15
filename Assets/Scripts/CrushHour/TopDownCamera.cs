using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    /// <summary>City-builder camera: WASD/arrows or middle-drag to pan, scroll to zoom, Q/E to rotate.</summary>
    public class TopDownCamera : MonoBehaviour
    {
        public Vector3 focus = new(FestivalVenue.Width * 0.5f, 0f, FestivalVenue.Height * 0.45f);
        public float distance = 85f;
        [Range(20f, 89f)] public float pitch = 58f;
        public float yaw;
        public float minDistance = 12f, maxDistance = 160f;
        [Tooltip("Pan speed, scaled by zoom distance.")]
        public float panSpeed = 0.6f;
        public float rotateSpeed = 90f;

        public void Focus(Vector3 point, float? newDistance = null)
        {
            focus = point;
            if (newDistance.HasValue) distance = newDistance.Value;
            Apply();
        }

        void LateUpdate()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            float dt = Time.unscaledDeltaTime;

            Vector2 move = Vector2.zero;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x += 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x -= 1f;
                if (keyboard.qKey.isPressed) yaw += rotateSpeed * dt;
                if (keyboard.eKey.isPressed) yaw -= rotateSpeed * dt;
            }

            var yawRotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 forward = yawRotation * Vector3.forward, right = yawRotation * Vector3.right;
            focus += (right * move.x + forward * move.y) * (panSpeed * distance * dt);

            if (mouse != null)
            {
                if (mouse.middleButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    focus -= (right * delta.x + forward * delta.y) * (distance * 0.0016f);
                }
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) distance *= scroll > 0f ? 0.9f : 1.1f;
            }

            distance = Mathf.Clamp(distance, minDistance, maxDistance);
            focus.x = Mathf.Clamp(focus.x, 0f, FestivalVenue.Width);
            focus.z = Mathf.Clamp(focus.z, 0f, FestivalVenue.Height);
            Apply();
        }

        public void Apply()
        {
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(focus - rotation * Vector3.forward * distance, rotation);
        }
    }
}
