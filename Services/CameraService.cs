using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FaceTrackingApp.Models;
using OpenCvSharp;

namespace FaceTrackingApp.Services
{
    /// <summary>
    /// Liest Frames auf einem eigenen Thread. Es wird immer nur das neueste Frame vorgehalten
    /// (Double-Buffer per Referenztausch, keine Kopie, keine Warteschlange) – Anzeige und
    /// Erkennung holen es sich in ihrem eigenen Takt ab. Dadurch staut sich nie etwas auf.
    /// </summary>
    public sealed class CameraService : IDisposable
    {
        private readonly object _frameLock = new();
        private Mat _front = new();  // zuletzt fertiges Frame (nur unter _frameLock)
        private Mat _back = new();   // wird gerade beschrieben (nur Capture-Thread)
        private long _sequence;

        private VideoCapture? _capture;
        private Thread? _thread;
        private volatile bool _running;
        private double _fps;

        /// <summary>Wird gesetzt, sobald ein neues Frame bereitsteht.</summary>
        public AutoResetEvent FrameAvailable { get; } = new(false);

        /// <summary>Kamera liefert keine Bilder mehr (z. B. abgesteckt). Wird vom Capture-Thread ausgelöst.</summary>
        public event Action<string>? Faulted;

        public CameraInfo? ActiveCamera { get; private set; }
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public string Backend { get; private set; } = "-";
        public bool IsRunning => _running;
        public double MeasuredFps => Volatile.Read(ref _fps);

        public Task StartAsync(CameraInfo camera, ResolutionOption resolution) =>
            Task.Run(() => Start(camera, resolution));

        private void Start(CameraInfo camera, ResolutionOption resolution)
        {
            Stop();

            // DirectShow: Index entspricht exakt der Enumeration und öffnet deutlich schneller als MSMF.
            var capture = TryOpen(camera.DirectShowIndex, VideoCaptureAPIs.DSHOW, resolution)
                          ?? TryOpen(camera.DirectShowIndex, VideoCaptureAPIs.MSMF, resolution)
                          ?? throw new InvalidOperationException(
                              $"„{camera.Name}“ konnte nicht geöffnet werden. Wird sie von einer anderen App benutzt " +
                              "oder ist der Kamerazugriff unter Einstellungen › Datenschutz › Kamera gesperrt?");

            _capture = capture;
            ActiveCamera = camera;
            FrameWidth = capture.FrameWidth;
            FrameHeight = capture.FrameHeight;
            Backend = capture.GetBackendName();
            Interlocked.Exchange(ref _fps, 0);

            _running = true;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "Camera capture",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        private static VideoCapture? TryOpen(int index, VideoCaptureAPIs api, ResolutionOption res)
        {
            VideoCapture? cap = null;
            try
            {
                cap = new VideoCapture(index, api);
                if (!cap.IsOpened())
                {
                    cap.Dispose();
                    return null;
                }

                // MJPG zuerst setzen: viele USB-Kameras schaffen 720p/1080p mit 30 FPS nur komprimiert.
                cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                cap.Set(VideoCaptureProperties.FrameWidth, res.Width);
                cap.Set(VideoCaptureProperties.FrameHeight, res.Height);
                cap.Set(VideoCaptureProperties.Fps, 30);
                cap.Set(VideoCaptureProperties.BufferSize, 1); // geringe Latenz (falls vom Treiber unterstützt)

                // Ein Testbild lesen – manche Treiber melden "offen", liefern aber nichts.
                using var probe = new Mat();
                for (int i = 0; i < 20; i++)
                {
                    if (cap.Read(probe) && !probe.Empty())
                        return cap;
                    Thread.Sleep(25);
                }

                cap.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Öffnen mit {api} fehlgeschlagen: {ex.Message}");
                cap?.Dispose();
                return null;
            }
        }

        private void CaptureLoop()
        {
            var capture = _capture!;
            var fpsWatch = Stopwatch.StartNew();
            int framesInWindow = 0;
            int failures = 0;

            while (_running)
            {
                bool ok;
                try
                {
                    ok = capture.Read(_back) && !_back.Empty();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Read fehlgeschlagen: {ex.Message}");
                    ok = false;
                }

                if (!ok)
                {
                    if (++failures > 100) // ~1–2 s ohne Bild
                    {
                        _running = false;
                        Faulted?.Invoke("Die Kamera liefert keine Bilder mehr (getrennt oder von einer anderen App belegt).");
                        break;
                    }
                    Thread.Sleep(10);
                    continue;
                }
                failures = 0;

                // Anzeige erwartet BGR (3 Kanäle)
                int ch = _back.Channels();
                if (ch == 1) Cv2.CvtColor(_back, _back, ColorConversionCodes.GRAY2BGR);
                else if (ch == 4) Cv2.CvtColor(_back, _back, ColorConversionCodes.BGRA2BGR);

                lock (_frameLock)
                {
                    (_front, _back) = (_back, _front);
                    _sequence++;
                }
                FrameAvailable.Set();

                framesInWindow++;
                if (fpsWatch.ElapsedMilliseconds >= 1000)
                {
                    Volatile.Write(ref _fps, framesInWindow * 1000.0 / fpsWatch.ElapsedMilliseconds);
                    framesInWindow = 0;
                    fpsWatch.Restart();
                }
            }
        }

        /// <summary>
        /// Übergibt das neueste Frame an <paramref name="consumer"/>, falls seit <paramref name="lastSequence"/>
        /// ein neues eingetroffen ist. Der Consumer darf das Mat nur während des Aufrufs benutzen (kurz halten!).
        /// </summary>
        public bool TryUseLatest(ref long lastSequence, Action<Mat> consumer)
        {
            lock (_frameLock)
            {
                if (_sequence == lastSequence || _front.Empty())
                    return false;
                lastSequence = _sequence;
                consumer(_front);
                return true;
            }
        }

        public Task StopAsync() => Task.Run(Stop);

        public void Stop()
        {
            _running = false;
            var thread = _thread;
            if (thread is not null && thread != Thread.CurrentThread)
                thread.Join(2000);
            _thread = null;

            var cap = _capture;
            _capture = null;
            if (cap is not null)
            {
                try { cap.Release(); } catch { /* Gerät evtl. schon weg */ }
                cap.Dispose();
            }

            ActiveCamera = null;
            Interlocked.Exchange(ref _fps, 0);
        }

        public void Dispose()
        {
            Stop();
            lock (_frameLock)
            {
                _front.Dispose();
                _back.Dispose();
            }
            FrameAvailable.Dispose();
        }
    }
}
