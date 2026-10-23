using System;
using System.Collections.Generic;
using System.Linq;
using FaceTrackingApp.Models;

namespace FaceTrackingApp.Services
{
    /// <summary>
    /// Einfacher, robuster Multi-Face-Tracker:
    /// IoU-/Abstands-Zuordnung, stabile IDs, exponentielle Glättung gegen Zittern
    /// und kurzes "Nachhalten", wenn der Detektor ein Gesicht für 1–2 Frames verliert.
    /// </summary>
    public sealed class FaceTracker
    {
        private sealed class Track
        {
            public int Id;
            public BoxF Box;
            public float Score;
            public int Hits;
            public int Missed;

            /// <summary>Augen relativ zum Gesichtsrahmen (0..1), damit sie dem geglätteten Rahmen folgen.</summary>
            public List<BoxF> EyesRel = new();
            public int EyesMissed;
        }

        private readonly List<Track> _tracks = new();
        private int _nextId = 1;

        /// <summary>Glättungsfaktor 0..1 (1 = keine Glättung).</summary>
        public double Smoothing { get; set; } = 0.55;

        /// <summary>So viele Erkennungsdurchläufe darf ein Gesicht fehlen, bevor es entfernt wird.</summary>
        public int MaxMissed { get; set; } = 6;

        /// <summary>So viele Durchläufe ohne Augen werden die letzten Augen noch angezeigt.</summary>
        public int MaxEyesMissed { get; set; } = 3;

        public void Reset()
        {
            _tracks.Clear();
            _nextId = 1;
        }

        public IReadOnlyList<TrackedFaceSnapshot> Update(IReadOnlyList<DetectedFace> detections)
        {
            var unmatched = new List<int>(Enumerable.Range(0, detections.Count));

            // Gierige Zuordnung nach bestem Match (IoU, ersatzweise Mittelpunktabstand)
            var pairs = new List<(double cost, int track, int det)>();
            for (int t = 0; t < _tracks.Count; t++)
            {
                for (int d = 0; d < detections.Count; d++)
                {
                    var a = _tracks[t].Box;
                    var b = detections[d].Box;
                    double iou = a.IoU(b);
                    double dist = Math.Sqrt(Math.Pow(a.CenterX - b.CenterX, 2) + Math.Pow(a.CenterY - b.CenterY, 2));
                    double norm = Math.Max(1, Math.Max(a.W, a.H));
                    if (iou > 0.15 || dist < norm * 0.6)
                        pairs.Add((1 - iou + dist / norm * 0.25, t, d));
                }
            }

            var usedTracks = new HashSet<int>();
            var usedDets = new HashSet<int>();
            foreach (var (_, t, d) in pairs.OrderBy(p => p.cost))
            {
                if (usedTracks.Contains(t) || usedDets.Contains(d))
                    continue;
                usedTracks.Add(t);
                usedDets.Add(d);

                var tr = _tracks[t];
                var det = detections[d];
                // Bei großen Sprüngen weniger glätten, damit die Box nicht hinterherhängt
                double jump = Math.Abs(tr.Box.CenterX - det.Box.CenterX) + Math.Abs(tr.Box.CenterY - det.Box.CenterY);
                double alpha = jump > Math.Max(tr.Box.W, tr.Box.H) * 0.3 ? 0.85 : Smoothing;
                tr.Box = tr.Box.Lerp(det.Box, alpha);
                tr.Score = float.IsNaN(det.Score) ? det.Score : (float.IsNaN(tr.Score) ? det.Score : tr.Score * 0.6f + det.Score * 0.4f);
                tr.Hits++;
                tr.Missed = 0;
                UpdateEyes(tr, det);
            }
            unmatched.RemoveAll(usedDets.Contains);

            for (int t = _tracks.Count - 1; t >= 0; t--)
            {
                if (usedTracks.Contains(t))
                    continue;
                if (++_tracks[t].Missed > MaxMissed)
                    _tracks.RemoveAt(t);
            }

            foreach (int d in unmatched)
            {
                var track = new Track
                {
                    Id = _nextId++,
                    Box = detections[d].Box,
                    Score = detections[d].Score,
                    Hits = 1
                };
                UpdateEyes(track, detections[d]);
                _tracks.Add(track);
            }

            // Nur bestätigte Tracks anzeigen (unterdrückt einzelne Fehlerkennungen)
            var visible = _tracks.Where(t => t.Hits >= 2 || t.Missed == 0).ToList();
            var primary = visible.OrderByDescending(t => t.Box.Area).FirstOrDefault();

            return visible
                .OrderBy(t => t.Id)
                .Select(t => new TrackedFaceSnapshot(t.Id, t.Box, t.Score, ReferenceEquals(t, primary), ToAbsolute(t.Box, t.EyesRel)))
                .ToList();
        }

        private void UpdateEyes(Track tr, DetectedFace det)
        {
            if (det.Eyes is { Count: > 0 } && det.Box.W > 0 && det.Box.H > 0)
            {
                var face = det.Box;
                var rel = det.Eyes
                    .Select(e => new BoxF((e.X - face.X) / face.W, (e.Y - face.Y) / face.H, e.W / face.W, e.H / face.H))
                    .OrderBy(e => e.CenterX)
                    .ToList();

                // Gleiche Anzahl wie zuvor → glätten, sonst übernehmen
                if (rel.Count == tr.EyesRel.Count)
                {
                    for (int i = 0; i < rel.Count; i++)
                        tr.EyesRel[i] = tr.EyesRel[i].Lerp(rel[i], 0.5);
                }
                else
                {
                    tr.EyesRel = rel;
                }
                tr.EyesMissed = 0;
            }
            else if (++tr.EyesMissed > MaxEyesMissed)
            {
                tr.EyesRel.Clear();
            }
        }

        private static IReadOnlyList<BoxF> ToAbsolute(BoxF face, List<BoxF> rel)
        {
            if (rel.Count == 0)
                return Array.Empty<BoxF>();
            var result = new BoxF[rel.Count];
            for (int i = 0; i < rel.Count; i++)
            {
                var r = rel[i];
                result[i] = new BoxF(face.X + r.X * face.W, face.Y + r.Y * face.H, r.W * face.W, r.H * face.H);
            }
            return result;
        }
    }
}
