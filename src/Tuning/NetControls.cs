using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// The network sign, drawn rather than taken from the icon font: Segoe's Wi-Fi glyph is a corner piece that sits
    /// off-centre in a tile. Three arcs and a dot, centred on the point, <paramref name="size"/> across.
    /// </summary>
    static class NetIcon
    {
        public static void Draw(Graphics g, PointF c, float size, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Math.Max(1.6f, size * 0.1f);
            // The arcs' centre sits low, so the whole sign (dot to widest arc) is centred on c.
            float r = size * 0.62f;
            var o = new PointF(c.X, c.Y + r * 0.42f);
            using (var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                foreach (float k in new[] { 1f, 0.66f, 0.33f })
                {
                    float rr = r * k;
                    g.DrawArc(pen, o.X - rr, o.Y - rr, rr * 2, rr * 2, 225, 90);
                }
            float d = w * 1.5f;
            using (var b = new SolidBrush(color)) g.FillEllipse(b, o.X - d / 2, o.Y - d / 2, d, d);
            g.SmoothingMode = old;
        }
    }

    /// <summary>
    /// "Check network": a small card under the modes. Not a mode — it opens the network report; while a check
    /// runs a spinner turns on it, afterwards it says when it ran and how many issues it found.
    /// </summary>
    class NetCard : SkinControl
    {
        readonly Tweened hover, sel, enabledT, busyT;
        readonly object spinKey = new object();
        bool selected, busy;
        /// <summary>The second line: what the check covers, or how the last one went.</summary>
        public Func<string> Note = () => "";
        /// <summary>Issues the last check found (a warning pill), 0 = none, -1 = not checked.</summary>
        public int Issues = -1;

        public NetCard()
        {
            hover = new Tweened(Invalidate);
            sel = new Tweened(Invalidate);
            enabledT = new Tweened(Invalidate, 1f);
            busyT = new Tweened(Invalidate);
            Cursor = Cursors.Hand;
        }

        public bool Selected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; sel.To(value ? 1 : 0, value ? 260 : 220); } }
        }

        public bool Busy
        {
            get { return busy; }
            set
            {
                if (busy == value) return;
                busy = value;
                busyT.To(value ? 1 : 0, 240);
                if (value)
                    Anim.Frame(spinKey, () =>
                    {
                        if (IsDisposed || (!busy && busyT.Value <= 0.001f)) return false;
                        if (Anim.Shown(this)) Invalidate(new Rectangle(Width - S(44), 0, S(44), Height));
                        return true;
                    });
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Enabled) hover.To(1, 120); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0, 240); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            enabledT.To(Enabled ? 1 : 0, 200);
            if (!Enabled) hover.To(0, 120);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color pb = ParentBg;
            g.Clear(pb);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Value, s = sel.Value, en = 0.55f + 0.45f * enabledT.Value;

            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fill = Anim.Blend(Anim.Blend(p.Surface, p.Hover, h * 0.45f), p.Accent, s * (p.IsDark ? 0.1f : 0.035f));
            Theme.FillRoundFast(g, fill, r, S(10));
            // Its own outline, thicker and dark red: not a mode, so it does not blend in with the mode cards above it.
            // Selected, it lights up to the accent like they do.
            Color dark = p.IsDark ? Color.FromArgb(128, 26, 40) : Color.FromArgb(150, 30, 46);
            Color edge = Anim.Blend(dark, Anim.Blend(dark, p.Accent, 0.35f), h);
            float pw = Math.Max(2f, S(2));
            using (var pen = new Pen(Anim.Blend(edge, p.Accent, s), pw))
            using (var path = Theme.RoundRect(new RectangleF(pw / 2, pw / 2, Width - pw, Height - pw), S(10)))
                g.DrawPath(pen, path);

            int tile = S(40);
            var tr = new RectangleF(S(16), (Height - tile) / 2f, tile, tile);
            Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Accent, 0.12f + 0.16f * s + 0.05f * h), tr, S(11));
            // Wi-Fi waves: "network" at a glance.
            NetIcon.Draw(g, new PointF(tr.X + tile / 2f, tr.Y + tile / 2f), S(22),
                Anim.Blend(fill, Anim.Blend(p.TextDim, p.Accent, 0.35f + 0.65f * Math.Max(s, h * 0.5f)), en));

            int x = S(72), right = Width - S(44);
            string title = L.T("Проверить сеть", "Check network");
            int tw = MeasureWidth(title, Theme.UiBold);
            int ty = Height / 2 - S(20);
            TextDraw.DrawText(g, title, Theme.UiBold, new Rectangle(x, ty, right - x, S(20)), Anim.Blend(fill, p.Text, en), TextLeft);
            if (Issues > 0 && x + tw + S(10) < right - S(40))
            {
                string pill = L.Count(Issues, "замечание", "замечания", "замечаний", "issue", "issues");
                if (x + tw + S(10) + Pill.Width(pill) < right) Pill.Draw(g, pill, x + tw + S(10), ty + S(10), p.Warning, fill, en);
            }
            TextDraw.DrawText(g, Note(), Theme.SmallFont, new Rectangle(x, ty + S(23), right - x, S(16)), Anim.Blend(fill, p.TextDim, en), TextLeft);

            // Right: a chevron, or a spinner while the check runs (they cross-fade).
            float cx = Width - S(24), cy = Height / 2f, b = busyT.Value;
            if (b < 0.999f)
                using (var pen = new Pen(Anim.Blend(fill, Anim.Blend(p.TextDim, p.Accent, h), (1 - b) * en), Math.Max(1.5f, S(2) * 0.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float k = S(4), dx = h * S(2);
                    g.DrawLines(pen, new[] { new PointF(cx - k / 2 + dx, cy - k), new PointF(cx + k / 2 + dx, cy), new PointF(cx - k / 2 + dx, cy + k) });
                }
            if (b > 0.001f)
            {
                float rad = S(8);
                double ang = (Anim.FrameNow * 0.4) % 360;
                using (var track = new Pen(Anim.Blend(fill, p.Border, b), Math.Max(2f, S(2))))
                    g.DrawEllipse(track, cx - rad, cy - rad, rad * 2, rad * 2);
                using (var pen = new Pen(Anim.Blend(fill, p.Accent, b), Math.Max(2f, S(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, cx - rad, cy - rad, rad * 2, rad * 2, (float)ang, 100);
            }
        }
    }

    /// <summary>
    /// A quiet offer that slides up from the bottom after a mode was applied: "check the network too?". It goes
    /// away by itself after a while (not while the pointer is on it), or with ×.
    /// </summary>
    class NetOffer : SkinControl
    {
        readonly Tweened show, hoverGo, hoverClose;
        readonly Timer hideTimer = new Timer();
        int targetTop;
        bool hiding;
        public event EventHandler Accepted, Dismissed;

        public NetOffer()
        {
            show = new Tweened(Step);
            hoverGo = new Tweened(Invalidate);
            hoverClose = new Tweened(Invalidate);
            hideTimer.Interval = 20000;
            hideTimer.Tick += (s, e) => { if (!ClientRectangle.Contains(PointToClient(MousePosition))) Close(); };
            Visible = false;
        }

        public bool Showing { get { return Visible && !hiding; } }

        /// <summary>Slides up into place: its bottom edge at <paramref name="bottom"/>.</summary>
        public void Open(Rectangle area, int bottom)
        {
            Size = new Size(Math.Min(S(540), area.Width), S(64));
            Left = area.X + (area.Width - Width) / 2;
            targetTop = bottom - Height;
            var old = Region;
            using (var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), S(12)))
                Region = new Region(path);
            if (old != null) old.Dispose();
            hiding = false;
            if (!Visible) show.Set(0);
            Visible = true;
            BringToFront();
            show.To(1, 420);
            hideTimer.Stop();
            hideTimer.Start();
            Step();
        }

        public void Place(Rectangle area, int bottom)
        {
            if (!Visible) return;
            Width = Math.Min(S(540), area.Width);
            Left = area.X + (area.Width - Width) / 2;
            targetTop = bottom - Height;
            Step();
        }

        public void Close()
        {
            hideTimer.Stop();
            if (!Visible || hiding) return;
            hiding = true;
            show.To(0, 260, () => { Visible = false; hiding = false; });
        }

        void Step()
        {
            // Rises with a gentle settle; leaves the same way down.
            float t = Anim.EaseOut(show.Value);
            Top = targetTop + (int)Math.Round((1 - t) * S(28));
            Invalidate();
        }

        Rectangle GoRect
        {
            get
            {
                int w = MeasureWidth(L.T("Проверить", "Check"), Theme.UiBold) + S(32);
                return new Rectangle(Width - S(48) - w, (Height - S(32)) / 2, w, S(32));
            }
        }

        Rectangle CloseRect { get { return new Rectangle(Width - S(40), (Height - S(28)) / 2, S(28), S(28)); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool go = GoRect.Contains(e.Location), x = CloseRect.Contains(e.Location);
            hoverGo.To(go ? 1 : 0, go ? 110 : 200);
            hoverClose.To(x ? 1 : 0, x ? 110 : 200);
            Cursor = go || x ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverGo.To(0, 200);
            hoverClose.To(0, 200);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || hiding) return;
            if (GoRect.Contains(e.Location)) { Close(); if (Accepted != null) Accepted(this, EventArgs.Empty); }
            else if (CloseRect.Contains(e.Location)) { Close(); if (Dismissed != null) Dismissed(this, EventArgs.Empty); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            float a = show.Value;
            Color behind = ParentBg;
            // Lifted above the list: the alternate surface with an accent edge, faded in from what is behind.
            Color bg = Anim.Blend(behind, Anim.Blend(p.SurfaceAlt, p.Accent, p.IsDark ? 0.08f : 0.04f), a);
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(12)))
            using (var pen = new Pen(Anim.Blend(bg, Anim.Blend(p.Border, p.Accent, 0.45f), a)))
                g.DrawPath(pen, path);

            int tile = S(36);
            var tr = new RectangleF(S(14), (Height - tile) / 2f, tile, tile);
            Theme.FillRound(g, Anim.Blend(bg, Anim.Blend(p.Surface, p.Accent, 0.16f), a), tr, S(10));
            NetIcon.Draw(g, new PointF(tr.X + tile / 2f, tr.Y + tile / 2f), S(20), Anim.Blend(bg, p.Accent, a));

            var go = GoRect;
            int x = S(62), right = go.Left - S(12);
            TextDraw.DrawText(g, L.T("Проверим и сеть?", "Check the network too?"), Theme.UiBold, new Rectangle(x, Height / 2 - S(19), right - x, S(20)), Anim.Blend(bg, p.Text, a), TextLeft);
            TextDraw.DrawText(g, L.T("Задержка, Wi-Fi, DNS и скорость — около 30 секунд", "Delay, Wi-Fi, DNS and speed — about 30 seconds"), Theme.SmallFont,
                new Rectangle(x, Height / 2 + S(2), right - x, S(18)), Anim.Blend(bg, p.TextDim, a), TextLeft);

            float hg = hoverGo.Value;
            Theme.FillRound(g, Anim.Blend(bg, Anim.Blend(p.Accent, Color.White, hg * 0.12f), a), go, S(8));
            TextDraw.DrawText(g, L.T("Проверить", "Check"), Theme.UiBold, go, Anim.Blend(bg, p.AccentText, a), TextCenter);

            var cr = CloseRect;
            float hc = hoverClose.Value;
            if (hc > 0.01f) Theme.FillRound(g, Anim.Blend(bg, p.Hover, hc * a), cr, S(6));
            DrawGlyph(g, "\uE711", cr, Anim.Blend(bg, Anim.Blend(p.TextDim, p.Text, hc), a), Theme.GlyphSmall);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hideTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
