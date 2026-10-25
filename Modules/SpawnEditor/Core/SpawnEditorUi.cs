using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.Injection;
using TMPro;
using UnityEngine;

namespace SpawnEditorKit;

// Thanks: KYMario/TownOfHost-K (custom spawn editor in freeplay)
// Freeplay-only editor: two ability buttons (drop/remove a pin where the player stands, open the settings window),
// pins on the ship, and a settings window. Nothing here is sent over the network; spawn points only change the host's local file.
public class SpawnEditorUi : MonoBehaviour
{
    public static SpawnEditorUi Instance;

    private const int VisibleRows = 5;

    // Standing this close to a pin turns the pin button into "remove".
    private const float NearRadius = 0.6f;
    private const float NearCheckSeconds = 0.1f;

    private ISpawnEditorHost Host => SpawnStore.Host;

    private int _mapId = -1;
    private bool _details;
    private bool _fileMode;
    private int _selected = -1;
    private int _near = -1;
    private float _nextNearTime;
    private bool _dirty = true;
    private bool _failed;

    private float _screenW = 1920f, _screenH = 1080f;
    private float _lastScale = -1f;

    private Rect _window;
    private bool _windowPlaced;
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2 _scroll;

    private GUIStyle _sWindow, _sTitle, _sHint, _sRow, _sRowSel, _sRowWarn, _sPct, _sEmpty, _sWarn, _sStatus, _sHead, _sRooms;
    private GUIStyle _sBtn, _sBtnOff, _sBtnAccent, _sBtn3, _sBtn3Off, _sBtn3Danger, _sSq, _sSqOff, _sSeg, _sSegOn, _sSegOff, _sX;
    private GUIStyle _sCheckOn, _sCheckOff, _sCheckMark;
    private GUIStyle[] _sColors;

    private string _tPin, _tUnpin, _tSettings;
    private GUIContent _cTitle, _cCount, _cRooms, _cEmpty, _cNoFiles, _cWarn, _cX;
    private GUIContent _cModeOff, _cModeMix, _cModeOnly;
    private GUIContent _cHeadSelected, _cHeadTest, _cHeadShare, _cGoTo, _cMoveHere, _cDelete, _cWeight, _cMinus, _cPlus;
    private GUIContent _cOpenMap, _cTest, _cTestBack, _cExport, _cImport, _cBack, _cFolder, _cStatus, _cBlank;
    private GUIContent[] _cRowNames = [];
    private GUIContent[] _cRowPcts = [];
    private GUIContent[] _cFiles = [];
    private List<string> _files = [];
    private int _mode;
    private bool _modeAvailable;
    private DateTime _statusUntil;

    private GameObject _markerRoot;

    private float Scale => _screenW / 1080f * (OperatingSystem.IsAndroid() ? 0.6f : 0.5f);

    private void Awake()
    {
        Instance = this;
    }

    private List<SpawnPoint> Points => SpawnStore.Library.Current.GetPoints(_mapId, true);

    public bool Active => _mapId >= 0;
    public List<SpawnPoint> CurrentPoints => Points;
    public int Selected => _selected;

    private bool[] _offFloor = [];
    private bool _floorTried;
    private readonly List<Texture2D> _styleTextures = [];

    public static Color PinColor(SpawnPoint p, bool onFloor)
    {
        if (!onFloor) return new Color(1f, 0.15f, 0.15f, p.Enabled ? 1f : 0.45f);
        (byte r, byte g, byte b) = SpawnPalette.Colors[SpawnPalette.Wrap(p.Color)];
        return new Color(r / 255f, g / 255f, b / 255f, p.Enabled ? 1f : 0.45f);
    }

    private void Update()
    {
        int map;
        try { map = Host?.EditMapId ?? -1; }
        catch { map = -1; }

        SetPainter(map >= 0 && !_failed);

        if (map < 0)
        {
            _failed = false;
            if (_mapId >= 0) Shutdown();
            return;
        }

        // After an exception the editor stays off until freeplay is left, so a lasting fault cannot repeat every frame.
        if (_failed)
        {
            if (_mapId >= 0) Shutdown();
            return;
        }

        try
        {
            _screenW = Screen.width;
            _screenH = Screen.height;

            if (map != _mapId || !_markerRoot)
            {
                if (map != _mapId)
                {
                    SpawnFloorCheck.Clear();
                    _floorTried = false;
                    ForgetTest();
                }
                _mapId = map;
                _selected = -1;
                _near = -1;
                _dirty = true;
            }

            // One attempt per map: a scan that cannot build (no rooms, too large, exception) must not retry every frame.
            if (!_floorTried)
            {
                _floorTried = true;
                try { SpawnFloorCheck.Build(ShipStatus.Instance, Host.LocalPlayerPosition); }
                catch (Exception e) { Host.Warn($"Spawn editor floor scan failed: {e}"); }
                if (SpawnFloorCheck.Built) _dirty = true;
            }

            if (_tPin != null) SpawnEditorButtons.Ensure(_near >= 0 ? _tUnpin : _tPin, _tSettings);

            UpdateNear();
            HandleMapClick();

            if (!_dirty) return;
            _dirty = false;

            RebuildFloorFlags();
            RebuildMarkers();
            RebuildLabels();
            SpawnEditorButtons.SetPinLabel(_near >= 0 ? _tUnpin : _tPin);
            SpawnEditorMap.Refresh();
        }
        catch (Exception e)
        {
            Host.Warn($"Spawn editor refresh failed: {e}");
            _failed = true;
            Shutdown();
        }
    }

    // OnGUI を持つ有効な MonoBehaviour は描く物が無くても毎フレーム呼ばれるので、描画役はエディタを使っている間だけ有効にする。
    private SpawnEditorUiPainter _painter;
    private bool _painterOn;

    private void SetPainter(bool on)
    {
        if (on == _painterOn) return;
        _painterOn = on;

        if (on && !_painter)
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<SpawnEditorUiPainter>()) ClassInjector.RegisterTypeInIl2Cpp<SpawnEditorUiPainter>();
            _painter = gameObject.AddComponent<SpawnEditorUiPainter>();
        }

        if (_painter) _painter.enabled = on;
    }

    private void Shutdown()
    {
        _mapId = -1;
        _details = false;
        _fileMode = false;
        _selected = -1;
        _near = -1;
        ClearMarkers();
        SpawnEditorMap.Clear();
        SpawnEditorButtons.Destroy();
        SpawnFloorCheck.Clear();
        _floorTried = false;
        ForgetTest();
        _dragging = false;
        _cStatus = null;

        foreach (Texture2D tex in _styleTextures)
            if (tex) Destroy(tex);
        _styleTextures.Clear();
        _lastScale = -1f;
    }

    private void RebuildFloorFlags()
    {
        List<SpawnPoint> points = Points;
        _offFloor = new bool[points.Count];
        for (int i = 0; i < points.Count; i++) _offFloor[i] = !SpawnFloorCheck.IsSpawnOnFloor(points[i].X, points[i].Y);
    }

    // ==== the pin under the player: checked a few times a second, rebuilds only when it changes ====

    private void UpdateNear()
    {
        float now = Time.unscaledTime;
        if (now < _nextNearTime) return;
        _nextNearTime = now + NearCheckSeconds;

        Vector2 pos = Host.LocalPlayerPosition;
        List<SpawnPoint> points = Points;
        int near = -1;
        float best = NearRadius * NearRadius;

        for (int i = 0; i < points.Count; i++)
        {
            float dx = points[i].X - pos.x, dy = points[i].Y - pos.y;
            float d = dx * dx + dy * dy;
            if (d >= best) continue;
            best = d;
            near = i;
        }

        if (near == _near) return;
        _near = near;
        if (near >= 0) _selected = near;
        _dirty = true;
    }

    // ==== ability buttons ====

    public void PressPin()
    {
        if (_mapId < 0) return;
        if (_near >= 0 && _near < Points.Count) RemoveAt(_near);
        else AddHere();
    }

    public void PressSettings()
    {
        if (_mapId < 0) return;
        _details = !_details;
        _fileMode = false;
    }

    // ==== map clicks: select a pin near the click, or add a point on the nearest floor ====

    private const float MapPickRadius = 0.9f;
    private const float MapSnapRadius = 1.5f;

    private void HandleMapClick()
    {
        if (!SpawnEditorMap.IsOpen || !Input.GetMouseButtonDown(0)) return;

        Vector3 mouse = Input.mousePosition;
        var gui = new Vector2(mouse.x, _screenH - mouse.y);
        if (_details && _window.Contains(gui)) return;
        if (!SpawnEditorMap.TryScreenToShip(mouse, out Vector2 ship)) return;

        List<SpawnPoint> points = Points;
        int nearest = -1;
        float best = MapPickRadius * MapPickRadius;

        for (int i = 0; i < points.Count; i++)
        {
            float dx = points[i].X - ship.x, dy = points[i].Y - ship.y;
            float d = dx * dx + dy * dy;
            if (d >= best) continue;
            best = d;
            nearest = i;
        }

        if (nearest >= 0)
        {
            _selected = nearest;
            _dirty = true;
            return;
        }

        if (!SpawnFloorCheck.TryNearestSpawn(ship, MapSnapRadius, out Vector2 spot)) return;
        AddPoint(spot);
    }

    // ==== world pins ====

    private void ClearMarkers()
    {
        if (_markerRoot) Destroy(_markerRoot);
        _markerRoot = null;
    }

    private void RebuildMarkers()
    {
        ClearMarkers();
        _markerRoot = new GameObject("SpawnEditorPins");

        List<SpawnPoint> points = Points;

        for (int i = 0; i < points.Count; i++)
        {
            SpawnPoint p = points[i];
            bool sel = i == _selected;

            var go = new GameObject($"Pin{i}");
            go.transform.SetParent(_markerRoot.transform, false);
            // Same depth rule as players (z follows y) so pins sort naturally with the crew.
            go.transform.position = new Vector3(p.X, p.Y, p.Y / 1000f + 0.0005f);
            go.transform.localScale = sel ? new Vector3(1.35f, 1.35f, 1f) : Vector3.one;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpawnEditorArt.Pin;
            bool off = i < _offFloor.Length && _offFloor[i];
            sr.color = PinColor(p, !off);

            var label = new GameObject("Label");
            label.transform.SetParent(go.transform, false);
            label.transform.localPosition = new Vector3(0f, 0.78f, -0.001f);
            var tmp = label.AddComponent<TextMeshPro>();
            tmp.text = p.Name;
            tmp.fontSize = 1.5f;
            tmp.enableAutoSizing = false;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            Host.StyleText(tmp);
            tmp.color = off ? new Color(1f, 0.45f, 0.45f, 1f) : p.Enabled ? Color.white : new Color(1f, 1f, 1f, 0.5f);
        }
    }

    // ==== text cache (rebuilt on change, never inside OnGUI) ====

    private void RebuildLabels()
    {
        _tPin = Host.Text("SpawnEditor.AddHere");
        _tUnpin = Host.Text("SpawnEditor.Remove");
        _tSettings = Host.Text("SpawnEditor.Details");

        _cBlank ??= new GUIContent("");
        _cX = new GUIContent("×");
        _cTitle = new GUIContent(Host.Text("SpawnEditor.Title"));
        _cEmpty = new GUIContent(Host.Text("SpawnEditor.Empty"));
        _cNoFiles = new GUIContent(Host.Text("SpawnEditor.NoFiles"));
        _cModeOff = new GUIContent(Host.Text("SpawnEditor.ModeOff"));
        _cModeMix = new GUIContent(Host.Text("SpawnEditor.ModeMix"));
        _cModeOnly = new GUIContent(Host.Text("SpawnEditor.ModeOnly"));
        _cHeadTest = new GUIContent(Host.Text("SpawnEditor.HeadTest"));
        _cHeadShare = new GUIContent(Host.Text("SpawnEditor.HeadShare"));
        _cGoTo = new GUIContent(Host.Text("SpawnEditor.GoTo"));
        _cMoveHere = new GUIContent(Host.Text("SpawnEditor.MoveHere"));
        _cDelete = new GUIContent(Host.Text("SpawnEditor.Delete"));
        _cOpenMap = new GUIContent(Host.Text("SpawnEditor.OpenMap"));
        _cTest = new GUIContent(Host.Text("SpawnEditor.Test"));
        _cTestBack = new GUIContent(Host.Text("SpawnEditor.TestBack"));
        _cExport = new GUIContent(Host.Text("SpawnEditor.Export"));
        _cImport = new GUIContent(Host.Text("SpawnEditor.Import"));
        _cBack = new GUIContent(Host.Text("SpawnEditor.Back"));
        _cFolder = new GUIContent(Host.Text("SpawnEditor.Folder"));
        _cMinus = new GUIContent("-");
        _cPlus = new GUIContent("+");

        _modeAvailable = Host.SpawnModeAvailable;
        _mode = Host.SpawnMode;

        List<SpawnPoint> points = Points;
        _cCount = new GUIContent(points.Count.ToString());

        // Chance of each pin for one player; every player draws on their own, so this is exact.
        int total = 0;
        foreach (SpawnPoint p in points)
            if (p.Enabled) total += p.Weight;
        int rooms = _mode == 2 && total > 0 ? 0 : Host.DefaultSpawnPositions(_mapId).Count * SpawnPoint.DefaultRoomWeight;
        int all = total + rooms;
        bool used = _mode > 0;

        _cRowNames = new GUIContent[points.Count];
        _cRowPcts = new GUIContent[points.Count];

        for (int i = 0; i < points.Count; i++)
        {
            SpawnPoint p = points[i];
            _cRowNames[i] = new GUIContent($"  {p.Name}");
            _cRowPcts[i] = new GUIContent(used && p.Enabled && total > 0 && all > 0 ? Percent(p.Weight, all) : "-");
        }

        _cRooms = used && rooms > 0 && all > 0 ? new GUIContent(string.Format(Host.Text("SpawnEditor.Rooms"), Percent(rooms, all))) : _cBlank;
        _cWarn = Array.IndexOf(_offFloor, true) >= 0 ? new GUIContent(Host.Text("SpawnEditor.OffFloor")) : _cBlank;

        bool has = _selected >= 0 && _selected < points.Count;
        string head = Host.Text("SpawnEditor.HeadSelected");
        _cHeadSelected = new GUIContent(has ? $"{head} · {points[_selected].Name}" : head);
        _cWeight = new GUIContent(has ? string.Format(Host.Text("SpawnEditor.Weight"), points[_selected].Weight) : "");
    }

    private static string Percent(int part, int whole)
    {
        float pct = part * 100f / whole;
        return pct < 1f ? "<1%" : $"{MathF.Round(pct):0}%";
    }

    // ==== IMGUI: only the settings window; the editor itself lives on the ability buttons ====

    internal void DrawGui()
    {
        if (_failed || _mapId < 0 || !_details || _cTitle == null) return;

        try
        {
            if (MathF.Abs(_lastScale - Scale) > 0.01f) BuildStyles();
            if (!_windowPlaced) PlaceWindow();

            HandleDrag();
            DrawWindow();
        }
        catch (Exception e)
        {
            Host?.Warn($"Spawn editor GUI failed: {e}");
            _failed = true;
        }
    }

    private float RowH => 54f * Scale;
    private float Pad => 12f * Scale;
    private float TitleH => 64f * Scale;
    private float SmallH => RowH * 0.65f;
    private float WindowW => 520f * Scale;
    private float ListH => RowH * VisibleRows + Pad;
    private float WindowH => TitleH + RowH * 5f + ListH + SmallH * 5f + Pad * 2f;

    private void PlaceWindow()
    {
        _window = new Rect(24f * Scale, _screenH * 0.08f, WindowW, WindowH);
        _windowPlaced = true;
    }

    private Texture2D Rounded(int w, int h, int radius, Color fill, Color edge)
    {
        Texture2D tex = SpawnEditorArt.Rounded(w, h, radius, fill, edge);
        _styleTextures.Add(tex);
        return tex;
    }

    private void BuildStyles()
    {
        foreach (Texture2D tex in _styleTextures)
            if (tex) Destroy(tex);
        _styleTextures.Clear();

        _window.width = WindowW;
        _window.height = WindowH;

        int fs = Math.Max(12, (int)(24f * Scale));
        int small = Math.Max(10, fs - 5);
        int rh = Math.Max(1, (int)RowH);
        float w = WindowW;
        int half = (int)((w - Pad * 3f) * 0.5f);
        int third = (int)((w - Pad * 4f) / 3f);
        int rowW = (int)(w - Pad * 2f - 30f * Scale);
        int xs = (int)(TitleH * 0.62f);

        var blue = new Color(0.14f, 0.24f, 0.4f, 1f);
        var blueHover = new Color(0.2f, 0.34f, 0.56f, 1f);
        var off = new Color(0.12f, 0.13f, 0.17f, 1f);
        var red = new Color(0.45f, 0.11f, 0.11f, 1f);
        var redHover = new Color(0.62f, 0.16f, 0.16f, 1f);
        var teal = new Color(0.08f, 0.42f, 0.46f, 1f);
        var tealHover = new Color(0.12f, 0.54f, 0.6f, 1f);
        var dim = new Color(0.55f, 0.65f, 0.72f, 1f);

        _sWindow = new GUIStyle { normal = { background = Rounded((int)WindowW, (int)WindowH, (int)(22f * Scale), new Color(0.07f, 0.08f, 0.13f, 0.96f), new Color(0.35f, 0.75f, 0.85f, 1f)) } };
        _sTitle = Text(fs + 3, FontStyle.Bold, TextAnchor.MiddleLeft, new Color(0.62f, 0.93f, 1f, 1f));
        _sHint = Text(small, FontStyle.Normal, TextAnchor.MiddleRight, dim);
        _sEmpty = Text(fs - 2, FontStyle.Italic, TextAnchor.MiddleCenter, dim);
        _sEmpty.wordWrap = true;
        _sWarn = Text(small, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(1f, 0.45f, 0.45f, 1f));
        _sStatus = Text(small, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0.55f, 1f, 0.75f, 1f));
        _sHead = Text(small, FontStyle.Bold, TextAnchor.LowerLeft, new Color(0.62f, 0.93f, 1f, 0.85f));
        _sRooms = Text(small, FontStyle.Normal, TextAnchor.MiddleRight, dim);
        _sPct = Text(fs - 1, FontStyle.Bold, TextAnchor.MiddleRight, Color.white);

        _sRow = Button(new Color(0.11f, 0.13f, 0.2f, 1f), new Color(0.16f, 0.2f, 0.3f, 1f), rowW, rh - 4, fs - 1, TextAnchor.MiddleLeft);
        _sRowSel = Button(new Color(0.12f, 0.34f, 0.42f, 1f), new Color(0.15f, 0.42f, 0.5f, 1f), rowW, rh - 4, fs - 1, TextAnchor.MiddleLeft);
        _sRowWarn = Button(new Color(0.11f, 0.13f, 0.2f, 1f), new Color(0.16f, 0.2f, 0.3f, 1f), rowW, rh - 4, fs - 1, TextAnchor.MiddleLeft);
        _sRowWarn.normal.textColor = _sRowWarn.hover.textColor = _sRowWarn.active.textColor = new Color(1f, 0.5f, 0.5f, 1f);

        _sBtn = Button(blue, blueHover, half, rh - 6, fs, TextAnchor.MiddleCenter);
        _sBtnOff = Disabled(Button(off, off, half, rh - 6, fs, TextAnchor.MiddleCenter));
        _sBtnAccent = Button(new Color(0.36f, 0.22f, 0.5f, 1f), new Color(0.48f, 0.3f, 0.66f, 1f), half, rh - 6, fs, TextAnchor.MiddleCenter);
        _sBtn3 = Button(blue, blueHover, third, rh - 6, fs, TextAnchor.MiddleCenter);
        _sBtn3Off = Disabled(Button(off, off, third, rh - 6, fs, TextAnchor.MiddleCenter));
        _sBtn3Danger = Button(red, redHover, third, rh - 6, fs, TextAnchor.MiddleCenter);
        _sSq = Button(blue, blueHover, rh - 6, rh - 6, fs, TextAnchor.MiddleCenter);
        _sSqOff = Disabled(Button(off, off, rh - 6, rh - 6, fs, TextAnchor.MiddleCenter));
        _sSeg = Button(new Color(0.11f, 0.13f, 0.2f, 1f), new Color(0.16f, 0.2f, 0.3f, 1f), third, rh - 6, fs - 2, TextAnchor.MiddleCenter);
        _sSeg.normal.textColor = _sSeg.hover.textColor = new Color(1f, 1f, 1f, 0.6f);
        _sSegOn = Button(teal, tealHover, third, rh - 6, fs - 2, TextAnchor.MiddleCenter);
        _sSegOff = Disabled(Button(off, off, third, rh - 6, fs - 2, TextAnchor.MiddleCenter));
        _sX = Button(new Color(0.16f, 0.18f, 0.25f, 1f), red, xs, xs, fs, TextAnchor.MiddleCenter);

        _sCheckOn = Button(new Color(0.2f, 0.55f, 0.35f, 1f), new Color(0.26f, 0.66f, 0.42f, 1f), rh - 12, rh - 12, fs, TextAnchor.MiddleCenter);
        _sCheckOff = Button(new Color(0.2f, 0.22f, 0.27f, 1f), new Color(0.27f, 0.29f, 0.35f, 1f), rh - 12, rh - 12, fs, TextAnchor.MiddleCenter);
        int mark = Math.Max(2, (rh - 12) / 2);
        _sCheckMark = new GUIStyle { normal = { background = Rounded(mark, mark, mark / 3, Color.white, Color.white) } };

        _sColors = new GUIStyle[SpawnPalette.Colors.Length];
        for (int i = 0; i < _sColors.Length; i++)
        {
            (byte r, byte g, byte b) = SpawnPalette.Colors[i];
            var c = new Color(r / 255f, g / 255f, b / 255f, 1f);
            _sColors[i] = Button(c, Color.Lerp(c, Color.white, 0.3f), rh - 12, rh - 12, fs, TextAnchor.MiddleCenter);
        }

        _lastScale = Scale;
    }

    private static GUIStyle Text(int size, FontStyle style, TextAnchor anchor, Color color) =>
        new() { fontSize = size, fontStyle = style, alignment = anchor, richText = false, normal = { textColor = color } };

    private static GUIStyle Disabled(GUIStyle s)
    {
        s.normal.textColor = s.hover.textColor = s.active.textColor = new Color(1f, 1f, 1f, 0.3f);
        return s;
    }

    private GUIStyle Button(Color normal, Color hover, int w, int h, int fontSize, TextAnchor anchor)
    {
        int radius = Math.Max(2, (int)(h * 0.3f));
        Color rim = Color.Lerp(normal, Color.white, 0.18f);
        return new GUIStyle
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Bold,
            alignment = anchor,
            richText = false,
            normal = { background = Rounded(w, h, radius, normal, rim), textColor = Color.white },
            hover = { background = Rounded(w, h, radius, hover, rim), textColor = Color.white },
            active = { background = Rounded(w, h, radius, Color.Lerp(normal, Color.black, 0.25f), rim), textColor = Color.white }
        };
    }

    private void HandleDrag()
    {
        Event e = Event.current;
        var titleRect = new Rect(_window.x, _window.y, _window.width - TitleH, TitleH);

        switch (e.type)
        {
            case EventType.MouseDown when titleRect.Contains(e.mousePosition):
                _dragging = true;
                _dragOffset = e.mousePosition - new Vector2(_window.x, _window.y);
                e.Use();
                break;
            case EventType.MouseDrag when _dragging:
                _window.x = Math.Clamp(e.mousePosition.x - _dragOffset.x, 0f, Math.Max(0f, _screenW - _window.width));
                _window.y = Math.Clamp(e.mousePosition.y - _dragOffset.y, 0f, Math.Max(0f, _screenH - _window.height));
                e.Use();
                break;
            case EventType.MouseUp:
                _dragging = false;
                break;
        }
    }

    private void DrawWindow()
    {
        List<SpawnPoint> points = Points;
        float x = _window.x, y = _window.y, w = _window.width;
        float ix = x + Pad, iw = w - Pad * 2f;
        float bh = RowH - 6f;
        float third = (w - Pad * 4f) / 3f;

        GUI.Box(_window, _cBlank, _sWindow);

        float xs = TitleH * 0.62f;
        GUI.Label(new Rect(x + Pad * 1.5f, y, w - Pad * 3f - xs, TitleH), _cTitle, _sTitle);
        GUI.Label(new Rect(x + Pad * 1.5f, y, w - Pad * 4f - xs, TitleH), _cCount, _sHint);
        if (GUI.Button(new Rect(x + w - Pad - xs, y + (TitleH - xs) * 0.5f, xs, xs), _cX, _sX))
        {
            _details = false;
            _fileMode = false;
            return;
        }

        float cy = y + TitleH;

        // lobby setting: [off | mixed with rooms | pins only]
        DrawMode(0, _cModeOff, ix, cy, third, bh);
        DrawMode(1, _cModeMix, ix + third + Pad, cy, third, bh);
        DrawMode(2, _cModeOnly, ix + (third + Pad) * 2f, cy, third, bh);
        cy += RowH;

        var outer = new Rect(ix, cy, iw, ListH);
        float rowW = outer.width - 30f * Scale;
        int count = _fileMode ? _cFiles.Length : points.Count;

        if (count == 0)
            GUI.Label(outer, _fileMode ? _cNoFiles : _cEmpty, _sEmpty);
        else
        {
            var inner = new Rect(0f, 0f, rowW, Math.Max(ListH - 1f, RowH * count));
            _scroll = GUI.BeginScrollView(outer, _scroll, inner, false, false);
            try
            {
                if (_fileMode) DrawFileRows(rowW);
                else DrawPinRows(points, rowW);
            }
            finally { GUI.EndScrollView(); }
        }

        DrawWindowBottom(points, x, cy + ListH, w);
    }

    private void DrawMode(int mode, GUIContent label, float x, float y, float w, float h)
    {
        GUIStyle style = !_modeAvailable ? _sSegOff : _mode == mode ? _sSegOn : _sSeg;
        if (!GUI.Button(new Rect(x, y, w, h), label, style) || !_modeAvailable || _mode == mode) return;
        Host.SpawnMode = mode;
        _dirty = true;
    }

    private void DrawPinRows(List<SpawnPoint> points, float rowW)
    {
        float cs = RowH - 12f;
        float lx = (cs + Pad * 0.6f) * 2f;

        for (int i = 0; i < points.Count && i < _cRowNames.Length; i++)
        {
            SpawnPoint p = points[i];
            float ry = i * RowH;

            var check = new Rect(0f, ry + 6f, cs, cs);
            if (GUI.Button(check, _cBlank, p.Enabled ? _sCheckOn : _sCheckOff))
            {
                p.Enabled = !p.Enabled;
                Commit();
            }

            if (p.Enabled)
            {
                float m = cs * 0.5f;
                GUI.Box(new Rect(check.x + (cs - m) * 0.5f, check.y + (cs - m) * 0.5f, m, m), _cBlank, _sCheckMark);
            }

            if (GUI.Button(new Rect(cs + Pad * 0.6f, ry + 6f, cs, cs), _cBlank, _sColors[SpawnPalette.Wrap(p.Color)]))
            {
                p.Color = SpawnPalette.Wrap(p.Color + 1);
                Commit();
            }

            bool offFloor = i < _offFloor.Length && _offFloor[i];
            var rowRect = new Rect(lx, ry + 2f, rowW - lx, RowH - 4f);
            if (GUI.Button(rowRect, _cRowNames[i], i == _selected ? _sRowSel : offFloor ? _sRowWarn : _sRow))
            {
                _selected = i == _selected ? -1 : i;
                _dirty = true;
            }

            GUI.Label(new Rect(rowRect.x, rowRect.y, rowRect.width - Pad, rowRect.height), _cRowPcts[i], _sPct);
        }
    }

    private void DrawFileRows(float rowW)
    {
        for (int i = 0; i < _cFiles.Length; i++)
        {
            if (!GUI.Button(new Rect(0f, i * RowH + 2f, rowW, RowH - 4f), _cFiles[i], _sRow)) continue;
            ImportFile(i);
            return;
        }
    }

    private void DrawWindowBottom(List<SpawnPoint> points, float x, float cy, float w)
    {
        float ix = x + Pad, iw = w - Pad * 2f;
        float bh = RowH - 6f;
        float half = (w - Pad * 3f) * 0.5f;
        float third = (w - Pad * 4f) / 3f;

        GUI.Label(new Rect(ix, cy, iw - Pad, SmallH), _fileMode ? _cBlank : _cRooms, _sRooms);
        cy += SmallH;

        bool status = _cStatus != null && DateTime.UtcNow < _statusUntil;
        GUI.Label(new Rect(ix, cy, iw, SmallH), status ? _cStatus : _cWarn, status ? _sStatus : _sWarn);
        cy += SmallH;

        // selected pin
        bool has = !_fileMode && _selected >= 0 && _selected < points.Count;
        GUI.Label(new Rect(ix + Pad * 0.5f, cy, iw, SmallH), _cHeadSelected, _sHead);
        cy += SmallH;

        if (GUI.Button(new Rect(ix, cy, third, bh), _cGoTo, has ? _sBtn3 : _sBtn3Off) && has)
            Host.TeleportLocalPlayer(new Vector2(points[_selected].X, points[_selected].Y));
        if (GUI.Button(new Rect(ix + third + Pad, cy, third, bh), _cMoveHere, has ? _sBtn3 : _sBtn3Off) && has) MoveSelectedHere();
        if (GUI.Button(new Rect(ix + (third + Pad) * 2f, cy, third, bh), _cDelete, has ? _sBtn3Danger : _sBtn3Off) && has) RemoveAt(_selected);
        cy += RowH;

        if (GUI.Button(new Rect(ix, cy, bh, bh), _cMinus, has ? _sSq : _sSqOff) && has) AddWeight(-1);
        GUI.Label(new Rect(ix + bh, cy, iw - bh * 2f, bh), has ? _cWeight : _cBlank, _sEmpty);
        if (GUI.Button(new Rect(x + w - Pad - bh, cy, bh, bh), _cPlus, has ? _sSq : _sSqOff) && has) AddWeight(1);
        cy += RowH;

        // test with the freeplay dummies
        GUI.Label(new Rect(ix + Pad * 0.5f, cy, iw, SmallH), _cHeadTest, _sHead);
        cy += SmallH;
        if (GUI.Button(new Rect(ix, cy, half, bh), _cOpenMap, _sBtn)) OpenMap();
        if (GUI.Button(new Rect(ix + half + Pad, cy, half, bh), Testing ? _cTestBack : _cTest, _sBtnAccent)) ToggleTest();
        cy += RowH;

        // share as files
        GUI.Label(new Rect(ix + Pad * 0.5f, cy, iw, SmallH), _cHeadShare, _sHead);
        cy += SmallH;

        if (Host.CanOpenFolder)
        {
            if (GUI.Button(new Rect(ix, cy, third, bh), _cExport, points.Count > 0 ? _sBtn3 : _sBtn3Off)) ExportFile();
            if (GUI.Button(new Rect(ix + third + Pad, cy, third, bh), _fileMode ? _cBack : _cImport, _sBtn3)) ToggleFileMode();
            if (GUI.Button(new Rect(ix + (third + Pad) * 2f, cy, third, bh), _cFolder, _sBtn3)) OpenShareFolder();
        }
        else
        {
            if (GUI.Button(new Rect(ix, cy, half, bh), _cExport, points.Count > 0 ? _sBtn : _sBtnOff)) ExportFile();
            if (GUI.Button(new Rect(ix + half + Pad, cy, half, bh), _fileMode ? _cBack : _cImport, _sBtn)) ToggleFileMode();
        }
    }

    // ==== edits ====

    private void Commit()
    {
        SpawnStore.Save();
        _dirty = true;
    }

    private void AddHere()
    {
        AddPoint(Host.LocalPlayerPosition);
        _near = _selected;
    }

    private void AddPoint(Vector2 pos)
    {
        List<SpawnPoint> points = Points;

        string baseName = Host.RoomNameAt(pos);
        if (string.IsNullOrEmpty(baseName)) baseName = Host.Text("SpawnEditor.Point");
        string name = baseName;
        for (int n = 2; points.Exists(p => p.Name == name); n++) name = $"{baseName} {n}";

        points.Add(new SpawnPoint { Name = name, X = pos.x, Y = pos.y, Color = points.Count });
        _selected = points.Count - 1;
        _scroll.y = Math.Max(0f, RowH * points.Count - ListH);
        Host.Log($"Spawn point added: {name} ({pos.x:0.00}, {pos.y:0.00}) map {_mapId}");

        // A pin is placed to be used: switch the lobby setting on the first time.
        if (Host.SpawnModeAvailable && Host.SpawnMode == 0)
        {
            Host.SpawnMode = 1;
            Host.Notify(Host.Text("SpawnEditor.AutoOn"));
        }

        Commit();
    }

    private void MoveSelectedHere()
    {
        Vector2 pos = Host.LocalPlayerPosition;
        SpawnPoint p = Points[_selected];
        p.X = pos.x;
        p.Y = pos.y;
        Commit();
    }

    private void RemoveAt(int index)
    {
        List<SpawnPoint> points = Points;
        if (index < 0 || index >= points.Count) return;
        points.RemoveAt(index);
        _selected = -1;
        _near = -1;
        Commit();
    }

    private void AddWeight(int delta)
    {
        SpawnPoint p = Points[_selected];
        p.Weight = Math.Clamp(p.Weight + delta, SpawnPoint.MinWeight, SpawnPoint.MaxWeight);
        Commit();
    }

    private void ShowStatus(string text)
    {
        _cStatus = new GUIContent(text);
        _statusUntil = DateTime.UtcNow.AddSeconds(5);
    }

    private static void OpenMap()
    {
        if (MapBehaviour.Instance && MapBehaviour.Instance.IsOpen)
        {
            SpawnEditorMap.Refresh();
            return;
        }

        if (HudManager.InstanceExists) HudManager.Instance.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Normal });
    }

    // ==== share files ====

    private void ExportFile()
    {
        List<SpawnPoint> points = Points;
        if (points.Count == 0) return;

        try
        {
            string name = SpawnShareFile.Export(Host.ShareFolder, _mapId, points);
            ShowStatus(string.Format(Host.Text("SpawnEditor.Exported"), name));
            Host.Log($"Spawn file exported: {name} ({points.Count} points)");
        }
        catch (Exception e)
        {
            Host.Warn($"Spawn file export failed: {e.Message}");
            ShowStatus(Host.Text("SpawnEditor.ExportFailed"));
        }
    }

    private void ToggleFileMode()
    {
        _fileMode = !_fileMode;
        _scroll = Vector2.zero;
        if (!_fileMode) return;

        try { _files = SpawnShareFile.List(Host.ShareFolder, _mapId); }
        catch (Exception e)
        {
            Host.Warn($"Spawn file list failed: {e.Message}");
            _files = [];
        }

        _cFiles = new GUIContent[_files.Count];
        for (int i = 0; i < _files.Count; i++) _cFiles[i] = new GUIContent($"  {Path.GetFileNameWithoutExtension(_files[i])}");
    }

    private void ImportFile(int index)
    {
        _fileMode = false;
        if (index < 0 || index >= _files.Count) return;

        List<SpawnPoint> imported = SpawnShareFile.Read(_files[index], _mapId);
        if (imported == null)
        {
            ShowStatus(Host.Text("SpawnEditor.ImportInvalid"));
            return;
        }

        Points.AddRange(imported);
        _selected = -1;
        ShowStatus(string.Format(Host.Text("SpawnEditor.Imported"), imported.Count));
        Host.Log($"Spawn file imported: {Path.GetFileName(_files[index])} ({imported.Count} points) map {_mapId}");
        Commit();
    }

    private void OpenShareFolder()
    {
        try { Host.OpenFolder(Host.ShareFolder); }
        catch (Exception e) { Host.Warn($"Spawn folder open failed: {e.Message}"); }
    }

    // ==== dummy test: sends every freeplay dummy where a real game would spawn it, and marks them on the map ====

    private readonly List<(byte Id, Vector2 Home)> _dummyHomes = [];
    private readonly List<(Vector2 Pos, Color Color)> _testDots = [];

    private bool Testing => _dummyHomes.Count > 0;

    public List<(Vector2 Pos, Color Color)> ScatterDots => _testDots;

    // Drops the test state without moving anyone (the ship is gone or being replaced).
    private void ForgetTest()
    {
        _dummyHomes.Clear();
        _testDots.Clear();
    }

    private void ToggleTest()
    {
        if (Testing)
        {
            foreach ((byte id, Vector2 home) in _dummyHomes)
            {
                PlayerControl pc = FindPlayer(id);
                if (pc) Host.TeleportPlayer(pc, home);
            }

            ForgetTest();
            SpawnEditorMap.Refresh();
            return;
        }

        var enabled = new List<SpawnPoint>();
        if (Host.CustomActive)
        {
            foreach (SpawnPoint p in Points)
                if (p.Enabled) enabled.Add(p);
        }

        IReadOnlyList<Vector2> defaults = Host.DefaultSpawnPositions(_mapId);
        bool customOnly = Host.CustomOnly && enabled.Count > 0;
        var rng = new System.Random();
        var stack = new Dictionary<(int, int), int>();
        int custom = 0, rooms = 0;

        foreach (PlayerControl pc in PlayerControl.AllPlayerControls)
        {
            if (!pc || !pc.isDummy || pc.AmOwner) continue;

            Vector2 pos;
            if (SpawnPicker.TryPick(enabled, defaults.Count, customOnly, out SpawnPoint picked))
            {
                pos = new Vector2(picked.X, picked.Y);
                custom++;
            }
            else if (defaults.Count > 0)
            {
                pos = defaults[rng.Next(defaults.Count)];
                rooms++;
            }
            else continue;

            _dummyHomes.Add((pc.PlayerId, pc.transform.position));
            Host.TeleportPlayer(pc, pos);

            // Dummies sent to the same spot stand on each other; their map marks are fanned out so each stays visible.
            var key = ((int)MathF.Round(pos.x * 10f), (int)MathF.Round(pos.y * 10f));
            stack.TryGetValue(key, out int k);
            stack[key] = k + 1;
            if (k > 0)
            {
                float a = k * 2.39996f, d = 0.32f * MathF.Sqrt(k);
                pos = new Vector2(pos.x + MathF.Cos(a) * d, pos.y + MathF.Sin(a) * d);
            }

            _testDots.Add((pos, DummyColor(pc)));
        }

        ShowStatus(string.Format(Host.Text("SpawnEditor.TestResult"), custom, rooms));
        Host.Log($"Spawn dummy test: {custom} on pins, {rooms} in rooms, map {_mapId}");
        OpenMap();
        SpawnEditorMap.Refresh();
    }

    private static PlayerControl FindPlayer(byte id)
    {
        foreach (PlayerControl pc in PlayerControl.AllPlayerControls)
            if (pc && pc.PlayerId == id) return pc;
        return null;
    }

    private static Color DummyColor(PlayerControl pc)
    {
        int id = pc.Data?.DefaultOutfit?.ColorId ?? -1;
        return id >= 0 && id < Palette.PlayerColors.Length ? Palette.PlayerColors[id] : Color.white;
    }
}

public class SpawnEditorUiPainter : MonoBehaviour
{
    private void Awake()
    {
        try { useGUILayout = false; }
        catch { }
    }

    private void OnGUI()
    {
        SpawnEditorUi ui = SpawnEditorUi.Instance;
        if (ui) ui.DrawGui();
    }
}
