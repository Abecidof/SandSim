using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public sealed class SandInputAndUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private SandSim sim;
    [SerializeField] private RawImage simView;

    [Header("Block Painting When Over This UI (Side Menu)")]
    [SerializeField] private RectTransform sideMenuRoot;

    [Header("UI (TMP)")]
    [SerializeField] private TMP_Dropdown materialDropdown;
    [SerializeField] private Slider brushSizeSlider;
    [SerializeField] private Slider stepsPerFrameSlider;
    [SerializeField] private Toggle pauseToggle;
    [SerializeField] private Button clearButton;

    [Header("Optional Labels (TMP)")]
    [SerializeField] private TMP_Text brushSizeLabel;
    [SerializeField] private TMP_Text stepsLabel;

    [SerializeField] private Slider gravitySlider;
    [SerializeField] private TMP_Text gravityLabel;



    private readonly List<SandSim.CellType> _materials = new()
{
    SandSim.CellType.Sand,
    SandSim.CellType.Water,
    SandSim.CellType.Oil,
    SandSim.CellType.Acid,
    SandSim.CellType.Lava,
    SandSim.CellType.Stone,
    SandSim.CellType.Wood,
    SandSim.CellType.Plant,
    SandSim.CellType.Fire,
    SandSim.CellType.Steam,
    SandSim.CellType.Smoke,
    SandSim.CellType.Empty // eraser
};


    private Camera _uiCam;

    private void Start()
    {
        if (sim == null || simView == null) return;

        // Pick the correct camera for screen->UI conversions
        var canvas = simView.canvas;
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            _uiCam = canvas.worldCamera; // ScreenSpace-Camera or WorldSpace
        else
            _uiCam = null; // Overlay expects null

        SetupDropdown();
        SetupSliders();
        SetupButtons();
        SyncUIToSim();
    }

    private void Update()
    {
        if (sim == null || simView == null) return;

        // Block painting ONLY when over the side menu panel
        if (sideMenuRoot != null &&
            RectTransformUtility.RectangleContainsScreenPoint(sideMenuRoot, Input.mousePosition, _uiCam))
        {
            return;
        }

        if (Input.GetMouseButton(0))
        {
            if (TryGetGridPosFromMouse(out int gx, out int gy))
                sim.Paint(gx, gy);
        }
    }

    private bool TryGetGridPosFromMouse(out int gx, out int gy)
    {
        gx = 0; gy = 0;

        RectTransform rt = simView.rectTransform;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, _uiCam, out var local))
            return false;

        Rect rect = rt.rect;

        // local -> 0..1
        float u = Mathf.InverseLerp(rect.xMin, rect.xMax, local.x);
        float v = Mathf.InverseLerp(rect.yMin, rect.yMax, local.y);

        // Must be inside the RawImage rect
        if (u < 0f || u > 1f || v < 0f || v > 1f) return false;

        gx = Mathf.Clamp(Mathf.FloorToInt(u * sim.Width), 0, sim.Width - 1);
        gy = Mathf.Clamp(Mathf.FloorToInt(v * sim.Height), 0, sim.Height - 1);
        return true;
    }

    private void SetupDropdown()
    {
        if (materialDropdown == null) return;

        materialDropdown.ClearOptions();
        materialDropdown.AddOptions(new List<string>
        {
            "Sand",
            "Water",
            "Oil",
            "Acid",
            "Lava",
            "Stone",
            "Wood",
            "Plant",
            "Fire",
            "Steam",
            "Smoke",
            "Eraser"
        });


        materialDropdown.onValueChanged.AddListener(i =>
        {
            sim.PaintType = _materials[Mathf.Clamp(i, 0, _materials.Count - 1)];
        });
    }

    private void SetupSliders()
    {
        if (brushSizeSlider != null)
        {
            brushSizeSlider.minValue = 1;
            brushSizeSlider.maxValue = 32;
            brushSizeSlider.wholeNumbers = true;
            brushSizeSlider.onValueChanged.AddListener(v =>
            {
                sim.BrushRadius = Mathf.RoundToInt(v);
                UpdateLabels();
            });
        }

        if (stepsPerFrameSlider != null)
        {
            stepsPerFrameSlider.minValue = 1;
            stepsPerFrameSlider.maxValue = 20;
            stepsPerFrameSlider.wholeNumbers = true;
            stepsPerFrameSlider.onValueChanged.AddListener(v =>
            {
                sim.StepsPerFrame = Mathf.RoundToInt(v);
                UpdateLabels();
            });
        }

        if (pauseToggle != null)
        {
            pauseToggle.onValueChanged.AddListener(isOn =>
            {
                sim.Paused = isOn;
            });
        }
    }

    private void SetupButtons()
    {
        if (clearButton != null)
        {
            clearButton.onClick.AddListener(() => sim.Clear());
        }
    }

    private void SyncUIToSim()
    {
        if (brushSizeSlider != null) brushSizeSlider.value = sim.BrushRadius;
        if (stepsPerFrameSlider != null) stepsPerFrameSlider.value = sim.StepsPerFrame;
        if (pauseToggle != null) pauseToggle.isOn = sim.Paused;
        if (gravitySlider != null) gravitySlider.value = sim.FlowSpread;
        if (materialDropdown != null) materialDropdown.value = 0;

        UpdateLabels();
    }

    private void UpdateLabels()
    {
        if (brushSizeLabel != null) brushSizeLabel.text = $"Brush: {sim.BrushRadius}";
        if (stepsLabel != null) stepsLabel.text = $"Steps/Frame: {sim.StepsPerFrame}";
        if (gravityLabel != null) gravityLabel.text = $"Flow Spread: {sim.FlowSpread:0.00}";

    }
}
