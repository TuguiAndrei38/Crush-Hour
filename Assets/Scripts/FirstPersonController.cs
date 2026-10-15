using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// CharacterController-based first person movement.
/// Keyboard/mouse: WASD move, mouse look, Space jump, Left Shift sprint, Esc frees the cursor, click recaptures it.
/// Gamepad: left stick move, right stick look, South button jump, left stick press sprint.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Pitch pivot, usually the camera. Defaults to the first child camera.")]
    public Transform cameraRoot;

    [Header("Movement")]
    public float walkSpeed = 4.5f;
    public float sprintSpeed = 7.5f;
    public float groundAcceleration = 60f;
    public float airAcceleration = 20f;
    [Tooltip("1.25 is enough to hop up one cube.")]
    public float jumpHeight = 1.25f;
    public float gravity = -25f;

    [Header("Look")]
    [Tooltip("Degrees per pixel of mouse movement.")]
    public float mouseSensitivity = 0.1f;
    [Tooltip("Degrees per second at full stick deflection.")]
    public float gamepadLookSpeed = 180f;
    [Range(0f, 90f)] public float maxPitch = 89f;
    public bool invertY;

    [Header("Misc")]
    [Tooltip("On start, drop the player onto the highest surface below/above its position.")]
    public bool snapToGroundOnStart = true;
    [Tooltip("Falling below this height teleports the player back to the spawn point.")]
    public float respawnBelowY = -30f;
    public bool showCrosshair = true;

    CharacterController controller;
    Vector3 velocity;
    float pitch;
    Vector3 spawnPosition;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (cameraRoot == null)
        {
            var cam = GetComponentInChildren<Camera>();
            if (cam != null) cameraRoot = cam.transform;
        }
        if (cameraRoot != null)
        {
            pitch = cameraRoot.localEulerAngles.x;
            if (pitch > 180f) pitch -= 360f;
        }
    }

    void Start()
    {
        if (snapToGroundOnStart) SnapToGround();
        spawnPosition = transform.position;
        LockCursor(true);
    }

    void Update()
    {
        HandleCursor();
        Look();
        Move();
        if (transform.position.y < respawnBelowY) Teleport(spawnPosition);
    }

    public void Teleport(Vector3 position)
    {
        controller.enabled = false;
        transform.position = position;
        controller.enabled = true;
        velocity = Vector3.zero;
    }

    void SnapToGround()
    {
        controller.enabled = false; // keep the ray from hitting our own capsule
        var origin = transform.position + Vector3.up * 500f;
        bool hit = Physics.Raycast(origin, Vector3.down, out var info, 1000f, ~0, QueryTriggerInteraction.Ignore);
        controller.enabled = true;
        if (hit) Teleport(info.point + Vector3.up * 0.05f);
    }

    void HandleCursor()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            LockCursor(false);
        else if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            LockCursor(true);
    }

    static void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void Look()
    {
        Vector2 look = Vector2.zero;
        var mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            look += mouse.delta.ReadValue() * mouseSensitivity;
        var pad = Gamepad.current;
        if (pad != null)
            look += pad.rightStick.ReadValue() * (gamepadLookSpeed * Time.deltaTime);

        transform.Rotate(0f, look.x, 0f, Space.Self);
        pitch = Mathf.Clamp(pitch + (invertY ? look.y : -look.y), -maxPitch, maxPitch);
        if (cameraRoot != null) cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void Move()
    {
        var keyboard = Keyboard.current;
        var pad = Gamepad.current;

        Vector2 input = Vector2.zero;
        bool sprint = false, jump = false;
        if (keyboard != null)
        {
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) input.y += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) input.y -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) input.x += 1f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) input.x -= 1f;
            sprint |= keyboard.leftShiftKey.isPressed;
            jump |= keyboard.spaceKey.isPressed;
        }
        if (pad != null)
        {
            input += pad.leftStick.ReadValue();
            sprint |= pad.leftStickButton.isPressed;
            jump |= pad.buttonSouth.isPressed;
        }
        input = Vector2.ClampMagnitude(input, 1f);

        bool grounded = controller.isGrounded;
        Vector3 target = (transform.right * input.x + transform.forward * input.y) * (sprint ? sprintSpeed : walkSpeed);
        var horizontal = new Vector3(velocity.x, 0f, velocity.z);
        horizontal = Vector3.MoveTowards(horizontal, target, (grounded ? groundAcceleration : airAcceleration) * Time.deltaTime);
        velocity.x = horizontal.x;
        velocity.z = horizontal.z;

        if (grounded && velocity.y < 0f) velocity.y = -2f; // keep the controller pressed onto the ground
        if (grounded && jump) velocity.y = Mathf.Sqrt(2f * jumpHeight * -gravity);
        velocity.y += gravity * Time.deltaTime;

        var flags = controller.Move(velocity * Time.deltaTime);
        if ((flags & CollisionFlags.Above) != 0 && velocity.y > 0f) velocity.y = 0f;
    }

    void OnGUI()
    {
        if (!showCrosshair || Cursor.lockState != CursorLockMode.Locked) return;
        const float length = 8f, thickness = 2f;
        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
        GUI.DrawTexture(new Rect(cx - length, cy - thickness * 0.5f, length * 2f, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - thickness * 0.5f, cy - length, thickness, length * 2f), Texture2D.whiteTexture);
    }
}
