using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ps2ToXbox
{
    internal static class Theme
    {
        public static readonly Color BgApp = Color.FromArgb(11, 14, 20);
        public static readonly Color CardBg = Color.FromArgb(19, 24, 35);
        public static readonly Color CardWell = Color.FromArgb(13, 17, 26);
        public static readonly Color Border = Color.FromArgb(32, 40, 58);
        public static readonly Color BorderActive = Color.FromArgb(50, 64, 92);

        public static readonly Color Emerald = Color.FromArgb(16, 185, 129);
        public static readonly Color EmeraldGlow = Color.FromArgb(52, 211, 153);
        public static readonly Color EmeraldBg = Color.FromArgb(18, 48, 38);
        public static readonly Color Cyan = Color.FromArgb(14, 165, 233);
        public static readonly Color Red = Color.FromArgb(239, 68, 68);
        public static readonly Color Amber = Color.FromArgb(245, 158, 11);
        public static readonly Color Blue = Color.FromArgb(59, 130, 246);

        public static readonly Color TextWhite = Color.FromArgb(244, 247, 252);
        public static readonly Color TextMuted = Color.FromArgb(140, 153, 174);
        public static readonly Color TextFaint = Color.FromArgb(88, 101, 122);
    }

    internal static class EmbeddedRuntime
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        public static void PreloadNativeClient()
        {
            Assembly assembly = typeof(EmbeddedRuntime).Assembly;
            const string resourceName = "ps2toxbox.ViGEmClient.dll";
            string relative = Path.Combine("ps2toxbox", "runtime", assembly.ManifestModule.ModuleVersionId.ToString("N"));
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), relative);
            try { Directory.CreateDirectory(root); }
            catch (UnauthorizedAccessException)
            {
                root = Path.Combine(Path.GetTempPath(), relative);
                Directory.CreateDirectory(root);
            }
            string destination = Path.Combine(root, "ViGEmClient.dll");

            if (!File.Exists(destination))
            {
                using (Stream resource = assembly.GetManifestResourceStream(resourceName))
                {
                    if (resource == null) throw new FileNotFoundException("ViGEmClient was not found inside the executable.");
                    string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            resource.CopyTo(file);
                        try { File.Move(temporary, destination); }
                        catch (IOException) { if (!File.Exists(destination)) throw; }
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }

            if (LoadLibrary(destination) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ViGEmClient could not be loaded.");
        }

        public static Bitmap LoadLogo()
        {
            using (Stream stream = GetResource("ps2toxbox.Logo.png"))
            using (var image = Image.FromStream(stream)) return new Bitmap(image);
        }

        public static Icon LoadIcon()
        {
            using (Stream stream = GetResource("ps2toxbox.AppIcon.ico")) return new Icon(stream);
        }

        private static Stream GetResource(string name)
        {
            Stream stream = typeof(EmbeddedRuntime).Assembly.GetManifestResourceStream(name);
            if (stream == null) throw new FileNotFoundException("Application asset not found: " + name);
            return stream;
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            try { EmbeddedRuntime.PreloadNativeClient(); }
            catch (Exception ex)
            {
                MessageBox.Show("ps2toxbox could not prepare ViGEmClient.\r\n\r\n" + ex.Message,
                    "ps2toxbox - Startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private readonly ComboBox devices = new ComboBox();
        private readonly StatusPill physicalStatus = new StatusPill();
        private readonly StatusPill outputStatus = new StatusPill();
        private readonly Label leftValues = new Label();
        private readonly Label rightValues = new Label();
        private readonly StickVisual leftStick = new StickVisual("L3");
        private readonly StickVisual rightStick = new StickVisual("R3");
        private readonly XboxControllerVisual xboxVisual = new XboxControllerVisual();
        private readonly ActiveButtonsHud buttonsHud = new ActiveButtonsHud();
        private readonly ModernButton startBtn = new ModernButton();
        private readonly ModernButton stopBtn = new ModernButton();
        private readonly ModernButton scanBtn = new ModernButton();
        private readonly Timer timer = new Timer();
        private VirtualXbox360 virtualPad;
        private bool running;

        public MainForm()
        {
            Text = "ps2toxbox — PlayStation 2 to Xbox 360 Bridge";
            Icon = EmbeddedRuntime.LoadIcon();
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 780);
            MinimumSize = new Size(980, 700);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.BgApp;
            ForeColor = Theme.TextWhite;
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
            DoubleBuffered = true;

            BuildUi();
            timer.Interval = 8; // ~125 Hz
            timer.Tick += OnTick;
            timer.Start(); // Always poll for live monitor
            Rescan();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int useDark = 1;
                DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
                DwmSetWindowAttribute(Handle, 19, ref useDark, sizeof(int));
            }
            catch { }
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(24, 18, 24, 14),
                BackColor = Theme.BgApp
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 175));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildConnectionCards(), 0, 1);
            root.Controls.Add(BuildMonitor(), 0, 2);
            root.Controls.Add(BuildFooter(), 0, 3);

            devices.SelectedIndexChanged += delegate { UpdatePhysicalStatus(); };
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));

            var logoBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 4, 12, 4),
                Image = EmbeddedRuntime.LoadLogo()
            };

            var titlePanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var titleLabel = new Label
            {
                Text = "ps2toxbox",
                Font = new Font("Segoe UI", 22F, FontStyle.Bold),
                ForeColor = Theme.TextWhite,
                AutoSize = true,
                Location = new Point(0, -2)
            };
            var subtitleLabel = new Label
            {
                Text = "PLAYSTATION 2 GAMEPAD  ➜  XBOX 360 XINPUT BRIDGE",
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Theme.Emerald,
                AutoSize = true,
                Location = new Point(2, 40)
            };
            titlePanel.Controls.Add(titleLabel);
            titlePanel.Controls.Add(subtitleLabel);

            var badge = new ModernBadge
            {
                Text = "v0.1.0 · PUBLIC RELEASE",
                Size = new Size(190, 32),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Location = new Point(10, 14),
                BadgeColor = Theme.Emerald
            };

            header.Controls.Add(logoBox, 0, 0);
            header.Controls.Add(titlePanel, 1, 0);
            header.Controls.Add(badge, 2, 0);
            return header;
        }

        private Control BuildConnectionCards()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // Card 1: Input Device
            var inputCard = new ModernCard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
            var inputLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(16, 12, 16, 12)
            };
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            inputLayout.Controls.Add(CardHeaderTitle("01   PHYSICAL PS2 ADAPTER", Theme.Cyan), 0, 0);

            physicalStatus.Dock = DockStyle.Fill;
            physicalStatus.SetStatus("Scanning for controller...", StatusType.Neutral);
            inputLayout.Controls.Add(physicalStatus, 0, 1);

            var selectRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            selectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            selectRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            devices.Dock = DockStyle.Fill;
            devices.Margin = new Padding(0, 4, 10, 4);
            StyleComboBox(devices);

            scanBtn.Text = "↻  Rescan";
            scanBtn.Dock = DockStyle.Fill;
            scanBtn.Margin = new Padding(0, 4, 0, 4);
            scanBtn.Click += delegate { Rescan(); };

            selectRow.Controls.Add(devices, 0, 0);
            selectRow.Controls.Add(scanBtn, 1, 0);
            inputLayout.Controls.Add(selectRow, 0, 2);

            var inputHint = new Label
            {
                Text = "Detected game controllers exposed via Windows Multimedia Joystick API.",
                ForeColor = Theme.TextFaint,
                Font = new Font("Segoe UI", 8.25F),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            inputLayout.Controls.Add(inputHint, 0, 3);
            inputCard.Controls.Add(inputLayout);

            // Card 2: Virtual Output
            var outputCard = new ModernCard { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
            var outputLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(16, 12, 16, 12)
            };
            outputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            outputLayout.Controls.Add(CardHeaderTitle("02   XBOX 360 VIRTUAL CONTROLLER", Theme.Emerald), 0, 0);

            outputStatus.Dock = DockStyle.Fill;
            outputStatus.SetStatus("Standby · Ready to emulate", StatusType.Neutral);
            outputLayout.Controls.Add(outputStatus, 0, 1);

            var actionRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            actionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            startBtn.Text = "▶   Start Controller";
            startBtn.IsPrimary = true;
            startBtn.Dock = DockStyle.Fill;
            startBtn.Margin = new Padding(0, 4, 6, 4);
            startBtn.Click += OnStart;

            stopBtn.Text = "■   Stop";
            stopBtn.IsDanger = true;
            stopBtn.Dock = DockStyle.Fill;
            stopBtn.Margin = new Padding(6, 4, 0, 4);
            stopBtn.Enabled = false;
            stopBtn.Click += delegate { StopVirtual("Emulation stopped."); };

            actionRow.Controls.Add(startBtn, 0, 0);
            actionRow.Controls.Add(stopBtn, 1, 0);
            outputLayout.Controls.Add(actionRow, 0, 2);

            var outputHint = new Label
            {
                Text = "Creates a virtual Xbox 360 device via ViGEmBus for Steam, Game Pass, and PC games.",
                ForeColor = Theme.TextFaint,
                Font = new Font("Segoe UI", 8.25F),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            outputLayout.Controls.Add(outputHint, 0, 3);
            outputCard.Controls.Add(outputLayout);

            grid.Controls.Add(inputCard, 0, 0);
            grid.Controls.Add(outputCard, 1, 0);
            return grid;
        }

        private Control BuildMonitor()
        {
            var card = new ModernCard { Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 6) };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(18, 12, 18, 12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

            // Monitor Header
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var title = new Label
            {
                Text = "LIVE INPUT & TELEMETRY MONITOR",
                UseMnemonic = false,
                Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
                ForeColor = Theme.TextWhite,
                AutoSize = true,
                Location = new Point(0, 0)
            };
            var sub = new Label
            {
                Text = "Move sticks or press buttons to test adapter responsiveness in real-time.",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Theme.TextMuted,
                AutoSize = true,
                Location = new Point(1, 22)
            };
            heading.Controls.Add(title);
            heading.Controls.Add(sub);

            var badgeBox = new ModernBadge
            {
                Text = "POLLING · 125 HZ",
                Size = new Size(180, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Location = new Point(35, 4),
                BadgeColor = Theme.Cyan
            };

            header.Controls.Add(heading, 0, 0);
            header.Controls.Add(badgeBox, 1, 0);
            layout.Controls.Add(header, 0, 0);

            // Columns: Left Stick (33%), Right Stick (33%), Xbox Controller (34%)
            var columns = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 6)
            };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            columns.Controls.Add(BuildStickPanel("LEFT ANALOG STICK (LX / LY)", leftStick, leftValues), 0, 0);
            columns.Controls.Add(BuildStickPanel("RIGHT ANALOG STICK (RX / RY)", rightStick, rightValues), 1, 0);
            columns.Controls.Add(BuildXboxPanel(), 2, 0);
            layout.Controls.Add(columns, 0, 1);

            // Bottom row: Active Buttons HUD
            buttonsHud.Dock = DockStyle.Fill;
            layout.Controls.Add(buttonsHud, 0, 2);

            card.Controls.Add(layout);
            return card;
        }

        private Control BuildStickPanel(string title, StickVisual visual, Label values)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(4, 0, 4, 0)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            var label = new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.25F, FontStyle.Bold),
                ForeColor = Theme.TextMuted
            };
            panel.Controls.Add(label, 0, 0);

            var stage = new ModernWell { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4) };
            visual.Dock = DockStyle.Fill;
            stage.Controls.Add(visual);
            panel.Controls.Add(stage, 0, 1);

            values.Text = "X:  0.00     Y:  0.00";
            values.Dock = DockStyle.Fill;
            values.TextAlign = ContentAlignment.MiddleCenter;
            values.ForeColor = Theme.EmeraldGlow;
            values.Font = new Font("Consolas", 10F, FontStyle.Bold);
            values.BackColor = Theme.CardWell;
            panel.Controls.Add(values, 0, 2);

            return panel;
        }

        private Control BuildXboxPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(6, 0, 0, 0)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var label = new Label
            {
                Text = "XBOX 360 OUTPUT DIAGRAM",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.25F, FontStyle.Bold),
                ForeColor = Theme.TextMuted
            };
            panel.Controls.Add(label, 0, 0);

            var stage = new ModernWell { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };
            xboxVisual.Dock = DockStyle.Fill;
            stage.Controls.Add(xboxVisual);
            panel.Controls.Add(stage, 0, 1);

            return panel;
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var left = new Label
            {
                Text = "ps2toxbox v0.1.0 · PlayStation 2 to Xbox 360 Bridge",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextFaint,
                Font = new Font("Segoe UI", 8F)
            };
            var right = new Label
            {
                Text = "Windows x64 · Powered by ViGEmBus",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Theme.TextFaint,
                Font = new Font("Segoe UI", 8F)
            };

            footer.Controls.Add(left, 0, 0);
            footer.Controls.Add(right, 1, 0);
            return footer;
        }

        private static Label CardHeaderTitle(string text, Color dotColor)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextMuted,
                Font = new Font("Segoe UI", 8.25F, FontStyle.Bold),
                Margin = Padding.Empty
            };
        }

        private static void StyleComboBox(ComboBox c)
        {
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Color.FromArgb(28, 35, 50);
            c.ForeColor = Theme.TextWhite;
            c.Font = new Font("Segoe UI", 9.5F);
            c.IntegralHeight = false;
            c.DropDownHeight = 280;
            c.DrawMode = DrawMode.OwnerDrawFixed;
            c.ItemHeight = 24;
            c.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                Color bg = selected ? Color.FromArgb(38, 50, 75) : Color.FromArgb(24, 30, 44);
                using (var brush = new SolidBrush(bg)) e.Graphics.FillRectangle(brush, e.Bounds);
                string text = c.Items[e.Index].ToString();
                using (var brush = new SolidBrush(selected ? Theme.EmeraldGlow : Theme.TextWhite))
                {
                    var rect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 3, e.Bounds.Width - 12, e.Bounds.Height - 6);
                    e.Graphics.DrawString(text, c.Font, brush, rect);
                }
            };
        }

        private void Rescan()
        {
            var old = devices.SelectedItem as JoystickDevice;
            devices.Items.Clear();
            foreach (var d in WinMmInput.GetDevices()) devices.Items.Add(d);
            if (old != null)
            {
                for (int i = 0; i < devices.Items.Count; i++)
                    if (((JoystickDevice)devices.Items[i]).Id == old.Id) devices.SelectedIndex = i;
            }
            if (devices.SelectedIndex < 0 && devices.Items.Count > 0) devices.SelectedIndex = 0;
            UpdatePhysicalStatus();
        }

        private void UpdatePhysicalStatus()
        {
            if (devices.SelectedItem == null)
            {
                physicalStatus.SetStatus("No PS2 gamepad adapter detected", StatusType.Inactive);
                startBtn.Enabled = false;
            }
            else
            {
                physicalStatus.SetStatus("Connected: " + devices.SelectedItem, StatusType.Active);
                startBtn.Enabled = !running;
            }
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (devices.SelectedItem == null) return;
            try
            {
                virtualPad = new VirtualXbox360();
                virtualPad.Connect();
                running = true;
                devices.Enabled = false;
                scanBtn.Enabled = false;
                startBtn.Enabled = false;
                stopBtn.Enabled = true;
                outputStatus.SetStatus("Xbox 360 controller active (Emulating)", StatusType.Active);
                xboxVisual.SetEmulating(true);
            }
            catch (DllNotFoundException)
            {
                StopVirtual("ViGEmClient DLL missing. Keep files intact.");
            }
            catch (Exception ex)
            {
                StopVirtual("Failed: " + ex.Message);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            var d = devices.SelectedItem as JoystickDevice;
            if (d == null)
            {
                if (running) StopVirtual("Controller disconnected.");
                leftValues.Text = "X:  0.00     Y:  0.00";
                rightValues.Text = "X:  0.00     Y:  0.00";
                leftStick.SetState(0, 0, false);
                rightStick.SetState(0, 0, false);
                buttonsHud.SetReport(new XusbReport(), 0);
                xboxVisual.SetReport(new XusbReport());
                return;
            }

            JoystickState state;
            if (!WinMmInput.TryRead(d, out state))
            {
                if (running) StopVirtual("Controller disconnected. Reconnect adapter.");
                return;
            }

            // Always update live telemetry
            leftValues.Text = string.Format(CultureInfo.InvariantCulture, "X: {0,5:+0.00;-0.00; 0.00}   Y: {1,5:+0.00;-0.00; 0.00}", state.LeftX, state.LeftY);
            rightValues.Text = string.Format(CultureInfo.InvariantCulture, "X: {0,5:+0.00;-0.00; 0.00}   Y: {1,5:+0.00;-0.00; 0.00}", state.RightX, state.RightY);

            bool l3 = (state.Buttons & (1u << 10)) != 0;
            bool r3 = (state.Buttons & (1u << 11)) != 0;
            leftStick.SetState(state.LeftX, state.LeftY, l3);
            rightStick.SetState(state.RightX, state.RightY, r3);

            XusbReport report = state.ToXusbReport();
            buttonsHud.SetReport(report, state.Buttons);
            xboxVisual.SetReport(report);

            if (running && virtualPad != null)
            {
                try { virtualPad.Update(report); }
                catch (Exception ex) { StopVirtual("Emulation error: " + ex.Message); }
            }
        }

        private void StopVirtual(string message)
        {
            if (virtualPad != null) { virtualPad.Dispose(); virtualPad = null; }
            running = false;
            devices.Enabled = true;
            scanBtn.Enabled = true;
            stopBtn.Enabled = false;
            startBtn.Enabled = devices.SelectedItem != null;
            outputStatus.SetStatus(message, StatusType.Neutral);
            xboxVisual.SetEmulating(false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            StopVirtual("Stopped.");
            base.OnFormClosing(e);
        }
    }

    public enum StatusType { Neutral, Active, Inactive }

    internal sealed class StatusPill : Control
    {
        private string text = "";
        private StatusType type = StatusType.Neutral;

        public StatusPill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 28;
            Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        }

        public void SetStatus(string newText, StatusType newType)
        {
            if (text == newText && type == newType) return;
            text = newText;
            type = newType;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color dotColor = type == StatusType.Active ? Theme.Emerald :
                             (type == StatusType.Inactive ? Theme.Red : Theme.TextMuted);
            Color dotGlow = type == StatusType.Active ? Color.FromArgb(60, Theme.Emerald) :
                            (type == StatusType.Inactive ? Color.FromArgb(60, Theme.Red) : Color.Transparent);

            // Draw status dot with aura
            int cy = Height / 2;
            if (dotGlow != Color.Transparent)
            {
                using (var glowBrush = new SolidBrush(dotGlow))
                    e.Graphics.FillEllipse(glowBrush, 2, cy - 8, 16, 16);
            }
            using (var dotBrush = new SolidBrush(dotColor))
                e.Graphics.FillEllipse(dotBrush, 6, cy - 4, 8, 8);

            // Draw text
            using (var brush = new SolidBrush(type == StatusType.Active ? Theme.TextWhite : Theme.TextMuted))
            {
                var rect = new Rectangle(24, 0, Width - 26, Height);
                var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                e.Graphics.DrawString(text, Font, brush, rect, sf);
            }
        }
    }

    internal sealed class ModernBadge : Control
    {
        public Color BadgeColor { get; set; }

        public ModernBadge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BadgeColor = Theme.Emerald;
            Font = new Font("Segoe UI", 8F, FontStyle.Bold);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = GetRoundPath(rect, 14))
            {
                using (var fill = new SolidBrush(Color.FromArgb(24, BadgeColor)))
                    e.Graphics.FillPath(fill, path);
                using (var pen = new Pen(Color.FromArgb(90, BadgeColor), 1f))
                    e.Graphics.DrawPath(pen, path);
            }

            using (var brush = new SolidBrush(BadgeColor))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(Text, Font, brush, ClientRectangle, sf);
            }
        }

        private static GraphicsPath GetRoundPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class ModernButton : Button
    {
        public bool IsPrimary { get; set; }
        public bool IsDanger { get; set; }
        private bool isHovered, isPressed;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            Height = 36;
        }

        protected override void OnMouseEnter(EventArgs e) { isHovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { isHovered = false; isPressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { isPressed = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { isPressed = false; Invalidate(); base.OnMouseUp(mevent); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg, border, text;

            if (!Enabled)
            {
                bg = Color.FromArgb(24, 28, 38);
                border = Color.FromArgb(36, 42, 56);
                text = Color.FromArgb(70, 80, 100);
            }
            else if (IsPrimary)
            {
                bg = isPressed ? Color.FromArgb(10, 140, 95) : (isHovered ? Color.FromArgb(20, 205, 142) : Theme.Emerald);
                border = isHovered ? Theme.EmeraldGlow : Color.FromArgb(14, 160, 110);
                text = Color.FromArgb(10, 22, 18);
            }
            else if (IsDanger)
            {
                bg = isPressed ? Color.FromArgb(140, 30, 30) : (isHovered ? Color.FromArgb(195, 45, 45) : Color.FromArgb(150, 36, 36));
                border = isHovered ? Theme.Red : Color.FromArgb(180, 40, 40);
                text = Theme.TextWhite;
            }
            else
            {
                bg = isPressed ? Color.FromArgb(36, 46, 66) : (isHovered ? Color.FromArgb(44, 56, 80) : Color.FromArgb(28, 36, 52));
                border = isHovered ? Theme.Cyan : Theme.BorderActive;
                text = isHovered ? Theme.TextWhite : Theme.TextMuted;
            }

            using (var path = GetRoundPath(rect, 6))
            {
                using (var brush = new SolidBrush(bg)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(border, 1.2f)) e.Graphics.DrawPath(pen, path);
            }

            using (var brush = new SolidBrush(text))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(Text, Font, brush, ClientRectangle, sf);
            }
        }

        private static GraphicsPath GetRoundPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal class ModernCard : Panel
    {
        public ModernCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
            BackColor = Theme.CardBg;
            Padding = new Padding(1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = GetRoundPath(rect, 8))
            {
                using (var fill = new SolidBrush(Theme.CardBg)) e.Graphics.FillPath(fill, path);
                using (var pen = new Pen(Theme.Border, 1.2f)) e.Graphics.DrawPath(pen, path);
            }
        }

        private static GraphicsPath GetRoundPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal class ModernWell : Panel
    {
        public ModernWell()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
            BackColor = Theme.CardWell;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = GetRoundPath(rect, 6))
            {
                using (var fill = new SolidBrush(Theme.CardWell)) e.Graphics.FillPath(fill, path);
                using (var pen = new Pen(Color.FromArgb(26, 33, 48), 1f)) e.Graphics.DrawPath(pen, path);
            }
        }

        private static GraphicsPath GetRoundPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class StickVisual : Control
    {
        private double xValue, yValue;
        private bool isClicked;
        private readonly string clickLabel;

        public StickVisual(string clickLabel)
        {
            this.clickLabel = clickLabel;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.CardWell;
        }

        public void SetState(double x, double y, bool clicked)
        {
            x = Math.Max(-1, Math.Min(1, x));
            y = Math.Max(-1, Math.Min(1, y));
            if (Math.Abs(x - xValue) < 0.005 && Math.Abs(y - yValue) < 0.005 && isClicked == clicked) return;
            xValue = x;
            yValue = y;
            isClicked = clicked;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float cx = Width / 2f, cy = Height / 2f;
            float radius = Math.Max(12, Math.Min(Width, Height) * 0.40f);

            // Radar background gradient
            using (var grad = new GraphicsPath())
            {
                grad.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2);
                using (var brush = new PathGradientBrush(grad))
                {
                    brush.CenterColor = Color.FromArgb(20, 26, 40);
                    brush.SurroundColors = new[] { Color.FromArgb(12, 16, 24) };
                    e.Graphics.FillPath(brush, grad);
                }
            }

            // Radar rings
            using (var penOuter = new Pen(Color.FromArgb(46, 58, 80), 1.5f))
            using (var penMid = new Pen(Color.FromArgb(32, 42, 60), 1f))
            using (var penInner = new Pen(Color.FromArgb(26, 34, 50), 1f))
            using (var penDeadzone = new Pen(Color.FromArgb(50, Theme.Emerald), 1f))
            {
                penDeadzone.DashStyle = DashStyle.Dot;
                e.Graphics.DrawEllipse(penOuter, cx - radius, cy - radius, radius * 2, radius * 2);
                e.Graphics.DrawEllipse(penMid, cx - radius * 0.66f, cy - radius * 0.66f, radius * 1.32f, radius * 1.32f);
                e.Graphics.DrawEllipse(penInner, cx - radius * 0.33f, cy - radius * 0.33f, radius * 0.66f, radius * 0.66f);
                // Deadzone (0.08)
                float dz = radius * 0.08f;
                e.Graphics.DrawEllipse(penDeadzone, cx - dz, cy - dz, dz * 2, dz * 2);
            }

            // Crosshairs with ticks
            using (var penAxis = new Pen(Color.FromArgb(34, 44, 64), 1f))
            {
                e.Graphics.DrawLine(penAxis, cx - radius, cy, cx + radius, cy);
                e.Graphics.DrawLine(penAxis, cx, cy - radius, cx, cy + radius);
            }

            // Guide vector line to stick position
            float px = cx + (float)xValue * radius;
            float py = cy - (float)yValue * radius;
            float dist = (float)Math.Sqrt(xValue * xValue + yValue * yValue);

            Color pipColor = isClicked ? Theme.Amber : (dist > 0.08f ? Theme.Emerald : Theme.TextMuted);

            if (dist > 0.05f)
            {
                using (var laser = new Pen(Color.FromArgb(140, pipColor), 1.8f))
                    e.Graphics.DrawLine(laser, cx, cy, px, py);
            }

            // Pip outer glow + body
            using (var glowBrush = new SolidBrush(Color.FromArgb(40, pipColor)))
                e.Graphics.FillEllipse(glowBrush, px - 11, py - 11, 22, 22);

            using (var bodyBrush = new SolidBrush(pipColor))
            using (var borderPen = new Pen(Color.White, 2f))
            {
                e.Graphics.FillEllipse(bodyBrush, px - 6, py - 6, 12, 12);
                e.Graphics.DrawEllipse(borderPen, px - 6, py - 6, 12, 12);
            }

            // Center origin mark
            using (var brush = new SolidBrush(Color.FromArgb(70, 85, 110)))
                e.Graphics.FillEllipse(brush, cx - 2.5f, cy - 2.5f, 5, 5);

            // L3/R3 Click badge
            if (isClicked)
            {
                var badgeRect = new Rectangle(10, 10, 75, 22);
                using (var fill = new SolidBrush(Color.FromArgb(180, Theme.Amber)))
                using (var font = new Font("Segoe UI", 7.5F, FontStyle.Bold))
                {
                    e.Graphics.FillRectangle(fill, badgeRect);
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    e.Graphics.DrawString(clickLabel + " PRESSED", font, Brushes.Black, badgeRect, sf);
                }
            }
        }
    }

    internal sealed class XboxControllerVisual : Control
    {
        private XusbReport report;
        private bool isEmulating;

        public XboxControllerVisual()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.CardWell;
        }

        public void SetReport(XusbReport value) { report = value; Invalidate(); }
        public void SetEmulating(bool emulating) { isEmulating = emulating; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float scale = Math.Min(Width / 420f, Height / 290f);
            if (scale <= 0) return;

            e.Graphics.TranslateTransform((Width - 420 * scale) / 2f, (Height - 290 * scale) / 2f);
            e.Graphics.ScaleTransform(scale, scale);

            // Trigger Analog Meters (LT & RT)
            DrawTriggerGauge(e.Graphics, 86, 12, "LT", report.LeftTrigger);
            DrawTriggerGauge(e.Graphics, 260, 12, "RT", report.RightTrigger);

            // Bumpers (LB & RB)
            bool lbActive = (report.Buttons & 0x0100) != 0;
            bool rbActive = (report.Buttons & 0x0200) != 0;
            DrawBumper(e.Graphics, 84, 46, "LB", lbActive);
            DrawBumper(e.Graphics, 258, 46, "RB", rbActive);

            // Controller Body Outline
            using (var path = new GraphicsPath())
            using (var fill = new SolidBrush(Color.FromArgb(24, 30, 42)))
            using (var edge = new Pen(Color.FromArgb(58, 72, 98), 2f))
            {
                path.StartFigure();
                path.AddBezier(60, 72, 78, 28, 114, 26, 154, 48);
                path.AddBezier(154, 48, 174, 60, 186, 52, 198, 42);
                path.AddBezier(198, 42, 215, 28, 237, 28, 254, 42);
                path.AddBezier(254, 42, 274, 57, 290, 52, 318, 37);
                path.AddBezier(318, 37, 344, 23, 367, 41, 375, 72);
                path.AddBezier(375, 72, 392, 128, 379, 198, 346, 240);
                path.AddBezier(346, 240, 322, 270, 291, 264, 266, 228);
                path.AddBezier(266, 228, 246, 199, 158, 199, 138, 228);
                path.AddBezier(138, 228, 113, 264, 82, 270, 58, 240);
                path.AddBezier(58, 240, 25, 198, 13, 128, 30, 72);
                path.AddBezier(30, 72, 38, 42, 53, 29, 60, 72);
                path.CloseFigure();
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
            }

            // Xbox Center Guide Button (Silver orb with green quadrant LED)
            DrawGuideButton(e.Graphics, 210, 85, isEmulating);

            // Back & Start
            bool backActive = (report.Buttons & 0x0020) != 0;
            bool startActive = (report.Buttons & 0x0010) != 0;
            DrawNavButton(e.Graphics, 168, 85, "BACK", backActive);
            DrawNavButton(e.Graphics, 252, 85, "START", startActive);

            // Thumbsticks (with deflection!)
            DrawStick(e.Graphics, 118, 110, report.ThumbLX, report.ThumbLY, (report.Buttons & 0x0040) != 0);
            DrawStick(e.Graphics, 264, 172, report.ThumbRX, report.ThumbRY, (report.Buttons & 0x0080) != 0);

            // D-Pad
            DrawDpad(e.Graphics, report.Buttons);

            // ABXY Buttons
            DrawFaceButton(e.Graphics, 324, 94, "Y", Theme.Amber, 0x8000);
            DrawFaceButton(e.Graphics, 350, 120, "B", Theme.Red, 0x2000);
            DrawFaceButton(e.Graphics, 298, 120, "X", Theme.Blue, 0x4000);
            DrawFaceButton(e.Graphics, 324, 146, "A", Theme.Emerald, 0x1000);

            e.Graphics.ResetTransform();
        }

        private static void DrawTriggerGauge(Graphics g, float x, float y, string label, byte value)
        {
            float fillPct = value / 255f;
            using (var font = new Font("Segoe UI", 7.5F, FontStyle.Bold))
            using (var textBrush = new SolidBrush(Theme.TextMuted))
            {
                g.DrawString(label, font, textBrush, x, y);
                string valText = string.Format("{0}%", (int)(fillPct * 100));
                g.DrawString(valText, font, fillPct > 0 ? Brushes.White : textBrush, x + 48, y);
            }

            var barRect = new RectangleF(x, y + 14, 76, 10);
            using (var bgBrush = new SolidBrush(Color.FromArgb(20, 26, 38)))
            using (var pen = new Pen(Color.FromArgb(46, 58, 80), 1f))
            {
                g.FillRectangle(bgBrush, barRect);
                g.DrawRectangle(pen, barRect.X, barRect.Y, barRect.Width, barRect.Height);
            }

            if (fillPct > 0)
            {
                var fillRect = new RectangleF(x, y + 14, 76 * fillPct, 10);
                using (var fillBrush = new SolidBrush(Theme.Emerald))
                    g.FillRectangle(fillBrush, fillRect);
            }
        }

        private static void DrawBumper(Graphics g, float x, float y, string label, bool active)
        {
            var rect = new RectangleF(x, y, 78, 16);
            using (var path = GetRoundPath(Rectangle.Round(rect), 4))
            {
                using (var fill = new SolidBrush(active ? Theme.Emerald : Color.FromArgb(32, 40, 56)))
                    g.FillPath(fill, path);
                using (var pen = new Pen(active ? Theme.EmeraldGlow : Color.FromArgb(55, 68, 94), 1.2f))
                    g.DrawPath(pen, path);
            }
            using (var font = new Font("Segoe UI", 7F, FontStyle.Bold))
            using (var textBrush = new SolidBrush(active ? Color.Black : Theme.TextMuted))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(label, font, textBrush, rect, sf);
            }
        }

        private static void DrawGuideButton(Graphics g, float x, float y, bool emulating)
        {
            // Outer silver rim
            using (var rim = new SolidBrush(Color.FromArgb(44, 54, 72)))
            using (var pen = new Pen(Color.FromArgb(80, 95, 125), 1.5f))
            {
                g.FillEllipse(rim, x - 15, y - 15, 30, 30);
                g.DrawEllipse(pen, x - 15, y - 15, 30, 30);
            }

            // Inner dark center
            using (var center = new SolidBrush(Color.FromArgb(16, 20, 28)))
                g.FillEllipse(center, x - 11, y - 11, 22, 22);

            // Player 1 Quad LED glow
            Color led = emulating ? Theme.Emerald : Color.FromArgb(50, 65, 85);
            using (var pen = new Pen(led, 2.5f))
            {
                // Top-left quadrant (Player 1)
                g.DrawArc(pen, x - 8, y - 8, 16, 16, 190, 70);
            }

            // 'X' logo
            using (var penX = new Pen(Color.FromArgb(180, 195, 215), 1.8f))
            {
                g.DrawLine(penX, x - 4, y - 4, x + 4, y + 4);
                g.DrawLine(penX, x + 4, y - 4, x - 4, y + 4);
            }
        }

        private static void DrawNavButton(Graphics g, float x, float y, string label, bool active)
        {
            var rect = new RectangleF(x - 12, y - 6, 24, 12);
            using (var path = GetRoundPath(Rectangle.Round(rect), 3))
            {
                using (var fill = new SolidBrush(active ? Theme.Emerald : Color.FromArgb(32, 40, 56)))
                    g.FillPath(fill, path);
                using (var pen = new Pen(active ? Theme.EmeraldGlow : Color.FromArgb(55, 68, 92), 1f))
                    g.DrawPath(pen, path);
            }
            using (var font = new Font("Segoe UI", 5.5F, FontStyle.Bold))
            using (var text = new SolidBrush(active ? Color.Black : Theme.TextFaint))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(label, font, text, rect, sf);
            }
        }

        private static void DrawStick(Graphics g, float x, float y, short axisX, short axisY, bool pressed)
        {
            using (var rim = new SolidBrush(Color.FromArgb(16, 22, 32)))
            using (var outline = new Pen(pressed ? Theme.Amber : Color.FromArgb(64, 78, 106), 2f))
            using (var center = new SolidBrush(Color.FromArgb(36, 45, 62)))
            using (var dot = new SolidBrush(pressed ? Theme.Amber : Theme.Emerald))
            {
                g.FillEllipse(rim, x - 25, y - 25, 50, 50);
                g.DrawEllipse(outline, x - 25, y - 25, 50, 50);
                g.FillEllipse(center, x - 18, y - 18, 36, 36);

                float px = x + (axisX / 32767f) * 14f;
                float py = y - (axisY / 32767f) * 14f;
                g.FillEllipse(dot, px - 6, py - 6, 12, 12);
            }
        }

        private static void DrawDpad(Graphics g, ushort buttons)
        {
            DrawDpadArm(g, 137, 132, 18, 20, (buttons & 1) != 0, "▲");
            DrawDpadArm(g, 137, 170, 18, 20, (buttons & 2) != 0, "▼");
            DrawDpadArm(g, 118, 151, 20, 18, (buttons & 4) != 0, "◄");
            DrawDpadArm(g, 154, 151, 20, 18, (buttons & 8) != 0, "►");

            using (var center = new SolidBrush(Color.FromArgb(22, 28, 40)))
                g.FillRectangle(center, 137, 151, 18, 18);
        }

        private static void DrawDpadArm(Graphics g, float x, float y, float w, float h, bool active, string arrow)
        {
            var rect = new RectangleF(x, y, w, h);
            using (var fill = new SolidBrush(active ? Theme.Emerald : Color.FromArgb(32, 40, 56)))
            using (var pen = new Pen(active ? Theme.EmeraldGlow : Color.FromArgb(50, 62, 85), 1f))
            {
                g.FillRectangle(fill, rect);
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            }
            using (var font = new Font("Segoe UI", 6.5F, FontStyle.Bold))
            using (var text = new SolidBrush(active ? Color.Black : Theme.TextMuted))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(arrow, font, text, rect, sf);
            }
        }

        private void DrawFaceButton(Graphics g, float x, float y, string label, Color color, ushort mask)
        {
            bool active = (report.Buttons & mask) != 0;
            using (var fill = new SolidBrush(active ? color : Color.FromArgb(28, 36, 50)))
            using (var pen = new Pen(active ? Color.White : Color.FromArgb(70, color), active ? 2.2f : 1.5f))
            using (var font = new Font("Segoe UI", 9F, FontStyle.Bold))
            using (var text = new SolidBrush(active ? (color == Theme.Amber ? Color.Black : Color.White) : color))
            {
                g.FillEllipse(fill, x - 12, y - 12, 24, 24);
                g.DrawEllipse(pen, x - 12, y - 12, 24, 24);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(label, font, text, new RectangleF(x - 12, y - 12, 24, 24), sf);
            }
        }

        private static GraphicsPath GetRoundPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class ActiveButtonsHud : Control
    {
        private XusbReport xusb;
        private uint rawButtons;

        private static readonly string[] Names = {
            "▲ UP", "▼ DOWN", "◄ LEFT", "► RIGHT",
            "✕  (A)", "◯  (B)", "▢  (X)", "△  (Y)",
            "L1 (LB)", "R1 (RB)", "L2 (LT)", "R2 (RT)",
            "SELECT", "START", "L3", "R3"
        };

        public ActiveButtonsHud()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 36;
            BackColor = Theme.CardWell;
        }

        public void SetReport(XusbReport report, uint raw)
        {
            if (xusb.Buttons == report.Buttons &&
                xusb.LeftTrigger == report.LeftTrigger &&
                xusb.RightTrigger == report.RightTrigger &&
                rawButtons == raw) return;

            xusb = report;
            rawButtons = raw;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool[] states = new bool[16];
            states[0] = (xusb.Buttons & 0x0001) != 0; // Up
            states[1] = (xusb.Buttons & 0x0002) != 0; // Down
            states[2] = (xusb.Buttons & 0x0004) != 0; // Left
            states[3] = (xusb.Buttons & 0x0008) != 0; // Right
            states[4] = (xusb.Buttons & 0x1000) != 0; // A
            states[5] = (xusb.Buttons & 0x2000) != 0; // B
            states[6] = (xusb.Buttons & 0x4000) != 0; // X
            states[7] = (xusb.Buttons & 0x8000) != 0; // Y
            states[8] = (xusb.Buttons & 0x0100) != 0; // LB
            states[9] = (xusb.Buttons & 0x0200) != 0; // RB
            states[10] = xusb.LeftTrigger > 0;        // LT
            states[11] = xusb.RightTrigger > 0;       // RT
            states[12] = (xusb.Buttons & 0x0020) != 0;// Back
            states[13] = (xusb.Buttons & 0x0010) != 0;// Start
            states[14] = (xusb.Buttons & 0x0040) != 0;// L3
            states[15] = (xusb.Buttons & 0x0080) != 0;// R3

            float pad = 4;
            float totalW = Width - pad * 2;
            float chipW = (totalW - (states.Length - 1) * 3) / states.Length;
            chipW = Math.Max(42, Math.Min(68, chipW));
            float startX = (Width - (states.Length * (chipW + 3) - 3)) / 2f;
            if (startX < 2) startX = 2;

            using (var font = new Font("Segoe UI", 7F, FontStyle.Bold))
            {
                for (int i = 0; i < states.Length; i++)
                {
                    bool active = states[i];
                    float x = startX + i * (chipW + 3);
                    var rect = new RectangleF(x, 6, chipW, Height - 12);

                    Color bg = active ? Theme.Emerald : Color.FromArgb(22, 28, 40);
                    Color border = active ? Theme.EmeraldGlow : Color.FromArgb(36, 46, 64);
                    Color text = active ? Color.FromArgb(10, 24, 18) : Theme.TextFaint;

                    using (var path = GetRoundPath(Rectangle.Round(rect), 3))
                    {
                        using (var brush = new SolidBrush(bg)) e.Graphics.FillPath(brush, path);
                        using (var pen = new Pen(border, 1f)) e.Graphics.DrawPath(pen, path);
                    }

                    using (var textBrush = new SolidBrush(text))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        e.Graphics.DrawString(Names[i], font, textBrush, rect, sf);
                    }
                }
            }
        }

        private static GraphicsPath GetRoundPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class JoystickDevice
    {
        public uint Id;
        public JoystickCaps Caps;
        public override string ToString() { return Caps.ProductName + " (ID " + Id + ")"; }
    }

    internal struct JoystickState
    {
        public uint Buttons, Pov;
        public double LeftX, LeftY, RightX, RightY;

        public XusbReport ToXusbReport()
        {
            ushort b = 0;
            if ((Buttons & (1u << 2)) != 0) b |= 0x1000;
            if ((Buttons & (1u << 1)) != 0) b |= 0x2000;
            if ((Buttons & (1u << 3)) != 0) b |= 0x4000;
            if ((Buttons & (1u << 0)) != 0) b |= 0x8000;
            if ((Buttons & (1u << 4)) != 0) b |= 0x0100;
            if ((Buttons & (1u << 5)) != 0) b |= 0x0200;
            if ((Buttons & (1u << 8)) != 0) b |= 0x0020;
            if ((Buttons & (1u << 9)) != 0) b |= 0x0010;
            if ((Buttons & (1u << 10)) != 0) b |= 0x0040;
            if ((Buttons & (1u << 11)) != 0) b |= 0x0080;
            if (Pov != WinMmInput.PovCentered)
            {
                if (Pov >= 31500 || Pov < 4500) b |= 0x0001;
                if (Pov >= 4500 && Pov < 13500) b |= 0x0008;
                if (Pov >= 13500 && Pov < 22500) b |= 0x0002;
                if (Pov >= 22500 && Pov < 31500) b |= 0x0004;
            }
            return new XusbReport
            {
                Buttons = b,
                LeftTrigger = (byte)(((Buttons & (1u << 6)) != 0) ? 255 : 0),
                RightTrigger = (byte)(((Buttons & (1u << 7)) != 0) ? 255 : 0),
                ThumbLX = Axis(LeftX),
                ThumbLY = Axis(LeftY),
                ThumbRX = Axis(RightX),
                ThumbRY = Axis(RightY)
            };
        }

        private static short Axis(double value) { return (short)Math.Max(-32768, Math.Min(32767, value * 32767.0)); }
    }

    internal static class WinMmInput
    {
        public const uint PovCentered = 0xFFFF;
        private const uint ReturnAll = 0xFF;
        [DllImport("winmm.dll", EntryPoint = "joyGetNumDevs")] private static extern uint JoyGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)] private static extern uint JoyGetDevCaps(uint id, out JoystickCaps caps, uint size);
        [DllImport("winmm.dll", EntryPoint = "joyGetPosEx")] private static extern uint JoyGetPosEx(uint id, ref JoystickInfoEx info);

        public static List<JoystickDevice> GetDevices()
        {
            var found = new List<JoystickDevice>();
            uint count = Math.Min(JoyGetNumDevs(), 16);
            for (uint i = 0; i < count; i++)
            {
                JoystickCaps caps;
                if (JoyGetDevCaps(i, out caps, (uint)Marshal.SizeOf(typeof(JoystickCaps))) != 0) continue;
                var info = new JoystickInfoEx { Size = (uint)Marshal.SizeOf(typeof(JoystickInfoEx)), Flags = ReturnAll };
                if (JoyGetPosEx(i, ref info) != 0) continue;
                found.Add(new JoystickDevice { Id = i, Caps = caps });
            }
            return found;
        }

        public static bool TryRead(JoystickDevice device, out JoystickState state)
        {
            var info = new JoystickInfoEx { Size = (uint)Marshal.SizeOf(typeof(JoystickInfoEx)), Flags = ReturnAll };
            if (JoyGetPosEx(device.Id, ref info) != 0) { state = new JoystickState(); return false; }
            var c = device.Caps;
            state = new JoystickState
            {
                Buttons = info.Buttons,
                Pov = info.Pov,
                LeftX = Normalize(info.X, c.XMin, c.XMax),
                LeftY = -Normalize(info.Y, c.YMin, c.YMax),
                RightX = Normalize(info.Z, c.ZMin, c.ZMax),
                RightY = -Normalize(info.R, c.RMin, c.RMax)
            };
            return true;
        }

        private static double Normalize(uint value, uint min, uint max)
        {
            if (max <= min) return 0;
            double center = (min + max) / 2.0;
            double result = (value - center) / ((max - min) / 2.0);
            if (Math.Abs(result) < 0.08) return 0;
            return Math.Max(-1, Math.Min(1, result));
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct JoystickCaps
    {
        public ushort ManufacturerId, ProductId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ProductName;
        public uint XMin, XMax, YMin, YMax, ZMin, ZMax, ButtonCount, PeriodMin, PeriodMax;
        public uint RMin, RMax, UMin, UMax, VMin, VMax, Capabilities, MaxAxes, AxisCount, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegistryKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxd;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JoystickInfoEx
    {
        public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct XusbReport
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    internal sealed class VirtualXbox360 : IDisposable
    {
        private const int VigemErrorNone = unchecked((int)0x20000000);
        private IntPtr client, target;
        private bool connected, added;

        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr vigem_alloc();
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vigem_connect(IntPtr client);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr vigem_target_x360_alloc();
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vigem_target_add(IntPtr client, IntPtr target);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vigem_target_x360_update(IntPtr client, IntPtr target, XusbReport report);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vigem_target_remove(IntPtr client, IntPtr target);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vigem_target_free(IntPtr target);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vigem_disconnect(IntPtr client);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vigem_free(IntPtr client);

        public void Connect()
        {
            client = vigem_alloc();
            if (client == IntPtr.Zero) throw new InvalidOperationException("Could not create the ViGEm client.");
            int result = vigem_connect(client);
            if (result != VigemErrorNone) throw new InvalidOperationException("Could not connect to ViGEmBus (code 0x" + result.ToString("X") + "). Make sure ViGEmBus is installed and running.");
            connected = true;
            target = vigem_target_x360_alloc();
            if (target == IntPtr.Zero) throw new InvalidOperationException("Could not create the Xbox 360 target.");
            result = vigem_target_add(client, target);
            if (result != VigemErrorNone) throw new InvalidOperationException("Could not add the virtual controller (code 0x" + result.ToString("X") + ").");
            added = true;
        }

        public void Update(XusbReport report)
        {
            int result = vigem_target_x360_update(client, target, report);
            if (result != VigemErrorNone) throw new InvalidOperationException("Input update failed (code 0x" + result.ToString("X") + ").");
        }

        public void Dispose()
        {
            if (added && client != IntPtr.Zero && target != IntPtr.Zero) vigem_target_remove(client, target);
            if (target != IntPtr.Zero) { vigem_target_free(target); target = IntPtr.Zero; }
            if (connected && client != IntPtr.Zero) vigem_disconnect(client);
            if (client != IntPtr.Zero) { vigem_free(client); client = IntPtr.Zero; }
        }
    }
}
