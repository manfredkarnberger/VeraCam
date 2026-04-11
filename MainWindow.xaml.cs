using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using System.Management;
using System.Windows.Controls;

namespace VeraCam
{
    record CameraItem(int Index, string Name, int W, int H)
    {
        public override string ToString() => $"[{Index}]  {Name}  ({W}x{H})";
    }

    enum DetState { Idle, Buffering, ChangeDetected, Countdown, Saving }

    public partial class MainWindow : System.Windows.Window
    {
        // ── Feste Kamera-Parameter ────────────────────────────────────────────
        const int    WIDTH = 640;
        const int    HEIGHT = 480;
        const double FPS   = 30.0;

        // ── Einstellbare Parameter (aus Toolbar) ──────────────────────────────
        private int _maxSec      = 120;   // Videolänge in Sekunden (Standard 2 min)
        private int _countdownSec = 60;   // Nachlaufzeit in Sekunden (Standard 1 min)

        // Maximale Frame-Anzahl dynamisch aus _maxSec
        private int MaxFrames => (int)(FPS * _maxSec);

        // ── Ringpuffer ────────────────────────────────────────────────────────
        private readonly Queue<byte[]> _ring  = new();
        private readonly object        _rLock = new();

        // ── Zustand ───────────────────────────────────────────────────────────
        private int      _camIndex    = 0;
        private bool     _blackWhite  = true;
        private double   _sensitivity = 5.0;
        private DetState _state       = DetState.Idle;
        private DateTime _changeAt;
        private double   _lastDiffPct = 0;
        private Mat?     _refFrame;
        private int      _refCounter  = 0;

        private CancellationTokenSource? _cts;
        private Task?                    _captureTask;

        private readonly DispatcherTimer _uiTimer;

        // ─────────────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            FilePath.Text = DefaultPath();

            // Standard: S/W aktiv
            BwBadge.Visibility = Visibility.Visible;
            TbColorIcon.Text   = "◑";
            TbColorText.Text   = "Farbe";

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _uiTimer.Tick += UiTick;
            _uiTimer.Start();

            Task.Run(ScanCamerasAsync);
        }

        // ════════════════════════════════════════════════════════════════════
        //  TOOLBAR: Videolänge und Nachlaufzeit validieren
        // ════════════════════════════════════════════════════════════════════
        private void TbMaxLen_LostFocus(object s, RoutedEventArgs e)
        {
            if (int.TryParse(TbMaxLen.Text, out int v))
            {
                v = Math.Clamp(v, 10, 600);   // 10 s bis 10 min
                _maxSec = v;
            }
            TbMaxLen.Text = _maxSec.ToString();
            UpdateBufferLabel();
        }

        private void TbCountdown_LostFocus(object s, RoutedEventArgs e)
        {
            if (int.TryParse(TbCountdown.Text, out int v))
            {
                v = Math.Clamp(v, 5, 600);
                _countdownSec = v;
            }
            TbCountdown.Text = _countdownSec.ToString();
        }

        private void SensSlider_ValueChanged(object s,
            RoutedPropertyChangedEventArgs<double> e)
        {
            _sensitivity = e.NewValue;
            if (SensLabel  != null) SensLabel.Text  = $"{_sensitivity:F0}%";
            if (DiffLabel  != null) DiffLabel.Text  = $"{_lastDiffPct:F1}%  / {_sensitivity:F0}%";
        }

        void UpdateBufferLabel()
        {
            int count;
            lock (_rLock) count = _ring.Count;
            double sec = count / FPS;
            int    max = _maxSec;
            BufferLabel.Text = $"{(int)(sec/60)}:{(int)(sec%60):D2} / {max/60}:{max%60:D2}";
        }

        // ════════════════════════════════════════════════════════════════════
        //  UI TIMER
        // ════════════════════════════════════════════════════════════════════
        void UiTick(object? s, EventArgs e)
        {
            // Puffer-Balken
            int count;
            lock (_rLock) count = _ring.Count;
            double pct = MaxFrames > 0 ? Math.Min(count / (double)MaxFrames, 1.0) : 0;
            double sec = count / FPS;
            int    max = _maxSec;

            UpdateBar(BufferBar, pct, "#2060A0");
            BufferPct.Text   = $"{pct*100:F0}%";
            BufferLabel.Text = $"{(int)(sec/60)}:{(int)(sec%60):D2} / {max/60}:{max%60:D2}";

            // Diff-Balken
            double dp = Math.Min(_lastDiffPct / 100.0, 1.0);
            bool   hit = _lastDiffPct >= _sensitivity;
            UpdateBar(DiffBar, dp, hit ? "#8A1A1A" : "#602020");
            DiffPct.Text  = $"{_lastDiffPct:F1}%";
            DiffLabel.Text = $"{_lastDiffPct:F1}%  / {_sensitivity:F0}%";
            DiffLabel.Foreground = hit
                ? new SolidColorBrush(Color.FromRgb(0xFF,0x70,0x70))
                : new SolidColorBrush(Colors.White);

            // Schwellwert-Markierung
            double pw = ((FrameworkElement)DiffBar.Parent).ActualWidth;
            ThresholdMark.Margin = new Thickness(pw * (_sensitivity / 100.0), 0, 0, 0);

            // Zustandsabhängige Anzeige
            switch (_state)
            {
                case DetState.Buffering:
                    RecTimeText.Text       = "PUFFER";
                    RecTimeText.Foreground = new SolidColorBrush(Color.FromRgb(0x60,0x90,0xFF));
                    RecDotBig.Fill         = new SolidColorBrush(Color.FromRgb(0x60,0x90,0xFF));
                    RecDot.Fill            = new SolidColorBrush(Color.FromRgb(0x30,0x60,0xCC));
                    CountdownBadge.Visibility = Visibility.Collapsed;
                    ChangeFlash.Visibility    = Visibility.Collapsed;
                    ToolbarStatus.Text       = "Puffer läuft — Überwachung aktiv";
                    ToolbarStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x60,0x90,0xFF));
                    break;

                case DetState.ChangeDetected:
                case DetState.Countdown:
                    int rem = _countdownSec - (int)(DateTime.Now - _changeAt).TotalSeconds;
                    rem = Math.Max(0, rem);
                    RecTimeText.Text          = "ÄNDERUNG";
                    RecTimeText.Foreground    = new SolidColorBrush(Color.FromRgb(0xFF,0x50,0x50));
                    RecDotBig.Fill            = new SolidColorBrush(Color.FromRgb(0xFF,0x50,0x50));
                    RecDot.Fill               = new SolidColorBrush(Color.FromRgb(0xFF,0x30,0x30));
                    ChangeFlash.Visibility    = Visibility.Visible;
                    CountdownBadge.Visibility = Visibility.Visible;
                    CountdownText.Text        = rem.ToString();
                    ToolbarStatus.Text        = $"Änderung erkannt — speichert in {rem} s";
                    ToolbarStatus.Foreground  = new SolidColorBrush(Color.FromRgb(0xFF,0x60,0x60));
                    break;

                case DetState.Saving:
                    RecTimeText.Text         = "SPEICHERT";
                    ToolbarStatus.Text       = "Video wird gespeichert...";
                    ToolbarStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF,0xCC,0x40));
                    break;
            }
        }

        void UpdateBar(Border bar, double pct, string hex)
        {
            double pw = ((FrameworkElement)bar.Parent).ActualWidth;
            bar.Width      = pw * pct;
            bar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        // ════════════════════════════════════════════════════════════════════
        //  KAMERA SCAN
        // ════════════════════════════════════════════════════════════════════
        void ScanCamerasAsync()
        {
            var wmiNames = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity " +
                    "WHERE (PNPClass = 'Camera' OR PNPClass = 'Image')");
                foreach (ManagementObject obj in searcher.Get())
                    wmiNames.Add(obj["Name"]?.ToString() ?? "Unbekannt");
            }
            catch { }

            var found = new List<CameraItem>();
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    using var cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                    if (!cap.IsOpened()) continue;
                    using var fr = new Mat();
                    bool ok = false;
                    for (int t = 0; t < 6 && !ok; t++)
                    {
                        ok = cap.Read(fr) && !fr.Empty();
                        if (!ok) Thread.Sleep(80);
                    }
                    if (!ok) continue;
                    string name = i < wmiNames.Count ? wmiNames[i] : $"Kamera {i}";
                    found.Add(new CameraItem(i, name, fr.Width, fr.Height));
                }
                catch { }
            }

            Dispatcher.Invoke(() =>
            {
                CameraCombo.Items.Clear();
                if (found.Count == 0)
                {
                    CamCountLabel.Text = "Keine Kamera";
                    Status("Keine Kamera gefunden.", error: true);
                    return;
                }
                foreach (var c in found) CameraCombo.Items.Add(c);
                CameraCombo.SelectedIndex = 0;
                CamCountLabel.Text = $"{found.Count} Kamera(s)";
                TbPlay.IsEnabled = true;
                Status($"{found.Count} Kamera(s) gefunden — Play drücken.");
            });
        }

        private void CameraCombo_SelectionChanged(object s,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_state != DetState.Idle) return;
            if (CameraCombo.SelectedItem is CameraItem cam) _camIndex = cam.Index;
        }

        // ════════════════════════════════════════════════════════════════════
        //  PLAY
        // ════════════════════════════════════════════════════════════════════
        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_state != DetState.Idle) return;
            StartBuffering();
        }

        void StartBuffering()
        {
            _state = DetState.Buffering;
            lock (_rLock) _ring.Clear();
            _refFrame?.Dispose(); _refFrame = null;
            _refCounter  = 0;
            _lastDiffPct = 0;

            _cts         = new CancellationTokenSource();
            _captureTask = Task.Run(() => CaptureLoop(_cts.Token));

            TbPlay.IsEnabled      = false;
            TbStop.IsEnabled      = true;
            CameraCombo.IsEnabled = false;
            FilePath.IsReadOnly   = true;
            TbMaxLen.IsReadOnly   = true;
            TbCountdown.IsReadOnly = true;

            RecBadge.Visibility = Visibility.Visible;
        }

        // ════════════════════════════════════════════════════════════════════
        //  STOP (manuell)
        // ════════════════════════════════════════════════════════════════════
        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_state == DetState.Idle || _state == DetState.Saving) return;
            TbStop.IsEnabled = false;
            await DoSaveAndRestart(autoRestart: false);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SPEICHERN + OPTIONAL NEUSTART
        // ════════════════════════════════════════════════════════════════════
        async Task DoSaveAndRestart(bool autoRestart)
        {
            _state = DetState.Saving;

            _cts?.Cancel();
            if (_captureTask != null) await _captureTask.ConfigureAwait(false);

            await Task.Run(FlushBufferToFile);

            Dispatcher.Invoke(() =>
            {
                // Dateiname für nächste Aufnahme
                string dir = Path.GetDirectoryName(FilePath.Text) ?? "";
                FilePath.Text = Path.Combine(dir, $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                _refFrame?.Dispose(); _refFrame = null;

                CountdownBadge.Visibility = Visibility.Collapsed;
                ChangeFlash.Visibility    = Visibility.Collapsed;

                if (autoRestart)
                {
                    // Direkt neu starten — Überwachung läuft weiter
                    _state = DetState.Idle;
                    StartBuffering();
                    Status("Gespeichert — Überwachung läuft weiter.");
                }
                else
                {
                    // Manuell gestoppt
                    _state = DetState.Idle;
                    TbPlay.IsEnabled       = true;
                    TbStop.IsEnabled       = false;
                    CameraCombo.IsEnabled  = true;
                    FilePath.IsReadOnly    = false;
                    TbMaxLen.IsReadOnly    = false;
                    TbCountdown.IsReadOnly = false;
                    RecBadge.Visibility    = Visibility.Collapsed;
                    RecDot.Fill = new SolidColorBrush(Color.FromRgb(0x55,0x55,0x55));
                    ToolbarStatus.Text       = "Gestoppt.";
                    ToolbarStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xAA,0xAA,0xAA));
                    Status("Gestoppt — Video gespeichert.");
                }
            });
        }

        void FlushBufferToFile()
        {
            byte[][] frames;
            lock (_rLock) { frames = _ring.ToArray(); _ring.Clear(); }

            if (frames.Length == 0)
            {
                Dispatcher.Invoke(() => Status("Puffer leer — nichts gespeichert.", error: true));
                return;
            }

            string path = Dispatcher.Invoke(() => FilePath.Text.Trim());
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }

            using var writer = new VideoWriter(
                path, FourCC.MP4V, FPS, new OpenCvSharp.Size(WIDTH, HEIGHT));

            if (!writer.IsOpened())
            {
                Dispatcher.Invoke(() => Status("VideoWriter Fehler.", error: true));
                return;
            }

            foreach (var jpg in frames)
            {
                using var mat = Cv2.ImDecode(jpg, ImreadModes.Color);
                if (!mat.Empty()) writer.Write(mat);
            }
            writer.Release();

            double secs = frames.Length / FPS;
            Dispatcher.Invoke(() =>
                Status($"Gespeichert: {secs:F1} s  →  {System.IO.Path.GetFileName(path)}"));
        }

        // ════════════════════════════════════════════════════════════════════
        //  CAPTURE LOOP
        // ════════════════════════════════════════════════════════════════════
        void CaptureLoop(CancellationToken ct)
        {
            VideoCapture? cap = null;
            foreach (var api in new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.ANY })
            {
                cap = new VideoCapture(_camIndex, api);
                if (cap.IsOpened()) break;
                cap.Dispose(); cap = null;
            }
            if (cap == null)
            {
                Dispatcher.Invoke(() => Status($"Kamera {_camIndex} nicht erreichbar.", error: true));
                return;
            }

            using (cap)
            {
                cap.Set(VideoCaptureProperties.FrameWidth,  WIDTH);
                cap.Set(VideoCaptureProperties.FrameHeight, HEIGHT);
                cap.Set(VideoCaptureProperties.Fps,         FPS);

                using var warm = new Mat();
                for (int i = 0; i < 10; i++) { cap.Read(warm); if (!warm.Empty()) break; Thread.Sleep(80); }

                Dispatcher.Invoke(() => NoSignal.Visibility = Visibility.Collapsed);

                using var frame = new Mat();
                var sw    = System.Diagnostics.Stopwatch.StartNew();
                int delay = (int)(1000.0 / FPS);

                while (!ct.IsCancellationRequested)
                {
                    long t0 = sw.ElapsedMilliseconds;

                    if (!cap.Read(frame) || frame.Empty()) { Thread.Sleep(10); continue; }

                    using var safeFrame = frame.Clone();

                    // S/W konvertieren
                    Mat display = safeFrame;
                    Mat? bwMat  = null;
                    if (_blackWhite)
                    {
                        bwMat = new Mat();
                        Cv2.CvtColor(safeFrame, bwMat, ColorConversionCodes.BGR2GRAY);
                        Cv2.CvtColor(bwMat, bwMat, ColorConversionCodes.GRAY2BGR);
                        display = bwMat;
                    }

                    // Änderungsdetektion
                    double diff = ComputeDiff(display);
                    _lastDiffPct = diff;

                    bool triggerSave = false;

                    if (_state == DetState.Buffering && diff >= _sensitivity)
                    {
                        _state    = DetState.ChangeDetected;
                        _changeAt = DateTime.Now;
                        Dispatcher.Invoke(() =>
                            Status($"Änderung erkannt ({diff:F1}%) — Nachlauf {_countdownSec} s"));
                    }
                    else if (_state == DetState.ChangeDetected || _state == DetState.Countdown)
                    {
                        _state = DetState.Countdown;
                        if ((DateTime.Now - _changeAt).TotalSeconds >= _countdownSec)
                            triggerSave = true;
                    }

                    // Referenz nur im Buffering-Modus aktualisieren
                    if (_state == DetState.Buffering)
                        UpdateReference(display);

                    // Ringpuffer befüllen
                    EnqueueFrame(display);

                    ShowFrame(display);
                    bwMat?.Dispose();

                    if (triggerSave)
                    {
                        // Automatisch speichern und Überwachung neu starten
                        _ = Dispatcher.BeginInvoke(async () =>
                            await DoSaveAndRestart(autoRestart: true));
                        return;
                    }

                    int sleep = delay - (int)(sw.ElapsedMilliseconds - t0);
                    if (sleep > 0) Thread.Sleep(sleep);
                }
            }

            Dispatcher.Invoke(() => NoSignal.Visibility = Visibility.Visible);
        }

        // ── Referenz-Frame ────────────────────────────────────────────────────
        void UpdateReference(Mat frame)
        {
            _refCounter++;
            if (_refCounter % 5 != 0) return;

            if (_refFrame == null || _refFrame.Width != frame.Width)
            {
                _refFrame?.Dispose();
                _refFrame = frame.Clone();
            }
            else
            {
                Cv2.AddWeighted(_refFrame, 0.90, frame, 0.10, 0, _refFrame);
            }
        }

        // ── Pixel-Differenz ───────────────────────────────────────────────────
        double ComputeDiff(Mat frame)
        {
            if (_refFrame == null || _refFrame.Size() != frame.Size()) return 0;
            try
            {
                using var g1  = new Mat();
                using var g2  = new Mat();
                using var pos = new Mat();
                using var neg = new Mat();
                using var d   = new Mat();
                using var thr = new Mat();
                Cv2.CvtColor(_refFrame, g1, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(frame,     g2, ColorConversionCodes.BGR2GRAY);
                Cv2.Subtract(g1, g2, pos);
                Cv2.Subtract(g2, g1, neg);
                Cv2.Max(pos, neg, d);
                Cv2.Threshold(d, thr, 25, 255, ThresholdTypes.Binary);
                int nz    = Cv2.CountNonZero(thr);
                int total = thr.Rows * thr.Cols;
                return total > 0 ? nz * 100.0 / total : 0;
            }
            catch { return 0; }
        }

        // ── Ringpuffer-Enqueue ────────────────────────────────────────────────
        void EnqueueFrame(Mat frame)
        {
            Cv2.ImEncode(".jpg", frame, out byte[] jpg,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));
            lock (_rLock)
            {
                _ring.Enqueue(jpg);
                if (_ring.Count > MaxFrames) _ring.Dequeue();
            }
        }

        // ── Farbe / S/W ───────────────────────────────────────────────────────
        private void BtnColor_Click(object sender, RoutedEventArgs e)
        {
            _blackWhite = !_blackWhite;
            if (_blackWhite)
            {
                TbColorText.Text   = "Farbe";
                TbColor.Foreground = new SolidColorBrush(Color.FromRgb(0xDD,0xDD,0xDD));
                BwBadge.Visibility = Visibility.Visible;
            }
            else
            {
                TbColorText.Text   = "S/W";
                TbColor.Foreground = new SolidColorBrush(Color.FromRgb(0x00,0xCC,0xFF));
                BwBadge.Visibility = Visibility.Collapsed;
            }
            _refFrame?.Dispose(); _refFrame = null;
        }

        // ── Frame → WPF ───────────────────────────────────────────────────────
        void ShowFrame(Mat src)
        {
            try
            {
                int stride = src.Width * 3;
                var data   = new byte[stride * src.Height];
                Marshal.Copy(src.Data, data, 0, data.Length);
                var bmp = BitmapSource.Create(src.Width, src.Height, 96, 96,
                    PixelFormats.Bgr24, null, data, stride);
                bmp.Freeze();
                Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    () => Preview.Source = bmp);
            }
            catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        static string DefaultPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VeraCam");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        }

        void Status(string msg, bool error = false)
        {
            StatusLabel.Text       = msg;
            StatusLabel.Foreground = error
                ? new SolidColorBrush(Color.FromRgb(0xFF,0x70,0x70))
                : new SolidColorBrush(Color.FromRgb(0xCC,0xCC,0xCC));
        }

        private async void Window_Closing(object s, System.ComponentModel.CancelEventArgs e)
        {
            _cts?.Cancel();
            if (_captureTask != null) await _captureTask.ConfigureAwait(false);
            _refFrame?.Dispose();
        }
    }
}
