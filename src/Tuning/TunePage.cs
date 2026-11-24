using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// DriveUP: system tuning. Left: the smart pick and the three modes. Right: what the
    /// chosen mode changes on this computer (now → new, and why), or everything changed so far
    /// (was → now) with a Return button on each. Every change can be undone, one by one or all.
    /// </summary>
    class TunePage : Page
    {
        readonly Card smartCard = new Card(), listCard = new Card();
        // What the computer is (one dim line) and what suits it (up to two lines).
        readonly StatusText smartText = new StatusText { Flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis };
        readonly StatusText recText = new StatusText { Flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis };
        readonly FlatButton btnSmart, btnMeasure, btnApply, btnReturnAll;
        /// <summary>Measuring before / after: off by default, the Measure button shows only while it is on.</summary>
        readonly ToggleSwitch swMeasure = new ToggleSwitch("");
        readonly Segmented view;
        readonly TuneList list = new TuneList();
        readonly StatusText info = new StatusText();
        readonly List<ModeCard> cards = new List<ModeCard>();
        readonly ToolTip tip = new ToolTip();

        // ---------- "Check network" ----------
        readonly NetCard netCard = new NetCard();
        readonly NetOffer offer = new NetOffer();
        /// <summary>Two more minutes of quiet pinging (drops, Wi-Fi's periodic spikes).</summary>
        readonly ToggleSwitch swLong = new ToggleSwitch("");
        readonly FlatButton btnRecheck;
        /// <summary>The right side shows the network report, not a mode's plan.</summary>
        bool netView;
        /// <summary>The check running or last run (this session); its report and fix rows stay while other views show.</summary>
        NetCheck net;
        bool netRunning, netWanted;
        string netProgress = "";
        List<TuneRow> netRows = new List<TuneRow>();
        readonly Dictionary<NetFinding, TuneRow> netRowOf = new Dictionary<NetFinding, TuneRow>();
        /// <summary>Rows waiting to come in: several results at once arrive one after another (a wave, one per few frames).</summary>
        readonly Queue<TuneRow> netQueue = new Queue<TuneRow>();
        bool netPumping;

        Machine machine;
        TuneMode selected, recommended;
        /// <summary>The mode the plan rows on screen were built for (Apply uses this one), and whether a plan is being read.</summary>
        TuneMode rowsMode;
        bool planLoading;
        string reason = "";
        bool loaded, working, showChanged;
        int loadSeq;

        public TunePage(IHost host) : base(host)
        {
            smartCard.TitleBase = 46;
            btnSmart = new FlatButton("", "\uE945", ButtonKind.Primary);
            btnSmart.SetBaseHeight(Theme.ControlH);
            btnSmart.Click += (s, e) => SmartPick();
            smartText.Font = Theme.SmallFont;
            smartCard.Controls.Add(smartText);
            smartCard.Controls.Add(recText);
            smartCard.Controls.Add(btnSmart);
            btnMeasure = new FlatButton("", "\uE9D9");
            btnMeasure.SetBaseHeight(Theme.ControlH);
            btnMeasure.Click += (s, e) => MeasureNow();
            btnMeasure.Visible = Settings.TuneMeasure;
            smartCard.Controls.Add(btnMeasure);
            swMeasure.Checked = Settings.TuneMeasure;
            swMeasure.CheckedChanged += (s, e) => MeasureSwitched();
            smartCard.Controls.Add(swMeasure);
            smartCard.Resize += (s, e) => LayoutSmart();
            Controls.Add(smartCard);

            foreach (var m in Tuner.Modes)
            {
                var c = new ModeCard(m);
                c.Click += (s, e) => { if (!working) Select(((ModeCard)s).Mode, true); };
                cards.Add(c);
                Controls.Add(c);
            }

            view = new Segmented(new[] { "", "" }, new[] { "\uE8FD", "\uE81C" }, 0);
            view.SelectedIndexChanged += (s, e) => ShowView(view.SelectedIndex == 1);
            listCard.TitleBase = 56;
            list.CheckedChanged += (s, e) => UpdateInfo();
            list.ReturnClicked += row => ReturnRows(new List<TuneRow> { row });
            btnApply = new FlatButton("", "\uE73E", ButtonKind.Primary);
            btnApply.SetBaseHeight(40);
            btnApply.Font = Theme.UiBold;
            btnApply.Click += (s, e) => Apply();
            btnReturnAll = new FlatButton("", "\uE7A7");
            btnReturnAll.SetBaseHeight(40);
            btnReturnAll.Visible = false;
            btnReturnAll.Click += (s, e) => ReturnRows(list.Rows.Where(r => r.Kind == TuneKind.Changed && r.Ready).ToList());
            list.ReturnGroup += rows => ReturnRows(rows);
            foreach (Control c in new Control[] { view, list, info, btnApply, btnReturnAll }) listCard.Controls.Add(c);
            listCard.Resize += (s, e) => LayoutList();
            Controls.Add(listCard);

            netCard.Click += (s, e) => { if (!working) ShowNet(true); };
            netCard.Note = NetNote;
            netCard.Issues = Settings.NetIssues;
            Controls.Add(netCard);
            btnRecheck = new FlatButton("", "\uE72C");
            btnRecheck.SetBaseHeight(40);
            btnRecheck.Visible = false;
            btnRecheck.Click += (s, e) => StartNet();
            listCard.Controls.Add(btnRecheck);
            swLong.Checked = Settings.NetLong;
            swLong.Visible = false;
            swLong.CheckedChanged += (s, e) => { lock (Settings.Sync) Settings.NetLong = swLong.Checked; Settings.Save(); };
            listCard.Controls.Add(swLong);
            list.ActionClicked += row => { if (row.Finding != null && row.Finding.Id == "driver") Host.OpenTab(0); };
            offer.Accepted += (s, e) => { if (!working) ShowNet(true); };
            Controls.Add(offer);

            Resize += (s, e) => LayoutPage();
        }

        /// <summary>Settings are being changed or put back (the window does not close meanwhile).</summary>
        public bool Working { get { return working; } }

        // ---------- layout ----------

        void LayoutPage()
        {
            int pad = S(16);
            int leftW = Math.Max(S(320), Math.Min(S(500), (int)(Width * 0.36)));
            int smartH = S(158);
            smartCard.SetBounds(pad, pad, leftW, smartH);
            int gap = S(10), netH = S(72);
            // The modes share what is left after the network card under them (they never get taller than 118).
            int avail = Height - pad * 2 - smartH - S(14) - netH - gap;
            int cardH = Math.Max(S(72), Math.Min(S(118), (avail - gap * (cards.Count - 1)) / Math.Max(1, cards.Count)));
            int y = smartCard.Bottom + S(14);
            foreach (var c in cards)
            {
                c.SetBounds(pad, y, leftW, cardH);
                y += cardH + gap;
            }
            netCard.SetBounds(pad, y, leftW, netH);
            int x = pad + leftW + S(20);
            listCard.SetBounds(x, pad, Math.Max(S(300), Width - x - pad), Height - pad * 2);
            offer.Place(OfferArea, OfferBottom);
            LayoutSmart();
            LayoutList();
        }

        void LayoutSmart()
        {
            int x = S(18);
            btnSmart.Width = btnSmart.PreferredWidth;
            btnSmart.Location = new Point(x, smartCard.Height - btnSmart.Height - S(14));
            btnMeasure.Width = btnMeasure.PreferredWidth;
            btnMeasure.Location = new Point(btnSmart.Right + S(8), btnSmart.Top);
            btnMeasure.Visible = Settings.TuneMeasure && btnMeasure.Right <= smartCard.Width - S(12);
            swMeasure.Location = new Point(smartCard.Width - swMeasure.Width - S(14), Math.Max(0, (smartCard.TitleHeight - swMeasure.Height) / 2));
            smartText.SetBounds(x, smartCard.TitleHeight - S(4), smartCard.Width - x * 2, S(18));
            recText.ForeOverride = Theme.Current.Text;
            recText.SetBounds(x, smartText.Bottom + S(6), smartCard.Width - x * 2, Math.Max(S(20), btnSmart.Top - smartText.Bottom - S(12)));
        }

        void LayoutList()
        {
            view.Location = new Point(S(14), (listCard.TitleHeight - view.Height) / 2);
            int footH = S(64);
            int fy = listCard.Height - footH;
            foreach (var b in new[] { btnApply, btnReturnAll })
            {
                b.Width = b.PreferredWidth + S(8);
                b.Location = new Point(listCard.Width - b.Width - S(16), fy + (footH - b.Height) / 2);
            }
            if (btnRecheck.Visible)
            {
                btnRecheck.Width = btnRecheck.PreferredWidth + S(8);
                btnRecheck.Location = new Point(btnApply.Left - btnRecheck.Width - S(8), btnApply.Top);
            }
            swLong.Location = new Point(listCard.Width - swLong.Width - S(14), Math.Max(0, (listCard.TitleHeight - swLong.Height) / 2));
            var right = btnApply.Visible ? (btnRecheck.Visible ? btnRecheck : btnApply) : btnReturnAll;
            info.SetBounds(S(20), fy, Math.Max(S(60), right.Left - S(32)), footH);
            list.SetBounds(S(2), listCard.TitleHeight, listCard.Width - S(4), fy - listCard.TitleHeight);
            listCard.Invalidate();
        }

        public override void ApplyScale()
        {
            LayoutPage();
        }

        // ---------- texts ----------

        public override void ApplyLanguage()
        {
            smartCard.Title = L.T("Умный подбор", "Smart pick");
            SetButton(btnSmart, L.T("Подобрать для этого ПК", "Pick for this PC"));
            SetButton(btnMeasure, L.T("Замер", "Measure"));
            tip.SetToolTip(btnMeasure, L.T("Процессы, память, фоновая загрузка, службы, автозагрузка и время загрузки Windows — сейчас и до применения режима",
                "Processes, memory, background load, services, startup and Windows boot time — now and before a mode was applied"));
            swMeasure.Text = L.T("Замер до/после", "Measure before/after");
            tip.SetToolTip(swMeasure, L.T("Включите, чтобы сравнить систему до и после оптимизации:\nперед первым применением режима DriveUP замерит процессы, память, службы и автозагрузку (≈ 2 с),\nа кнопка «Замер» покажет, что изменилось. По умолчанию выключено.",
                "Turn on to compare the system before and after tuning:\nbefore a mode is first applied DriveUP measures processes, memory, services and startup (≈ 2 s),\nand the Measure button shows what changed. Off by default."));
            tip.SetToolTip(btnSmart, L.T("DriveUP смотрит на компьютер — ноутбук или ПК, батарея, видеокарта, версия Windows —\nи выбирает режим и значения, которые ему подходят",
                "DriveUP looks at the computer — laptop or desktop, battery, graphics card, Windows version —\nand picks the mode and values that suit it"));
            SetButton(btnReturnAll, L.T("Вернуть всё", "Return all"));
            SetButton(btnRecheck, L.T("Проверить снова", "Check again"));
            swLong.Text = L.T("Долгий замер", "Long check");
            tip.SetToolTip(swLong, L.T("Ещё 2 минуты следить за задержкой: видны редкие обрывы и всплески,\nкоторые Wi-Fi даёт раз в 20–60 секунд при фоновом поиске сетей",
                "Watch the delay for 2 more minutes: shows rare drops and the spikes\nWi-Fi makes every 20–60 seconds while scanning for networks"));
            tip.SetToolTip(netCard, L.T("Задержка до роутера и до интернета, сигнал Wi-Fi, DNS, скорость под нагрузкой,\nIPv6 и сетевые настройки Windows — с понятными выводами и исправлениями.\nОптимизация режимов сеть не трогает.",
                "Delay to the router and to the internet, Wi-Fi signal, DNS, speed under load,\nIPv6 and Windows network settings — with plain conclusions and fixes.\nThe tuning modes never touch the network."));
            UpdateNetButtons();
            netCard.Invalidate();
            offer.Invalidate();
            list.EmptyGlyph = "\uE73E";
            UpdateViewTexts();
            UpdateSmart();
            UpdateApplyText();
            UpdateInfo();
            UpdateEmpty();
            LayoutPage();
            foreach (var c in cards) c.Invalidate();
            list.Invalidate();
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            recText.ForeOverride = Theme.Current.Text;
            foreach (var c in cards) c.Invalidate();
            list.Invalidate();
        }

        void UpdateViewTexts()
        {
            int n = Tuner.ChangedCount;
            view.SetTexts(new[]
            {
                netView ? L.T("Проверка сети", "Network check") : L.T("Что изменится", "What changes"),
                L.T("Изменено", "Changed") + (n > 0 ? " · " + n : ""),
            });
            LayoutList();
        }

        void UpdateSmart()
        {
            if (machine == null)
            {
                smartText.SetText(L.T("Смотрю на компьютер…", "Looking at the computer…"));
                recText.SetText("");
                return;
            }
            smartText.SetText(machine.Summary());
            tip.SetToolTip(smartText, machine.Summary().Replace(" · ", "\n"));
            var mode = Tuner.Recommend(machine, out reason);
            recText.SetText(L.T("Рекомендуем «", "Suggested: ") + mode.Name() + L.T("» — ", " — ") + reason);
        }

        void UpdateApplyText()
        {
            btnApply.Glyph = "\uE73E";
            if (selected == null) SetButton(btnApply, L.T("Применить", "Apply"));
            else
            {
                // The mode's name in the other language too (its pair is known): the button keeps its width.
                string name = selected.Name(), other = L.Other(name) ?? name;
                SetButton(btnApply, L.T("Применить «", "Apply ") + name + L.T("»", ""));
                btnApply.AltText = L.En ? "Применить «" + other + "»" : "Apply " + other;
                btnApply.Width = btnApply.PreferredWidth;
            }
            LayoutList();
        }

        void UpdateEmpty()
        {
            list.EmptyGlyph = netView && !showChanged ? "\uE701" : "\uE73E";
            if (netView && !showChanged)
            {
                list.EmptyText = netRunning || netWanted ? "" : L.T("Около 30 секунд: задержка до роутера и интернета, Wi-Fi, DNS, скорость под нагрузкой и сетевые настройки.\nНичего не меняется, пока вы сами не отметите исправления.",
                    "About 30 seconds: delay to the router and the internet, Wi-Fi, DNS, speed under load and network settings.\nNothing changes until you tick fixes yourself.");
                list.Invalidate();
                return;
            }
            list.EmptyText = showChanged
                ? L.T("Пока ничего не изменено.\nВыберите режим и нажмите «Применить» — здесь появится каждое изменение с кнопкой «Вернуть».",
                      "Nothing changed yet.\nPick a mode and click Apply — every change shows up here with its own Return button.")
                : L.T("Режиму нечего менять на этом компьютере", "This mode has nothing to change on this computer");
            list.Invalidate();
        }

        void UpdateInfo()
        {
            if (netView && !showChanged) { UpdateNetButtons(); return; }
            if (!loaded) { info.SetText(""); return; }
            var rows = list.Rows;
            if (showChanged)
            {
                int n = rows.Count(r => r.Kind == TuneKind.Changed);
                info.SetText(n == 0 ? "" : L.Count(n, "параметр изменён", "параметра изменено", "параметров изменено", "setting changed", "settings changed") +
                    L.T(" · каждый можно вернуть отдельно", " · each one can be returned on its own"));
                btnReturnAll.Enabled = !working && rows.Any(r => r.Kind == TuneKind.Changed && r.Ready);
                return;
            }
            int change = rows.Count(r => r.Kind == TuneKind.Change && r.Checked && r.Ready);
            int back = rows.Count(r => r.Kind == TuneKind.Return && r.Checked && r.Ready);
            int reboot = rows.Count(r => r.Actionable && r.Checked && r.Tweak.NeedsReboot);
            int same = rows.Count(r => r.Kind == TuneKind.Same);
            var parts = new List<string>();
            if (change > 0) parts.Add(L.T("изменится: ", "changes: ") + change);
            if (back > 0) parts.Add(L.T("вернётся: ", "goes back: ") + back);
            if (reboot > 0) parts.Add(L.T("после перезагрузки: ", "after a restart: ") + reboot);
            var stopping = rows.Where(r => r.Actionable && r.Checked && r.Procs > 0).ToList();
            if (stopping.Count > 0)
                parts.Add(L.T("уйдёт из фона: ", "leaves the background: ") + L.Count(stopping.Sum(r => r.Procs), "процесс", "процесса", "процессов", "process", "processes") +
                          " · " + L.Size(stopping.Sum(r => r.ProcMemory)));
            if (parts.Count == 0 && same > 0) parts.Add(L.T("всё уже настроено так", "everything is already set like this"));
            else if (same > 0) parts.Add(L.T("уже так: ", "already set: ") + same);
            info.SetText(string.Join(" · ", parts));
            btnApply.Enabled = !working && !planLoading && change + back > 0;
        }

        public override string Status
        {
            get
            {
                if (netRunning) return L.T("Проверяю сеть…", "Checking the network…");
                if (!loaded) return L.T("Смотрю настройки Windows…", "Reading Windows settings…");
                var mode = Tuner.Mode(Settings.TuneMode);
                int n = Tuner.ChangedCount;
                if (n == 0) return L.T("Настройки Windows не менялись — всё как было до DriveUP", "Windows settings untouched — all as it was before DriveUP");
                return (mode != null ? L.T("Режим «", "Mode: ") + mode.Name() + L.T("» · ", " · ") : "") +
                       L.Count(n, "параметр изменён", "параметра изменено", "параметров изменено", "setting changed", "settings changed");
            }
        }

        // ---------- loading ----------

        protected override void FirstActivate()
        {
            list.Loading = true;
            UpdateSmart();
            Reload(true);
        }

        public override void RefreshData()
        {
            if (!working) Reload(false);
        }

        /// <summary>Reads the computer and the settings in the background, then shows them.</summary>
        void Reload(bool first)
        {
            int seq = ++loadSeq;
            bool changedView = showChanged;
            var mode = selected;
            Host.BeginBusy();
            new Thread(() =>
            {
                Machine m = null;
                TuneMode rec = null;
                List<TuneRow> rows = null;
                try
                {
                    m = machine ?? Machine.Read();
                    string why;
                    rec = Tuner.Recommend(m, out why);
                    if (mode == null) mode = Tuner.Mode(Settings.TuneMode) ?? rec;
                    rows = changedView ? Tuner.Changed() : Tuner.Plan(mode, m);
                }
                catch { rows = rows ?? new List<TuneRow>(); }
                Post(() =>
                {
                    Host.EndBusy();
                    if (seq != loadSeq) return;
                    machine = m;
                    recommended = rec;
                    loaded = true;
                    if (selected == null) SetSelected(mode);
                    foreach (var c in cards)
                    {
                        c.Recommended = c.Mode == recommended;
                        c.Active = c.Mode.Id == Settings.TuneMode && Tuner.ChangedCount > 0;
                    }
                    UpdateSmart();
                    UpdateViewTexts();
                    UpdateEmpty();
                    if (!changedView) rowsMode = mode;
                    // The network report stays on screen (the plan is still read, for when a mode is picked).
                    bool report = netView && !changedView;
                    if (!report)
                    {
                        if (first) list.SetRows(rows, true);
                        else Transition.CrossFade(list, () => list.SetRows(rows, false), 200);
                    }
                    UpdateInfo();
                    Host.UpdateStatus();
                    if (netWanted && net == null) StartNet();
                    // Another mode was picked while this was being read: show that one.
                    if (!changedView && !report && selected != null && selected != mode) Select(selected, false, true);
                });
            }) { IsBackground = true, Name = "Tune read" }.Start();
        }

        void SetSelected(TuneMode m)
        {
            selected = m;
            foreach (var c in cards) c.Selected = c.Mode == m && !netView;
            UpdateApplyText();
        }

        /// <summary>Picks a mode: its plan slides in from the side the card is on.</summary>
        void Select(TuneMode m, bool user, bool force = false)
        {
            if (m == null) return;
            if (m == selected && m == rowsMode && !force && !showChanged && !netView) return;
            int from = rowsMode != null ? Tuner.Modes.IndexOf(rowsMode) : 0, to = Tuner.Modes.IndexOf(m);
            offer.Close();
            if (netView)
            {
                // Back from the network report (its card sits under the modes: the plan comes from below).
                netView = false;
                netWanted = false;
                netCard.Selected = false;
                from = Tuner.Modes.Count;
                swLong.Visible = false;
                btnRecheck.Visible = false;
                UpdateViewTexts();
                LayoutList();
            }
            SetSelected(m);
            if (showChanged)
            {
                view.SelectedIndex = 0;   // ShowView reloads the plan
                return;
            }
            if (!loaded || machine == null) return;
            int seq = ++loadSeq;
            var mc = machine;
            planLoading = true;
            UpdateInfo();
            new Thread(() =>
            {
                List<TuneRow> rows;
                try { rows = Tuner.Plan(m, mc); }
                catch { rows = new List<TuneRow>(); }
                Post(() =>
                {
                    if (seq != loadSeq) return;
                    planLoading = false;
                    // The network report was opened meanwhile: it stays.
                    if (netView) return;
                    rowsMode = m;
                    UpdateEmpty();
                    Transition.CrossFade(list, () => list.SetRows(rows, false), 240, Math.Sign(to - from) * S(28));
                    UpdateInfo();
                });
            }) { IsBackground = true, Name = "Tune plan" }.Start();
        }

        bool measuring;

        void MeasureSwitched()
        {
            lock (Settings.Sync) Settings.TuneMeasure = swMeasure.Checked;
            Settings.Save();
            LayoutSmart();
            if (!swMeasure.Checked) return;
            if (btnMeasure.Visible) btnMeasure.Appear();
            bool fresh;
            lock (Settings.Sync) fresh = string.IsNullOrEmpty(Settings.TuneBaseline);
            Host.ShowToast(fresh && Tuner.ChangedCount == 0
                ? L.T("Замер включён: перед применением режима DriveUP запомнит, как было", "Measuring is on: DriveUP takes a \"before\" ahead of applying a mode")
                : L.T("Замер включён: кнопка «Замер» сравнит с запомненным «до»", "Measuring is on: the Measure button compares with the stored \"before\""), ToastKind.Info);
        }

        /// <summary>Measures the system now and compares with the "before" taken ahead of the first change.</summary>
        void MeasureNow()
        {
            if (measuring) return;
            measuring = true;
            btnMeasure.Enabled = false;
            Host.BeginBusy();
            new Thread(() =>
            {
                Measure now = null;
                try { now = Measure.Take(); } catch { }
                Post(() =>
                {
                    measuring = false;
                    btnMeasure.Enabled = true;
                    Host.EndBusy();
                    if (now == null) return;
                    Measure before;
                    lock (Settings.Sync) before = Measure.Parse(Settings.TuneBaseline);
                    string text = (before != null
                        ? L.T("До (", "Before (") + L.When(before.When) + L.T(") → сейчас:", ") → now:")
                        : L.T("Сейчас (замера «до» ещё нет — он делается сам перед первым применением режима):", "Now (no \"before\" yet — it is taken by itself before a mode is first applied):"))
                        + "\n\n" + Measure.Compare(before, now) + "\n\n" +
                        L.T("Время загрузки меняется после перезагрузки. Запомнить текущее состояние как «до»?", "Boot time changes after a restart. Keep the current state as \"before\"?");
                    if (ThemedDialog.Confirm(Host.Window, L.T("Замер системы", "System measurement"), text, L.T("Запомнить как «до»", "Keep as \"before\""), "\uE74E", false))
                    {
                        lock (Settings.Sync) Settings.TuneBaseline = now.ToString();
                        Settings.Save();
                    }
                });
            }) { IsBackground = true, Name = "measure" }.Start();
        }

        void SmartPick()
        {
            if (working || recommended == null) return;
            // Point at the suggested card, then show its plan.
            var card = cards.FirstOrDefault(c => c.Mode == recommended);
            if (selected == recommended && !showChanged)
            {
                Host.ShowToast(L.T("Режим «", "") + recommended.Name() + L.T("» уже выбран — проверьте список и нажмите «Применить»", " is already picked — check the list and click Apply"), ToastKind.Info);
                return;
            }
            Select(recommended, true);
            if (card != null) card.Focus();
        }

        void ShowView(bool changed)
        {
            if (showChanged == changed && loaded) return;
            showChanged = changed;
            btnApply.Visible = !changed;
            btnReturnAll.Visible = changed;
            if (changed) btnReturnAll.Appear(); else btnApply.Appear();
            swLong.Visible = netView && !changed;
            UpdateNetButtons();
            LayoutList();
            UpdateEmpty();
            if (loaded)
            {
                if (!changed && netView) ShowNetRows(true);
                else Reload(false);
            }
        }

        // ---------- applying ----------

        /// <summary>Why a setting could not be changed, in words: "access denied" while elevated means Windows itself guards it.</summary>
        static string ErrorText(Exception ex)
        {
            if (!(ex is UnauthorizedAccessException || ex is System.Security.SecurityException)) return ex.Message;
            return SafePath.Elevated
                ? L.T("Windows не даёт программам менять этот параметр", "Windows does not let programs change this setting")
                : L.T("Нет доступа — нужны права администратора", "Access denied — administrator rights needed");
        }

        void SetWorking(bool on)
        {
            working = on;
            list.Locked = on;
            btnSmart.Enabled = !on;
            swMeasure.Enabled = !on;
            view.Enabled = !on;
            foreach (var c in cards) c.Enabled = !on;
            netCard.Enabled = !on;
            swLong.Enabled = !on;
            btnRecheck.Enabled = !on;
            if (on) { btnApply.Enabled = false; btnReturnAll.Enabled = false; offer.Close(); }
            if (on) Host.BeginBusy(); else Host.EndBusy();
        }

        void Apply()
        {
            if (working || showChanged) return;
            if (netView)
            {
                // The same button stops a running check, starts one, checks again, or applies the ticked fixes.
                if (netRunning) { net.Cancel = true; btnApply.Enabled = false; return; }
                if (!list.Rows.Any(r => r.Actionable && r.Checked && r.Ready)) { StartNet(); return; }
            }
            else if (planLoading || rowsMode == null) return;
            bool forNet = netView;
            var todo = list.Rows.Where(r => r.Actionable && r.Checked && r.Ready).ToList();
            if (todo.Count == 0) return;
            if (!Host.CanStartOperation()) return;
            // Settings with a catch go only with the user's explicit yes, each one named with what it costs.
            var risky = todo.Where(r => r.Kind == TuneKind.Change && r.Risky).ToList();
            if (risky.Count > 0 && !ThemedDialog.Confirm(Host.Window, L.T("Подтвердите рискованные пункты", "Confirm the risky settings"),
                    L.T("Вы отметили пункты, у которых есть последствия:", "You ticked settings that have a catch:") + "\n\n" +
                    string.Join("\n", risky.Select(r => "• " + r.Tweak.Name() + " — " + r.Warn())) + "\n\n" +
                    L.T("Каждый можно вернуть во вкладке «Изменено». Применить их вместе с остальными?",
                        "Each can be returned under Changed. Apply them along with the rest?"),
                    L.T("Применить", "Apply"), "\uE73E", true))
                return;
            var mode = forNet ? NetFix.Mode : rowsMode;
            SetWorking(true);
            new Thread(() =>
            {
                int done = 0, failed = 0;
                bool reboot = false;
                var names = new List<string>();
                // The "before" measurement, once, ahead of the first change — only when the user turned measuring on.
                bool noBaseline;
                lock (Settings.Sync) noBaseline = Settings.TuneMeasure && string.IsNullOrEmpty(Settings.TuneBaseline);
                if (noBaseline && Tuner.ChangedCount == 0 && !forNet)
                {
                    Post(() => info.SetText(L.T("Замеряю систему «до»…", "Measuring the system \"before\"…")));
                    try
                    {
                        var before = Measure.Take();
                        lock (Settings.Sync) Settings.TuneBaseline = before.ToString();
                        Settings.Save();
                    }
                    catch { }
                }
                // A System Restore point first: if Windows misbehaves after the changes, it can be rolled back
                // even from the recovery screen. Protection off: offered to turn on; otherwise the user decides.
                if (Settings.RestorePoint && todo.Any(r => r.Kind == TuneKind.Change))
                {
                    Post(() => info.SetText(L.T("Создаю точку восстановления…", "Creating a restore point…")));
                    bool ok;
                    string desc = "DriveUP: " + mode.Name();
                    string why = SysRestore.Create(desc, out ok);
                    if (!SysRestore.Resolve(AskUI, Host.Window, desc, ref why, ref ok))
                    {
                        Post(() =>
                        {
                            SetWorking(false);
                            UpdateInfo();
                            Host.ShowToast(L.T("Отменено — ничего не изменено", "Cancelled — nothing changed"), ToastKind.Info);
                        });
                        return;
                    }
                    if (ok && why != null) Post(() => Host.ShowToast(why, ToastKind.Info));
                    else if (!ok && why != null && !SysRestore.Unsupported)
                        Post(() => Host.ShowToast(L.T("Без точки восстановления: ", "No restore point: ") + why, ToastKind.Info));
                }
                // Signed-out users' settings stay loaded for the whole run, not once per setting.
                using (AllUsers.Mount())
                    foreach (var row in Tuner.Order(todo))
                    {
                        var r = row;
                        Post(() => list.SetState(r, TuneState.Working));
                        Thread.Sleep(70);   // long enough to see each one being done
                        try
                        {
                            if (r.Kind == TuneKind.Return) Tuner.Return(r.Tweak);
                            else Tuner.Change(r.Tweak, r.Target, mode);
                            done++;
                            names.Add(r.Tweak.Name());
                            if (r.Tweak.NeedsReboot) reboot = true;
                            Post(() => list.SetState(r, TuneState.Done));
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            string msg = ErrorText(ex);
                            Post(() => list.SetState(r, TuneState.Failed, msg));
                        }
                    }
                RestartAdapters();
                if (done > 0 && !forNet) Tuner.SetMode(mode.Id);
                Journal.Add(JournalKind.Tune, done, failed, mode.Name() + ": " + string.Join(", ", names));
                Thread.Sleep(250);
                Post(() => Applied(mode, done, failed, reboot));
            }) { IsBackground = true, Name = "Tune apply" }.Start();
        }

        void Applied(TuneMode mode, int done, int failed, bool reboot)
        {
            SetWorking(false);
            if (mode == NetFix.Mode)
            {
                UpdateViewTexts();
                UpdateInfo();
                Host.UpdateStatus();
                if (failed == 0)
                    Host.ShowToast(L.T("Исправлено: ", "Fixed: ") + done + L.T(". Проверьте сеть снова — увидите разницу", ". Check the network again to see the difference"), ToastKind.Success);
                else
                    Host.ShowToast(L.T("Не удалось исправить: ", "Could not fix: ") + failed + L.T(" — причина в списке", " — see the list for why"), ToastKind.Error);
                if (reboot) AskRestart();
                return;
            }
            foreach (var c in cards) c.Active = c.Mode.Id == Settings.TuneMode && Tuner.ChangedCount > 0;
            UpdateViewTexts();
            UpdateInfo();
            Host.UpdateStatus();
            if (failed == 0)
                Host.ShowToast(L.T("Режим «", "") + mode.Name() + L.T("» включён. Что изменено — во вкладке «Изменено»", " mode is on. See what changed under Changed"), ToastKind.Success);
            else
                Host.ShowToast(L.T("Не удалось изменить: ", "Could not change: ") + failed + L.T(" — причина в списке", " — see the list for why"), ToastKind.Error);
            // After a mode, a quiet offer to check the network too (not when it was checked within a day).
            DateTime last;
            lock (Settings.Sync) last = Settings.NetChecked;
            if (failed == 0 && done > 0 && !netRunning && (DateTime.Now - last).TotalHours > 24)
                Anim.Delay(1600, () => { if (!IsDisposed && !working && !netView && Visible) offer.Open(OfferArea, OfferBottom); });
            // Show what was changed, each with its Return button (failures stay in view to be read).
            if (failed == 0 && done > 0)
                Anim.Delay(900, () =>
                {
                    if (IsDisposed) return;
                    if (!working && !showChanged) view.SelectedIndex = 1;
                    if (reboot) AskRestart();
                });
            else if (reboot) AskRestart();
        }

        void AskRestart()
        {
            if (ThemedDialog.Confirm(Host.Window, L.T("Нужна перезагрузка", "Restart needed"),
                    L.T("Часть настроек заработает только после перезагрузки компьютера.\nПерезагрузить сейчас? Сохраните открытые файлы.",
                        "Some settings take effect only after the computer restarts.\nRestart now? Save your open files first."),
                    L.T("Перезагрузить", "Restart"), "\uE777", false))
                UserShell.RestartPc();
        }

        /// <summary>Puts settings back as they were: each row turns, gets its tick and folds away.</summary>
        void ReturnRows(List<TuneRow> rows)
        {
            if (working || rows.Count == 0) return;
            if (!Host.CanStartOperation()) return;
            SetWorking(true);
            new Thread(() =>
            {
                int done = 0, failed = 0;
                bool reboot = false;
                var names = new List<string>();
                using (AllUsers.Mount())
                foreach (var row in Tuner.Order(rows))
                {
                    var r = row;
                    Post(() => list.SetState(r, TuneState.Working));
                    Thread.Sleep(70);
                    try
                    {
                        Tuner.Return(r.Tweak);
                        done++;
                        names.Add(r.Tweak.Name());
                        if (r.Tweak.NeedsReboot) reboot = true;
                        Post(() =>
                        {
                            list.SetState(r, TuneState.Done);
                            Anim.Delay(520, () => list.Remove(r, () => { UpdateInfo(); UpdateEmpty(); }));
                        });
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        string msg = ErrorText(ex);
                        Post(() => list.SetState(r, TuneState.Failed, msg));
                    }
                }
                RestartAdapters();
                Journal.Add(JournalKind.TuneReturn, done, failed, string.Join(", ", names));
                Thread.Sleep(300);
                Post(() =>
                {
                    SetWorking(false);
                    foreach (var c in cards) c.Active = c.Mode.Id == Settings.TuneMode && Tuner.ChangedCount > 0;
                    UpdateViewTexts();
                    UpdateInfo();
                    Host.UpdateStatus();
                    if (failed == 0)
                        Host.ShowToast(done == 1 ? L.T("Возвращено как было: ", "Returned: ") + names[0]
                                                 : L.T("Возвращено как было: ", "Returned: ") + L.Count(done, "параметр", "параметра", "параметров", "setting", "settings"), ToastKind.Success);
                    else
                        Host.ShowToast(L.T("Не удалось вернуть: ", "Could not return: ") + failed, ToastKind.Error);
                    if (reboot)
                        Host.ShowToast(L.T("Часть настроек вернётся после перезагрузки", "Some settings go back after a restart"), ToastKind.Info);
                });
            }) { IsBackground = true, Name = "Tune return" }.Start();
        }
    
        // ---------- "Check network" ----------

        /// <summary>Where the offer slides up: across the list, just above its footer.</summary>
        Rectangle OfferArea { get { return new Rectangle(listCard.Left + S(20), 0, Math.Max(S(200), listCard.Width - S(40)), 0); } }
        int OfferBottom { get { return listCard.Bottom - S(64) - S(10); } }

        /// <summary>The network card's second line: what it checks, how far it is, or how the last check went.</summary>
        string NetNote()
        {
            if (netRunning) return netProgress.Length > 0 ? netProgress : L.T("Проверяю…", "Checking…");
            DateTime when;
            int issues;
            lock (Settings.Sync) { when = Settings.NetChecked; issues = Settings.NetIssues; }
            if (when == DateTime.MinValue) return L.T("Задержка, Wi-Fi, DNS, скорость", "Delay, Wi-Fi, DNS, speed");
            return L.T("Проверено ", "Checked ") + L.When(when) + (issues == 0 ? L.T(" · всё в порядке", " · all fine") : "");
        }

        /// <summary>Opens the network report (from the card or the offer); starts a check if none ran yet.</summary>
        void ShowNet(bool start)
        {
            if (working) return;
            offer.Close();
            // About to start: no "press start" text flashing in the list meanwhile.
            if (start && net == null) netWanted = true;
            if (!netView)
            {
                netView = true;
                planLoading = false;
                foreach (var c in cards) c.Selected = false;
                netCard.Selected = true;
                UpdateViewTexts();
                if (showChanged) view.SelectedIndex = 0;   // ShowView brings the report in
                else
                {
                    swLong.Visible = true;
                    UpdateEmpty();
                    ShowNetRows(true);
                }
            }
            if (start && !netRunning && net == null) StartNet();
            UpdateNetButtons();
        }

        void ShowNetRows(bool slide)
        {
            netQueue.Clear();
            var rows = netRows.ToList();
            Transition.CrossFade(list, () => list.SetRows(rows, false), 240, slide ? S(28) : 0);
            UpdateEmpty();
            UpdateNetButtons();
        }

        /// <summary>A row at the end of its group in the kept report (a new group gets its title first).</summary>
        static void InsertGrouped(List<TuneRow> rows, TuneRow row)
        {
            int at = rows.FindLastIndex(r => r.Category == row.Category);
            if (at < 0)
            {
                rows.Add(new TuneRow { Kind = TuneKind.Header, Category = row.Category, Checked = false });
                at = rows.Count - 1;
            }
            rows.Insert(at + 1, row);
        }

        /// <summary>Runs the check in the background: each result appears as it is measured.</summary>
        void StartNet()
        {
            if (netRunning || working) return;
            // The computer is still being read: the check starts as soon as it is.
            if (machine == null) { netWanted = true; return; }
            netWanted = false;
            offer.Close();
            if (!netView) ShowNet(false);
            var check = new NetCheck(machine) { Long = Settings.NetLong };
            net = check;
            netRows = new List<TuneRow>();
            netRowOf.Clear();
            netQueue.Clear();
            netRunning = true;
            netProgress = "";
            netCard.Busy = true;
            Host.BeginBusy();
            Host.UpdateStatus();
            if (netView && !showChanged)
            {
                if (list.Rows.Count > 0) Transition.CrossFade(list, () => list.SetRows(new List<TuneRow>(), false), 200);
                else list.SetRows(new List<TuneRow>(), false);
            }
            UpdateEmpty();
            UpdateNetButtons();
            check.Added = f => Post(() => NetAdded(check, f));
            check.Updated = f => Post(() => NetUpdated(check, f));
            check.Progress = t => Post(() =>
            {
                if (check != net) return;
                netProgress = t;
                netCard.Invalidate();
                UpdateNetButtons();
            });
            new Thread(() =>
            {
                List<TuneRow> fixes = null;
                try { check.Run(); }
                catch (Exception ex) { Journal.Add(JournalKind.Tune, 0, 1, L.T("Проверка сети: ", "Network check: ") + ex.Message); }
                if (!check.Cancel)
                    try
                    {
                        if (Tuner.Pc == null) Tuner.Pc = check.Pc;
                        using (AllUsers.Mount()) fixes = Tuner.RowsFor(NetFix.Steps(check));
                    }
                    catch { }
                Post(() => NetDone(check, fixes));
            }) { IsBackground = true, Name = "Net check" }.Start();
        }

        void NetAdded(NetCheck check, NetFinding f)
        {
            if (check != net) return;
            // Always measuring at first: the result that follows draws its mark in.
            var row = new TuneRow { Kind = TuneKind.Info, Category = f.Group, Finding = f, Checked = false, State = TuneState.Working };
            netRowOf[f] = row;
            InsertGrouped(netRows, row);
            if (netView && !showChanged)
            {
                netQueue.Enqueue(row);
                PumpNet();
            }
        }

        void PumpNet()
        {
            if (netPumping) return;
            if (netQueue.Count == 0 || IsDisposed) return;
            var row = netQueue.Dequeue();
            if (netView && !showChanged && netRows.Contains(row) && !list.Rows.Contains(row))
            {
                list.Append(row);
                // Its result came while it waited: the mark draws in once the row is there.
                if (row.Finding.Level != NetLevel.Pending) Anim.Delay(140, () => list.Refresh(row));
            }
            netPumping = true;
            Anim.Delay(55, () => { netPumping = false; PumpNet(); });
        }

        void NetUpdated(NetCheck check, NetFinding f)
        {
            if (check != net) return;
            TuneRow row;
            if (!netRowOf.TryGetValue(f, out row)) return;
            if (netView && !showChanged && list.Rows.Contains(row)) list.Refresh(row);
            else if (netQueue.Contains(row)) { }   // refreshed when it comes in
            else if (f.Level != NetLevel.Pending) row.State = TuneState.Done;
        }

        void NetDone(NetCheck check, List<TuneRow> fixes)
        {
            if (check != net) return;
            netRunning = false;
            netCard.Busy = false;
            Host.EndBusy();
            // Stopped: whatever was still being measured says so.
            foreach (var f in check.Findings.ToList())
                if (f.Level == NetLevel.Pending)
                {
                    f.Level = NetLevel.Info;
                    f.Value = () => L.T("не измерено — проверка остановлена", "not measured — the check was stopped");
                    f.Detail = () => "";
                    NetUpdated(check, f);
                }
            int issues = check.Findings.Count(f => f.Problem);
            var change = (fixes ?? new List<TuneRow>()).Where(r => r.Kind == TuneKind.Change).ToList();
            if (change.Count > 0)
            {
                var head = new TuneRow { Kind = TuneKind.Header, Category = TuneCat.NetFixes, Checked = false };
                netRows.Add(head);
                netRows.AddRange(change);
                if (netView && !showChanged)
                {
                    // After the report rows still coming in, then one by one.
                    int wait = 55 * (netQueue.Count + 1);
                    Anim.Delay(wait, () => { if (net == check && !list.Rows.Contains(head) && netView && !showChanged) list.AppendRow(head); });
                    for (int i = 0; i < change.Count; i++)
                    {
                        var r = change[i];
                        Anim.Delay(wait + 70 * (i + 1), () => { if (net == check && list.Rows.Contains(head) && !list.Rows.Contains(r) && netView && !showChanged) list.AppendRow(r); });
                    }
                    // The fixes are what to look at now.
                    Anim.Delay(wait + 70 * change.Count + 120, () => { if (net == check && list.Rows.Contains(head)) list.ScrollToRow(head); });
                }
            }
            if (!check.Cancel)
            {
                lock (Settings.Sync)
                {
                    Settings.NetChecked = DateTime.Now;
                    Settings.NetIssues = issues;
                }
                Settings.Save();
                netCard.Issues = issues;
                Host.ShowToast(issues == 0
                    ? L.T("Сеть в порядке — проблем не найдено", "The network is fine — no problems found")
                    : L.T("Проверка сети: ", "Network check: ") + L.Count(issues, "замечание", "замечания", "замечаний", "issue", "issues") +
                      (change.Count > 0 ? L.T(", исправить можно: ", ", fixable: ") + change.Count : L.T(", советы — в строках", ", advice is in the rows")),
                    issues == 0 ? ToastKind.Success : ToastKind.Info);
            }
            netCard.Invalidate();
            Host.UpdateStatus();
            UpdateEmpty();
            UpdateNetButtons();
            Anim.Delay(55 * (netQueue.Count + 1) + 70 * change.Count + 40, UpdateNetButtons);
        }

        /// <summary>The footer while the report shows: the one button stops, starts, fixes or checks again; the line says where it is.</summary>
        void UpdateNetButtons()
        {
            if (!netView || showChanged)
            {
                if (btnRecheck.Visible) { btnRecheck.Visible = false; LayoutList(); }
                return;
            }
            var rows = list.Rows;
            int ready = rows.Count(r => r.Actionable && r.Ready);
            int ticked = rows.Count(r => r.Actionable && r.Checked && r.Ready);
            string text;
            if (netRunning) { text = L.T("Остановить", "Stop"); btnApply.Glyph = "\uE71A"; }
            else if (net == null) { text = L.T("Начать проверку", "Start the check"); btnApply.Glyph = "\uE768"; }
            else if (ready > 0) { text = L.T("Исправить", "Fix") + (ticked > 0 ? " · " + ticked : ""); btnApply.Glyph = "\uE90F"; }
            else { text = L.T("Проверить снова", "Check again"); btnApply.Glyph = "\uE72C"; }
            if (btnApply.Text != text) SetButton(btnApply, text);
            btnApply.Enabled = !working && (netRunning ? !net.Cancel : (ready == 0 || ticked > 0));
            bool recheck = !netRunning && net != null && ready > 0;
            if (btnRecheck.Visible != recheck)
            {
                btnRecheck.Visible = recheck;
                if (recheck) btnRecheck.Appear();
            }
            string t;
            if (netRunning) t = netProgress;
            else if (net == null) t = "";
            else
            {
                int issues = net.Findings.Count(f => f.Problem);
                t = (net.Cancel ? L.T("Проверка остановлена · ", "Check stopped · ") : "") +
                    (issues == 0 ? L.T("Проблем не найдено", "No problems found") : L.Count(issues, "замечание", "замечания", "замечаний", "issue", "issues")) +
                    (ready > 0 ? L.T(" · исправить можно: ", " · can be fixed: ") + ready : issues > 0 ? L.T(" · что делать — в строках", " · what to do is in the rows") : "");
            }
            info.SetText(t);
            LayoutList();
        }

        /// <summary>Adapters whose driver settings changed are restarted once, after all the changes (worker thread).</summary>
        void RestartAdapters()
        {
            if (!NicRestart.Pending) return;
            Post(() => info.SetText(L.T("Перезапускаю сетевую карту — связь пропадёт на несколько секунд…", "Restarting the network card — the connection drops for a few seconds…")));
            if (NicRestart.Flush().Count > 0)
                Post(() => Host.ShowToast(L.T("Сетевую карту не удалось перезапустить — изменения заработают после перезагрузки", "Could not restart the network card — the changes apply after a restart"), ToastKind.Info));
        }
    }
}
