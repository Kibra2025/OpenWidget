using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskHardwareWidget
{
    internal static class GraphRenderer
    {
        public const int Capacity = 28;
        public static List<double> NewHistory()
        {
            var history = new List<double>();
            for (int i = 0; i < Capacity; i++) history.Add(double.NaN);
            return history;
        }
        public static void Add(List<double> history, double value)
        {
            if (history.Count >= Capacity) history.RemoveAt(0);
            history.Add(value);
        }
        public static void Draw(Path line, Path fill, IList<double> history, double maximum)
        {
            var canvas = line.Parent as Canvas;
            if (canvas == null || canvas.ActualWidth <= 2 || history.Count < 2) return;
            double width = canvas.ActualWidth - 2;
            double height = Math.Max(8, canvas.ActualHeight);
            double baseline = height - 4;
            double step = width / (history.Count - 1);
            maximum = Math.Max(1, maximum);
            var stroke = new StreamGeometry();
            var area = new StreamGeometry();
            using (var lineContext = stroke.Open())
            using (var fillContext = area.Open())
            {
                bool inSegment = false;
                double previousX = 0;
                for (int i = 0; i < history.Count; i++)
                {
                    if (double.IsNaN(history[i]) || double.IsInfinity(history[i]))
                    {
                        if (inSegment) fillContext.LineTo(new Point(previousX, baseline), true, false);
                        inSegment = false;
                        continue;
                    }
                    double x = 1 + i * step;
                    double y = baseline - Math.Max(0, Math.Min(maximum, history[i])) / maximum * (height - 8);
                    if (!inSegment)
                    {
                        lineContext.BeginFigure(new Point(x, y), false, false);
                        fillContext.BeginFigure(new Point(x, baseline), true, true);
                        inSegment = true;
                    }
                    else lineContext.LineTo(new Point(x, y), true, false);
                    fillContext.LineTo(new Point(x, y), true, false);
                    previousX = x;
                }
                if (inSegment) fillContext.LineTo(new Point(previousX, baseline), true, false);
            }
            stroke.Freeze(); area.Freeze(); line.Data = stroke; fill.Data = area;
        }
    }
}
