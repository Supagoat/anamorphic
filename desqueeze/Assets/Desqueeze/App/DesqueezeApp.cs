using System;
using System.Collections.Generic;
using System.IO;
using Desqueeze.App.UI;
using Desqueeze.Core;
using Desqueeze.Core.Codecs;
using UnityEngine;

namespace Desqueeze.App
{
    /// <summary>
    /// The application: a single MonoBehaviour drawing an IMGUI interface with two modes
    /// (Interactive and Batch) that share one set of de-squeeze settings.
    /// Created automatically at start-up by <see cref="AppBootstrap"/>; no scene setup needed.
    /// </summary>
    public sealed class DesqueezeApp : MonoBehaviour
    {
        private enum Mode
        {
            Interactive,
            Batch,
        }

        public AppConfig Config { get; private set; }
        public DesqueezeSettings Settings { get; private set; }

        private readonly UiStyles _ui = new UiStyles();
        private SettingsPanel _panel;
        private InteractiveMode _interactive;
        private BatchMode _batch;
        private Mode _mode;
        private Vector2 _settingsScroll;
        private string _message;
        private bool _messageIsError;
        private string _tooltip = "";
        private string _frameTooltip = "";
        private readonly Queue<Action> _deferred = new Queue<Action>();

        /// <summary>
        /// Runs an action on the next Update instead of inside OnGUI. Anything that changes which controls
        /// are drawn (opening a list, starting a job, switching modes) or opens a native dialog must go
        /// through here: IMGUI lays out each event in two passes and errors if the control set changes
        /// between them, and editor file panels opened inside OnGUI corrupt the GUI layout state.
        /// </summary>
        public void Defer(Action action)
        {
            if (action != null) _deferred.Enqueue(action);
        }

        private void Update()
        {
            int n = _deferred.Count; // actions queued while running wait for the next frame
            for (int i = 0; i < n; i++)
            {
                var a = _deferred.Dequeue();
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    ShowError(e.Message);
                    Debug.LogException(e);
                }
            }
        }

        private void Awake()
        {
            Application.runInBackground = true;   // keep batches running when the window is in the background
            Application.targetFrameRate = 60;
            string warning;
            Config = AppConfig.Load(out warning);
            Settings = Prefs.LoadSettings(Config.CreateDefaultSettings());
            _panel = new SettingsPanel(Config, Settings, Defer, () => Prefs.SaveSettings(Settings));
            _interactive = new InteractiveMode(this);
            _batch = new BatchMode(this);
            _mode = Prefs.GetString("Mode", "Interactive") == "Batch" ? Mode.Batch : Mode.Interactive;
            if (warning != null) ShowError(warning);
            LoadDetailModel();
            EnsureCamera();
        }

        private void LoadDetailModel()
        {
            try
            {
                string path = AppConfig.ModelPath;
                if (File.Exists(path))
                {
                    Desqueeze.Core.Resampling.AxisSRModel.Default = Desqueeze.Core.Resampling.AxisSRModel.Load(path);
                    return;
                }
                if (Settings.Widen != WidenMethod.Kernel)
                    ShowError("Detail model not found (" + path + "); widening uses the kernel instead.");
            }
            catch (Exception e)
            {
                ShowError("Could not load the detail model: " + e.Message + " - widening uses the kernel instead.");
            }
        }

        private void Start()
        {
            // Allow "Desqueeze path/to/image.jpg" from a command line.
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (ImageIO.IsSupportedPath(args[i]) && File.Exists(args[i]))
                {
                    _mode = Mode.Interactive;
                    _interactive.Open(args[i]);
                    break;
                }
            }
        }

        private static void EnsureCamera()
        {
            var cam = Camera.main;
            if (cam == null) cam = FindAnyObjectByType<Camera>();
            if (cam == null) cam = new GameObject("Desqueeze Camera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = UiStyles.Background;
            cam.cullingMask = 0; // the UI is drawn with IMGUI; the camera only clears the screen
        }

        public void ShowInfo(string message)
        {
            _message = message;
            _messageIsError = false;
        }

        public void ShowError(string message)
        {
            _message = message;
            _messageIsError = true;
            Debug.LogWarning("[Desqueeze] " + message);
        }

        private float ComputeUiScale()
        {
            if (Config.uiScale > 0) return Config.uiScale;
            // Retina / HiDPI: IMGUI works in physical pixels, so enlarge the UI on dense screens.
            float dpi = Screen.dpi;
            int displayH = Screen.currentResolution.height;
            if (dpi >= 160f || displayH >= 1800) return 2f;
            if (dpi >= 120f || displayH >= 1400) return 1.25f;
            return 1f;
        }

        private void OnGUI()
        {
            float scale = ComputeUiScale();
            if (!_ui.IsBuiltFor(scale)) _ui.Build(scale);
            if (Event.current.type == EventType.Repaint) _frameTooltip = "";

            float ppp = UiStyles.PixelsPerPoint;
            var screen = new Rect(0, 0, Screen.width / ppp, Screen.height / ppp);
            _ui.Fill(screen, UiStyles.Background);

            float topH = _ui.S(48);
            float panelW = Mathf.Min(_ui.S(360), Mathf.Max(_ui.S(260), screen.width * 0.4f));
            var top = new Rect(0, 0, screen.width, topH);
            var panel = new Rect(screen.width - panelW, topH, panelW, screen.height - topH);
            var main = new Rect(0, topH, screen.width - panelW, screen.height - topH);

            if (_mode == Mode.Interactive) _interactive.HandleShortcuts(Event.current);

            if (_mode == Mode.Interactive) _interactive.DrawMain(main, _ui);
            else _batch.DrawMain(main, _ui);
            CaptureTooltip();

            DrawTopBar(top);
            DrawPanel(panel);
            DrawTooltip(screen);
        }

        /// <summary>Records the hovered control's tooltip; call inside each GUILayout area before it ends.</summary>
        internal void CaptureTooltip()
        {
            if (Event.current.type == EventType.Repaint && !string.IsNullOrEmpty(GUI.tooltip)) _frameTooltip = GUI.tooltip;
        }

        private void DrawTopBar(Rect r)
        {
            _ui.Fill(r, UiStyles.Hex(0x1B1C20));
            GUILayout.BeginArea(new Rect(r.x + _ui.S(14), r.y + _ui.S(8), r.width - _ui.S(28), r.height - _ui.S(10)));
            GUILayout.BeginHorizontal();
            GUILayout.Label("DESQUEEZE", _ui.Title);
            GUILayout.Label("anamorphic de-squeeze", _ui.MutedSmall);
            GUILayout.Space(_ui.S(24));
            if (GUILayout.Button(new GUIContent("Interactive", "Open, inspect, de-squeeze and save one image"), _mode == Mode.Interactive ? _ui.TabSelected : _ui.Tab))
                Defer(() => SetMode(Mode.Interactive));
            if (GUILayout.Button(new GUIContent("Batch", "De-squeeze every image in a folder"), _mode == Mode.Batch ? _ui.TabSelected : _ui.Tab))
                Defer(() => SetMode(Mode.Batch));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Config", "Open the folder holding " + AppConfig.FileName + " (presets and defaults). Restart the app after editing."), _ui.Tab))
                Defer(RevealConfig);
            GUILayout.EndHorizontal();
            CaptureTooltip();
            GUILayout.EndArea();
        }

        private void SetMode(Mode m)
        {
            _mode = m;
            Prefs.SetString("Mode", m.ToString());
            if (m == Mode.Batch) _batch.Plan();
        }

        private void RevealConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(AppConfig.FilePath);
                if (Directory.Exists(dir)) Application.OpenURL(new Uri(dir).AbsoluteUri);
                ShowInfo("Config: " + AppConfig.FilePath);
            }
            catch (Exception e)
            {
                ShowError("Could not open the config folder: " + e.Message);
            }
        }

        private void DrawPanel(Rect r)
        {
            _ui.Fill(r, UiStyles.PanelColor);
            _ui.Fill(new Rect(r.x, r.y, Mathf.Max(1, _ui.S(1)), r.height), UiStyles.PanelBorder);
            GUILayout.BeginArea(r, _ui.Panel);

            _settingsScroll = GUILayout.BeginScrollView(_settingsScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
            bool busy = _mode == Mode.Interactive ? _interactive.Busy : _batch.Busy;
            GUI.enabled = !busy;
            var img = _mode == Mode.Interactive ? _interactive.Original : null;
            if (_panel.Draw(Settings, _ui, img)) Prefs.SaveSettings(Settings);
            GUI.enabled = true;
            CaptureTooltip();
            GUILayout.EndScrollView();

            _ui.Separator();
            if (_mode == Mode.Interactive) _interactive.DrawActions(_ui);
            else GUILayout.Label("The batch uses these settings. Outputs keep each file's format; JPEGs are written at maximum quality (Q100, 4:4:4).", _ui.MutedSmall);

            if (!string.IsNullOrEmpty(_message))
            {
                GUILayout.Space(_ui.S(4));
                GUILayout.Label(_message, _messageIsError ? _ui.Error : _ui.MutedSmall);
            }
            CaptureTooltip();
            GUILayout.EndArea();
        }

        private void DrawTooltip(Rect screen)
        {
            if (Event.current.type == EventType.Repaint) _tooltip = _frameTooltip;
            if (string.IsNullOrEmpty(_tooltip) || Event.current.type != EventType.Repaint) return;
            var content = new GUIContent(_tooltip);
            float width = _ui.S(300);
            float height = _ui.Tooltip.CalcHeight(content, width);
            Vector2 m = Event.current.mousePosition;
            float x = Mathf.Min(m.x + _ui.S(14), screen.width - width - _ui.S(4));
            float y = m.y + _ui.S(20);
            if (y + height > screen.height) y = m.y - height - _ui.S(8);
            GUI.Label(new Rect(x, y, width, height), content, _ui.Tooltip);
        }

        private void OnApplicationQuit()
        {
            Prefs.SaveSettings(Settings);
            Prefs.Flush();
        }

        private void OnDestroy()
        {
            if (_interactive != null) _interactive.Dispose();
            if (_batch != null) _batch.Dispose();
            _ui.Release();
            Prefs.Flush();
        }
    }

    /// <summary>Creates the app object after the first scene loads, so any scene (e.g. the template's SampleScene) works.</summary>
    internal static class AppBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
#if !DESQUEEZE_NO_AUTOSTART
            if (UnityEngine.Object.FindAnyObjectByType<DesqueezeApp>() != null) return;
            var go = new GameObject("Desqueeze App");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<DesqueezeApp>();
#endif
        }
    }
}
