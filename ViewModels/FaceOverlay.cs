using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FaceTrackingApp.ViewModels
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Ein verfolgtes Gesicht für Overlay und Liste. Koordinaten in Bild-Pixeln.</summary>
    public sealed class FaceOverlay : ObservableObject
    {
        private double _x, _y, _width, _height, _strokeThickness = 3, _fontSize = 18;
        private bool _isPrimary;
        private string _scoreText = "–";

        public FaceOverlay(int id) => Id = id;

        public int Id { get; }
        public string Title => $"Gesicht #{Id}";

        public double X { get => _x; private set => SetProperty(ref _x, value); }
        public double Y { get => _y; private set => SetProperty(ref _y, value); }
        public double Width { get => _width; private set => SetProperty(ref _width, value); }
        public double Height { get => _height; private set => SetProperty(ref _height, value); }
        public double StrokeThickness { get => _strokeThickness; private set => SetProperty(ref _strokeThickness, value); }
        public double FontSize { get => _fontSize; private set => SetProperty(ref _fontSize, value); }
        public double CrossSize => Math.Max(10, Math.Min(Width, Height) * 0.12);

        public bool IsPrimary { get => _isPrimary; private set => SetProperty(ref _isPrimary, value); }
        public string ScoreText { get => _scoreText; private set => SetProperty(ref _scoreText, value); }

        public string Label => IsPrimary ? $"#{Id} · {ScoreText} · Fokus" : $"#{Id} · {ScoreText}";
        public string PositionText => $"Mitte: {X + Width / 2:0}, {Y + Height / 2:0}";
        public string SizeText => $"Größe: {Width:0} × {Height:0} px";

        public void Update(double x, double y, double w, double h, float score, bool isPrimary, int frameWidth)
        {
            X = x;
            Y = y;
            Width = w;
            Height = h;
            IsPrimary = isPrimary;
            ScoreText = float.IsNaN(score) ? "–" : $"{score * 100:0} %";
            StrokeThickness = Math.Max(2, frameWidth / 420.0);
            FontSize = Math.Max(12, frameWidth / 64.0);
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(CrossSize));
        }
    }
}
