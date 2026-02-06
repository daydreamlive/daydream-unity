using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Demo scene for testing Daydream.
/// Spawns colorful objects and adds WASD + mouse camera controls.
/// Attach to the same Camera as Daydream.
/// </summary>
public class DaydreamDemo : MonoBehaviour
{
    [Header("Camera Controls")]
    public float moveSpeed = 5f;
    public float lookSpeed = 0.1f;

    private float rotX, rotY;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        var rot = transform.eulerAngles;
        rotX = rot.y;
        rotY = rot.x;

        SpawnScene();
    }

    void SpawnScene()
    {
        // Floor
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.transform.position = new Vector3(0, -1, 0);
        floor.transform.localScale = new Vector3(3, 1, 3);
        floor.GetComponent<Renderer>().material.color = new Color(0.2f, 0.2f, 0.3f);

        // Spinning cubes
        for (int i = 0; i < 5; i++)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = new Vector3(
                Mathf.Cos(i * Mathf.PI * 2 / 5) * 4,
                0.5f,
                Mathf.Sin(i * Mathf.PI * 2 / 5) * 4 + 5
            );
            cube.GetComponent<Renderer>().material.color = Color.HSVToRGB(i / 5f, 0.8f, 0.9f);
            cube.AddComponent<Spinner>();
        }

        // Floating spheres
        for (int i = 0; i < 3; i++)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.position = new Vector3(i * 3 - 3, 2f, 6);
            sphere.GetComponent<Renderer>().material.color = Color.HSVToRGB(0.5f + i * 0.15f, 0.6f, 1f);
            var bob = sphere.AddComponent<Bobber>();
            bob.offset = i * 1.2f;
        }

        // Central tall cylinder
        var pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pillar.transform.position = new Vector3(0, 1.5f, 5);
        pillar.transform.localScale = new Vector3(0.5f, 3f, 0.5f);
        pillar.GetComponent<Renderer>().material.color = new Color(0.9f, 0.3f, 0.2f);

        // Directional light
        if (FindFirstObjectByType<Light>() == null)
        {
            var lightObj = new GameObject("Demo Light");
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            lightObj.transform.rotation = Quaternion.Euler(50, -30, 0);
        }
    }

    void Update()
    {
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        if (mouse == null || kb == null) return;

        // Mouse look
        var delta = mouse.delta.ReadValue();
        rotX += delta.x * lookSpeed;
        rotY -= delta.y * lookSpeed;
        rotY = Mathf.Clamp(rotY, -80f, 80f);
        transform.rotation = Quaternion.Euler(rotY, rotX, 0);

        // WASD movement
        var move = Vector3.zero;
        if (kb.wKey.isPressed) move += transform.forward;
        if (kb.sKey.isPressed) move -= transform.forward;
        if (kb.aKey.isPressed) move -= transform.right;
        if (kb.dKey.isPressed) move += transform.right;
        if (kb.eKey.isPressed) move += Vector3.up;
        if (kb.qKey.isPressed) move -= Vector3.up;

        transform.position += move * moveSpeed * Time.deltaTime;

        // Escape to unlock cursor
        if (kb.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}

public class Spinner : MonoBehaviour
{
    void Update()
    {
        transform.Rotate(0, 90 * Time.deltaTime, 30 * Time.deltaTime);
    }
}

public class Bobber : MonoBehaviour
{
    public float offset;
    private Vector3 startPos;

    void Start() => startPos = transform.position;

    void Update()
    {
        var pos = startPos;
        pos.y += Mathf.Sin(Time.time * 2f + offset) * 0.5f;
        transform.position = pos;
    }
}
