using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MiniFan
{
    public class MainForm : Form
    {
        private readonly Config _cfg;
        private readonly EcBridge _ec;
        private readonly FanController _ctl;
        private readonly Updater _upd;

        // Palette
        public static readonly Color ColBack = Color.FromArgb(18, 20, 28);
        public static readonly Color ColCard = Color.FromArgb(28, 31, 42);
        public static readonly Color ColCard2 = Color.FromArgb(36, 40, 54);
        public static readonly Color ColText = Color.FromArgb(230, 233, 240);
        public static readonly Color ColMuted = Color.FromArgb(130, 138, 156);
        public static readonly Color ColAccent = Color.FromArgb(79, 195, 247);
        public static readonly Color ColHot = Color.FromArgb(255, 112, 82);
        public static readonly Color ColOk = Color.FromArgb(84, 214, 156);

        private NotifyIcon _tray;
        private Icon _icoIdle, _icoBoost, _icoHot;
        private TempCard _cpuCard, _gpuCard;
        private Label _statusPill;
        private ModeButton _btnAuto, _btnBoost, _btnSilent;
        private Stepper _stepCpu, _stepGpu;
        private TextBox _gamesBox;
        private ToggleRow _gameToggle, _startupToggle;
        private Label _footer;
        private Timer _logicTimer, _uiTimer;
        private bool _allowVisible;

        public MainForm(Config cfg, EcBridge ec, FanController ctl, Updater upd, bool startVisible)
        {
            _cfg = cfg; _ec = ec; _ctl = ctl; _upd = upd;
            _allowVisible = startVisible;
            BuildWindow();
            BuildControls();
            BuildTray();

            _logicTimer = new Timer { Interval = _cfg.PollSeconds * 1000 };
            _logicTimer.Tick += delegate { _ctl.Tick(); };
            _logicTimer.Start();
            _uiTimer = new Timer { Interval = 1000 };
            _uiTimer.Tick += delegate { if (Visible) RefreshUi(); };
            _uiTimer.Start();
            _ctl.Updated += delegate { RefreshTray(); if (Visible) RefreshUi(); };
            _upd.Changed += delegate
            {
                try { BeginInvoke((Action)RefreshUi); } catch { }
            };
            // L'updateur appelle Environment.Exit(0) juste après avoir levé cet événement pour
            // lancer l'installation : sans ça, l'icône de la zone de notification restait
            // visible ("fantôme") jusqu'à ce que l'utilisateur passe la souris dessus.
            _upd.BeforeExit += delegate
            {
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
            };

            _ctl.Tick();
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(_allowVisible && value);
            _allowVisible = true;
        }

        // ---------------- Fenêtre ----------------

        private void BuildWindow()
        {
            Text = "Mini Fan";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(340, 508);
            BackColor = ColBack;
            ShowInTaskbar = false;
            TopMost = true;
            Font = new Font("Segoe UI", 9f);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var path = Rounded(new Rectangle(0, 0, Width, Height), 14))
                Region = new Region(path);
        }

        public static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern int SendMessage(IntPtr h, int msg, int wp, int lp);

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 0x2, 0);
            }
        }

        // ---------------- Contrôles ----------------

        private void BuildControls()
        {
            // En-tête
            var header = new Panel { Bounds = new Rectangle(0, 0, 340, 52), BackColor = ColBack };
            header.MouseDown += DragWindow;
            var title = new Label
            {
                Text = "MINI FAN",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = ColText,
                Location = new Point(18, 12),
                AutoSize = true
            };
            title.MouseDown += DragWindow;
            var close = MakeGlyph("✕", 340 - 40, 10);
            close.Click += delegate { Hide(); };
            header.Controls.Add(title);
            header.Controls.Add(close);
            Controls.Add(header);

            // Cartes de température
            _cpuCard = new TempCard("CPU") { Bounds = new Rectangle(16, 56, 150, 108) };
            _gpuCard = new TempCard("GPU") { Bounds = new Rectangle(174, 56, 150, 108) };
            Controls.Add(_cpuCard);
            Controls.Add(_gpuCard);

            // Pastille d'état
            _statusPill = new Label
            {
                Bounds = new Rectangle(16, 174, 308, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = ColMuted,
                BackColor = ColCard
            };
            Controls.Add(_statusPill);

            // Boutons de mode
            var lblMode = MakeSection("MODE", 214);
            _btnAuto = new ModeButton("Auto") { Bounds = new Rectangle(16, 234, 100, 34) };
            _btnBoost = new ModeButton("Boost") { Bounds = new Rectangle(120, 234, 100, 34) };
            _btnSilent = new ModeButton("Repos") { Bounds = new Rectangle(224, 234, 100, 34) };
            _btnAuto.Click += delegate { SetMode("auto"); };
            _btnBoost.Click += delegate { SetMode("boost"); };
            _btnSilent.Click += delegate { SetMode("silent"); };
            Controls.Add(lblMode);
            Controls.Add(_btnAuto);
            Controls.Add(_btnBoost);
            Controls.Add(_btnSilent);

            // Seuils
            var lblSeuils = MakeSection("SEUILS D'ACTIVATION (°C)", 278);
            _stepCpu = new Stepper("CPU", _cfg.CpuOn, 45, 95) { Bounds = new Rectangle(16, 298, 150, 40) };
            _stepGpu = new Stepper("GPU", _cfg.GpuOn, 45, 95) { Bounds = new Rectangle(174, 298, 150, 40) };
            _stepCpu.ValueChanged += delegate
            {
                _cfg.CpuOn = _stepCpu.Value; _cfg.CpuOff = _stepCpu.Value - 10; _cfg.Save();
            };
            _stepGpu.ValueChanged += delegate
            {
                _cfg.GpuOn = _stepGpu.Value; _cfg.GpuOff = _stepGpu.Value - 10; _cfg.Save();
            };
            Controls.Add(lblSeuils);
            Controls.Add(_stepCpu);
            Controls.Add(_stepGpu);

            // Jeux
            _gameToggle = new ToggleRow("Boost auto si jeu lancé", _cfg.GameBoost)
            {
                Bounds = new Rectangle(16, 348, 308, 30)
            };
            _gameToggle.Toggled += delegate { _cfg.GameBoost = _gameToggle.Checked; _cfg.Save(); };
            Controls.Add(_gameToggle);

            var gamesPanel = new Panel { Bounds = new Rectangle(16, 382, 308, 30), BackColor = ColCard };
            _gamesBox = new TextBox
            {
                Bounds = new Rectangle(10, 6, 288, 20),
                BackColor = ColCard,
                ForeColor = ColMuted,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 8.5f),
                Text = _cfg.Games
            };
            _gamesBox.Leave += delegate { _cfg.Games = _gamesBox.Text; _cfg.Save(); };
            gamesPanel.Controls.Add(_gamesBox);
            Controls.Add(gamesPanel);

            // Démarrage automatique
            _startupToggle = new ToggleRow("Démarrage avec Windows", StartupTask.Exists())
            {
                Bounds = new Rectangle(16, 418, 308, 30)
            };
            _startupToggle.Toggled += delegate
            {
                string err = _startupToggle.Checked ? StartupTask.Create() : StartupTask.Remove();
                if (err != null)
                {
                    _startupToggle.SetSilently(StartupTask.Exists());
                    MessageBox.Show(err, "Mini Fan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(_startupToggle);

            // Pied de page
            _footer = new Label
            {
                Bounds = new Rectangle(16, 456, 176, 20),
                ForeColor = ColMuted,
                Font = new Font("Segoe UI", 8f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            var linkMaj = MakeLink("MAJ", 198, 458);
            linkMaj.Click += delegate { _upd.CheckAsync(true); };
            var linkDiag = MakeLink("Diagnostic", 246, 458);
            linkDiag.Click += delegate { ShowDiagnostic(); };
            Controls.Add(_footer);
            Controls.Add(linkMaj);
            Controls.Add(linkDiag);

            RefreshUi();
        }

        private Label MakeGlyph(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Bounds = new Rectangle(x, y, 30, 30),
                ForeColor = ColMuted,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f),
                Cursor = Cursors.Hand
            };
        }

        private Label MakeSection(string text, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(18, y),
                AutoSize = true,
                ForeColor = ColMuted,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold)
            };
        }

        private Label MakeLink(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = ColAccent,
                Font = new Font("Segoe UI", 8f, FontStyle.Underline),
                Cursor = Cursors.Hand
            };
        }

        private void SetMode(string mode)
        {
            _cfg.Mode = mode;
            _cfg.Save();
            _ctl.Tick();
            RefreshUi();
        }

        private void ShowDiagnostic()
        {
            string diag = _ec.BuildDiagnostic();
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostic.txt"), diag);
                Clipboard.SetText(diag);
                MessageBox.Show(
                    "Rapport copié dans le presse-papiers et enregistré dans diagnostic.txt " +
                    "(dossier de l'application).", "Mini Fan — Diagnostic",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(diag.Length > 2000 ? diag.Substring(0, 2000) : diag,
                    "Mini Fan — Diagnostic (" + ex.Message + ")");
            }
        }

        // ---------------- Rafraîchissement ----------------

        private void RefreshUi()
        {
            _cpuCard.SetValue(_ctl.Cpu, _cfg.CpuOn);
            _gpuCard.SetValue(_ctl.Gpu, _cfg.GpuOn);

            if (!_ec.ClassPresent)
            {
                _statusPill.Text = "MODE DÉMO — PC NON MSI";
                _statusPill.ForeColor = ColMuted;
            }
            else if (!_ec.WriteSupported)
            {
                _statusPill.Text = "PILOTAGE KO → DIAGNOSTIC";
                _statusPill.ForeColor = ColHot;
            }
            else if (_ctl.BoostActive)
            {
                _statusPill.Text = "❄  COOLER BOOST ACTIF" +
                    (_ctl.Reason.Length > 0 ? "  ·  " + _ctl.Reason : "");
                _statusPill.ForeColor = ColAccent;
            }
            else
            {
                _statusPill.Text = "VENTILATION AUTO (silencieuse)";
                _statusPill.ForeColor = ColOk;
            }

            _btnAuto.Active = _cfg.Mode == "auto";
            _btnBoost.Active = _cfg.Mode == "boost";
            _btnSilent.Active = _cfg.Mode == "silent";
            _footer.Text = _upd.Status;
        }

        private void RefreshTray()
        {
            if (_tray == null) return;
            Icon ico = _ctl.BoostActive ? _icoBoost
                : (_ctl.Cpu.HasValue && _ctl.Cpu.Value >= _cfg.CpuOn - 5) ? _icoHot : _icoIdle;
            if (_tray.Icon != ico) _tray.Icon = ico;
            string tip = "Mini Fan — CPU " + FmtT(_ctl.Cpu) + "  GPU " + FmtT(_ctl.Gpu) +
                (_ctl.BoostActive ? "  [BOOST]" : "");
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            try { _tray.Text = tip; } catch { }
        }

        private static string FmtT(int? v) { return v.HasValue ? v.Value + "°" : "—"; }

        // ---------------- Tray ----------------

        private void BuildTray()
        {
            _icoIdle = MakeFanIcon(Color.FromArgb(150, 158, 176));
            _icoHot = MakeFanIcon(Color.FromArgb(245, 166, 35));
            _icoBoost = MakeFanIcon(ColAccent);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Ouvrir Mini Fan", null, delegate { ShowNearTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Mode auto", null, delegate { SetMode("auto"); });
            menu.Items.Add("Boost permanent", null, delegate { SetMode("boost"); });
            menu.Items.Add("Repos (jamais de boost)", null, delegate { SetMode("silent"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Vérifier les mises à jour", null, delegate { _upd.CheckAsync(true); ShowNearTray(); });
            menu.Items.Add("Quitter", null, delegate
            {
                _tray.Visible = false;
                Application.Exit();
            });

            _tray = new NotifyIcon
            {
                Icon = _icoIdle,
                Text = "Mini Fan",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (Visible) Hide(); else ShowNearTray();
                }
            };
        }

        private void ShowNearTray()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
            RefreshUi();
            Show();
            Activate();
        }

        private static Icon MakeFanIcon(Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bg = new SolidBrush(Color.FromArgb(30, 33, 44)))
                    g.FillEllipse(bg, 1, 1, 30, 30);
                using (var b = new SolidBrush(color))
                {
                    // 3 pales autour du centre
                    for (int i = 0; i < 3; i++)
                    {
                        g.TranslateTransform(16, 16);
                        g.RotateTransform(120);
                        g.TranslateTransform(-16, -16);
                        g.FillEllipse(b, 13.5f, 3f, 5f, 11f);
                    }
                    g.ResetTransform();
                    g.FillEllipse(b, 13, 13, 6, 6);
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    }

    // ---------------- Petits contrôles custom ----------------

    /// <summary>Carte affichant une température avec barre colorée.</summary>
    public class TempCard : Control
    {
        private readonly string _title;
        private int? _value;
        private int _threshold = 75;

        public TempCard(string title)
        {
            _title = title;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetValue(int? v, int threshold)
        {
            if (_value.Equals(v) && _threshold == threshold) return;
            _value = v; _threshold = threshold;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = MainForm.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            using (var bg = new SolidBrush(MainForm.ColCard))
                g.FillPath(bg, path);

            using (var f = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var muted = new SolidBrush(MainForm.ColMuted))
                g.DrawString(_title, f, muted, 12, 10);

            double frac = 0;
            Color c = MainForm.ColMuted;
            string txt = "—";
            if (_value.HasValue)
            {
                txt = _value.Value + "°";
                frac = Math.Max(0.0, Math.Min(1.0, (_value.Value - 30.0) / 65.0));
                c = _value.Value >= _threshold ? MainForm.ColHot
                  : _value.Value >= _threshold - 10 ? Color.FromArgb(245, 166, 35)
                  : MainForm.ColOk;
            }
            using (var f = new Font("Segoe UI", 26f, FontStyle.Bold))
            using (var b = new SolidBrush(MainForm.ColText))
                g.DrawString(txt, f, b, 8, 26);

            var barBg = new Rectangle(12, Height - 22, Width - 24, 6);
            using (var pb = MainForm.Rounded(barBg, 3))
            using (var bb = new SolidBrush(MainForm.ColCard2))
                g.FillPath(bb, pb);
            if (frac > 0.02)
            {
                var bar = new Rectangle(barBg.X, barBg.Y, Math.Max(6, (int)(barBg.Width * frac)), 6);
                using (var pf = MainForm.Rounded(bar, 3))
                using (var bf = new SolidBrush(c))
                    g.FillPath(bf, pf);
            }
        }
    }

    /// <summary>Bouton de mode (segment) avec état actif.</summary>
    public class ModeButton : Control
    {
        private bool _active;
        public bool Active
        {
            get { return _active; }
            set { if (_active != value) { _active = value; Invalidate(); } }
        }

        public ModeButton(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = MainForm.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 9))
            using (var bg = new SolidBrush(_active ? MainForm.ColAccent : MainForm.ColCard))
                g.FillPath(bg, path);
            using (var f = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            {
                var sz = g.MeasureString(Text, f);
                using (var b = new SolidBrush(_active ? Color.FromArgb(10, 20, 30) : MainForm.ColMuted))
                    g.DrawString(Text, f, b, (Width - sz.Width) / 2f, (Height - sz.Height) / 2f);
            }
        }
    }

    /// <summary>Sélecteur numérique  [−  valeur  +].</summary>
    public class Stepper : Control
    {
        private readonly string _label;
        private readonly int _min, _max;
        public int Value { get; private set; }
        public event Action ValueChanged;

        public Stepper(string label, int value, int min, int max)
        {
            _label = label; Value = value; _min = min; _max = max;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int delta = 0;
            if (e.X < Width / 3) delta = -1;
            else if (e.X > Width * 2 / 3) delta = 1;
            if (delta == 0) return;
            int nv = Math.Max(_min, Math.Min(_max, Value + delta));
            if (nv == Value) return;
            Value = nv;
            Invalidate();
            var h = ValueChanged;
            if (h != null) h();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = MainForm.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 9))
            using (var bg = new SolidBrush(MainForm.ColCard))
                g.FillPath(bg, path);
            using (var fSmall = new Font("Segoe UI", 7f, FontStyle.Bold))
            using (var muted = new SolidBrush(MainForm.ColMuted))
                g.DrawString(_label, fSmall, muted, 10, 4);
            using (var f = new Font("Segoe UI", 12f, FontStyle.Bold))
            {
                string txt = Value + "°";
                var sz = g.MeasureString(txt, f);
                using (var b = new SolidBrush(MainForm.ColText))
                    g.DrawString(txt, f, b, (Width - sz.Width) / 2f, 12f);
                using (var b = new SolidBrush(MainForm.ColAccent))
                {
                    g.DrawString("−", f, b, 12, 8);
                    g.DrawString("+", f, b, Width - 26, 8);
                }
            }
        }
    }

    /// <summary>Ligne avec interrupteur on/off.</summary>
    public class ToggleRow : Control
    {
        public bool Checked { get; private set; }
        public event Action Toggled;

        public ToggleRow(string text, bool value)
        {
            Text = text; Checked = value;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetSilently(bool value) { Checked = value; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Checked = !Checked;
            Invalidate();
            var h = Toggled;
            if (h != null) h();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var f = new Font("Segoe UI", 9f))
            using (var b = new SolidBrush(MainForm.ColText))
                g.DrawString(Text, f, b, 2, (Height - 18) / 2f);
            var track = new Rectangle(Width - 42, (Height - 18) / 2, 38, 18);
            using (var pt = MainForm.Rounded(track, 9))
            using (var bt = new SolidBrush(Checked ? MainForm.ColAccent : MainForm.ColCard2))
                g.FillPath(bt, pt);
            int kx = Checked ? track.Right - 16 : track.X + 2;
            using (var bk = new SolidBrush(Color.White))
                g.FillEllipse(bk, kx, track.Y + 2, 14, 14);
        }
    }

    /// <summary>Tâche planifiée de démarrage (élévation admin sans UAC à chaque boot).</summary>
    public static class StartupTask
    {
        private const string Name = "MiniFan";

        public static bool Exists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", "/query /tn \"" + Name + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(4000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public static string Create()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                // Échappement PowerShell : dans un littéral entre apostrophes, une apostrophe
                // se double. Un chemin contenant une apostrophe casserait sinon la commande.
                string exePs = exe.Replace("'", "''");
                // Via PowerShell : schtasks ne sait pas autoriser le lancement sur batterie
                // ni retirer la limite de durée (72 h) — indispensable sur un portable.
                string cmd =
                    "$a=New-ScheduledTaskAction -Execute '" + exePs + "' -Argument '--tray';" +
                    "$t=New-ScheduledTaskTrigger -AtLogOn;" +
                    "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
                    "-ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable;" +
                    "Register-ScheduledTask -TaskName '" + Name + "' -Action $a -Trigger $t -Settings $s " +
                    "-RunLevel Highest -Force | Out-Null";
                var psi = new ProcessStartInfo("powershell",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "\\\"") + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode == 0 ? null
                        : "Impossible de créer la tâche planifiée (lance Mini Fan en administrateur).\n" + err.Trim();
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string Remove()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", "/delete /f /tn \"" + Name + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(6000);
                    return p.ExitCode == 0 ? null : "Suppression de la tâche impossible.";
                }
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
