using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ps2ToXbox
{
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
        [STAThread]
        private static void Main()
        {
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
        private readonly ComboBox devices = new ComboBox();
        private readonly Label physicalStatus = new Label();
        private readonly Label outputStatus = new Label();
        private readonly Label leftValues = new Label();
        private readonly Label rightValues = new Label();
        private readonly StickVisual leftStick = new StickVisual();
        private readonly StickVisual rightStick = new StickVisual();
        private readonly XboxControllerVisual xboxVisual = new XboxControllerVisual();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly Timer timer = new Timer();
        private VirtualXbox360 virtualPad;
        private bool running;

        public MainForm()
        {
            Text = "ps2toxbox";
            Icon = EmbeddedRuntime.LoadIcon();
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1080, 740);
            MinimumSize = new Size(900, 660);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(16, 19, 26);
            ForeColor = Color.FromArgb(237, 242, 250);
            Font = new Font("Segoe UI", 9.5F);
            BuildUi();
            timer.Interval = 8;
            timer.Tick += OnTick;
            Rescan();
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                Padding = new Padding(26, 22, 26, 18), BackColor = Color.FromArgb(16, 19, 26) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildConnectionCards(), 0, 1);
            root.Controls.Add(BuildMonitor(), 0, 2);
            root.Controls.Add(BuildFooter(), 0, 3);
            devices.SelectedIndexChanged += delegate { UpdatePhysicalStatus(); };
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            var mark = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(16, 19, 26), Margin = new Padding(0, 2, 12, 2), Image = EmbeddedRuntime.LoadLogo() };
            var copy = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            copy.Controls.Add(new Label { Text = "ps2toxbox", Font = new Font("Segoe UI", 23F, FontStyle.Bold),
                ForeColor = Color.FromArgb(239, 243, 249), AutoSize = true, Location = new Point(0, 0) });
            copy.Controls.Add(new Label { Text = "PS2 GAMEPAD  /  XBOX 360 XINPUT", Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(139, 151, 169), AutoSize = true, Location = new Point(3, 42) });
            var badge = new Label { Text = "EARLY ACCESS  ·  v0.1", AutoSize = false, Size = new Size(176, 30),
                TextAlign = ContentAlignment.MiddleCenter, Anchor = AnchorStyles.Right | AnchorStyles.Top,
                BackColor = Color.FromArgb(27, 39, 42), ForeColor = Color.FromArgb(112, 224, 182),
                Font = new Font("Segoe UI", 8F, FontStyle.Bold), Location = new Point(14, 19) };
            header.Controls.Add(mark, 0, 0); header.Controls.Add(copy, 1, 0); header.Controls.Add(badge, 2, 0);
            return header;
        }

        private Control BuildConnectionCards()
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var input = NewCard();
            var inputLayout = CardLayout(4);
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            inputLayout.Controls.Add(SectionTitle("01   INPUT DEVICE"), 0, 0);
            physicalStatus.Dock = DockStyle.Fill; physicalStatus.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            inputLayout.Controls.Add(physicalStatus, 0, 1);
            var selectRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            selectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            devices.Dock = DockStyle.Fill; devices.Margin = new Padding(0, 2, 10, 2); StyleCombo(devices);
            var scan = MakeButton("↻   Scan", false); scan.Click += delegate { Rescan(); };
            scan.Dock = DockStyle.Fill; scan.Margin = new Padding(0, 2, 0, 2);
            selectRow.Controls.Add(devices, 0, 0); selectRow.Controls.Add(scan, 1, 0);
            inputLayout.Controls.Add(selectRow, 0, 2);
            inputLayout.Controls.Add(MutedLabel("Select a gamepad adapter detected by Windows."), 0, 3);
            input.Controls.Add(inputLayout);

            var output = NewCard();
            var outputLayout = CardLayout(4);
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outputLayout.Controls.Add(SectionTitle("02   XBOX VIRTUAL OUTPUT"), 0, 0);
            outputStatus.Dock = DockStyle.Fill; outputStatus.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            outputLayout.Controls.Add(outputStatus, 0, 1);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            start.Text = "Start controller"; StyleButton(start, true); start.Dock = DockStyle.Fill; start.Margin = new Padding(0, 2, 6, 2);
            stop.Text = "Stop"; StyleButton(stop, false); stop.Dock = DockStyle.Fill; stop.Margin = new Padding(6, 2, 0, 2); stop.Enabled = false;
            start.Click += OnStart; stop.Click += delegate { StopVirtual("Virtual controller stopped."); };
            actions.Controls.Add(start, 0, 0); actions.Controls.Add(stop, 1, 0);
            outputLayout.Controls.Add(actions, 0, 2);
            outputLayout.Controls.Add(MutedLabel("ViGEmBus will create one virtual Xbox 360 controller."), 0, 3);
            output.Controls.Add(outputLayout);

            grid.Controls.Add(input, 0, 0); grid.Controls.Add(output, 1, 0);
            return grid;
        }

        private Control BuildMonitor()
        {
            var card = NewCard();
            var layout = CardLayout(2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
            var heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            heading.Controls.Add(new Label { Text = "LIVE INPUT MONITOR", Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(235, 240, 247), AutoSize = true, Location = new Point(0, 0) });
            heading.Controls.Add(new Label { Text = "Move a stick or press a button to see its input.", Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(139, 151, 169), AutoSize = true, Location = new Point(1, 25) });
            var frequency = new Label { Text = "POLLING  ·  125 HZ", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(106, 120, 140), Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
            header.Controls.Add(heading, 0, 0); header.Controls.Add(frequency, 1, 0);
            layout.Controls.Add(header, 0, 0);

            var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            columns.Controls.Add(BuildStickPanel("LEFT STICK", leftStick, leftValues), 0, 0);
            columns.Controls.Add(BuildStickPanel("RIGHT STICK", rightStick, rightValues), 1, 0);
            columns.Controls.Add(BuildXboxPanel(), 2, 0);
            layout.Controls.Add(columns, 0, 1);
            card.Controls.Add(layout);
            return card;
        }

        private Control BuildStickPanel(string title, StickVisual visual, Label values)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 8, 0) };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            panel.Controls.Add(SectionTitle(title), 0, 0);
            var stage = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 25, 33), Margin = new Padding(0, 3, 0, 3) };
            visual.Dock = DockStyle.Fill;
            stage.Controls.Add(visual);
            panel.Controls.Add(stage, 0, 1);
            values.Text = "X  0.00        Y  0.00"; values.Dock = DockStyle.Fill;
            values.TextAlign = ContentAlignment.MiddleCenter; values.ForeColor = Color.FromArgb(116, 224, 183);
            values.Font = new Font("Consolas", 10F, FontStyle.Bold);
            panel.Controls.Add(values, 0, 2);
            return panel;
        }

        private Control BuildXboxPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(12, 0, 0, 0) };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.Controls.Add(SectionTitle("XBOX 360 OUTPUT"), 0, 0);
            xboxVisual.Dock = DockStyle.Fill;
            panel.Controls.Add(xboxVisual, 0, 1);
            return panel;
        }

        private Control BuildFooter()
        {
            return new Label { Text = "PS2TOXBOX  ·  PS2 GAMEPAD TO XINPUT", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(91, 103, 121), Font = new Font("Segoe UI", 8F, FontStyle.Bold), Margin = Padding.Empty };
        }

        private static Panel NewCard()
        {
            return new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(18), BackColor = Color.FromArgb(25, 30, 41), Margin = new Padding(0, 0, 12, 0) };
        }

        private static TableLayoutPanel CardLayout(int rows)
        {
            return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Margin = Padding.Empty,
                BackColor = Color.FromArgb(25, 30, 41) };
        }

        private static Label SectionTitle(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(139, 151, 169), Font = new Font("Segoe UI", 8F, FontStyle.Bold), Margin = Padding.Empty };
        }

        private static Label MutedLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(112, 124, 142), Font = new Font("Segoe UI", 8.5F), AutoEllipsis = true, Margin = Padding.Empty };
        }

        private static void StyleCombo(ComboBox c)
        {
            c.DropDownStyle = ComboBoxStyle.DropDownList; c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Color.FromArgb(35, 42, 55); c.ForeColor = Color.FromArgb(233, 238, 246);
            c.Font = new Font("Segoe UI", 9F); c.IntegralHeight = false; c.DropDownHeight = 300;
        }

        private Button MakeButton(string text, bool primary)
        {
            var b = new Button { Text = text, Height = 38, FlatStyle = FlatStyle.Flat };
            StyleButton(b, primary); return b;
        }

        private static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Color.FromArgb(70, 210, 158) : Color.FromArgb(58, 70, 88);
            b.BackColor = primary ? Color.FromArgb(44, 115, 88) : Color.FromArgb(37, 45, 59);
            b.ForeColor = Color.FromArgb(239, 244, 250); b.Cursor = Cursors.Hand;
            b.Font = new Font("Segoe UI", 9F, FontStyle.Bold); b.UseVisualStyleBackColor = false;
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
            physicalStatus.Text = devices.SelectedItem == null ? "●  No controller detected" : "●  Connected: " + devices.SelectedItem;
            physicalStatus.ForeColor = devices.SelectedItem == null ? Color.FromArgb(155, 166, 184) : Color.FromArgb(119, 224, 181);
            start.Enabled = !running && devices.SelectedItem != null;
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (devices.SelectedItem == null) return;
            try
            {
                virtualPad = new VirtualXbox360();
                virtualPad.Connect();
                running = true; devices.Enabled = false; start.Enabled = false; stop.Enabled = true; timer.Start();
                outputStatus.Text = "●  Xbox 360 virtual aktif"; outputStatus.ForeColor = Color.FromArgb(119, 224, 181);
            }
            catch (DllNotFoundException)
            {
                StopVirtual("ViGEmClient could not be loaded. Make sure the ps2toxbox executable is intact and required Windows components are available.");
            }
            catch (Exception ex)
            {
                StopVirtual("Could not create the virtual controller: " + ex.Message);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            var d = devices.SelectedItem as JoystickDevice;
            if (d == null || virtualPad == null) { StopVirtual("Controller disconnected."); return; }
            JoystickState state;
            if (!WinMmInput.TryRead(d, out state)) { StopVirtual("Controller disconnected. Reconnect it to continue."); return; }
            leftValues.Text = "X " + state.LeftX.ToString("0.00") + "     Y " + state.LeftY.ToString("0.00");
            rightValues.Text = "X " + state.RightX.ToString("0.00") + "     Y " + state.RightY.ToString("0.00");
            leftStick.SetPosition(state.LeftX, state.LeftY);
            rightStick.SetPosition(state.RightX, state.RightY);
            try
            {
                XusbReport report = state.ToXusbReport();
                virtualPad.Update(report);
                xboxVisual.SetReport(report);
            }
            catch (Exception ex) { StopVirtual("Sesi output terhenti: " + ex.Message); }
        }

        private void StopVirtual(string message)
        {
            timer.Stop();
            if (virtualPad != null) { virtualPad.Dispose(); virtualPad = null; }
            xboxVisual.SetReport(new XusbReport());
            running = false; devices.Enabled = true; stop.Enabled = false; start.Enabled = devices.SelectedItem != null;
            outputStatus.Text = message; outputStatus.ForeColor = Color.FromArgb(155, 166, 184);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopVirtual("Virtual controller stopped."); base.OnFormClosing(e);
        }
    }

    internal sealed class SurfacePanel : Panel
    {
        public SurfacePanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(43, 51, 66)))
                e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
    }

    internal sealed class StickVisual : Control
    {
        private double xValue, yValue;
        private static readonly Color Accent = Color.FromArgb(89, 220, 169);

        public StickVisual()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(20, 25, 33);
        }

        public void SetPosition(double x, double y)
        {
            x = Math.Max(-1, Math.Min(1, x));
            y = Math.Max(-1, Math.Min(1, y));
            if (Math.Abs(x - xValue) < 0.005 && Math.Abs(y - yValue) < 0.005) return;
            xValue = x; yValue = y; Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float radius = Math.Max(8, Math.Min(Width, Height) * 0.39f);
            float cx = Width / 2f, cy = Height / 2f;
            using (var gridPen = new Pen(Color.FromArgb(55, 66, 82), 1f))
            using (var ringPen = new Pen(Color.FromArgb(70, 85, 103), 1.5f))
            using (var axisPen = new Pen(Color.FromArgb(39, 48, 62), 1f))
            using (var guidePen = new Pen(Color.FromArgb(84, 210, 164), 1.5f))
            using (var dotBrush = new SolidBrush(Accent))
            using (var dotOutline = new Pen(Color.FromArgb(225, 255, 245), 2f))
            using (var centerBrush = new SolidBrush(Color.FromArgb(98, 111, 130)))
            {
                e.Graphics.DrawEllipse(gridPen, cx - radius, cy - radius, radius * 2, radius * 2);
                e.Graphics.DrawEllipse(gridPen, cx - radius * 0.65f, cy - radius * 0.65f, radius * 1.3f, radius * 1.3f);
                e.Graphics.DrawLine(axisPen, cx - radius, cy, cx + radius, cy);
                e.Graphics.DrawLine(axisPen, cx, cy - radius, cx, cy + radius);
                e.Graphics.FillEllipse(centerBrush, cx - 2.5f, cy - 2.5f, 5, 5);
                float px = cx + (float)xValue * radius, py = cy - (float)yValue * radius;
                if (Math.Abs(xValue) > 0.01 || Math.Abs(yValue) > 0.01) e.Graphics.DrawLine(guidePen, cx, cy, px, py);
                e.Graphics.FillEllipse(dotBrush, px - 7, py - 7, 14, 14);
                e.Graphics.DrawEllipse(dotOutline, px - 7, py - 7, 14, 14);
            }
        }
    }

    internal sealed class XboxControllerVisual : Control
    {
        private XusbReport report;
        private static readonly Color Accent = Color.FromArgb(83, 218, 164);
        private static readonly Color Body = Color.FromArgb(39, 47, 61);

        public XboxControllerVisual()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(25, 30, 41);
        }

        public void SetReport(XusbReport value) { report = value; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float scale = Math.Min(Width / 400f, Height / 270f);
            if (scale <= 0) return;
            e.Graphics.TranslateTransform((Width - 400 * scale) / 2f, (Height - 270 * scale) / 2f);
            e.Graphics.ScaleTransform(scale, scale);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            using (var fill = new SolidBrush(Body))
            using (var edge = new Pen(Color.FromArgb(75, 88, 108), 2f))
            using (var detail = new Pen(Color.FromArgb(59, 71, 89), 1.2f))
            using (var textBrush = new SolidBrush(Color.FromArgb(130, 145, 165)))
            using (var font = new Font("Segoe UI", 8f, FontStyle.Bold))
            {
                path.StartFigure();
                path.AddBezier(60, 65, 76, 24, 111, 22, 151, 44);
                path.AddBezier(151, 44, 172, 56, 184, 47, 196, 37);
                path.AddBezier(196, 37, 213, 23, 235, 23, 252, 37);
                path.AddBezier(252, 37, 272, 53, 288, 48, 316, 33);
                path.AddBezier(316, 33, 342, 19, 365, 37, 373, 68);
                path.AddBezier(373, 68, 390, 124, 377, 194, 344, 236);
                path.AddBezier(344, 236, 320, 266, 289, 260, 264, 224);
                path.AddBezier(264, 224, 244, 195, 156, 195, 136, 224);
                path.AddBezier(136, 224, 111, 260, 80, 266, 56, 236);
                path.AddBezier(56, 236, 23, 194, 11, 124, 28, 68);
                path.AddBezier(28, 68, 36, 38, 51, 25, 60, 65);
                path.CloseFigure();
                e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path);

                using (var lt = new SolidBrush(report.LeftTrigger > 0 ? Accent : Color.FromArgb(50, 60, 75)))
                using (var rt = new SolidBrush(report.RightTrigger > 0 ? Accent : Color.FromArgb(50, 60, 75)))
                {
                    e.Graphics.FillRectangle(lt, 83, 39, 76, 10); e.Graphics.FillRectangle(rt, 241, 39, 76, 10);
                }
                e.Graphics.DrawString("LT", font, textBrush, 111, 22); e.Graphics.DrawString("RT", font, textBrush, 267, 22);
                e.Graphics.DrawRectangle(detail, 81, 54, 79, 14); e.Graphics.DrawRectangle(detail, 240, 54, 79, 14);
                if ((report.Buttons & 0x0100) != 0) e.Graphics.FillRectangle(Brushes.MediumSeaGreen, 81, 54, 79, 14);
                if ((report.Buttons & 0x0200) != 0) e.Graphics.FillRectangle(Brushes.MediumSeaGreen, 240, 54, 79, 14);

                DrawStick(e.Graphics, 112, 99, report.ThumbLX, report.ThumbLY, (report.Buttons & 0x0040) != 0);
                DrawStick(e.Graphics, 256, 164, report.ThumbRX, report.ThumbRY, (report.Buttons & 0x0080) != 0);
                DrawDpad(e.Graphics, report.Buttons);
                DrawFaceButton(e.Graphics, 314, 86, "Y", Color.FromArgb(224, 183, 81), 0x8000);
                DrawFaceButton(e.Graphics, 338, 110, "B", Color.FromArgb(221, 92, 93), 0x2000);
                DrawFaceButton(e.Graphics, 290, 110, "X", Color.FromArgb(87, 151, 224), 0x4000);
                DrawFaceButton(e.Graphics, 314, 134, "A", Color.FromArgb(87, 198, 132), 0x1000);
                DrawSmallButton(e.Graphics, 184, 72, "VIEW", (report.Buttons & 0x0020) != 0);
                DrawSmallButton(e.Graphics, 220, 72, "MENU", (report.Buttons & 0x0010) != 0);
                e.Graphics.DrawString("XBOX 360", font, textBrush, 178, 97);
            }
            e.Graphics.ResetTransform();
        }

        private static void DrawStick(Graphics g, float x, float y, short axisX, short axisY, bool pressed)
        {
            using (var rim = new SolidBrush(Color.FromArgb(22, 27, 36)))
            using (var outline = new Pen(pressed ? Accent : Color.FromArgb(75, 88, 108), 2f))
            using (var center = new SolidBrush(Color.FromArgb(57, 67, 83)))
            using (var dot = new SolidBrush(Accent))
            {
                g.FillEllipse(rim, x - 24, y - 24, 48, 48); g.DrawEllipse(outline, x - 24, y - 24, 48, 48);
                g.FillEllipse(center, x - 17, y - 17, 34, 34);
                float px = x + axisX / 32767f * 13f, py = y - axisY / 32767f * 13f;
                g.FillEllipse(dot, px - 5, py - 5, 10, 10);
            }
        }

        private static void DrawDpad(Graphics g, ushort buttons)
        {
            using (var ub = new SolidBrush((buttons & 1) != 0 ? Accent : Color.FromArgb(81, 94, 113)))
            using (var db = new SolidBrush((buttons & 2) != 0 ? Accent : Color.FromArgb(81, 94, 113)))
            using (var lb = new SolidBrush((buttons & 4) != 0 ? Accent : Color.FromArgb(81, 94, 113)))
            using (var rb = new SolidBrush((buttons & 8) != 0 ? Accent : Color.FromArgb(81, 94, 113)))
            using (var center = new SolidBrush(Color.FromArgb(31, 38, 50)))
            {
                g.FillRectangle(ub, 130, 124, 18, 19); g.FillRectangle(db, 130, 160, 18, 19);
                g.FillRectangle(lb, 112, 142, 19, 18); g.FillRectangle(rb, 147, 142, 19, 18);
                g.FillRectangle(center, 131, 143, 15, 16);
            }
        }

        private void DrawFaceButton(Graphics g, float x, float y, string label, Color color, ushort mask)
        {
            bool active = (report.Buttons & mask) != 0;
            using (var fill = new SolidBrush(active ? color : Color.FromArgb(49, 59, 74)))
            using (var pen = new Pen(active ? Color.FromArgb(235, 244, 250) : color, active ? 2f : 1.5f))
            using (var font = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var text = new SolidBrush(active ? Color.White : color))
            {
                g.FillEllipse(fill, x - 11, y - 11, 22, 22); g.DrawEllipse(pen, x - 11, y - 11, 22, 22);
                SizeF size = g.MeasureString(label, font); g.DrawString(label, font, text, x - size.Width / 2f, y - size.Height / 2f);
            }
        }

        private static void DrawSmallButton(Graphics g, float x, float y, string label, bool active)
        {
            using (var fill = new SolidBrush(active ? Accent : Color.FromArgb(62, 74, 92)))
            using (var font = new Font("Segoe UI", 5.5f, FontStyle.Bold))
            using (var text = new SolidBrush(active ? Color.FromArgb(18, 25, 27) : Color.FromArgb(171, 183, 199)))
            {
                g.FillEllipse(fill, x - 8, y - 5, 16, 10);
                SizeF size = g.MeasureString(label, font); g.DrawString(label, font, text, x - size.Width / 2f, y + 9);
            }
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

        public string ButtonSummary()
        {
            var names = new List<string>();
            // Button order follows the user's x360ce.ini profile.
            string[] labels = { "Y", "B", "A", "X", "LB", "RB", "LT", "RT", "Back", "Start", "L3", "R3" };
            for (int i = 0; i < labels.Length; i++) if ((Buttons & (1u << i)) != 0) names.Add(labels[i]);
            if (Pov != WinMmInput.PovCentered)
            {
                if (Pov >= 31500 || Pov < 4500) names.Add("D-pad ↑");
                if (Pov >= 4500 && Pov < 13500) names.Add("D-pad →");
                if (Pov >= 13500 && Pov < 22500) names.Add("D-pad ↓");
                if (Pov >= 22500 && Pov < 31500) names.Add("D-pad ←");
            }
            return names.Count == 0 ? "No buttons pressed" : String.Join("  ·  ", names.ToArray());
        }

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
            return new XusbReport { Buttons = b, LeftTrigger = (byte)(((Buttons & (1u << 6)) != 0) ? 255 : 0),
                RightTrigger = (byte)(((Buttons & (1u << 7)) != 0) ? 255 : 0),
                ThumbLX = Axis(LeftX), ThumbLY = Axis(LeftY), ThumbRX = Axis(RightX), ThumbRY = Axis(RightY) };
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
            state = new JoystickState { Buttons = info.Buttons, Pov = info.Pov,
                LeftX = Normalize(info.X, c.XMin, c.XMax), LeftY = -Normalize(info.Y, c.YMin, c.YMax),
                RightX = Normalize(info.Z, c.ZMin, c.ZMax), RightY = -Normalize(info.R, c.RMin, c.RMax) };
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
