using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// The card that rises next to a row the pointer rests on: the row's text in full, in the app's colours, with a
    /// coloured edge for how it looks. The pointer passes through it to the list underneath.
    /// </summary>
    class HoverCard : SkinControl
    {
        readonly Tweened show;
        string title = "", hint = "";
        readonly List<Tuple<string, Color, Font>> lines = new List<Tuple<string, Color, Font>>();
        Color edge;
        int targetTop;
        bool hiding;

        public HoverCard()
        {
            show = new Tweened(Step);
            Visible = false;
        }

        protected override void WndProc(ref Message m)
        {
            // WM_NCHITTEST → HTTRANSPARENT: the list under the card keeps the pointer (no hover flicker).
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; }
            base.WndProc(ref m);
        }

        int Pad { get { return S(14); } }

        static int TextHeight(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextDraw.MeasureText(text, font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
        }

        /// <summary>
        /// Shows the card for a row drawn at <paramref name="row"/> (the parent's coordinates), below it, or above when
        /// there is no room below inside <paramref name="area"/>.
        /// </summary>
        public void Open(string title, IEnumerable<Tuple<string, Color, Font>> content, string hint, Color edge, Rectangle row, Rectangle area)
        {
            this.title = title ?? "";
            this.hint = hint ?? "";
            this.edge = edge;
            lines.Clear();
            lines.AddRange(content.Where(l => !string.IsNullOrEmpty(l.Item1)));
            // As wide as the longest line needs (within limits): a short note gets a small card.
            int natural = MeasureWidth(this.title, Theme.UiBold);
            foreach (var l in lines)
                foreach (var part in l.Item1.Split('\n')) natural = Math.Max(natural, MeasureWidth(part, l.Item3));
            if (this.hint.Length > 0) natural = Math.Max(natural, Math.Min(MeasureWidth(this.hint, Theme.SmallFont), S(360)));
            int w = Math.Min(Math.Min(S(460), area.Width - S(24)), Math.Max(S(260), natural + Pad * 2 + S(8)));
            int inner = w - Pad * 2 - S(4);
            int h = Pad + TextHeight(this.title, Theme.UiBold, inner);
            foreach (var l in lines) h += S(6) + TextHeight(l.Item1, l.Item3, inner);
            if (this.hint.Length > 0) h += S(10) + TextHeight(this.hint, Theme.SmallFont, inner);
            h += Pad;
            int x = Math.Max(area.X + S(12), Math.Min(row.X + S(88), area.Right - w - S(12)));
            int below = row.Bottom + S(2), above = row.Top - h - S(2);
            int y = below + h <= area.Bottom - S(6) || above < area.Top ? below : above;
            y = Math.Max(area.Top + S(4), Math.Min(y, area.Bottom - h - S(4)));
            bool moved = Visible && !hiding;
            Bounds = new Rectangle(x, y, w, h);
            targetTop = y;
            var old = Region;
            using (var path = Theme.RoundRect(new RectangleF(0, 0, w, h), S(10))) Region = new Region(path);
            if (old != null) old.Dispose();
            hiding = false;
            if (!moved) show.Set(0);
            Visible = true;
            BringToFront();
            show.To(1, moved ? 90 : 160);
            Step();
            Invalidate();
        }

        public void Close()
        {
            if (!Visible || hiding) return;
            hiding = true;
            show.To(0, 110, () => { if (hiding) { Visible = false; hiding = false; } });
        }

        void Step()
        {
            float t = Anim.EaseOut(show.Value);
            if (Visible) Top = targetTop + (int)Math.Round((1 - t) * S(6));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            float a = show.Value;
            Color behind = ParentBg;
            Color bg = Anim.Blend(behind, Anim.Blend(p.SurfaceAlt, p.Accent, p.IsDark ? 0.05f : 0.025f), a);
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10)))
            using (var pen = new Pen(Anim.Blend(bg, Anim.Blend(p.Border, edge, 0.55f), a)))
                g.DrawPath(pen, path);
            // How the row looks: a bar on the left edge.
            Theme.FillRound(g, Anim.Blend(bg, edge, a), new RectangleF(S(5), Pad, S(3), Height - Pad * 2), S(2));
            int x = Pad + S(4), w = Width - x - Pad, y = Pad;
            var wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int th = TextHeight(title, Theme.UiBold, w);
            TextDraw.DrawText(g, title, Theme.UiBold, new Rectangle(x, y, w, th), Anim.Blend(bg, p.Text, a), wrap);
            y += th;
            foreach (var l in lines)
            {
                int lh = TextHeight(l.Item1, l.Item3, w);
                y += S(6);
                TextDraw.DrawText(g, l.Item1, l.Item3, new Rectangle(x, y, w, lh), Anim.Blend(bg, l.Item2, a), wrap);
                y += lh;
            }
            if (hint.Length > 0)
            {
                y += S(10);
                int hh = TextHeight(hint, Theme.SmallFont, w);
                TextDraw.DrawText(g, hint, Theme.SmallFont, new Rectangle(x, y, w, hh), Anim.Blend(bg, Anim.Blend(p.TextDim, p.Accent, 0.55f), a), wrap);
            }
        }
    }

    /// <summary>
    /// A network report row opened in full over the list: what was found, what it means, what to do — each way out with
    /// its command to copy or its page to open, and the ones DriveUP does itself marked — and how it was measured.
    /// </summary>
    class NetDetails : SkinControl
    {
        enum Kind { Section, Text, Small, Step }

        class Block
        {
            public Kind Kind;
            public string Title = "", Text = "", Command, Url;
            public bool Auto;
            public int Number;
            /// <summary>Measured heights of a step's title and text (painting does not measure again).</summary>
            public int TitleH, TextH;
            /// <summary>Content coordinates (before scrolling); the copy / open buttons inside.</summary>
            public Rectangle R, Copy, Open, Code;
        }

        NetFinding finding;
        readonly List<Block> blocks = new List<Block>();
        float scroll, scrollTarget, contentH;
        readonly object scrollKey = new object();
        readonly Dictionary<string, Tweened> hovers = new Dictionary<string, Tweened>();
        string hoverKey, copiedKey;
        int laidW = -1;
        static Font mono;
        static float monoScale;

        public event EventHandler BackClicked;

        public NetDetails()
        {
            SetStyle(ControlStyles.Selectable, true);
            Visible = false;
        }

        public NetFinding Finding { get { return finding; } }

        static Font Mono
        {
            get
            {
                if (mono == null || monoScale != Theme.Scale)
                {
                    if (mono != null) mono.Dispose();
                    mono = new Font("Consolas", Theme.SmallFont.Size * 1.04f);
                    monoScale = Theme.Scale;
                }
                return mono;
            }
        }

        int HeadH { get { return S(84); } }
        int Margin { get { return S(28); } }

        public void ShowFinding(NetFinding f)
        {
            finding = f;
            scroll = scrollTarget = 0;
            Anim.Cancel(scrollKey);
            copiedKey = null;
            laidW = -1;
            Build();
            Invalidate();
        }

        Tweened Hover(string key)
        {
            Tweened t;
            if (!hovers.TryGetValue(key, out t)) hovers[key] = t = new Tweened(Invalidate);
            return t;
        }

        static int TextHeight(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return 0;
            return TextDraw.MeasureText(text, font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
        }

        void Build()
        {
            blocks.Clear();
            var f = finding;
            if (f == null) return;
            string detail = f.Detail();
            if (detail.Length > 0) blocks.Add(new Block { Kind = Kind.Text, Text = detail });
            if (f.More != null)
            {
                blocks.Add(new Block { Kind = Kind.Section, Title = L.T("Что это значит", "What it means") });
                blocks.Add(new Block { Kind = Kind.Text, Text = f.More() });
            }
            if (f.Advice.Count > 0)
            {
                blocks.Add(new Block { Kind = Kind.Section, Title = f.Problem ? L.T("Что делать", "What to do") : L.T("Полезно знать", "Good to know") });
                int n = 0;
                foreach (var a in f.Advice)
                    blocks.Add(new Block { Kind = Kind.Step, Title = a.Title, Text = a.Text, Command = a.Command, Url = a.Url, Auto = a.Auto, Number = ++n });
            }
            if (f.How != null)
            {
                blocks.Add(new Block { Kind = Kind.Section, Title = L.T("Как измерено и нормы", "How it was measured, and the norms") });
                blocks.Add(new Block { Kind = Kind.Small, Text = f.How() });
            }
            LayoutBlocks();
        }

        void LayoutBlocks()
        {
            int w = Width - Margin * 2 - S(10);
            if (w <= S(80)) return;
            laidW = Width;
            int y = S(4);
            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case Kind.Section:
                        y += S(18);
                        b.R = new Rectangle(Margin, y, w, S(22));
                        y += S(22) + S(6);
                        break;
                    case Kind.Text:
                    case Kind.Small:
                    {
                        var font = b.Kind == Kind.Small ? Theme.SmallFont : Font;
                        int h = TextHeight(b.Text, font, w);
                        b.R = new Rectangle(Margin, y, w, h);
                        y += h + S(4);
                        break;
                    }
                    case Kind.Step:
                    {
                        int x = Margin + S(38), tw = w - S(38);
                        int top = y;
                        int th = TextHeight(b.Title, Theme.UiBold, tw - (b.Auto ? Pill.Width("DriveUP") + S(10) : 0));
                        int bh = TextHeight(b.Text, Font, tw);
                        b.TitleH = th;
                        b.TextH = bh;
                        int yy = y + th + S(4) + bh;
                        b.Code = b.Copy = b.Open = Rectangle.Empty;
                        if (b.Command != null)
                        {
                            int bw = MeasureWidth(L.T("Скопировано", "Copied"), Font) + S(40);
                            int cw = tw - bw - S(8);
                            int ch = Math.Max(S(32), TextHeight(b.Command, Mono, cw - S(20)) + S(16));
                            yy += S(8);
                            b.Code = new Rectangle(x, yy, cw, ch);
                            b.Copy = new Rectangle(x + cw + S(8), yy + (ch - S(32)) / 2, bw, S(32));
                            yy += ch;
                        }
                        if (b.Url != null)
                        {
                            yy += S(8);
                            int ow = MeasureWidth(L.T("Открыть", "Open"), Font) + S(44);
                            b.Open = new Rectangle(x, yy, ow, S(32));
                            yy += S(32);
                        }
                        b.R = new Rectangle(Margin, top, w, yy - top);
                        y = yy + S(16);
                        break;
                    }
                }
            }
            contentH = y + S(16);
        }

        float MaxScroll { get { return Math.Max(0, contentH - (Height - HeadH)); } }

        void ScrollTo(float target)
        {
            scrollTarget = Math.Max(0, Math.Min(MaxScroll, target));
            Anim.Follow(scrollKey, () => scroll, v => { scroll = v; Invalidate(); }, () => scrollTarget);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (finding != null && Width != laidW) { LayoutBlocks(); scrollTarget = Math.Min(scrollTarget, MaxScroll); scroll = Math.Min(scroll, MaxScroll); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(scrollTarget - e.Delta / 120f * S(90));
        }

        Rectangle BackRect { get { return new Rectangle(S(16), S(20), S(40), S(40)); } }

        /// <summary>A control under the point: "back", or a block's copy / open button (by key).</summary>
        string HitKey(Point pt)
        {
            if (BackRect.Contains(pt)) return "back";
            if (pt.Y < HeadH) return null;
            var c = new Point(pt.X, pt.Y - HeadH + (int)Math.Round(scroll));
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (!b.Copy.IsEmpty && (b.Copy.Contains(c) || b.Code.Contains(c))) return "copy" + i;
                if (!b.Open.IsEmpty && b.Open.Contains(c)) return "open" + i;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string k = HitKey(e.Location);
            if (k == hoverKey) return;
            if (hoverKey != null) Hover(hoverKey).To(0, 200);
            if (k != null) Hover(k).To(1, 110);
            hoverKey = k;
            Cursor = k != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverKey != null) Hover(hoverKey).To(0, 200);
            hoverKey = null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            string k = HitKey(e.Location);
            if (k == null) return;
            if (k == "back") { if (BackClicked != null) BackClicked(this, EventArgs.Empty); return; }
            int i;
            if (k.StartsWith("copy") && int.TryParse(k.Substring(4), out i))
            {
                try { Clipboard.SetText(blocks[i].Command); } catch { return; }
                copiedKey = k;
                Invalidate();
                Anim.Delay(1600, () => { if (copiedKey == k) { copiedKey = null; Invalidate(); } });
            }
            else if (k.StartsWith("open") && int.TryParse(k.Substring(4), out i))
            {
                string url = blocks[i].Url;
                // Only web pages and Windows' own settings pages, with the user's normal rights.
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
                    try { UserShell.OpenUrl(url); } catch { }
            }
        }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Escape || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back) { if (BackClicked != null) BackClicked(this, EventArgs.Empty); }
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.PageDown) ScrollTo(scrollTarget + (e.KeyCode == Keys.Down ? S(60) : Height - HeadH - S(40)));
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.PageUp) ScrollTo(scrollTarget - (e.KeyCode == Keys.Up ? S(60) : Height - HeadH - S(40)));
        }

        static Color LevelColor(NetLevel l)
        {
            var p = Theme.Current;
            return l == NetLevel.Bad ? p.Danger : l == NetLevel.Warn ? p.Warning : l == NetLevel.Good ? p.Success : p.TextDim;
        }

        public static string LevelText(NetLevel l)
        {
            switch (l)
            {
                case NetLevel.Bad: return L.T("проблема", "problem");
                case NetLevel.Warn: return L.T("замечание", "issue");
                case NetLevel.Good: return L.T("в порядке", "fine");
                default: return L.T("для сведения", "for information");
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color bg = ParentBg;
            g.Clear(bg);
            var f = finding;
            if (f == null) return;
            if (laidW != Width) LayoutBlocks();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color tone = LevelColor(f.Level);

            // ---- the scrolling content, under the header ----
            var state = g.Save();
            g.SetClip(new Rectangle(0, HeadH, Width, Height - HeadH));
            int oy = HeadH - (int)Math.Round(scroll);
            var wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                var r = new Rectangle(b.R.X, b.R.Y + oy, b.R.Width, b.R.Height);
                if (r.Bottom < HeadH - S(20) || r.Top > Height) continue;
                switch (b.Kind)
                {
                    case Kind.Section:
                        TextDraw.DrawText(g, b.Title.ToUpperInvariant(), Theme.SmallFont, r, Anim.Blend(p.TextDim, p.Accent, 0.55f), TextLeft);
                        using (var pen = new Pen(p.Border))
                            g.DrawLine(pen, r.X + MeasureWidth(b.Title.ToUpperInvariant(), Theme.SmallFont) + S(12), r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
                        break;
                    case Kind.Text:
                        TextDraw.DrawText(g, b.Text, Font, r, p.Text, wrap);
                        break;
                    case Kind.Small:
                        TextDraw.DrawText(g, b.Text, Theme.SmallFont, r, p.TextDim, wrap);
                        break;
                    case Kind.Step:
                        PaintStep(g, b, i, oy, bg);
                        break;
                }
            }
            g.Restore(state);

            // A scroll bar like the list's.
            float view = Height - HeadH;
            if (contentH > view + 1)
            {
                int th = Math.Max(S(32), (int)(view * view / contentH));
                int ty = HeadH + (int)((view - th) * (scroll / Math.Max(1, contentH - view)));
                Theme.FillRound(g, p.Border, new RectangleF(Width - S(6), ty, S(4), th), S(2));
            }

            // ---- the header: back, the mark, the title and the value ----
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, Width, HeadH);
            if (scroll > 1)
                using (var pen = new Pen(Anim.Blend(bg, p.Border, Math.Min(1, scroll / S(20)))))
                    g.DrawLine(pen, 0, HeadH - 1, Width, HeadH - 1);
            var back = BackRect;
            float hb = Hover("back").Value;
            Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Hover, hb), back, S(20));
            DrawGlyph(g, "\uE72B", back, Anim.Blend(p.TextDim, p.Text, hb), Theme.GlyphSmall);

            int tile = S(40);
            var tr = new RectangleF(back.Right + S(14), back.Y, tile, tile);
            Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, f.Problem ? tone : p.Accent, 0.14f), tr, S(10));
            GlyphShape.Draw(g, f.Glyph, new PointF(tr.X + tile / 2f, tr.Y + tile / 2f), S(19), f.Problem ? tone : Anim.Blend(p.TextDim, p.Accent, 0.6f), Math.Max(0.8f, S(1) * 0.95f));

            int x = (int)tr.Right + S(14), right = Width - Margin;
            string title = f.Title();
            int tw = Math.Min(right - x, MeasureWidth(title, Theme.HeadFont));
            TextDraw.DrawText(g, title, Theme.HeadFont, new Rectangle(x, back.Y - S(2), right - x, S(24)), p.Text, TextLeft);
            string lvl = LevelText(f.Level);
            if (x + tw + S(12) + Pill.Width(lvl) < right)
                Pill.Draw(g, lvl, x + tw + S(12), back.Y + S(10), f.Level == NetLevel.Info ? p.TextDim : tone, bg, 1);
            TextDraw.DrawText(g, f.Value(), Font, new Rectangle(x, back.Y + S(24), right - x, S(20)), f.Problem ? Anim.Blend(p.Text, tone, 0.75f) : p.TextDim, TextLeft);
            g.SmoothingMode = SmoothingMode.None;
        }

        void PaintStep(Graphics g, Block b, int index, int oy, Color bg)
        {
            var p = Theme.Current;
            var r = new Rectangle(b.R.X, b.R.Y + oy, b.R.Width, b.R.Height);
            var wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            // The number in a circle; a tick in an accent circle for what DriveUP does itself.
            var dot = new RectangleF(r.X, r.Y - S(1), S(26), S(26));
            if (b.Auto)
            {
                using (var br = new SolidBrush(Anim.Blend(bg, p.Accent, 0.9f))) g.FillEllipse(br, dot);
                using (var pen = new Pen(p.AccentText, Math.Max(1.6f, S(2) * 0.9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float cx = dot.X + dot.Width / 2, cy = dot.Y + dot.Height / 2, k = Theme.Scale;
                    g.DrawLines(pen, new[] { new PointF(cx - 4.5f * k, cy + 0.3f * k), new PointF(cx - 1.3f * k, cy + 3.4f * k), new PointF(cx + 4.8f * k, cy - 3.4f * k) });
                }
            }
            else
            {
                using (var br = new SolidBrush(Anim.Blend(bg, p.SurfaceAlt, 1f))) g.FillEllipse(br, dot);
                using (var pen = new Pen(p.Border)) g.DrawEllipse(pen, dot);
                TextDraw.DrawText(g, b.Number.ToString(), Theme.SmallFont, Rectangle.Round(dot), p.TextDim, TextCenter);
            }
            int x = r.X + S(38), tw = r.Width - S(38);
            int pillW = b.Auto ? Pill.Width("DriveUP") + S(10) : 0;
            int th = b.TitleH;
            TextDraw.DrawText(g, b.Title, Theme.UiBold, new Rectangle(x, r.Y, tw - pillW, th), p.Text, wrap);
            if (b.Auto)
            {
                int lineW = Math.Min(tw - pillW, MeasureWidth(b.Title, Theme.UiBold));
                if (th <= S(24)) Pill.Draw(g, "DriveUP", x + lineW + S(10), r.Y + th / 2f, p.Accent, bg, 1);
            }
            int bh = b.TextH;
            TextDraw.DrawText(g, b.Text, Font, new Rectangle(x, r.Y + th + S(4), tw, bh), p.TextDim, wrap);

            if (!b.Code.IsEmpty)
            {
                var code = new Rectangle(b.Code.X, b.Code.Y + oy, b.Code.Width, b.Code.Height);
                float hc = Hover("copy" + index).Value;
                Theme.FillRound(g, Anim.Blend(Anim.Blend(bg, p.SurfaceAlt, 0.9f), p.Hover, hc * 0.35f), code, S(8));
                using (var path = Theme.RoundRect(new RectangleF(code.X + 0.5f, code.Y + 0.5f, code.Width - 1, code.Height - 1), S(8)))
                using (var pen = new Pen(Anim.Blend(p.Border, p.Accent, hc * 0.4f))) g.DrawPath(pen, path);
                TextDraw.DrawText(g, b.Command, Mono, new Rectangle(code.X + S(10), code.Y + S(8), code.Width - S(20), code.Height - S(16)), Anim.Blend(p.Text, p.Accent, 0.15f), wrap);

                var cb = new Rectangle(b.Copy.X, b.Copy.Y + oy, b.Copy.Width, b.Copy.Height);
                bool copied = copiedKey == "copy" + index;
                Theme.FillRound(g, copied ? Anim.Blend(bg, p.Success, 0.2f) : Anim.Blend(p.SurfaceAlt, p.Hover, hc), cb, S(6));
                using (var path = Theme.RoundRect(new RectangleF(cb.X + 0.5f, cb.Y + 0.5f, cb.Width - 1, cb.Height - 1), S(6)))
                using (var pen = new Pen(copied ? Anim.Blend(bg, p.Success, 0.6f) : Anim.Blend(p.Border, p.Hover, hc))) g.DrawPath(pen, path);
                int gx = cb.X + S(12);
                if (Theme.HasGlyphs)
                {
                    DrawGlyph(g, copied ? "\uE73E" : "\uE8C8", new Rectangle(gx, cb.Y, S(16), cb.Height), copied ? p.Success : Anim.Blend(p.TextDim, p.Accent, hc), Theme.GlyphSmall);
                    gx += S(22);
                }
                TextDraw.DrawText(g, copied ? L.T("Скопировано", "Copied") : L.T("Копировать", "Copy"), Font, new Rectangle(gx, cb.Y, cb.Right - gx - S(6), cb.Height), copied ? p.Success : p.Text, TextLeft);
            }
            if (!b.Open.IsEmpty)
            {
                var ob = new Rectangle(b.Open.X, b.Open.Y + oy, b.Open.Width, b.Open.Height);
                float ho = Hover("open" + index).Value;
                Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Hover, ho), ob, S(6));
                using (var path = Theme.RoundRect(new RectangleF(ob.X + 0.5f, ob.Y + 0.5f, ob.Width - 1, ob.Height - 1), S(6)))
                using (var pen = new Pen(Anim.Blend(p.Border, p.Accent, ho * 0.6f))) g.DrawPath(pen, path);
                int gx = ob.X + S(12);
                if (Theme.HasGlyphs)
                {
                    DrawGlyph(g, "\uE8A7", new Rectangle(gx, ob.Y, S(16), ob.Height), Anim.Blend(p.TextDim, p.Accent, 0.4f + 0.6f * ho), Theme.GlyphSmall);
                    gx += S(24);
                }
                TextDraw.DrawText(g, L.T("Открыть", "Open"), Font, new Rectangle(gx, ob.Y, ob.Right - gx - S(6), ob.Height), p.Text, TextLeft);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Anim.Cancel(scrollKey);
            base.Dispose(disposing);
        }
    }
}
