using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FaceTrackingApp.Models;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace FaceTrackingApp.Services
{
    // ====================================================================== Detektoren

    public interface IFaceDetector : IDisposable
    {
        string Name { get; }

        /// <summary>Bevorzugte Breite des Eingangsbildes (wird vorher herunterskaliert).</summary>
        int PreferredWidth { get; }

        IReadOnlyList<DetectedFace> Detect(Mat bgr);
    }

    /// <summary>YuNet (OpenCV Zoo): schnell, genau, auch kleine/seitliche Gesichter.</summary>
    public sealed class YuNetFaceDetector : IFaceDetector
    {
        private readonly string _modelPath;
        private readonly float _scoreThreshold;
        private readonly Mat _faces = new();
        private FaceDetectorYN _detector;
        private Size _inputSize;

        public YuNetFaceDetector(string modelPath, float scoreThreshold = 0.75f)
        {
            _modelPath = modelPath;
            _scoreThreshold = scoreThreshold;
            _inputSize = new Size(320, 240);
            _detector = CreateDetector(_inputSize);
        }

        private FaceDetectorYN CreateDetector(Size inputSize) =>
            FaceDetectorYN.Create(_modelPath, string.Empty, inputSize, _scoreThreshold, 0.3f, 5000);

        public string Name => "YuNet (DNN)";
        public int PreferredWidth => 480;

        public IReadOnlyList<DetectedFace> Detect(Mat bgr)
        {
            var size = new Size(bgr.Width, bgr.Height);
            if (size != _inputSize)
            {
                // OpenCvSharp 4.13.0.20260627 bietet noch kein SetInputSize → Detektor für die neue Größe neu anlegen.
                // Passiert nur bei Auflösungswechsel, nicht pro Frame.
                var old = _detector;
                _detector = CreateDetector(size);
                old.Dispose();
                _inputSize = size;
            }

            _detector.Detect(bgr, _faces);
            int rows = _faces.Rows;
            var result = new List<DetectedFace>(rows);
            for (int i = 0; i < rows; i++)
            {
                // Zeile: x, y, w, h, 5 Landmarken (x,y), Score
                float x = _faces.At<float>(i, 0), y = _faces.At<float>(i, 1);
                float w = _faces.At<float>(i, 2), h = _faces.At<float>(i, 3);
                float score = _faces.At<float>(i, 14);

                // Landmarken 0/1: rechtes/linkes Auge (Mittelpunkte) → Rechtecke als Rückfallebene für Stufe 2
                float rx = _faces.At<float>(i, 4), ry = _faces.At<float>(i, 5);
                float lx = _faces.At<float>(i, 6), ly = _faces.At<float>(i, 7);
                double dist = Math.Sqrt((rx - lx) * (rx - lx) + (ry - ly) * (ry - ly));
                BoxF[]? eyes = null;
                if (dist > 4)
                {
                    double ew = dist * 0.55, eh = dist * 0.35;
                    eyes = new[]
                    {
                        new BoxF(rx - ew / 2, ry - eh / 2, ew, eh),
                        new BoxF(lx - ew / 2, ly - eh / 2, ew, eh)
                    };
                }
                result.Add(new DetectedFace(new BoxF(x, y, w, h), score, eyes));
            }
            return result;
        }

        public void Dispose()
        {
            _faces.Dispose();
            _detector.Dispose();
        }
    }

    /// <summary>ResNet-10 SSD (OpenCV-DNN-Sample): robust, liefert Konfidenz, Modell ist im Projekt enthalten.</summary>
    public sealed class SsdFaceDetector : IFaceDetector
    {
        private readonly Net _net;
        private readonly float _threshold;

        public SsdFaceDetector(string prototxt, string model, float threshold = 0.6f)
        {
            _net = CvDnn.ReadNetFromCaffe(prototxt, model)
                   ?? throw new InvalidOperationException("SSD-Modell konnte nicht geladen werden.");
            _net.SetPreferableBackend(Backend.OPENCV);
            _net.SetPreferableTarget(Target.CPU);
            _threshold = threshold;
        }

        public string Name => "ResNet-SSD (DNN)";
        public int PreferredWidth => 640;

        public IReadOnlyList<DetectedFace> Detect(Mat bgr)
        {
            using var blob = CvDnn.BlobFromImage(bgr, 1.0, new Size(300, 300), new Scalar(104, 177, 123), false, false);
            _net.SetInput(blob);
            using var output = _net.Forward();

            // Ausgabe: [1, 1, N, 7] → (imageId, classId, conf, x1, y1, x2, y2) normiert
            using var det = Mat.FromPixelData(output.Size(2), output.Size(3), MatType.CV_32F, output.Ptr(0));
            var result = new List<DetectedFace>();
            int rows = det.Rows;
            for (int i = 0; i < rows; i++)
            {
                float conf = det.At<float>(i, 2);
                if (conf < _threshold)
                    continue;
                double x1 = Math.Clamp(det.At<float>(i, 3), 0, 1) * bgr.Width;
                double y1 = Math.Clamp(det.At<float>(i, 4), 0, 1) * bgr.Height;
                double x2 = Math.Clamp(det.At<float>(i, 5), 0, 1) * bgr.Width;
                double y2 = Math.Clamp(det.At<float>(i, 6), 0, 1) * bgr.Height;
                if (x2 - x1 < 8 || y2 - y1 < 8)
                    continue;
                result.Add(new DetectedFace(new BoxF(x1, y1, x2 - x1, y2 - y1), conf));
            }
            return result;
        }

        public void Dispose() => _net.Dispose();
    }

    /// <summary>Klassische Haar-Cascade – nur Notlösung, falls keine DNN-Modelle vorhanden sind.</summary>
    public sealed class HaarFaceDetector : IFaceDetector
    {
        private readonly CascadeClassifier _cascade;
        private readonly Mat _gray = new();

        public HaarFaceDetector(string cascadePath)
        {
            _cascade = new CascadeClassifier(cascadePath);
            if (_cascade.Empty())
                throw new InvalidOperationException("Haar-Cascade konnte nicht geladen werden.");
        }

        public string Name => "Haar-Cascade";
        public int PreferredWidth => 480;

        public IReadOnlyList<DetectedFace> Detect(Mat bgr)
        {
            Cv2.CvtColor(bgr, _gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(_gray, _gray);
            var rects = _cascade.DetectMultiScale(_gray, 1.1, 5, HaarDetectionTypes.ScaleImage, new Size(36, 36));
            var result = new List<DetectedFace>(rects.Length);
            foreach (var r in rects)
                result.Add(new DetectedFace(new BoxF(r.X, r.Y, r.Width, r.Height), float.NaN));
            return result;
        }

        public void Dispose()
        {
            _gray.Dispose();
            _cascade.Dispose();
        }
    }

    // ====================================================================== Stufe 2: Augen

    /// <summary>
    /// Sucht Augen mit einer Haar-Cascade – nur im oberen Bereich eines bereits gefundenen Gesichts.
    /// Kleine Gesichter werden dafür hochskaliert, damit die Cascade die Augen noch auflösen kann.
    /// </summary>
    public sealed class EyeDetector : IDisposable
    {
        private const int MinSearchWidth = 160;
        private readonly CascadeClassifier _cascade;
        private readonly Mat _scaled = new();

        public EyeDetector(string cascadePath)
        {
            _cascade = new CascadeClassifier(cascadePath);
            if (_cascade.Empty())
                throw new InvalidOperationException("Augen-Cascade konnte nicht geladen werden.");
        }

        public string Name => "Haar-Augen";

        /// <summary>
        /// Liefert höchstens ein Auge je Gesichtshälfte, in Bildkoordinaten von <paramref name="gray"/>.
        /// </summary>
        /// <param name="gray">Graustufenbild (histogrammausgeglichen) des ganzen Frames.</param>
        /// <param name="face">Gesichtsrahmen in Koordinaten von <paramref name="gray"/>.</param>
        public IReadOnlyList<BoxF> Detect(Mat gray, BoxF face)
        {
            // Augen liegen etwa bei 15–60 % der Gesichtshöhe
            int x1 = (int)Math.Max(0, face.X);
            int y1 = (int)Math.Max(0, face.Y + face.H * 0.15);
            int x2 = (int)Math.Min(gray.Width, face.X + face.W);
            int y2 = (int)Math.Min(gray.Height, face.Y + face.H * 0.60);
            int w = x2 - x1, h = y2 - y1;
            if (w < 16 || h < 8)
                return Array.Empty<BoxF>();

            using var roi = new Mat(gray, new Rect(x1, y1, w, h));
            double up = w < MinSearchWidth ? (double)MinSearchWidth / w : 1.0;
            Mat search = roi;
            if (up > 1)
            {
                Cv2.Resize(roi, _scaled, new Size(), up, up, InterpolationFlags.Linear);
                search = _scaled;
            }

            int sw = search.Width;
            var rects = _cascade.DetectMultiScale(search, 1.1, 5, HaarDetectionTypes.ScaleImage,
                new Size(Math.Max(8, sw / 9), Math.Max(8, sw / 9)),
                new Size(sw / 2, sw / 2));

            // Je Gesichtshälfte das größte Kandidaten-Auge behalten (verwirft Augenbrauen/Nasenlöcher)
            Rect? left = null, right = null;
            double mid = sw / 2.0;
            foreach (var r in rects)
            {
                double cx = r.X + r.Width / 2.0;
                if (cx < mid)
                {
                    if (left is null || r.Width * r.Height > left.Value.Width * left.Value.Height) left = r;
                }
                else
                {
                    if (right is null || r.Width * r.Height > right.Value.Width * right.Value.Height) right = r;
                }
            }

            var result = new List<BoxF>(2);
            foreach (var r in new[] { left, right })
            {
                if (r is { } e)
                    result.Add(new BoxF(x1 + e.X / up, y1 + e.Y / up, e.Width / up, e.Height / up));
            }
            return result;
        }

        public void Dispose()
        {
            _scaled.Dispose();
            _cascade.Dispose();
        }
    }

    public static class FaceDetectorFactory
    {
        public static string AssetsDirectory => Path.Combine(AppContext.BaseDirectory, "Assets");

        /// <summary>Wählt den besten verfügbaren Detektor: YuNet → SSD → Haar.</summary>
        public static IFaceDetector CreateBest()
        {
            var errors = new List<string>();
            string dir = AssetsDirectory;

            string yunet = Path.Combine(dir, "face_detection_yunet_2023mar.onnx");
            if (File.Exists(yunet) && new FileInfo(yunet).Length > 10_000)
            {
                try { return new YuNetFaceDetector(yunet); }
                catch (Exception ex) { errors.Add($"YuNet: {ex.Message}"); }
            }

            string proto = Path.Combine(dir, "deploy.prototxt");
            string caffe = Path.Combine(dir, "res10_300x300_ssd_iter_140000_fp16.caffemodel");
            if (File.Exists(proto) && File.Exists(caffe))
            {
                try { return new SsdFaceDetector(proto, caffe); }
                catch (Exception ex) { errors.Add($"SSD: {ex.Message}"); }
            }

            string haar = Path.Combine(dir, "haarcascade_frontalface_default.xml");
            if (File.Exists(haar))
            {
                try { return new HaarFaceDetector(haar); }
                catch (Exception ex) { errors.Add($"Haar: {ex.Message}"); }
            }

            throw new FileNotFoundException(
                $"Kein Gesichtserkennungsmodell in „{dir}“ gefunden oder ladbar. {string.Join(" | ", errors)}");
        }

        /// <summary>Augen-Detektor für Stufe 2, oder null, wenn die Cascade fehlt (dann nur YuNet-Landmarken).</summary>
        public static EyeDetector? CreateEyeDetector()
        {
            string path = Path.Combine(AssetsDirectory, "haarcascade_eye.xml");
            if (!File.Exists(path))
                return null;
            try { return new EyeDetector(path); }
            catch (Exception ex)
            {
                Debug.WriteLine($"Augen-Cascade: {ex.Message}");
                return null;
            }
        }
    }

    // ====================================================================== Hintergrund-Erkennung

    public sealed record DetectionResult(
        long Sequence,
        IReadOnlyList<TrackedFaceSnapshot> Faces,
        int FrameWidth,
        int FrameHeight,
        double DetectMilliseconds);

    /// <summary>
    /// Führt die Gesichtserkennung auf einem eigenen Thread aus – immer auf dem neuesten Frame.
    /// Ist die Erkennung langsamer als die Kamera, werden Frames einfach übersprungen;
    /// die Videoanzeige bleibt davon unberührt flüssig.
    /// </summary>
    public sealed class FaceDetectionService : IDisposable
    {
        private readonly CameraService _camera;
        private readonly IFaceDetector _detector;
        private readonly EyeDetector? _eyeDetector;
        private readonly FaceTracker _tracker = new();
        private readonly Mat _small = new();
        private readonly Mat _gray = new();
        private Thread? _thread;
        private volatile bool _running;
        private DetectionResult? _latest;
        private long _sequence;
        private double _detectFps;

        public FaceDetectionService(CameraService camera)
        {
            _camera = camera;
            _detector = FaceDetectorFactory.CreateBest();
            _eyeDetector = FaceDetectorFactory.CreateEyeDetector();
        }

        public string DetectorName => _eyeDetector is not null
            ? $"{_detector.Name} + {_eyeDetector.Name}"
            : _detector.Name;
        public DetectionResult? Latest => Volatile.Read(ref _latest);
        public double DetectionFps => Volatile.Read(ref _detectFps);

        public void Start()
        {
            if (_running) return;
            _tracker.Reset();
            Volatile.Write(ref _latest, null);
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Face detection" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join(2000);
            _thread = null;
            Volatile.Write(ref _latest, null);
            Volatile.Write(ref _detectFps, 0);
        }

        private void Loop()
        {
            long frameSeq = 0;
            var fpsWatch = Stopwatch.StartNew();
            int runs = 0;

            while (_running)
            {
                if (!_camera.FrameAvailable.WaitOne(200))
                    continue;

                int fullW = 0, fullH = 0;
                double scale = 1;

                // Unter dem Lock nur verkleinern (≈1 ms) – die eigentliche Erkennung läuft danach ohne Lock.
                bool got = _camera.TryUseLatest(ref frameSeq, frame =>
                {
                    fullW = frame.Width;
                    fullH = frame.Height;
                    int targetW = Math.Min(_detector.PreferredWidth, fullW);
                    scale = (double)fullW / targetW;
                    if (targetW == fullW)
                        frame.CopyTo(_small);
                    else
                        Cv2.Resize(frame, _small, new Size(targetW, (int)Math.Round(fullH / scale)), 0, 0, InterpolationFlags.Area);
                });
                if (!got)
                    continue;

                try
                {
                    var sw = Stopwatch.StartNew();
                    // Stufe 1: Gesichter
                    var detections = _detector.Detect(_small);

                    // Stufe 2: Augen innerhalb jedes Gesichts
                    if (_eyeDetector is not null && detections.Count > 0)
                        detections = DetectEyes(detections);

                    var scaled = new List<DetectedFace>(detections.Count);
                    foreach (var d in detections)
                        scaled.Add(d with { Box = d.Box.Scale(scale), Eyes = ScaleAll(d.Eyes, scale) });

                    var tracked = _tracker.Update(scaled);
                    sw.Stop();

                    Volatile.Write(ref _latest, new DetectionResult(
                        Interlocked.Increment(ref _sequence), tracked, fullW, fullH, sw.Elapsed.TotalMilliseconds));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Gesichtserkennung fehlgeschlagen: {ex.Message}");
                }

                runs++;
                if (fpsWatch.ElapsedMilliseconds >= 1000)
                {
                    Volatile.Write(ref _detectFps, runs * 1000.0 / fpsWatch.ElapsedMilliseconds);
                    runs = 0;
                    fpsWatch.Restart();
                }
            }
        }

        private IReadOnlyList<DetectedFace> DetectEyes(IReadOnlyList<DetectedFace> faces)
        {
            Cv2.CvtColor(_small, _gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(_gray, _gray);

            var result = new List<DetectedFace>(faces.Count);
            foreach (var face in faces)
            {
                var eyes = _eyeDetector!.Detect(_gray, face.Box);
                // Cascade-Treffer bevorzugen; YuNet-Landmarken nur behalten, wenn sie mehr Augen liefern
                bool useCascade = eyes.Count > 0 && eyes.Count >= (face.Eyes?.Count ?? 0);
                result.Add(useCascade ? face with { Eyes = eyes } : face);
            }
            return result;
        }

        private static IReadOnlyList<BoxF>? ScaleAll(IReadOnlyList<BoxF>? boxes, double scale)
        {
            if (boxes is null)
                return null;
            var result = new BoxF[boxes.Count];
            for (int i = 0; i < boxes.Count; i++)
                result[i] = boxes[i].Scale(scale);
            return result;
        }

        public void Dispose()
        {
            Stop();
            _detector.Dispose();
            _eyeDetector?.Dispose();
            _small.Dispose();
            _gray.Dispose();
        }
    }
}
