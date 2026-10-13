using UnityEngine;
using UnityEngine.InputSystem;

namespace GeoWorld
{
    // The Play-mode camera of the generated scenes.
    // Orbit: right drag orbits a focus point, the wheel zooms, middle drag pans, WASD and Q/E move the focus.
    // Fly (no clip), F to switch: the mouse looks, WASD flies where the camera looks, Space/E and Ctrl/Q climb and
    // sink, and nothing stops the camera (it goes through terrain and buildings); the wheel sets the speed.
    public class GeoPreviewCamera : MonoBehaviour
    {
        public Vector3 focus;
        public float distance = 450;
        public float yaw = -25, pitch = 52;
        public float moveSpeed = 80;
        [Tooltip("Fly mode speed in metres per second; the wheel changes it while flying.")]
        public float flySpeed = 25;
        [Tooltip("Degrees the view turns per pixel of mouse movement in fly mode.")]
        public float lookSensitivity = 0.12f;

        public bool Flying { get; private set; }
        public float FlyPitch => flyPitch;

        Vector3 startFocus;
        float startDistance, startYaw, startPitch;
        float flyPitch;   // fly mode may look straight up or down; the orbit stays between 12 and 88 degrees

        void Start()
        {
            startFocus = focus; startDistance = distance; startYaw = yaw; startPitch = pitch;
            Apply();
        }

        void OnDisable() => ReleaseCursor();

        void Update()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.homeKey.wasPressedThisFrame)
                {
                    SetFlying(false);
                    focus = startFocus; distance = startDistance; yaw = startYaw; pitch = startPitch;
                    Apply();
                    return;
                }
                if (keyboard.fKey.wasPressedThisFrame) SetFlying(!Flying);
            }
            if (Flying) Fly(mouse, keyboard);
            else Orbit(mouse, keyboard);
        }

        // Switches between orbit and fly without moving the camera: fly starts where the camera is, looking where it
        // looks; orbit comes back around the ground point the camera looks at.
        public void SetFlying(bool on)
        {
            if (on == Flying) return;
            Flying = on;
            if (on)
            {
                var euler = transform.rotation.eulerAngles;
                yaw = euler.y;
                flyPitch = Mathf.Clamp(Mathf.DeltaAngle(0, euler.x), -89, 89);
                CatchCursor();
                return;
            }
            ReleaseCursor();
            var position = transform.position;
            pitch = Mathf.Clamp(flyPitch, 12, 88);
            var forward = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
            distance = Physics.Raycast(position, forward, out var hit, 10000) ? Mathf.Clamp(hit.distance, 12, 10000) : Mathf.Clamp(distance, 12, 300);
            focus = position + forward * distance;
            Apply();
        }

        void Orbit(Mouse mouse, Keyboard keyboard)
        {
            if (mouse != null)
            {
                if (mouse.rightButton.isPressed)
                {
                    var delta = mouse.delta.ReadValue();
                    yaw += delta.x * 0.18f;
                    pitch = Mathf.Clamp(pitch - delta.y * 0.18f, 12, 88);
                }
                distance = Mathf.Clamp(distance * Mathf.Exp(-mouse.scroll.ReadValue().y * 0.001f), 12, 10000);
                if (mouse.middleButton.isPressed)
                {
                    var delta = mouse.delta.ReadValue();
                    focus += Quaternion.Euler(0, yaw, 0) * new Vector3(-delta.x, 0, -delta.y) * distance * 0.0015f;
                }
            }
            if (keyboard != null)
            {
                float x = (keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0);
                float z = (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0);
                float y = (keyboard.eKey.isPressed ? 1 : 0) - (keyboard.qKey.isPressed ? 1 : 0);
                float speed = moveSpeed * (keyboard.leftShiftKey.isPressed ? 3 : 1);
                focus += Quaternion.Euler(0, yaw, 0) * new Vector3(x, y, z) * speed * Time.deltaTime;
            }
            Apply();
        }

        void Fly(Mouse mouse, Keyboard keyboard)
        {
            if (mouse != null)
            {
                if (Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame) CatchCursor();
                if (Cursor.lockState == CursorLockMode.Locked || mouse.rightButton.isPressed)
                {
                    var delta = mouse.delta.ReadValue();
                    yaw += delta.x * lookSensitivity;
                    flyPitch = Mathf.Clamp(flyPitch - delta.y * lookSensitivity, -89, 89);
                }
                float wheel = mouse.scroll.ReadValue().y;
                if (wheel != 0) flySpeed = Mathf.Clamp(flySpeed * (wheel > 0 ? 1.25f : 0.8f), 1, 2000);
            }
            var rotation = Quaternion.Euler(flyPitch, yaw, 0);
            if (keyboard != null)
            {
                if (keyboard.escapeKey.wasPressedThisFrame) ReleaseCursor();
                float x = (keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0);
                float z = (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0);
                float y = (keyboard.spaceKey.isPressed || keyboard.eKey.isPressed ? 1 : 0)
                          - (keyboard.leftCtrlKey.isPressed || keyboard.qKey.isPressed ? 1 : 0);
                float speed = flySpeed * (keyboard.leftShiftKey.isPressed ? 4 : 1) * (keyboard.leftAltKey.isPressed ? 0.25f : 1);
                var move = rotation * new Vector3(x, 0, z) + Vector3.up * y;
                transform.position += Vector3.ClampMagnitude(move, 1.5f) * speed * Time.unscaledDeltaTime;
            }
            transform.rotation = rotation;
        }

        static void CatchCursor()
        {
            if (!Application.isPlaying) return;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        static void ReleaseCursor()
        {
            if (!Application.isPlaying) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Apply()
        {
            transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            transform.position = focus - transform.forward * distance;
        }

        void OnGUI()
        {
            float scale = Mathf.Clamp(Screen.width / 1400f, 0.65f, 1.5f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14,
                padding = new RectOffset(18, 18, 14, 14) };
            if (Flying)
                GUI.Box(new Rect(24, 24, 470, 118), $"GEO WORLD  /  FLY (NO CLIP)   {flySpeed:F0} m/s\n\n" +
                    "Mouse: look (Esc frees the cursor, click catches it)\n" +
                    "WASD: fly   Space/E: up   Ctrl/Q: down   Shift: x4   Alt: slow\n" +
                    "Wheel: speed   F: back to orbit   Home: reset", style);
            else
                GUI.Box(new Rect(24, 24, 470, 99), "GEO WORLD  /  GENERATOR SAMPLE\n\n" +
                    "Right drag: orbit   |   Wheel: zoom   |   Middle drag: pan\n" +
                    "WASD: move   Q/E: height   Shift: faster   F: fly (no clip)   Home: reset", style);
        }
    }
}
