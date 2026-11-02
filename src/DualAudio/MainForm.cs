using System.Diagnostics;
using DualAudio.Audio;
using DualAudio.Platform;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DualAudio;

internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(29, 43, 55);
    private static readonly Color Muted = Color.FromArgb(96, 112, 125);
    private static readonly Color Accent = Color.FromArgb(0, 119, 105);
    private readonly ComboBox source = NewCombo();
    private readonly ComboBox[] outputs = [NewCombo(), NewCombo()];
    private readonly TrackBar[] volumes = [new(), new()];
    private readonly NumericUpDown[] delays = [new(), new()];
    private readonly Label[] routeNotes = [new(), new()];
    private readonly Label[] volumeLabels = [new(), new()];
    private readonly Button start = NewButton("Включить звук в двух наушниках");
    private readonly Button refresh = NewButton("Обновить");
    private readonly Button test = NewButton("Проверить звук");
    private readonly CheckBox switchDefault = new();
    private readonly Label status = new();
    private readonly Label details = new();
    private readonly Label routeHint = new();
    private readonly ProgressBar meter = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 150 };
    private readonly NotifyIcon tray;
    private readonly AudioRouter router = new();
    private DefaultOutputLease? lease;
    private bool loading;
    private bool stopping;
    private bool routingActive;
    private AppSettings settings;
    private readonly bool persistSettings;

    public MainForm(bool persistSettings = true)
    {
        this.persistSettings = persistSettings;
        Text = "Два наушника";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        ForeColor = Ink;
        BackColor = Color.FromArgb(242, 246, 248);
        ClientSize = new Size(920, 760);
        MinimumSize = SizeFromClientSize(new Size(920, 760));
        StartPosition = FormStartPosition.CenterScreen;
        Icon = MakeIcon();
        settings = AppSettings.Load();
        BuildLayout();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        menu.Items.Add("Остановить", null, (_, _) => StopRouting());
        menu.Items.Add("Закрыть", null, (_, _) => Close());
        tray = new NotifyIcon { Icon = Icon, Text = "Два наушника", ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowWindow();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        FormClosing += (_, _) => { StopRouting(); tray.Visible = false; };
        FormClosed += (_, _) => { timer.Dispose(); tray.Dispose(); router.Dispose(); };
        timer.Tick += (_, _) =>
        {
            meter.Value = Math.Clamp((int)(router.Peak * 100), 0, 100);
            if (router.IsRunning) details.Text = router.Peak > .0004f
                ? "Звук поступает. Громкость каждой пары можно менять независимо в Windows."
                : "Ожидаю звук. Включите музыку или нажмите «Проверить звук».";
        };
        router.Faulted += (_, message) =>
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(() =>
            {
                if (IsDisposed || Disposing) return;
                if (router.IsRunning) return; // Ignore a delayed notification from a previous route.
                StopRouting();
                SetStatus("Передача остановлена", FriendlyAudioError(message) + " Обновите список и включите снова.", true);
            }); }
            catch (InvalidOperationException) { /* Window may close between the check and dispatch. */ }
        };
        start.Click += (_, _) => { if (routingActive) StopRouting(); else StartRouting(); };
        refresh.Click += (_, _) => LoadDevices(true);
        test.Click += async (_, _) => await PlayTestAsync();
        test.Enabled = false;
        source.SelectedIndexChanged += (_, _) => UpdateRoutes();
        foreach (var box in outputs) box.SelectedIndexChanged += (_, _) => UpdateRoutes();
        LoadDevices(false);
        timer.Start();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 20), ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 169));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 269));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 61));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(TextLabel("Два наушника", 25, FontStyle.Bold, new Rectangle(0, 0, 650, 44)));
        header.Controls.Add(TextLabel("Один системный звук — в проводной и Bluetooth-паре.", 10, FontStyle.Regular, new Rectangle(2, 49, 800, 25), Muted));
        root.Controls.Add(header);

        var input = Card();
        input.Controls.Add(TextLabel("1   ИСТОЧНИК ЗВУКА", 10, FontStyle.Bold, new Rectangle(18, 12, 600, 25), Accent));
        source.SetBounds(18, 45, 670, 31); source.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        refresh.SetBounds(701, 44, 114, 32); refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        input.Controls.Add(source); input.Controls.Add(refresh);
        switchDefault.Text = "Назначать источник выходом Windows на время работы";
        switchDefault.SetBounds(18, 84, 790, 28);
        switchDefault.Checked = settings.SwitchWindowsOutput;
        input.Controls.Add(switchDefault);
        routeHint.SetBounds(18, 115, 794, 38); routeHint.ForeColor = Muted;
        routeHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        input.Controls.Add(routeHint);
        root.Controls.Add(input);

        var pair = new TableLayoutPanel { Dock = DockStyle.Fill, Size = new Size(864, 257), ColumnCount = 2, Margin = new Padding(0, 12, 0, 0) };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 2; i++)
        {
            int index = i;
            var card = Card(); card.Size = new Size(426, 257); card.Margin = i == 0 ? new Padding(0, 0, 6, 0) : new Padding(6, 0, 0, 0);
            card.Controls.Add(TextLabel(i == 0 ? "2   ПРОВОДНЫЕ / ПЕРВАЯ ПАРА" : "3   BLUETOOTH / ВТОРАЯ ПАРА", 10, FontStyle.Bold, new Rectangle(16, 14, 380, 25), Accent));
            outputs[i].SetBounds(16, 48, 376, 31); outputs[i].Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(outputs[i]);
            routeNotes[i].SetBounds(16, 88, 376, 36); routeNotes[i].ForeColor = Muted;
            routeNotes[i].Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(routeNotes[i]);
            volumeLabels[i].SetBounds(16, 131, 190, 25); card.Controls.Add(volumeLabels[i]);
            volumes[i].SetBounds(196, 126, 200, 35); volumes[i].Maximum = 100; volumes[i].TickStyle = TickStyle.None;
            volumes[i].Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            volumes[i].Value = Math.Clamp(i == 0 ? settings.FirstVolume : settings.SecondVolume, 0, 100);
            volumes[i].ValueChanged += (_, _) => ApplyOutput(index);
            card.Controls.Add(volumes[i]);
            card.Controls.Add(TextLabel("Задержка копии, мс", 10, FontStyle.Regular, new Rectangle(16, 177, 220, 28)));
            delays[i].SetBounds(285, 176, 103, 30); delays[i].Maximum = 1500; delays[i].Increment = 10;
            delays[i].Anchor = AnchorStyles.Top | AnchorStyles.Right;
            delays[i].Value = Math.Clamp(i == 0 ? settings.FirstDelay : settings.SecondDelay, 0, 1500);
            delays[i].ValueChanged += (_, _) => ApplyOutput(index);
            card.Controls.Add(delays[i]);
            card.Controls.Add(TextLabel("Если звук здесь раньше — увеличьте задержку.", 9, FontStyle.Regular, new Rectangle(16, 216, 380, 28), Muted));
            pair.Controls.Add(card, i, 0);
        }
        root.Controls.Add(pair);

        var state = new Panel { Dock = DockStyle.Fill, Size = new Size(864, 70), Margin = new Padding(0, 13, 0, 0) };
        status.SetBounds(0, 0, 800, 27); status.Font = new Font(Font, FontStyle.Bold);
        details.SetBounds(0, 30, 790, 40); details.ForeColor = Muted;
        state.Controls.Add(status); state.Controls.Add(details);
        meter.SetBounds(738, 5, 97, 13); meter.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        state.Controls.Add(meter);
        root.Controls.Add(state);

        var actions = new Panel { Dock = DockStyle.Fill, Size = new Size(864, 54), Margin = new Padding(0, 7, 0, 0) };
        start.SetBounds(0, 0, 377, 43); start.BackColor = Accent; start.ForeColor = Color.White; start.Font = new Font(Font, FontStyle.Bold);
        test.SetBounds(390, 0, 172, 43);
        var soundSettings = NewButton("Настройки Windows"); soundSettings.SetBounds(575, 0, 184, 43);
        soundSettings.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        var hide = NewButton("В трей"); hide.SetBounds(770, 0, 66, 43); hide.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        hide.Click += (_, _) => HideToTray();
        actions.Controls.Add(start); actions.Controls.Add(test); actions.Controls.Add(soundSettings); actions.Controls.Add(hide);
        root.Controls.Add(actions);
        var foot = TextLabel("Bluetooth всегда добавляет свою задержку. Начните с 120 мс для проводных и подстройте на слух.", 9,
            FontStyle.Regular, new Rectangle(0, 0, 800, 42), Muted);
        foot.Dock = DockStyle.Fill; foot.Padding = new Padding(0, 7, 0, 0); root.Controls.Add(foot);
    }

    private static Panel Card() => new() { Dock = DockStyle.Fill, Size = new Size(864, 169), BackColor = Color.White, Margin = Padding.Empty };
    private static ComboBox NewCombo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, DropDownHeight = 300, DisplayMember = "Name" };
    private static Button NewButton(string text) => new() { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
    private static Label TextLabel(string text, float size, FontStyle style, Rectangle bounds, Color? color = null) => new()
    { Text = text, Bounds = bounds, Font = new Font("Segoe UI", size, style), ForeColor = color ?? Ink };
    private static string? Selected(ComboBox box) => (box.SelectedItem as AudioDeviceInfo)?.Id;

    private void LoadDevices(bool preserve)
    {
        loading = true;
        try
        {
            string? oldSource = preserve ? Selected(source) : settings.SourceId;
            string? oldFirst = preserve ? Selected(outputs[0]) : settings.FirstId;
            string? oldSecond = preserve ? Selected(outputs[1]) : settings.SecondId;
            var devices = AudioDevices.ListActiveOutputs().ToList();
            string? bluetooth = devices.FirstOrDefault(d => !IsVirtual(d.Name) && !IsHandsFree(d.Name) &&
                d.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))?.Id;
            string? wired = devices.FirstOrDefault(d => d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase))?.Id;
            string? fallback = devices.FirstOrDefault(d => d.IsDefault)?.Id ?? devices.FirstOrDefault()?.Id;
            Fill(source, devices, oldSource ?? bluetooth ?? fallback);
            Fill(outputs[0], devices, oldFirst ?? wired ?? fallback);
            Fill(outputs[1], devices, oldSecond ?? bluetooth ?? devices.FirstOrDefault(d => d.Id != Selected(outputs[0]))?.Id);
            SetStatus(devices.Count >= 2 ? "Готово к включению" : "Нужно подключить два устройства",
                devices.Count >= 2 ? "Выберите две разные пары наушников. При первом запуске проверьте громкость." : "Подключите наушники и нажмите «Обновить».");
        }
        catch (Exception ex) { SetStatus("Не удалось прочитать устройства", ex.Message, true); }
        finally { loading = false; UpdateRoutes(); }
    }

    private static void Fill(ComboBox box, List<AudioDeviceInfo> devices, string? id)
    {
        box.Items.Clear(); foreach (var d in devices) box.Items.Add(d);
        box.SelectedItem = devices.FirstOrDefault(d => d.Id == id) ?? devices.FirstOrDefault();
    }
    private static bool IsVirtual(string name) => name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) || name.Contains("Cable", StringComparison.OrdinalIgnoreCase);
    private static bool IsHandsFree(string name) => name.Contains("Hands", StringComparison.OrdinalIgnoreCase) || name.Contains("Головной телефон", StringComparison.OrdinalIgnoreCase);

    private void UpdateRoutes()
    {
        if (loading) return;
        var id = Selected(source);
        for (int i = 0; i < 2; i++)
        {
            bool direct = id != null && id == Selected(outputs[i]);
            volumes[i].Enabled = !direct; delays[i].Enabled = !direct;
            routeNotes[i].Text = direct ? "Прямой звук Windows. Громкость — в Windows." : "Копия звука через приложение.";
            volumeLabels[i].Text = direct ? "Громкость Windows" : $"Громкость копии: {volumes[i].Value}%";
        }
        routeHint.Text = id == Selected(outputs[1])
            ? "Bluetooth играет напрямую; проводным можно добавить задержку для совпадения звука."
            : id == Selected(outputs[0])
                ? "Первая пара играет напрямую. Для выравнивания с Bluetooth выберите Bluetooth источником."
                : "Звук источника копируется в обе пары. Для этой схемы удобно использовать виртуальный кабель.";
    }

    private void ApplyOutput(int i)
    {
        if (loading) return;
        volumeLabels[i].Text = Selected(source) == Selected(outputs[i]) ? "Громкость Windows" : $"Громкость копии: {volumes[i].Value}%";
        if (router.IsRunning) router.SetOutput(i, (int)delays[i].Value, volumes[i].Value / 100f);
    }

    internal void StartFromCommandLine() => StartRouting();

    private void StartRouting()
    {
        try
        {
            if (lease != null || routingActive) StopRouting();
            string src = Selected(source) ?? throw new InvalidOperationException("Выберите источник звука.");
            string first = Selected(outputs[0]) ?? throw new InvalidOperationException("Выберите первую пару.");
            string second = Selected(outputs[1]) ?? throw new InvalidOperationException("Выберите вторую пару.");
            if (first == second) throw new InvalidOperationException("Для двух пар выберите два разных устройства.");
            if ((outputs[1].SelectedItem as AudioDeviceInfo)?.Name is string name && IsHandsFree(name))
                routeNotes[1].Text = "Выбран телефонный профиль: качество может быть ниже.";
            SaveSettings();
            // Open all audio streams first. If an endpoint cannot initialize, the Windows defaults stay untouched.
            router.Start(src, first, second, (int)delays[0].Value, (int)delays[1].Value, volumes[0].Value / 100f, volumes[1].Value / 100f);
            if (switchDefault.Checked) lease = DefaultOutputLease.Acquire(src);
            routingActive = true;
            source.Enabled = false; foreach (var box in outputs) box.Enabled = false;
            refresh.Enabled = false; switchDefault.Enabled = false;
            start.Text = "Остановить"; start.BackColor = Color.FromArgb(154, 58, 61);
            test.Enabled = true;
            tray.Text = "Два наушника — звук включён";
            SetStatus("Звук в двух наушниках включён", "Включите музыку или нажмите «Проверить звук».");
        }
        catch (Exception ex)
        {
            StopRouting();
            string message = FriendlyAudioError(ex.Message);
            SetStatus("Не удалось включить звук", message, true);
            MessageBox.Show(this, message + "\n\nПроверьте подключение и выбранные устройства. Если Windows не разрешает переключение выхода, выберите источник вручную в настройках звука и снимите галочку переключения.",
                "Два наушника", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void StopRouting()
    {
        if (stopping) return;
        stopping = true;
        try
        {
            Exception? stopError = null;
            try { router.Stop(); }
            catch (Exception ex) { stopError = ex; }
            finally { lease?.Dispose(); }
            string? restoreError = lease?.RestoreError?.Message;
            lease = null;
            routingActive = false;
            source.Enabled = true; foreach (var box in outputs) box.Enabled = true;
            refresh.Enabled = true; switchDefault.Enabled = true;
            start.Text = "Включить звук в двух наушниках"; start.BackColor = Accent;
            test.Enabled = false;
            tray.Text = "Два наушника"; meter.Value = 0;
            SetStatus("Передача остановлена", restoreError ?? stopError?.Message ?? "Можно изменить устройства и включить передачу снова.", restoreError != null || stopError != null);
            try { SaveSettings(); } catch { /* Read-only folders must not prevent audio cleanup. */ }
        }
        finally { stopping = false; }
    }

    private void SaveSettings()
    {
        if (!persistSettings) return;
        settings.SourceId = Selected(source); settings.FirstId = Selected(outputs[0]); settings.SecondId = Selected(outputs[1]);
        settings.FirstDelay = (int)delays[0].Value; settings.SecondDelay = (int)delays[1].Value;
        settings.FirstVolume = volumes[0].Value; settings.SecondVolume = volumes[1].Value;
        settings.SwitchWindowsOutput = switchDefault.Checked;
        settings.Save();
    }

    private async Task PlayTestAsync()
    {
        string? id = Selected(source); if (id == null) return;
        test.Enabled = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(id);
            using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
            var signal = new SignalGenerator(48000, 2) { Frequency = 440, Gain = .045, Type = SignalGeneratorType.Sin };
            player.Init(new SampleToWaveProvider(signal.Take(TimeSpan.FromSeconds(.8))));
            player.Play(); await Task.Delay(950); player.Stop();
            if (!router.IsRunning) SetStatus("Проверка источника завершена", "Чтобы сигнал шёл в две пары, сначала включите передачу.");
        }
        catch (Exception e) { SetStatus("Проверка не удалась", e.Message, true); }
        finally { if (!IsDisposed) test.Enabled = router.IsRunning; }
    }

    private void SetStatus(string title, string message, bool error = false)
    { status.Text = title; status.ForeColor = error ? Color.FromArgb(163, 53, 57) : Ink; details.Text = message; }
    private static string FriendlyAudioError(string message) => message.Contains("0x8889000A", StringComparison.OrdinalIgnoreCase)
        ? "Устройство занято другой программой в монопольном режиме. Закройте Voicemeeter или другой аудиомикшер и повторите запуск."
        : message.Contains("0x88890004", StringComparison.OrdinalIgnoreCase)
            ? "Аудиоустройство отключено или его режим изменился. Подключите наушники снова."
            : message;
    private void HideToTray() { tray.Visible = true; Hide(); }
    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); tray.Visible = false; }

    internal void VerifyLayout()
    {
        foreach (Control control in new Control[] { source, refresh, outputs[0], outputs[1], volumes[0], volumes[1], delays[0], delays[1], start, test })
            if (control.Parent == null || !control.Parent.ClientRectangle.Contains(control.Bounds))
                throw new InvalidOperationException($"Control outside panel: {control.GetType().Name} {control.Bounds}; parent {control.Parent?.ClientSize}; dpi {DeviceDpi}");
        var buttons = start.Parent!.Controls.OfType<Button>().ToArray();
        for (int i = 0; i < buttons.Length; i++)
            for (int j = i + 1; j < buttons.Length; j++)
                if (buttons[i].Bounds.IntersectsWith(buttons[j].Bounds))
                    throw new InvalidOperationException($"Overlapping buttons: {buttons[i].Text} / {buttons[j].Text}");
    }

    private static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bitmap); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var pen = new Pen(Accent, 7); g.DrawArc(pen, 11, 9, 42, 47, 180, 180);
        using var brush = new SolidBrush(Accent); g.FillRectangle(brush, new Rectangle(7, 29, 14, 25));
        g.FillRectangle(brush, new Rectangle(43, 29, 14, 25));
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
