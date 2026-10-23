using System;

namespace FaceTrackingApp.Models
{
    /// <summary>Art der Kamera, ermittelt aus den Windows-Geräteinformationen.</summary>
    public enum CameraKind
    {
        Intern,
        Extern,
        Virtuell,
        Unbekannt
    }

    /// <summary>Eine im System (Geräte-Manager) verfügbare Kamera.</summary>
    /// <param name="DirectShowIndex">Index für OpenCV (DirectShow-Reihenfolge).</param>
    /// <param name="Name">Anzeigename wie im Geräte-Manager.</param>
    /// <param name="DevicePath">Eindeutiger Gerätepfad (stabil über Neustarts/Umstecken am selben Port).</param>
    /// <param name="Kind">Intern/extern/virtuell.</param>
    public sealed record CameraInfo(int DirectShowIndex, string Name, string DevicePath, CameraKind Kind)
    {
        public string KindLabel => Kind switch
        {
            CameraKind.Intern => "Intern",
            CameraKind.Extern => "Extern (USB)",
            CameraKind.Virtuell => "Virtuell",
            _ => "Kamera"
        };

        public string KindIcon => Kind switch
        {
            CameraKind.Intern => "\uE7F8",   // Laptop
            CameraKind.Extern => "\uE88E",   // USB
            CameraKind.Virtuell => "\uE7F4", // Software/Bildschirm
            _ => "\uE722"                    // Kamera
        };

        public string DisplayName => $"{Name}  ·  {KindLabel}";

        /// <summary>Gleiche physische Kamera? (Index kann sich beim Umstecken anderer Geräte verschieben.)</summary>
        public bool IsSameDevice(CameraInfo? other) =>
            other is not null &&
            (!string.IsNullOrEmpty(DevicePath)
                ? string.Equals(DevicePath, other.DevicePath, StringComparison.OrdinalIgnoreCase)
                : Name == other.Name && DirectShowIndex == other.DirectShowIndex);

        public override string ToString() => DisplayName;
    }

    public sealed record ResolutionOption(int Width, int Height)
    {
        public override string ToString() => $"{Width} × {Height}";
    }

    /// <summary>Achsparalleles Rechteck in Gleitkomma-Pixelkoordinaten.</summary>
    public readonly record struct BoxF(double X, double Y, double W, double H)
    {
        public double Area => Math.Max(0, W) * Math.Max(0, H);
        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;

        public double IoU(BoxF o)
        {
            double x1 = Math.Max(X, o.X), y1 = Math.Max(Y, o.Y);
            double x2 = Math.Min(X + W, o.X + o.W), y2 = Math.Min(Y + H, o.Y + o.H);
            double inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            double union = Area + o.Area - inter;
            return union <= 0 ? 0 : inter / union;
        }

        public BoxF Lerp(BoxF target, double t) => new(
            X + (target.X - X) * t,
            Y + (target.Y - Y) * t,
            W + (target.W - W) * t,
            H + (target.H - H) * t);

        public BoxF Scale(double s) => new(X * s, Y * s, W * s, H * s);
    }

    /// <summary>Rohes Detektor-Ergebnis. Score = NaN, wenn der Detektor keine Konfidenz liefert.</summary>
    public readonly record struct DetectedFace(BoxF Box, float Score);

    /// <summary>Über mehrere Frames verfolgtes Gesicht (unveränderlicher Snapshot für die UI).</summary>
    public sealed record TrackedFaceSnapshot(int Id, BoxF Box, float Score, bool IsPrimary);
}
