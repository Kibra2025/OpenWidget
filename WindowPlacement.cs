using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DeskHardwareWidget
{
    internal static class WindowPlacement
    {
        public static Point Clamp(Rect window, IList<Rect> areas)
        {
            if (areas.Count == 0) return new Point(window.X, window.Y);
            Rect best = areas.OrderByDescending(area => IntersectionArea(window, area))
                .ThenBy(area => Distance(window, area)).First();
            return new Point(Math.Max(best.Left, Math.Min(window.Left, best.Right - window.Width)),
                Math.Max(best.Top, Math.Min(window.Top, best.Bottom - window.Height)));
        }
        private static double IntersectionArea(Rect first, Rect second)
        {
            first.Intersect(second);
            return first.IsEmpty ? 0 : first.Width * first.Height;
        }
        private static double Distance(Rect first, Rect second)
        {
            double x = first.X + first.Width / 2 - second.X - second.Width / 2;
            double y = first.Y + first.Height / 2 - second.Y - second.Height / 2;
            return x * x + y * y;
        }
    }
}
