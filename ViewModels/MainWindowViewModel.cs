using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FaceTrackingApp.Models;
using FaceTrackingApp.Services;
using Windows.Devices.Enumeration;

namespace FaceTrackingApp.ViewModels
{
    public enum StatusKind { Idle, Busy, Ok, Warning, Error }

    public sealed class MainWindowViewModel : ObservableObject, IDisposable
    {
        private static readonly Brush IdleBrush = Frozen(0x9A, 0xA0, 0xA6);
        private static readonly Brush BusyBrush = Frozen(0x4C, 0xC2, 0xFF);
        private static readonly Brush OkBrush = Frozen(0x6C, 0xCB, 0x5F);
        private static readonly Brush WarningBrush = Frozen(0xFC, 0xE1, 0x00);
        private static readonly Brush ErrorBrush = Frozen(0xFF, 0x63, 0x63);

        private readonly Dispatcher _dispatcher;
        private readonly CameraService _camera = new();
        private FaceDetectionService? _detection;
        private DeviceWatcher? _watcher;
        private volatile bool _watcherReady;
        private readonly DispatcherTimer _refreshDebounce;
        private readonly DispatcherTimer _statsTimer;

        private WriteableBitmap? _bitmap;
        private long _shownFrameSeq;
        private long _shownResultSeq = -1;
        private bool _renderingHooked;
        private bool _suppressRestart;
        private int _displayedFrames;
        private readonly Stopwatch _displayWatch = new();

        private CameraInfo? _selectedCamera;
        private ResolutionOption _selectedResolution;
        private bool _isRunning, _isBusy, _mirror = true, _showOverlay = true, _hasNoCameras;
        private string _statusText = "Starte …";
        private Brush _statusBrush = BusyBrush;
        private ImageSource? _videoSource;
        private int _frameWidth = 1280, _frameHeight = 720, _faceCount;
        private string _cameraFpsText = "–", _displayFpsText = "–", _detectionText = "–";
        private string _resolutionText = "–", _backendText = "–", _detectorName = "–";

        public MainWindowViewModel()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            Resolutions = new ObservableCollection<ResolutionOption>
            {
                new(640, 480), new(1280, 720), new(1920, 1080)
            };
            _selectedResolution = Resolutions[1];

            ToggleCameraCommand = new AsyncRelayCommand(ToggleCameraAsync,
                () => !IsBusy && (IsRunning || SelectedCamera is not null));
            RefreshCamerasCommand = new AsyncRelayCommand(RefreshCamerasAsync, () => !IsBusy);

            _refreshDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background,
                async (_, _) => { _refreshDebounce!.Stop(); await RefreshCamerasAsync(); }, _dispatcher);
            _refreshDebounce.Stop();

            _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                (_, _) => UpdateStats(), _dispatcher);
            _statsTimer.Stop();

            _camera.Faulted += message =>
                _dispatcher.InvokeAsync(async () => await StopCameraAsync(message, StatusKind.Error));
        }

        // ------------------------------------------------------------------ Bindings

        public ObservableCollection<CameraInfo> Cameras { get; } = new();
        public ObservableCollection<ResolutionOption> Resolutions { get; }
        public ObservableCollection<FaceOverlay> Faces { get; } = new();

        public ICommand ToggleCameraCommand { get; }
        public ICommand RefreshCamerasCommand { get; }

        public CameraInfo? SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                var old = _selectedCamera;
                if (!SetProperty(ref _selectedCamera, value))
                    return;
                if (!_suppressRestart && IsRunning && value is not null && !value.IsSameDevice(old))
                    _ = RestartAsync();
            }
        }

        public ResolutionOption SelectedResolution
        {
            get => _selectedResolution;
            set
            {
                if (value is null || !SetProperty(ref _selectedResolution, value))
                    return;
                if (IsRunning)
                    _ = RestartAsync();
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(ToggleButtonText));
                    OnPropertyChanged(nameof(ToggleButtonIcon));
                    OnPropertyChanged(nameof(ShowPlaceholder));
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ToggleButtonText => IsRunning ? "Kamera stoppen" : "Kamera starten";
        public string ToggleButtonIcon => IsRunning ? "" : "";
        public bool ShowPlaceholder => !IsRunning;

        public bool HasNoCameras { get => _hasNoCameras; private set => SetProperty(ref _hasNoCameras, value); }

        /// <summary>Vorschau spiegeln (wie ein Spiegel – bei Webcams üblich).</summary>
        public bool Mirror
        {
            get => _mirror;
            set
            {
                if (SetProperty(ref _mirror, value))
                {
                    OnPropertyChanged(nameof(ImageScaleX));
                    _shownResultSeq = -1; // Overlays sofort neu berechnen
                }
            }
        }

        public double ImageScaleX => Mirror ? -1 : 1;

        public bool ShowOverlay { get => _showOverlay; set => SetProperty(ref _showOverlay, value); }

        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }
        public ImageSource? VideoSource { get => _videoSource; private set => SetProperty(ref _videoSource, value); }
        public int FrameWidth { get => _frameWidth; private set => SetProperty(ref _frameWidth, value); }
        public int FrameHeight { get => _frameHeight; private set => SetProperty(ref _frameHeight, value); }
        public int FaceCount { get => _faceCount; private set => SetProperty(ref _faceCount, value); }
        public string CameraFpsText { get => _cameraFpsText; private set => SetProperty(ref _cameraFpsText, value); }
        public string DisplayFpsText { get => _displayFpsText; private set => SetProperty(ref _displayFpsText, value); }
        public string DetectionText { get => _detectionText; private set => SetProperty(ref _detectionText, value); }
        public string ResolutionText { get => _resolutionText; private set => SetProperty(ref _resolutionText, value); }
        public string BackendText { get => _backendText; private set => SetProperty(ref _backendText, value); }
        public string DetectorName { get => _detectorName; private set => SetProperty(ref _detectorName, value); }

        // ------------------------------------------------------------------ Start / Initialisierung

        public async Task InitializeAsync()
        {
            try
            {
                _detection = await Task.Run(() => new FaceDetectionService(_camera));
                DetectorName = _detection.DetectorName;
            }
            catch (Exception ex)
            {
                DetectorName = "nicht verfügbar";
                SetStatus($"Gesichtserkennung nicht verfügbar: {ex.Message}", StatusKind.Warning);
            }

            await RefreshCamerasAsync();
            StartDeviceWatcher();
        }

        private void StartDeviceWatcher()
        {
            try
            {
                _watcher = CameraEnumerator.CreateWatcher();
                _watcher.Added += (_, _) => OnDevicesChanged();
                _watcher.Removed += (_, _) => OnDevicesChanged();
                _watcher.Updated += (_, _) => OnDevicesChanged();
                _watcher.EnumerationCompleted += (_, _) => _watcherReady = true;
                _watcher.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeviceWatcher nicht verfügbar: {ex.Message}");
            }
        }

        private void OnDevicesChanged()
        {
            if (!_watcherReady)
                return; // initiale Aufzählung ignorieren
            _dispatcher.InvokeAsync(() =>
            {
                _refreshDebounce.Stop();
                _refreshDebounce.Start();
            });
        }

        // ------------------------------------------------------------------ Kameraliste

        private async Task RefreshCamerasAsync()
        {
            IReadOnlyList<CameraInfo> found;
            try
            {
                found = await CameraEnumerator.GetCamerasAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Kameras konnten nicht gelesen werden: {ex.Message}", StatusKind.Error);
                return;
            }

            var active = _camera.ActiveCamera;
            var previous = SelectedCamera;

            _suppressRestart = true;
            try
            {
                Cameras.Clear();
                foreach (var cam in found)
                    Cameras.Add(cam);

                SelectedCamera =
                    Cameras.FirstOrDefault(c => c.IsSameDevice(active)) ??
                    Cameras.FirstOrDefault(c => c.IsSameDevice(previous)) ??
                    Cameras.FirstOrDefault(c => c.Kind == CameraKind.Intern) ??
                    Cameras.FirstOrDefault();
            }
            finally
            {
                _suppressRestart = false;
            }

            HasNoCameras = Cameras.Count == 0;
            CommandManager.InvalidateRequerySuggested();

            if (IsRunning && active is not null && !Cameras.Any(c => c.IsSameDevice(active)))
            {
                await StopCameraAsync($"„{active.Name}“ wurde getrennt.", StatusKind.Warning);
            }
            else if (!IsRunning && !IsBusy)
            {
                if (Cameras.Count == 0)
                    SetStatus("Keine Kamera gefunden – bitte anschließen oder im Geräte-Manager aktivieren.", StatusKind.Warning);
                else
                    SetStatus($"{Cameras.Count} Kamera(s) verfügbar · bereit", StatusKind.Idle);
            }
        }

        // ------------------------------------------------------------------ Start / Stopp

        private Task ToggleCameraAsync() => IsRunning ? StopCameraAsync() : StartCameraAsync();

        private async Task RestartAsync()
        {
            if (IsBusy)
                return;
            await StopCameraAsync();
            await StartCameraAsync();
        }

        private async Task StartCameraAsync()
        {
            var cam = SelectedCamera;
            if (cam is null)
            {
                SetStatus("Keine Kamera ausgewählt.", StatusKind.Warning);
                return;
            }

            IsBusy = true;
            SetStatus($"Öffne „{cam.Name}“ …", StatusKind.Busy);
            try
            {
                await _camera.StartAsync(cam, SelectedResolution);
                _detection?.Start();

                ResolutionText = $"{_camera.FrameWidth} × {_camera.FrameHeight}";
                BackendText = _camera.Backend;
                _shownFrameSeq = 0;
                _shownResultSeq = -1;
                _displayedFrames = 0;
                _displayWatch.Restart();

                IsRunning = true;
                HookRendering();
                _statsTimer.Start();
                SetStatus($"Live · {cam.Name} ({cam.KindLabel})", StatusKind.Ok);
            }
            catch (Exception ex)
            {
                await _camera.StopAsync();
                SetStatus(ex.Message, StatusKind.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task StopCameraAsync() => await StopCameraAsync(null, StatusKind.Idle);

        private async Task StopCameraAsync(string? reason, StatusKind kind)
        {
            if (!IsRunning && !_camera.IsRunning)
            {
                if (reason is not null) SetStatus(reason, kind);
                return;
            }

            UnhookRendering();
            _statsTimer.Stop();

            var detection = _detection;
            await Task.Run(() =>
            {
                detection?.Stop();
                _camera.Stop();
            });

            IsRunning = false;
            Faces.Clear();
            FaceCount = 0;
            VideoSource = null;
            _bitmap = null;
            CameraFpsText = DisplayFpsText = DetectionText = "–";
            SetStatus(reason ?? "Kamera gestoppt", reason is null ? StatusKind.Idle : kind);
        }

        // ------------------------------------------------------------------ Rendering (UI-Thread, im Bildschirmtakt)

        private void HookRendering()
        {
            if (_renderingHooked) return;
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }

        private void UnhookRendering()
        {
            if (!_renderingHooked) return;
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!IsRunning)
                return;

            // 1) Neuestes Kamerabild direkt in ein wiederverwendetes WriteableBitmap kopieren (kein GDI+, kein Encoding)
            _camera.TryUseLatest(ref _shownFrameSeq, frame =>
            {
                int w = frame.Width, h = frame.Height;
                if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                {
                    _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
                    VideoSource = _bitmap;
                    FrameWidth = w;
                    FrameHeight = h;
                    ResolutionText = $"{w} × {h}";
                }

                int stride = (int)frame.Step();
                _bitmap.WritePixels(new Int32Rect(0, 0, w, h), frame.Data, stride * h, stride);
                _displayedFrames++;
            });

            // 2) Overlays nur aktualisieren, wenn die Erkennung ein neues Ergebnis hat
            var result = _detection?.Latest;
            if (result is not null && result.Sequence != _shownResultSeq)
            {
                _shownResultSeq = result.Sequence;
                UpdateOverlays(result);
            }
        }

        private void UpdateOverlays(DetectionResult result)
        {
            var seen = new HashSet<int>();
            foreach (var face in result.Faces)
            {
                seen.Add(face.Id);
                var b = face.Box;

                double x = Math.Clamp(b.X, 0, result.FrameWidth);
                double y = Math.Clamp(b.Y, 0, result.FrameHeight);
                double w = Math.Clamp(b.W, 0, result.FrameWidth - x);
                double h = Math.Clamp(b.H, 0, result.FrameHeight - y);
                if (Mirror)
                    x = result.FrameWidth - x - w;

                var overlay = Faces.FirstOrDefault(o => o.Id == face.Id);
                if (overlay is null)
                {
                    overlay = new FaceOverlay(face.Id);
                    Faces.Add(overlay);
                }
                overlay.Update(x, y, w, h, face.Score, face.IsPrimary, result.FrameWidth);
            }

            for (int i = Faces.Count - 1; i >= 0; i--)
            {
                if (!seen.Contains(Faces[i].Id))
                    Faces.RemoveAt(i);
            }

            FaceCount = result.Faces.Count;
        }

        private void UpdateStats()
        {
            CameraFpsText = $"{_camera.MeasuredFps:0}";

            double seconds = _displayWatch.Elapsed.TotalSeconds;
            if (seconds > 0.25)
            {
                DisplayFpsText = $"{_displayedFrames / seconds:0}";
                _displayedFrames = 0;
                _displayWatch.Restart();
            }

            if (_detection is not null)
            {
                var latest = _detection.Latest;
                DetectionText = latest is null
                    ? "–"
                    : $"{_detection.DetectionFps:0} /s · {latest.DetectMilliseconds:0} ms";
            }
        }

        private void SetStatus(string text, StatusKind kind)
        {
            StatusText = text;
            StatusBrush = kind switch
            {
                StatusKind.Busy => BusyBrush,
                StatusKind.Ok => OkBrush,
                StatusKind.Warning => WarningBrush,
                StatusKind.Error => ErrorBrush,
                _ => IdleBrush
            };
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // ------------------------------------------------------------------ Aufräumen

        public void Dispose()
        {
            UnhookRendering();
            _statsTimer.Stop();
            _refreshDebounce.Stop();

            if (_watcher is not null)
            {
                try
                {
                    if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                        _watcher.Stop();
                }
                catch { /* ignorieren */ }
                _watcher = null;
            }

            _detection?.Dispose();
            _camera.Dispose();
        }
    }
}
