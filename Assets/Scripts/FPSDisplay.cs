using UnityEngine;
using UnityEngine.UI;
using TMPro;
/// <summary>
/// Displays the current framerate (FPS) as text on screen.
/// Attach this script to a GameObject with a Text component (UI Text or TextMeshPro).
/// </summary>
public class FPSDisplay : MonoBehaviour
{
    [Header("Display Settings")]
    [Tooltip("Update interval in seconds. Lower values update more frequently.")]
    [SerializeField] private float updateInterval = 0.5f;
    
    [Tooltip("Enable color coding: Green (60+), Yellow (30-59), Red (<30)")]
    [SerializeField] private bool useColorCoding = true;
    
    [Header("References")]
    [Tooltip("UI Text component to display FPS. If not assigned, will try to find one on this GameObject.")]
    [SerializeField] private TextMeshProUGUI fpsText;
    
    private float fps = 0.0f;
    private int frameCount = 0;
    private float timeAccumulator = 0.0f;

    private void Start()
    {

        Application.targetFrameRate = -1;
        // Try to find Text component if not assigned
        if (fpsText == null)
        {
            fpsText = GetComponent<TextMeshProUGUI>();
        }
        
        // If still not found, try to find TextMeshPro component
        if (fpsText == null)
        {
            #if UNITY_TMPRO
            var tmpText = GetComponent<TMPro.TextMeshProUGUI>();
            if (tmpText != null)
            {
                // Create a wrapper or use reflection to set text
                // For simplicity, we'll use a different approach
                Debug.LogWarning("FPSDisplay: TextMeshPro detected. Please use UI Text component or modify script for TMP support.");
            }
            #endif
        }
        
        if (fpsText == null)
        {
            Debug.LogWarning("FPSDisplay: No Text component found! Please assign one or add a Text component to this GameObject.");
            enabled = false;
            return;
        }
    }

    private void Update()
    {
        frameCount++;
        timeAccumulator += Time.unscaledDeltaTime;
        
        // Update FPS at specified interval
        if (timeAccumulator >= updateInterval)
        {
            fps = frameCount / timeAccumulator;
            frameCount = 0;
            timeAccumulator = 0.0f;
            
            UpdateDisplay();
        }
    }

    private void UpdateDisplay()
    {
        if (fpsText != null)
        {
            // Format FPS with 1 decimal place
            fpsText.text = $"FPS: {fps:F1}";
            
            // Optional: Change color based on FPS
            if (useColorCoding)
            {
                if (fps >= 60)
                {
                    fpsText.color = Color.green;
                }
                else if (fps >= 30)
                {
                    fpsText.color = Color.yellow;
                }
                else
                {
                    fpsText.color = Color.red;
                }
            }
        }
    }

    /// <summary>
    /// Gets the current FPS value.
    /// </summary>
    public float GetFPS()
    {
        return fps;
    }
}

