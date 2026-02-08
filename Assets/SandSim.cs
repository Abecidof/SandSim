using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class SandSim : MonoBehaviour
{
    public enum CellType : byte
    {
        Empty = 0,

        // Solids
        Stone = 1,
        Wood = 2,

        // Powders
        Sand = 10,
        Ash = 11,

        // Liquids
        Water = 20,
        Oil = 21,
        Acid = 22,
        Lava = 23,

        // Gases / special
        Steam = 30,
        Smoke = 31,
        Fire = 32,

        // Organic / growth
        Plant = 40,
    }

    private enum State : byte
    {
        Solid = 0,
        Powder = 1,
        Liquid = 2,
        Gas = 3,
        Special = 4,
        Organic = 5,
    }

    private readonly struct Props
    {
        public readonly State State;
        public readonly byte Density;
        public readonly byte Viscosity;
        public readonly byte Dispersion;
        public readonly bool Flammable;
        public readonly byte Fuel;
        public readonly short IgnitionTemp;
        public readonly short HeatOutput;
        public readonly short CoolRate;
        public readonly short BoilTemp;
        public readonly CellType VaporType;
        public readonly short CondenseTemp;
        public readonly CellType CondenseType;
        public readonly short SolidifyTemp;
        public readonly CellType SolidifyType;
        public readonly byte Corrosive;
        public readonly Color32 Color;

        public Props(
            State state,
            byte density,
            byte viscosity,
            byte dispersion,
            bool flammable,
            byte fuel,
            short ignitionTemp,
            short heatOutput,
            short coolRate,
            short boilTemp,
            CellType vaporType,
            short condenseTemp,
            CellType condenseType,
            short solidifyTemp,
            CellType solidifyType,
            byte corrosive,
            Color32 color)
        {
            State = state;
            Density = density;
            Viscosity = viscosity;
            Dispersion = dispersion;
            Flammable = flammable;
            Fuel = fuel;
            IgnitionTemp = ignitionTemp;
            HeatOutput = heatOutput;
            CoolRate = coolRate;
            BoilTemp = boilTemp;
            VaporType = vaporType;
            CondenseTemp = condenseTemp;
            CondenseType = condenseType;
            SolidifyTemp = solidifyTemp;
            SolidifyType = solidifyType;
            Corrosive = corrosive;
            Color = color;
        }
    }

    [Header("Render Target")]
    [SerializeField] private RawImage targetImage;

    [Header("Grid Settings")]
    [SerializeField, Range(32, 512)] private int width = 256;
    [SerializeField, Range(32, 512)] private int height = 256;

    [Header("Simulation Settings")]
    [SerializeField, Range(1, 60)] private int stepsPerFrame = 8;
    [SerializeField] private bool paused;
    [SerializeField, Range(5, 60)] private short ambientTemp = 20;

    [Header("Thermal Pass Optimization")]
    [SerializeField, Range(1, 8)] private int phaseEvery = 3;
    private int _phaseTick;

    [Header("Painting Settings")]
    [SerializeField] private CellType paintType = CellType.Sand;
    [SerializeField, Range(1, 64)] private int brushRadius = 6;

    [Header("Flow Tuning")]
    [SerializeField, Range(0.25f, 3f)] private float flowSpread = 1f;
    public float FlowSpread { get => flowSpread; set => flowSpread = Mathf.Clamp(value, 0.25f, 3f); }

    [Header("Smoke")]
    [SerializeField, Range(10, 255)] private byte smokeLifetime = 180;

    // Public API for UI
    public int Width => width;
    public int Height => height;
    public int StepsPerFrame { get => stepsPerFrame; set => stepsPerFrame = Mathf.Clamp(value, 1, 60); }
    public bool Paused { get => paused; set => paused = value; }
    public int BrushRadius { get => brushRadius; set => brushRadius = Mathf.Clamp(value, 1, 64); }
    public CellType PaintType { get => paintType; set => paintType = value; }

    // Storage
    private Texture2D _tex;
    private Color32[] _pixels;
    private Color32[] _dirtyBuffer; // reused for region upload
    private CellType[] _cells;

    // Updated-stamp optimization
    private int[] _updatedStamp;
    private int _stamp = 1;

    // Per-cell data
    private short[] _temp;
    private byte[] _life; // fire + smoke lifetime

    private System.Random _rng;
    private Props[] _props;

    // Dirty-rectangle tracking
    private bool _dirtyAny;
    private int _dirtyMinX, _dirtyMaxX, _dirtyMinY, _dirtyMaxY;


    private void Awake()
    {
        _rng = new System.Random(Environment.TickCount);

        int n = width * height;
        _cells = new CellType[n];
        _pixels = new Color32[n];
        _temp = new short[n];
        _life = new byte[n];
        _updatedStamp = new int[n];

        _tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        if (targetImage != null)
            targetImage.texture = _tex;

        BuildProps();

        Clear();
        PushTexture(); // full upload once
    }

    private void Update()
    {
        if (paused) return;

        for (int i = 0; i < stepsPerFrame; i++)
            StepSimulation();

        PushTexture(); // uploads only dirty rect
    }

    public void Clear()
    {
        Array.Fill(_cells, CellType.Empty);
        Array.Fill(_life, (byte)0);

        for (int i = 0; i < _temp.Length; i++)
            _temp[i] = ambientTemp;

        Color32 empty = _props[(int)CellType.Empty].Color;
        for (int i = 0; i < _pixels.Length; i++)
            _pixels[i] = empty;

        // reset stamps safely
        Array.Clear(_updatedStamp, 0, _updatedStamp.Length);
        _stamp = 1;
        _phaseTick = 0;

        // full texture dirty
        MarkDirtyRect(0, 0, width - 1, height - 1);
    }

    /// <summary>Paint in grid coordinates (0..width-1, 0..height-1).</summary>
    public void Paint(int gx, int gy)
    {
        int r = brushRadius;
        int r2 = r * r;

        int minX = Mathf.Clamp(gx - r, 0, width - 1);
        int maxX = Mathf.Clamp(gx + r, 0, width - 1);
        int minY = Mathf.Clamp(gy - r, 0, height - 1);
        int maxY = Mathf.Clamp(gy + r, 0, height - 1);

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - gy;
            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - gx;
                if (dx * dx + dy * dy > r2) continue;

                int idx = Index(x, y);

                _cells[idx] = paintType;
                _pixels[idx] = _props[(int)paintType].Color;

                if (paintType == CellType.Lava)
                {
                    _temp[idx] = 600;
                    _life[idx] = 0;
                }
                else if (paintType == CellType.Fire)
                {
                    _temp[idx] = 450;
                    _life[idx] = 20;
                }
                else if (paintType == CellType.Steam)
                {
                    _temp[idx] = 110;
                    _life[idx] = 0;
                }
                else if (paintType == CellType.Smoke)
                {
                    _temp[idx] = 80;
                    _life[idx] = smokeLifetime;
                }
                else
                {
                    _temp[idx] = ambientTemp;
                    _life[idx] = 0;
                }
            }
        }

        MarkDirtyRect(minX, minY, maxX, maxY);
    }

    private void StepSimulation()
    {
        // stamp increment instead of Array.Clear
        _stamp++;
        if (_stamp == int.MaxValue)
        {
            Array.Clear(_updatedStamp, 0, _updatedStamp.Length);
            _stamp = 1;
        }

        // --- Pass 1: solids/powders/liquids bottom-up ---
        for (int y = 0; y < height; y++)
        {
            bool leftToRight = _rng.NextDouble() < 0.5;
            if (leftToRight)
            {
                for (int x = 0; x < width; x++)
                    UpdateCell(x, y);
            }
            else
            {
                for (int x = width - 1; x >= 0; x--)
                    UpdateCell(x, y);
            }
        }

        // --- Pass 2: gases + special top-down ---
        for (int y = height - 1; y >= 0; y--)
        {
            bool leftToRight = _rng.NextDouble() < 0.5;
            if (leftToRight)
            {
                for (int x = 0; x < width; x++)
                    UpdateGasOrSpecial(x, y);
            }
            else
            {
                for (int x = width - 1; x >= 0; x--)
                    UpdateGasOrSpecial(x, y);
            }
        }

        // thermal pass throttled
        _phaseTick++;
        if (_phaseTick >= phaseEvery)
        {
            _phaseTick = 0;
            PhaseAndCoolingPass();
        }
    }

    private void MarkUpdated(int idx) => _updatedStamp[idx] = _stamp;
    private bool IsUpdated(int idx) => _updatedStamp[idx] == _stamp;

    private void MarkDirtyXY(int x, int y)
    {
        if (!_dirtyAny)
        {
            _dirtyAny = true;
            _dirtyMinX = _dirtyMaxX = x;
            _dirtyMinY = _dirtyMaxY = y;
        }
        else
        {
            if (x < _dirtyMinX) _dirtyMinX = x;
            if (x > _dirtyMaxX) _dirtyMaxX = x;
            if (y < _dirtyMinY) _dirtyMinY = y;
            if (y > _dirtyMaxY) _dirtyMaxY = y;
        }
    }

    private void MarkDirtyRect(int minX, int minY, int maxX, int maxY)
    {
        if (!_dirtyAny)
        {
            _dirtyAny = true;
            _dirtyMinX = minX; _dirtyMinY = minY;
            _dirtyMaxX = maxX; _dirtyMaxY = maxY;
        }
        else
        {
            if (minX < _dirtyMinX) _dirtyMinX = minX;
            if (minY < _dirtyMinY) _dirtyMinY = minY;
            if (maxX > _dirtyMaxX) _dirtyMaxX = maxX;
            if (maxY > _dirtyMaxY) _dirtyMaxY = maxY;
        }
    }

    private void UpdateCell(int x, int y)
    {
        int idx = Index(x, y);
        if (IsUpdated(idx)) return;

        CellType t = _cells[idx];
        if (t == CellType.Empty) return;

        Props p = _props[(int)t];

        switch (p.State)
        {
            case State.Solid:
                return;

            case State.Organic:
                UpdatePlant(x, y, idx);
                return;

            case State.Powder:
                UpdatePowder(x, y, idx, t, p);
                return;

            case State.Liquid:
                UpdateLiquid(x, y, idx, t, p);
                return;

            default:
                return;
        }
    }

    private void UpdateGasOrSpecial(int x, int y)
    {
        int idx = Index(x, y);
        if (IsUpdated(idx)) return;

        CellType t = _cells[idx];
        if (t == CellType.Empty) return;

        Props p = _props[(int)t];

        if (p.State == State.Gas)
        {
            if (t == CellType.Smoke)
            {
                if (!TickSmoke(x, y, idx))
                    return; // disappeared this tick
            }

            UpdateGas(x, y, idx, t, p);
        }
        else if (p.State == State.Special)
        {
            if (t == CellType.Fire)
                UpdateFire(x, y, idx);
        }
    }

    private bool TickSmoke(int x, int y, int idx)
    {
        if (_life[idx] == 0)
            _life[idx] = smokeLifetime;

        _life[idx]--;

        if (_life[idx] == 0)
        {
            _cells[idx] = CellType.Empty;
            _pixels[idx] = _props[(int)CellType.Empty].Color;
            _temp[idx] = ambientTemp;

            MarkUpdated(idx);
            MarkDirtyXY(x, y);
            return false;
        }

        return true;
    }

    private void UpdatePowder(int x, int y, int idx, CellType t, Props p)
    {
        int belowY = y - 1;
        if (belowY < 0) return;

        if (TryMoveDensity(x, y, x, belowY, t, p)) return;

        int dir = RandDir();
        if (TryMoveDensity(x, y, x + dir, belowY, t, p)) return;
        if (TryMoveDensity(x, y, x - dir, belowY, t, p)) return;

        //Corrected sand behavior
        if (_rng.NextDouble() < 0.15)
        {
            if (TryMoveIntoEmpty(x, y, x + dir, y-1, t)) return;
            if (TryMoveIntoEmpty(x, y, x - dir, y-1, t)) return;
        }
    }

    // -----------------------------
    // Improved liquid pooling/spread
    // -----------------------------
    private void UpdateLiquid(int x, int y, int idx, CellType t, Props p)
    {
        int belowY = y - 1;
        if (belowY >= 0)
        {
            if (TryMoveDensity(x, y, x, belowY, t, p)) return;

            int dirFall = RandDir();
            if (TryMoveDensity(x, y, x + dirFall, belowY, t, p)) return;
            if (TryMoveDensity(x, y, x - dirFall, belowY, t, p)) return;
        }

        // Lateral: choose the better side (pool on support, spill into drops),
        // and respect viscosity + flowSpread without excessive repeated attempts.
        float visc01 = p.Viscosity / 255f;            // 0 = runny, 1 = very viscous
        float runny01 = 1f - visc01;

        // How often does this liquid even attempt lateral equalization?
        float lateralChance = Mathf.Clamp01(0.18f + runny01 * 0.65f) * Mathf.Clamp01(flowSpread / 1.25f);
        if (_rng.NextDouble() > lateralChance) return;

        int dir = RandDir();
        int aX = x + dir;
        int bX = x - dir;

        bool aOk = EvaluateLiquidLateralTarget(aX, y, p, out int aScore);
        bool bOk = EvaluateLiquidLateralTarget(bX, y, p, out int bScore);

        if (!aOk && !bOk) return;

        // Slight randomness to prevent lock-step flow patterns.
        // (No allocations; just integer noise.)
        aScore += _rng.Next(0, 2);
        bScore += _rng.Next(0, 2);

        // Prefer higher score; if tie, prefer randomized 'dir' side.
        if (aOk && (!bOk || aScore >= bScore))
        {
            if (TryMoveDensity(x, y, aX, y, t, p)) return;
            if (bOk) TryMoveDensity(x, y, bX, y, t, p);
            return;
        }

        if (bOk)
        {
            if (TryMoveDensity(x, y, bX, y, t, p)) return;
            if (aOk) TryMoveDensity(x, y, aX, y, t, p);
        }
    }

    // Score a lateral target for a liquid.
    // Higher is better. Intentionally cheap (small constant work).
    private bool EvaluateLiquidLateralTarget(int toX, int y, Props movingProps, out int score)
    {
        score = int.MinValue;

        if ((uint)toX >= (uint)width) return false;

        int toIdx = Index(toX, y);

        // If the target cell is already updated this stamp, avoid choosing it.
        if (IsUpdated(toIdx)) return false;

        CellType destType = _cells[toIdx];

        // Determine if we can enter (empty) or swap (less dense, non-solid).
        bool canEnter = false;
        bool destEmpty = destType == CellType.Empty;

        if (destEmpty)
        {
            canEnter = true;
        }
        else
        {
            Props destProps = _props[(int)destType];
            if (destProps.State != State.Solid && movingProps.Density > destProps.Density)
                canEnter = true;
        }

        if (!canEnter) return false;

        // Pooling preference: supported targets are better when there is no obvious drop.
        bool supported = (y == 0) || (_cells[Index(toX, y - 1)] != CellType.Empty);

        // Spill preference: if there is space below target, prefer moving toward deeper drops.
        // Keep this scan short to maintain perf.
        int drop = (y == 0) ? 0 : DropDistanceBelow(toX, y, maxDepth: 4);

        // Scoring:
        // - Drops matter a lot (spill into cavities / off ledges).
        // - Otherwise prefer supported lateral spread (pooling on surfaces).
        // - Slight bonus for moving into empty (reduces oscillation vs swapping).
        score = drop * 6;
        if (drop == 0 && supported) score += 3;
        if (destEmpty) score += 1;

        return true;
    }

    // Count how many consecutive empty cells are below (toX, y-1), up to maxDepth.
    private int DropDistanceBelow(int toX, int y, int maxDepth)
    {
        int d = 0;
        for (int yy = y - 1; yy >= 0 && d < maxDepth; yy--)
        {
            if (_cells[Index(toX, yy)] != CellType.Empty) break;
            d++;
        }
        return d;
    }

    // -----------------------------
    // Improved gas drift / diffusion
    // -----------------------------
    private void UpdateGas(int x, int y, int idx, CellType t, Props p)
    {
        int aboveY = y + 1;
        if (aboveY >= height) return;

        int dir = RandDir();

        // Gas meander: occasionally drift sideways even if it could rise.
        // Smoke meanders a bit more than steam (visually pleasing).
        float meanderChance = (t == CellType.Smoke ? 0.28f : 0.18f) * Mathf.Clamp01(flowSpread / 1.25f);

        // If blocked directly above, we prefer to slide sideways under ceilings sooner.
        bool aboveBlocked = !CanGasEnter(x, y, x, aboveY, p);

        if (!aboveBlocked && _rng.NextDouble() < meanderChance)
        {
            // Try a lateral gas-side move first (only into empty or heavier gas).
            if (TryMoveGasSide(x, y, x + dir, y, t, p)) return;
            if (TryMoveGasSide(x, y, x - dir, y, t, p)) return;
        }

        // Standard buoyant rise / diagonal rise.
        if (TryMoveDensityUp(x, y, x, aboveY, t, p)) return;
        if (TryMoveDensityUp(x, y, x + dir, aboveY, t, p)) return;
        if (TryMoveDensityUp(x, y, x - dir, aboveY, t, p)) return;

        // If we couldn't rise, drift sideways (ceiling-hug / diffusion).
        if (TryMoveGasSide(x, y, x + dir, y, t, p)) return;
        if (TryMoveGasSide(x, y, x - dir, y, t, p)) return;

        // Rarely, allow a tiny downward “eddy” to prevent trapped gas from becoming static.
        // Keep extremely low to avoid smoke “falling.”
        if (_rng.NextDouble() < 0.03)
        {
            int downY = y - 1;
            if (downY >= 0)
            {
                if (TryMoveIntoEmpty(x, y, x + dir, downY, t)) return;
                if (TryMoveIntoEmpty(x, y, x - dir, downY, t)) return;
            }
        }
    }

    // Can gas enter a target cell for upward movement? (Used only for 'aboveBlocked' heuristic.)
    private bool CanGasEnter(int fromX, int fromY, int toX, int toY, Props gasProps)
    {
        if ((uint)toX >= (uint)width || (uint)toY >= (uint)height) return false;

        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);

        if (IsUpdated(fromIdx) || IsUpdated(toIdx)) return false;

        CellType destType = _cells[toIdx];
        if (destType == CellType.Empty) return true;

        Props destProps = _props[(int)destType];
        if (destProps.State == State.Solid || destProps.State == State.Powder) return false;

        return gasProps.Density < destProps.Density;
    }

    // Lateral movement for gases: only into empty OR swap with heavier gas.
    // This avoids sideways shoving into liquids, which looks like smearing.
    private bool TryMoveGasSide(int fromX, int fromY, int toX, int toY, CellType gasType, Props gasProps)
    {
        if ((uint)toX >= (uint)width || (uint)toY >= (uint)height) return false;

        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);

        if (IsUpdated(fromIdx) || IsUpdated(toIdx)) return false;

        CellType destType = _cells[toIdx];

        if (destType == CellType.Empty)
            return DoSwap(fromX, fromY, toX, toY, fromIdx, toIdx);

        Props destProps = _props[(int)destType];

        // Only allow lateral swapping among gases (lets steam sift above smoke),
        // but do not laterally displace liquids/powders.
        if (destProps.State != State.Gas) return false;

        if (gasProps.Density < destProps.Density)
            return DoSwap(fromX, fromY, toX, toY, fromIdx, toIdx);

        return false;
    }

    private void UpdateFire(int x, int y, int idx)
    {
        if (_life[idx] == 0) _life[idx] = 18;
        else _life[idx]--;

        EmitHeat(x, y, idx, heat: 35);

        TryIgniteNeighbor(x + 1, y);
        TryIgniteNeighbor(x - 1, y);
        TryIgniteNeighbor(x, y + 1);
        TryIgniteNeighbor(x, y - 1);

        int dir = RandDir();
        int aboveY = y + 1;
        if (aboveY < height && _rng.NextDouble() < 0.1)//added stochastic skip to slow fire relative to emitted smoke. Should remove horizontal smoke line artifact.
        {
            if (TryMoveFire(x, y, x, aboveY)) return;
            if (TryMoveFire(x, y, x + dir, aboveY)) return;
            if (TryMoveFire(x, y, x - dir, aboveY)) return;
        }

        if (_life[idx] == 0)
        {
            _cells[idx] = CellType.Smoke;
            _pixels[idx] = _props[(int)CellType.Smoke].Color;
            _temp[idx] = (short)Mathf.Max(_temp[idx], 80);
            _life[idx] = smokeLifetime;

            MarkUpdated(idx);
            MarkDirtyXY(x, y);
        }
    }

    private void UpdatePlant(int x, int y, int idx)
    {
        if (_rng.NextDouble() > 0.02) return;
        if (!HasNeighborType(x, y, CellType.Water)) return;

        int aboveY = y + 1;
        if (aboveY >= height) return;

        int aboveIdx = Index(x, aboveY);
        if (_cells[aboveIdx] != CellType.Empty) return;

        _cells[aboveIdx] = CellType.Plant;
        _pixels[aboveIdx] = _props[(int)CellType.Plant].Color;
        _temp[aboveIdx] = ambientTemp;
        _life[aboveIdx] = 0;

        MarkUpdated(aboveIdx);
        MarkDirtyXY(x, aboveY);
    }

    private void PhaseAndCoolingPass()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            CellType t = _cells[i];
            if (t == CellType.Empty)
            {
                if (_temp[i] > ambientTemp) _temp[i]--;
                else if (_temp[i] < ambientTemp) _temp[i]++;
                continue;
            }

            Props p = _props[(int)t];

            short curT = _temp[i];
            if (curT > ambientTemp)
                curT = (short)Mathf.Max(ambientTemp, curT - Mathf.Max(1, p.CoolRate));
            else if (curT < ambientTemp)
                curT = (short)Mathf.Min(ambientTemp, curT + 1);

            _temp[i] = curT;

            // boiling
            if (p.BoilTemp > 0 && curT >= p.BoilTemp && p.VaporType != CellType.Empty)
            {
                if (_cells[i] != p.VaporType)
                {
                    _cells[i] = p.VaporType;
                    _pixels[i] = _props[(int)p.VaporType].Color;
                    _life[i] = (p.VaporType == CellType.Smoke) ? smokeLifetime : (byte)0;

                    int x = i % width;
                    int y = i / width;
                    MarkDirtyXY(x, y);
                }
                continue;
            }

            // condense
            if (p.CondenseTemp > 0 && p.State == State.Gas && curT <= p.CondenseTemp)
            {
                if (_cells[i] != p.CondenseType)
                {
                    _cells[i] = p.CondenseType;
                    _pixels[i] = _props[(int)p.CondenseType].Color;
                    _life[i] = 0;

                    int x = i % width;
                    int y = i / width;
                    MarkDirtyXY(x, y);
                }
                continue;
            }

            // solidify
            if (p.SolidifyTemp > 0 && p.State == State.Liquid && curT <= p.SolidifyTemp)
            {
                if (_cells[i] != p.SolidifyType)
                {
                    _cells[i] = p.SolidifyType;
                    _pixels[i] = _props[(int)p.SolidifyType].Color;
                    _life[i] = 0;

                    int x = i % width;
                    int y = i / width;
                    MarkDirtyXY(x, y);
                }
                continue;
            }

            // ignition
            if (p.Flammable && curT >= p.IgnitionTemp)
            {
                if (_rng.NextDouble() < 0.10)
                {
                    _cells[i] = CellType.Fire;
                    _pixels[i] = _props[(int)CellType.Fire].Color;
                    _life[i] = p.Fuel > 0 ? p.Fuel : (byte)18;
                    _temp[i] = (short)Mathf.Max(_temp[i], 350);

                    int x = i % width;
                    int y = i / width;
                    MarkDirtyXY(x, y);
                    continue;
                }
            }

            // corrosion
            if (t == CellType.Acid)
            {
                if (_rng.NextDouble() < 0.20)
                    TryCorrodeOneNeighbor(i);
            }

            // lava heat + water interaction
            if (t == CellType.Lava)
            {
                int x = i % width;
                int y = i / width;
                EmitHeat(x, y, i, heat: 25);

                if (HasNeighborType(x, y, CellType.Water))
                {
                    FlashBoilNeighbors(x, y);
                    _temp[i] = (short)Mathf.Max(ambientTemp, _temp[i] - 30);
                }
            }
        }
    }

    private void FlashBoilNeighbors(int x, int y)
    {
        TryConvertIfType(x + 1, y, CellType.Water, CellType.Steam, 120);
        TryConvertIfType(x - 1, y, CellType.Water, CellType.Steam, 120);
        TryConvertIfType(x, y + 1, CellType.Water, CellType.Steam, 120);
        TryConvertIfType(x, y - 1, CellType.Water, CellType.Steam, 120);
    }

    private void TryConvertIfType(int x, int y, CellType from, CellType to, short temp)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
        int idx = Index(x, y);
        if (_cells[idx] != from) return;

        _cells[idx] = to;
        _pixels[idx] = _props[(int)to].Color;
        _temp[idx] = (short)Mathf.Max(_temp[idx], temp);
        _life[idx] = (to == CellType.Smoke) ? smokeLifetime : (byte)0;

        MarkDirtyXY(x, y);
    }

    private void TryIgniteNeighbor(int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
        int idx = Index(x, y);
        CellType t = _cells[idx];
        if (t == CellType.Empty) return;

        Props p = _props[(int)t];
        if (!p.Flammable) return;

        _temp[idx] = (short)Mathf.Min(900, _temp[idx] + 25);

        if (_temp[idx] >= p.IgnitionTemp && _rng.NextDouble() < 0.25)
        {
            _cells[idx] = CellType.Fire;
            _pixels[idx] = _props[(int)CellType.Fire].Color;
            _life[idx] = p.Fuel > 0 ? p.Fuel : (byte)18;
            _temp[idx] = (short)Mathf.Max(_temp[idx], 350);

            MarkUpdated(idx);
            MarkDirtyXY(x, y);
        }
    }

    private void TryCorrodeOneNeighbor(int acidIdx)
    {
        int x = acidIdx % width;
        int y = acidIdx / width;

        int r = _rng.Next(4);
        for (int k = 0; k < 4; k++)
        {
            int d = (r + k) & 3;
            int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
            int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
            if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;

            int nIdx = Index(nx, ny);
            CellType nt = _cells[nIdx];
            if (nt == CellType.Empty || nt == CellType.Acid) continue;

            byte eatChance;
            switch (nt)
            {
                case CellType.Plant:
                case CellType.Wood:
                    eatChance = 45; break;
                case CellType.Sand:
                case CellType.Ash:
                    eatChance = 20; break;
                case CellType.Stone:
                    eatChance = 6; break;
                default:
                    eatChance = 10; break;
            }

            short acidTemp = _temp[acidIdx];
            if (acidTemp > 60) eatChance = (byte)Mathf.Min(80, eatChance + 10);

            if (_rng.Next(100) < eatChance)
            {
                _cells[nIdx] = CellType.Empty;
                _pixels[nIdx] = _props[(int)CellType.Empty].Color;
                _temp[nIdx] = ambientTemp;
                _life[nIdx] = 0;

                MarkDirtyXY(nx, ny);

                // Occasionally acid produces a puff (smoke)
                if (_rng.NextDouble() < 0.10)
                {
                    int upY = ny + 1;
                    if (upY < height)
                    {
                        int upIdx = Index(nx, upY);
                        if (_cells[upIdx] == CellType.Empty)
                        {
                            _cells[upIdx] = CellType.Smoke;
                            _pixels[upIdx] = _props[(int)CellType.Smoke].Color;
                            _temp[upIdx] = 70;
                            _life[upIdx] = smokeLifetime;

                            MarkDirtyXY(nx, upY);
                        }
                    }
                }
            }

            break;
        }
    }

    private void EmitHeat(int x, int y, int idx, short heat)
    {
        _temp[idx] = (short)Mathf.Min(900, _temp[idx] + heat);

        HeatAt(x + 1, y, heat);
        HeatAt(x - 1, y, heat);
        HeatAt(x, y + 1, heat);
        HeatAt(x, y - 1, heat);
    }

    private void HeatAt(int x, int y, short heat)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
        int idx = Index(x, y);
        if (_cells[idx] == CellType.Empty) return;
        _temp[idx] = (short)Mathf.Min(900, _temp[idx] + (short)(heat / 2));
    }

    private bool HasNeighborType(int x, int y, CellType t)
    {
        return CheckType(x + 1, y, t)
            || CheckType(x - 1, y, t)
            || CheckType(x, y + 1, t)
            || CheckType(x, y - 1, t);
    }

    private bool CheckType(int x, int y, CellType t)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
        return _cells[Index(x, y)] == t;
    }

    private bool TryMoveFire(int fromX, int fromY, int toX, int toY)
    {
        if ((uint)toX >= (uint)width || (uint)toY >= (uint)height) return false;

        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);
        if (IsUpdated(fromIdx) || IsUpdated(toIdx)) return false;

        CellType dest = _cells[toIdx];
        if (dest != CellType.Empty && dest != CellType.Smoke && dest != CellType.Steam) return false;

        _cells[toIdx] = CellType.Fire;
        _pixels[toIdx] = _props[(int)CellType.Fire].Color;
        _temp[toIdx] = (short)Mathf.Max(_temp[fromIdx], 300);
        _life[toIdx] = _life[fromIdx];

        _cells[fromIdx] = CellType.Smoke;
        _pixels[fromIdx] = _props[(int)CellType.Smoke].Color;
        _temp[fromIdx] = (short)Mathf.Max(_temp[fromIdx], 90);
        _life[fromIdx] = smokeLifetime;

        MarkUpdated(toIdx);
        MarkUpdated(fromIdx);

        MarkDirtyXY(fromX, fromY);
        MarkDirtyXY(toX, toY);

        return true;
    }

    private bool TryMoveIntoEmpty(int fromX, int fromY, int toX, int toY, CellType movingType)
    {
        if ((uint)toX >= (uint)width || (uint)toY >= (uint)height) return false;

        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);

        if (IsUpdated(fromIdx) || IsUpdated(toIdx)) return false;
        if (_cells[toIdx] != CellType.Empty) return false;

        _cells[toIdx] = movingType;
        _cells[fromIdx] = CellType.Empty;

        _pixels[toIdx] = _props[(int)movingType].Color;
        _pixels[fromIdx] = _props[(int)CellType.Empty].Color;

        _temp[toIdx] = _temp[fromIdx];
        _temp[fromIdx] = ambientTemp;

        _life[toIdx] = _life[fromIdx];
        _life[fromIdx] = 0;

        MarkUpdated(toIdx);
        MarkUpdated(fromIdx);

        MarkDirtyXY(fromX, fromY);
        MarkDirtyXY(toX, toY);

        return true;
    }

    private bool TryMoveDensity(int fromX, int fromY, int toX, int toY, CellType movingType, Props movingProps)
    {
        if ((uint)toX >= (uint)width || (uint)toY >= (uint)height) return false;

        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);

        if (IsUpdated(fromIdx) || IsUpdated(toIdx)) return false;

        CellType destType = _cells[toIdx];

        if (destType == CellType.Empty)
            return DoSwap(fromX, fromY, toX, toY, fromIdx, toIdx);

        Props destProps = _props[(int)destType];

        if (destProps.State == State.Solid) return false;

        if (movingProps.Density > destProps.Density)
            return DoSwap(fromX, fromY, toX, toY, fromIdx, toIdx);

        return false;
    }

    private bool TryMoveDensityUp(int fromX, int fromY, int toX, int toY, CellType gasType, Props gasProps)
    {
        if ((uint)toX >= (uint)width || (uint)toY >= (uint)height) return false;

        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);

        if (IsUpdated(fromIdx) || IsUpdated(toIdx)) return false;

        CellType destType = _cells[toIdx];

        if (destType == CellType.Empty)
            return DoSwap(fromX, fromY, toX, toY, fromIdx, toIdx);

        Props destProps = _props[(int)destType];

        if (destProps.State == State.Solid || destProps.State == State.Powder) return false;

        if (gasProps.Density < destProps.Density)
            return DoSwap(fromX, fromY, toX, toY, fromIdx, toIdx);

        return false;
    }

    private bool DoSwap(int ax, int ay, int bx, int by, int aIdx, int bIdx)
    {
        CellType aT = _cells[aIdx];
        CellType bT = _cells[bIdx];

        _cells[aIdx] = bT;
        _cells[bIdx] = aT;

        _pixels[aIdx] = _props[(int)bT].Color;
        _pixels[bIdx] = _props[(int)aT].Color;

        short aTemp = _temp[aIdx];
        _temp[aIdx] = _temp[bIdx];
        _temp[bIdx] = aTemp;

        byte aLife = _life[aIdx];
        _life[aIdx] = _life[bIdx];
        _life[bIdx] = aLife;

        MarkUpdated(aIdx);
        MarkUpdated(bIdx);

        MarkDirtyXY(ax, ay);
        MarkDirtyXY(bx, by);

        return true;
    }

    private void PushTexture()
    {
        if (!_dirtyAny) return;

        int minX = _dirtyMinX;
        int maxX = _dirtyMaxX;
        int minY = _dirtyMinY;
        int maxY = _dirtyMaxY;

        int w = maxX - minX + 1;
        int h = maxY - minY + 1;

        int needed = w * h;
        if (_dirtyBuffer == null || _dirtyBuffer.Length < needed)
            _dirtyBuffer = new Color32[needed];

        // Pack region into contiguous buffer for SetPixels32
        // IMPORTANT: keep row-contiguous packing to avoid streak artifacts.
        for (int row = 0; row < h; row++)
        {
            int src = minX + (minY + row) * width;
            int dst = row * w;
            Array.Copy(_pixels, src, _dirtyBuffer, dst, w);
        }

        _tex.SetPixels32(minX, minY, w, h, _dirtyBuffer);
        _tex.Apply(false, false);

        _dirtyAny = false;
    }

    private int Index(int x, int y) => x + y * width;
    private int RandDir() => (_rng.NextDouble() < 0.5) ? -1 : 1;

    private void BuildProps()
    {
        _props = new Props[256];

        Props P(
            State state,
            byte density,
            byte viscosity,
            byte dispersion,
            bool flammable,
            byte fuel,
            short ignitionTemp,
            short heatOutput,
            short coolRate,
            short boilTemp,
            CellType vaporType,
            short condenseTemp,
            CellType condenseType,
            short solidifyTemp,
            CellType solidifyType,
            byte corrosive,
            Color32 color)
        => new Props(state, density, viscosity, dispersion, flammable, fuel, ignitionTemp, heatOutput, coolRate,
                    boilTemp, vaporType, condenseTemp, condenseType, solidifyTemp, solidifyType, corrosive, color);

        _props[(int)CellType.Empty] = P(State.Solid, 0, 0, 0, false, 0, 0, 0, 1, 0, CellType.Empty, 0, CellType.Empty, 0, CellType.Empty, 0,
            new Color32(0, 0, 0, 255));

        _props[(int)CellType.Stone] = P(State.Solid, 240, 255, 0, false, 0, 0, 0, 1, 0, CellType.Empty, 0, CellType.Empty, 0, CellType.Empty, 0,
            new Color32(130, 130, 130, 255));

        _props[(int)CellType.Wood] = P(State.Solid, 140, 255, 0, true, 28, 180, 0, 1, 0, CellType.Empty, 0, CellType.Empty, 0, CellType.Empty, 0,
            new Color32(120, 80, 40, 255));

        _props[(int)CellType.Sand] = P(State.Powder, 160, 180, 0, false, 0, 0, 0, 1, 0, CellType.Empty, 0, CellType.Empty, 0, CellType.Empty, 0,
            new Color32(220, 200, 120, 255));

        _props[(int)CellType.Ash] = P(State.Powder, 80, 160, 0, false, 0, 0, 0, 1, 0, CellType.Empty, 0, CellType.Empty, 0, CellType.Empty, 0,
            new Color32(90, 90, 90, 255));

        _props[(int)CellType.Water] = P(State.Liquid, 120, 30, 6, false, 0, 0, 0, 1,
            100, CellType.Steam,
            70, CellType.Water,
            0, CellType.Empty, 0,
            new Color32(80, 140, 220, 255));

        _props[(int)CellType.Oil] = P(State.Liquid, 90, 60, 5, true, 22, 160, 0, 1,
            0, CellType.Empty,
            0, CellType.Empty,
            0, CellType.Empty, 0,
            new Color32(70, 70, 20, 255));

        _props[(int)CellType.Acid] = P(State.Liquid, 115, 40, 6, false, 0, 0, 0, 1,
            0, CellType.Empty,
            0, CellType.Empty,
            0, CellType.Empty, 1,
            new Color32(90, 220, 90, 255));

        _props[(int)CellType.Lava] = P(State.Liquid, 200, 120, 3, false, 0, 0, 35, 1,
            0, CellType.Empty,
            0, CellType.Empty,
            220, CellType.Stone, 0,
            new Color32(240, 90, 20, 255));

        _props[(int)CellType.Steam] = P(State.Gas, 20, 0, 4, false, 0, 0, 0, 1,
            0, CellType.Empty,
            70, CellType.Water, 0, CellType.Empty, 0,
            new Color32(170, 170, 170, 255));

        _props[(int)CellType.Smoke] = P(State.Gas, 25, 0, 4, false, 0, 0, 0, 1,
            0, CellType.Empty,
            0, CellType.Empty, 0, CellType.Empty, 0,
            new Color32(60, 60, 60, 255));

        _props[(int)CellType.Fire] = P(State.Special, 5, 0, 0, false, 18, 0, 45, 1,
            0, CellType.Empty, 0, CellType.Empty, 0, CellType.Empty, 0,
            new Color32(255, 160, 40, 255));

        _props[(int)CellType.Plant] = P(State.Organic, 130, 255, 0, true, 20, 170, 0, 1,
            0, CellType.Empty, 0, CellType.Empty, 0, CellType.Empty, 0,
            new Color32(40, 200, 60, 255));
    }
}
