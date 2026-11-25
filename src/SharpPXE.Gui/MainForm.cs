// =====================================================================================
//  MainForm.cs - the SharpPXE window
// =====================================================================================
//
//  Layout:
//    ┌───────────────────────────────────────────────────────────────┐
//    │ [▶ Start server] [Save] [Reload] [Edit INI]   status text     │  toolbar
//    ├───────────────────────────────────────────────────────────────┤
//    │ General | Boot files | Per machine | DHCP server | TFTP | Service │  settings tabs
//    ├───────────────────────────────────────────────────────────────┤
//    │ live log (colored)                                            │  log pane
//    └───────────────────────────────────────────────────────────────┘
//
//  The UI is built in code instead of the Designer, so the whole window can be read in one
//  file. Each setting control is paired with its INI section/key through a small "binding"
//  (Field) record: LoadSettings() copies INI -> controls, SaveSettings() copies controls ->
//  INI. Adding a setting is one line in a Build*Tab method.
// =====================================================================================

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;

namespace SharpPXE.Gui;

public sealed class MainForm : Form
{
    /// <summary>One INI setting bound to one control.</summary>
    sealed record Field(string Section, string Key, string Default, Func<string> Get, Action<string> Set);

    readonly List<Field> _fields = new();
    readonly ServerProcess _server = new();
    readonly LogView _log = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _serviceTimer = new() { Interval = 2000 };
    readonly System.Windows.Forms.Timer _tailTimer = new() { Interval = 500 };

    string _serverExe = "";
    string _iniPath = "";
    IniDocument _ini = null!;
    bool _loading, _dirty, _stopRequested;
    ServiceControllerStatus? _serviceStatus;

    // Tailing the service's log file
    string? _tailPath;
    long _tailPos;
    bool _tailSkipPartial;

    // ---- controls ----
    readonly Button _btnOnline = new() { AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    readonly Button _btnSave = new() { Text = "Save", AutoSize = true, Enabled = false };
    readonly Button _btnReload = new() { Text = "Reload", AutoSize = true };
    readonly Button _btnEditIni = new() { Text = "Edit INI...", AutoSize = true };
    readonly Label _lblStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 8, 0, 0) };
    readonly ComboBox _cbMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    readonly CheckedListBox _clbBind = new() { CheckOnClick = true, Height = 110, IntegralHeight = false };
    readonly TextBox _txtRoot = new();
    readonly DataGridView _gridMac = new();
    readonly Dictionary<string, (TextBox Box, Label State)> _bootFields = new();
    readonly List<Control> _dhcpControls = new();
    readonly Label _lblService = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    readonly Button _btnSvcInstall = new() { Text = "Install service", AutoSize = true };
    readonly Button _btnSvcUninstall = new() { Text = "Uninstall", AutoSize = true };
    readonly Button _btnSvcStart = new() { Text = "Start", AutoSize = true };
    readonly Button _btnSvcStop = new() { Text = "Stop", AutoSize = true };
    readonly ToolStripStatusLabel _stServer = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _stService = new();

    static readonly string[] ModeValues = { "proxy", "dhcp", "off" };

    public MainForm(string[] args)
    {
        Text = "SharpPXE";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1000, 800);
        MinimumSize = new Size(820, 600);
        Icon = SystemIcons.Application;

        ParseArgs(args);
        BuildLayout();

        _server.Line += _log.Enqueue;
        _server.Exited += code => BeginInvoke(() => OnServerExited(code));
        _serviceTimer.Tick += (_, _) => RefreshService();
        _tailTimer.Tick += (_, _) => TailServiceLog();

        Load += (_, _) =>
        {
            LoadSettings();
            RefreshService();
            UpdateServerUi();
            _serviceTimer.Start();
            if (!File.Exists(_serverExe))
                _log.Enqueue($"ERR SharpPXE.exe not found at {_serverExe}. Put SharpPXE.Gui.exe next to it, or pass --server <path>.");
            else
                _log.Enqueue($"-- Server: {_serverExe}");
            _log.Enqueue($"-- Config: {_iniPath}");
        };
        FormClosing += OnClosing;
    }

    // =====================================================================================
    //  Startup: where are SharpPXE.exe and SharpPXE.ini?
    // =====================================================================================

    void ParseArgs(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].Equals("--server", StringComparison.OrdinalIgnoreCase)) _serverExe = Path.GetFullPath(args[++i]);
            else if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase)) _iniPath = Path.GetFullPath(args[++i]);
        }
        if (_serverExe.Length == 0) _serverExe = FindServerExe();
        if (_iniPath.Length == 0) _iniPath = Path.Combine(Path.GetDirectoryName(_serverExe)!, "SharpPXE.ini");
    }

    /// <summary>
    /// Next to the GUI in a published folder. When running from Visual Studio / dotnet run,
    /// fall back to the server project's build output so development works without copying.
    /// </summary>
    static string FindServerExe()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "SharpPXE.exe");
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", "SharpPXE", "bin");
            if (!Directory.Exists(bin)) continue;
            var found = Directory.EnumerateFiles(bin, "SharpPXE.exe", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (found is not null) return found.FullName;
        }
        return beside;
    }

    /// <summary>The server resolves relative paths (root=, log=) against its own folder.</summary>
    string ServerDir => Path.GetDirectoryName(_serverExe)!;

    string RootFullPath
    {
        get
        {
            var r = _txtRoot.Text.Trim();
            if (r.Length == 0) r = "files";
            try { return Path.GetFullPath(Path.Combine(ServerDir, r)); } catch { return r; }
        }
    }

    // =====================================================================================
    //  Layout
    // =====================================================================================

    void BuildLayout()
    {
        // ---- toolbar ----
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4), WrapContents = false };
        _btnOnline.Font = new Font(Font, FontStyle.Bold);
        _btnOnline.Click += (_, _) => ToggleServer();
        _btnSave.Click += (_, _) => SaveSettings(askRestart: true);
        _btnReload.Click += (_, _) => { if (ConfirmDiscard()) LoadSettings(); };
        _btnEditIni.Click += (_, _) => OpenInEditor(_iniPath);
        toolbar.Controls.AddRange(new Control[] { _btnOnline, _btnSave, _btnReload, _btnEditIni, _lblStatus });

        // ---- tabs ----
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildBootTab());
        tabs.TabPages.Add(BuildMacTab());
        tabs.TabPages.Add(BuildDhcpTab());
        tabs.TabPages.Add(BuildTftpTab());
        tabs.TabPages.Add(BuildServiceTab());

        // ---- log ----
        var logBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
        var lblLog = new Label { Text = "Log", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 7, 12, 0) };
        var btnClear = new Button { Text = "Clear", AutoSize = true };
        btnClear.Click += (_, _) => _log.ClearLog();
        var btnCopy = new Button { Text = "Copy all", AutoSize = true };
        btnCopy.Click += (_, _) => { if (_log.TextLength > 0) Clipboard.SetText(_log.Text); };
        logBar.Controls.AddRange(new Control[] { lblLog, btnClear, btnCopy });
        var logPanel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
        logPanel.Controls.Add(_log);
        logPanel.Controls.Add(logBar);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Panel1MinSize = 200, Panel2MinSize = 120 };
        split.Panel1.Controls.Add(tabs);
        split.Panel2.Controls.Add(logPanel);
        split.Panel1.Padding = new Padding(8, 0, 8, 4);
        split.Panel2.Padding = new Padding(8, 0, 8, 0);

        var status = new StatusStrip();
        status.Items.Add(_stServer);
        status.Items.Add(_stService);

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(status);
        Shown += (_, _) => split.SplitterDistance = (int)(split.Height * 0.55);
    }

    TabPage BuildGeneralTab()
    {
        var t = NewGrid();

        foreach (var m in new[] { "proxyDHCP - add boot info next to your existing DHCP server", "DHCP server - also hand out IP addresses", "Off - TFTP and HTTP only" })
            _cbMode.Items.Add(m);
        _cbMode.SelectedIndexChanged += (_, _) => { UpdateDhcpEnabled(); MarkDirty(); };
        Bind("server", "mode", "proxy",
            () => ModeValues[Math.Max(0, _cbMode.SelectedIndex)],
            v => _cbMode.SelectedIndex = Math.Max(0, Array.IndexOf(ModeValues, v.ToLowerInvariant() switch
            {
                "proxydhcp" => "proxy", "full" => "dhcp", "none" or "0" => "off", var x => x,
            })));
        Row(t, "Mode", _cbMode);

        // ItemCheck fires before the check state changes; only react to the user's clicks, not to loading.
        _clbBind.ItemCheck += (_, _) => { if (!_loading) SetDirty(true); };
        Bind("server", "bind", "", GetBind, SetBind);
        Row(t, "Serve on", _clbBind, hint: "Tick the adapters to serve on. None ticked = all adapters (except 169.254.x.x). Ticking only the boot network is recommended.");

        var btnRoot = SmallButton("...");
        btnRoot.Click += (_, _) => BrowseRoot();
        var btnOpenRoot = SmallButton("Open");
        btnOpenRoot.Click += (_, _) => { if (Directory.Exists(RootFullPath)) Process.Start("explorer.exe", RootFullPath); };
        _txtRoot.TextChanged += (_, _) => { MarkDirty(); RefreshBootStates(); };
        Bind("server", "root", "files", () => _txtRoot.Text.Trim(), v => _txtRoot.Text = v);
        Row(t, "Root folder", _txtRoot, Flow(btnRoot, btnOpenRoot), "Served over TFTP and HTTP. Relative paths are relative to SharpPXE.exe.");

        Row(t, "TFTP", BoolField("server", "tftp", true, "Enable TFTP server (UDP 69)"));
        var http = BoolField("server", "http", true, "Enable HTTP server, port");
        var port = NumField("server", "http_port", 80, 1, 65535);
        Row(t, "HTTP", Flow(http, port));
        Row(t, "Log file", TextField("server", "log", ""), hint: "Optional, e.g. SharpPXE.log. Needed to see the log of the Windows service here.");
        Row(t, "Firewall", BoolField("server", "firewall", true, "Create the Windows Firewall rule when the server starts"),
            hint: "An inbound rule named SharpPXE for SharpPXE.exe. If it's missing, Start server asks for administrator rights once. Turn off if Group Policy manages the firewall.");
        Row(t, "Debug", BoolField("server", "verbose", false, "Verbose logging (also shows ignored DHCP packets)"));
        return Page("General", t);
    }

    TabPage BuildBootTab()
    {
        var t = NewGrid();
        void BootRow(string key, string label, string def, bool url)
        {
            var box = TextField("boot", key, def);
            var state = new Label { AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
            var browse = SmallButton("...");
            browse.Click += (_, _) => BrowseBootFile(box, url);
            box.TextChanged += (_, _) => UpdateBootState(box, state);
            _bootFields[key] = (box, state);
            Row(t, label, box, Flow(browse, state));
        }

        Header(t, "First stage: loaded by the firmware over TFTP");
        BootRow("uefi_x64", "UEFI x64", "ipxe.efi", false);
        BootRow("bios", "BIOS", "undionly.kpxe", false);
        BootRow("uefi_arm64", "UEFI ARM64", "ipxe-arm64.efi", false);
        BootRow("uefi_ia32", "UEFI IA32", "ipxe-i386.efi", false);
        Header(t, "UEFI HTTP Boot: firmware that downloads over HTTP directly");
        BootRow("httpboot_x64", "HTTP Boot x64", "http://{server}/ipxe.efi", true);
        BootRow("httpboot_arm64", "HTTP Boot ARM64", "http://{server}/ipxe-arm64.efi", true);
        Header(t, "Second stage: what iPXE loads once it is running");
        BootRow("ipxe", "iPXE script", "http://{server}/boot.ipxe", true);

        var editScript = new Button { Text = "Edit boot.ipxe...", AutoSize = true };
        editScript.Click += (_, _) => EditIpxeScript();
        var openRoot = new Button { Text = "Open root folder", AutoSize = true };
        openRoot.Click += (_, _) => { if (Directory.Exists(RootFullPath)) Process.Start("explorer.exe", RootFullPath); };
        Row(t, "", Flow(editScript, openRoot),
            hint: "{server} = this server's IP on the adapter the client used. {mac} = client MAC. Leave a field empty to ignore that kind of client.");
        return Page("Boot files", t);
    }

    TabPage BuildMacTab()
    {
        _gridMac.Dock = DockStyle.Fill;
        _gridMac.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridMac.RowHeadersWidth = 28;
        _gridMac.BackgroundColor = SystemColors.Window;
        _gridMac.BorderStyle = BorderStyle.None;
        _gridMac.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "MAC address", FillWeight = 30 });
        _gridMac.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "iPXE script for this machine", FillWeight = 70 });
        _gridMac.CellValueChanged += (_, _) => MarkDirty();
        _gridMac.UserDeletedRow += (_, _) => MarkDirty();

        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 40, ForeColor = SystemColors.GrayText, Padding = new Padding(2, 6, 2, 0),
            Text = "Machines listed here get their own iPXE script instead of the default one. Any MAC format works " +
                   "(00-15-5D-01-02-03, 00:15:5d:01:02:03). Example script: http://{server}/lab-pc.ipxe",
        };
        var page = new TabPage("Per machine") { Padding = new Padding(8) };
        page.Controls.Add(_gridMac);
        page.Controls.Add(hint);
        return page;
    }

    TabPage BuildDhcpTab()
    {
        var t = NewGrid();
        Header(t, "Only used when Mode is \"DHCP server\"");
        Control D(Control c) { _dhcpControls.Add(c); return c; }
        Row(t, "Pool start", D(TextField("dhcp", "pool_start", "")));
        Row(t, "Pool end", D(TextField("dhcp", "pool_end", "")));
        Row(t, "Subnet mask", D(TextField("dhcp", "subnet", "")), hint: "e.g. 255.255.255.0");
        Row(t, "Router", D(TextField("dhcp", "router", "")), hint: "Default gateway. Optional on an isolated lab network.");
        Row(t, "DNS servers", D(TextField("dhcp", "dns", "")), hint: "Comma-separated");
        Row(t, "Domain name", D(TextField("dhcp", "domain", "")));
        Row(t, "Lease time (s)", D(NumField("dhcp", "lease_time", 3600, 60, 604800)));
        return Page("DHCP server", t);
    }

    TabPage BuildTftpTab()
    {
        var t = NewGrid();
        Header(t, "Upper limits: clients ask for a value and get at most this");
        Row(t, "Max block size", NumField("tftp", "max_blksize", 1468, 512, 65464),
            hint: "1468 fills one 1500-byte Ethernet frame. Larger values need IP fragmentation; only raise if every hop handles it.");
        Row(t, "Max window size", NumField("tftp", "max_windowsize", 16, 1, 64),
            hint: "Blocks sent per acknowledgement (RFC 7440). 1 = classic lock-step TFTP (slow).");
        Row(t, "Timeout (ms)", NumField("tftp", "timeout_ms", 1500, 100, 30000), hint: "Wait before resending.");
        Row(t, "Retries", NumField("tftp", "retries", 6, 1, 50));
        return Page("TFTP", t);
    }

    TabPage BuildServiceTab()
    {
        var t = NewGrid();
        Row(t, "Status", _lblService);
        _btnSvcInstall.Click += (_, _) => ServiceAction("install");
        _btnSvcUninstall.Click += (_, _) => ServiceAction("uninstall");
        _btnSvcStart.Click += (_, _) => ServiceAction("start");
        _btnSvcStop.Click += (_, _) => ServiceAction("stop");
        Row(t, "", Flow(_btnSvcInstall, _btnSvcUninstall, _btnSvcStart, _btnSvcStop));
        var info = new Label
        {
            AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 12, 3, 3),
            Text = "The Windows service runs the same server in the background: it starts with Windows and needs nobody " +
                   "signed in. It uses this SharpPXE.exe and this SharpPXE.ini, so settings saved here apply to it after " +
                   "a restart. While it runs, the log pane follows its log file.\n\n" +
                   "Installing also adds a Windows Firewall rule for SharpPXE.exe. Each action asks for administrator rights.",
        };
        Row(t, "", info);
        return Page("Service", t);
    }

    // ---- layout helpers ----

    static TableLayoutPanel NewGrid()
    {
        // Dock = Top + AutoSize: the grid is as tall as its rows, and the page's panel scrolls it.
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(8, 10, 16, 8) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    static TabPage Page(string title, TableLayoutPanel t)
    {
        var p = new TabPage(title) { AutoScroll = true };
        p.Controls.Add(t);
        return p;
    }

    static void Row(TableLayoutPanel t, string label, Control c, Control? extra = null, string? hint = null)
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 12, 3) }, 0, r);
        if (c is TextBox or ComboBox or CheckedListBox) c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        if (c is ComboBox) c.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        c.Margin = new Padding(3, 4, 3, 3);
        t.Controls.Add(c, 1, r);
        if (extra is not null) t.Controls.Add(extra, 2, r);
        if (hint is not null)
        {
            int h = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = hint, AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 6) };
            t.Controls.Add(l, 1, h);
            t.SetColumnSpan(l, 2);
        }
    }

    static void Header(TableLayoutPanel t, string text)
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.HotTrack, Margin = new Padding(3, r == 0 ? 2 : 12, 3, 4) };
        t.Controls.Add(l, 0, r);
        t.SetColumnSpan(l, 3);
    }

    static FlowLayoutPanel Flow(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Top };
        f.Controls.AddRange(cs);
        return f;
    }

    static Button SmallButton(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4, 0, 4, 0) };

    // ---- field factories: create a control and bind it to an INI key ----

    void Bind(string section, string key, string def, Func<string> get, Action<string> set) =>
        _fields.Add(new Field(section, key, def, get, set));

    TextBox TextField(string section, string key, string def)
    {
        var b = new TextBox();
        b.TextChanged += (_, _) => MarkDirty();
        Bind(section, key, def, () => b.Text.Trim(), v => b.Text = v);
        return b;
    }

    NumericUpDown NumField(string section, string key, int def, int min, int max)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Width = 100, ThousandsSeparator = false };
        n.ValueChanged += (_, _) => MarkDirty();
        Bind(section, key, def.ToString(), () => ((int)n.Value).ToString(),
            v => n.Value = int.TryParse(v, out var i) ? Math.Clamp(i, min, max) : def);
        return n;
    }

    CheckBox BoolField(string section, string key, bool def, string text)
    {
        var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        c.CheckedChanged += (_, _) => MarkDirty();
        Bind(section, key, def ? "1" : "0", () => c.Checked ? "1" : "0",
            v => c.Checked = v.ToLowerInvariant() is "1" or "true" or "yes" or "on");
        return c;
    }

    // =====================================================================================
    //  Settings: INI <-> controls
    // =====================================================================================

    void LoadSettings()
    {
        _loading = true;
        try
        {
            _ini = IniDocument.Load(_iniPath);
            foreach (var f in _fields) f.Set(_ini.Get(f.Section, f.Key) ?? f.Default);

            _gridMac.Rows.Clear();
            foreach (var (mac, script) in _ini.GetEntries("mac")) _gridMac.Rows.Add(mac, script);

            UpdateDhcpEnabled();
            RefreshBootStates();
        }
        finally { _loading = false; }
        SetDirty(false);
        if (!File.Exists(_iniPath)) _log.Enqueue($"WRN {_iniPath} does not exist yet; it will be created when you save.");
    }

    bool SaveSettings(bool askRestart)
    {
        if (!Validate(out var problem))
        {
            MessageBox.Show(this, problem, "SharpPXE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _gridMac.EndEdit();
        foreach (var f in _fields)
        {
            var v = f.Get();
            // Don't clutter the file with defaults that weren't there to begin with.
            if (_ini.Get(f.Section, f.Key) is null && v == f.Default) continue;
            _ini.Set(f.Section, f.Key, v);
        }
        _ini.SetEntries("mac", _gridMac.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => (Key: (r.Cells[0].Value as string ?? "").Trim(), Value: (r.Cells[1].Value as string ?? "").Trim()))
            .Where(e => e.Key.Length > 0 && e.Value.Length > 0));
        try { _ini.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save {_iniPath}:\n{ex.Message}", "SharpPXE", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        SetDirty(false);
        _log.Enqueue($"-- Settings saved to {_iniPath}");

        if (!askRestart) return true;
        if (_server.IsRunning &&
            MessageBox.Show(this, "The server is running. Restart it now to apply the new settings?", "SharpPXE",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            StopServer();
            StartServer();
        }
        else if (_serviceStatus == ServiceControllerStatus.Running &&
            MessageBox.Show(this, "The SharpPXE service is running. Restart it now to apply the new settings?", "SharpPXE",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            ServiceManager.Stop();
            ServiceManager.Start();
            RefreshService();
        }
        return true;
    }

    /// <summary>Catches the mistakes that would stop the server from starting.</summary>
    bool Validate(out string problem)
    {
        problem = "";
        if (_cbMode.SelectedIndex == 1)
        {
            foreach (var key in new[] { "pool_start", "pool_end", "subnet" })
            {
                var v = _fields.First(f => f.Section == "dhcp" && f.Key == key).Get();
                if (!System.Net.IPAddress.TryParse(v, out _))
                {
                    problem = $"DHCP server mode needs a valid {key.Replace('_', ' ')} (DHCP server tab).";
                    return false;
                }
            }
        }
        foreach (var key in new[] { "router", "dns" })
        {
            var v = _fields.First(f => f.Section == "dhcp" && f.Key == key).Get();
            foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!System.Net.IPAddress.TryParse(part, out _)) { problem = $"'{part}' is not a valid IP address ({key})."; return false; }
        }
        return true;
    }

    void MarkDirty() { if (!_loading) SetDirty(true); }

    void SetDirty(bool dirty)
    {
        _dirty = dirty;
        _btnSave.Enabled = dirty;
        Text = "SharpPXE" + (dirty ? " *" : "");
    }

    bool ConfirmDiscard() =>
        !_dirty || MessageBox.Show(this, "Discard unsaved changes?", "SharpPXE", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    void UpdateDhcpEnabled()
    {
        foreach (var c in _dhcpControls) c.Enabled = _cbMode.SelectedIndex == 1;
    }

    // ---- "Serve on" adapter list ----

    sealed record Adapter(string Ip, string Name)
    {
        public override string ToString() => $"{Ip,-16} {Name}";
    }

    string GetBind() => string.Join(",", _clbBind.CheckedItems.Cast<Adapter>().Select(a => a.Ip));

    void SetBind(string value)
    {
        _clbBind.Items.Clear();
        var configured = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            bool loopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    (!loopback || configured.Contains(ua.Address.ToString())))   // loopback only if configured (testing)
                    _clbBind.Items.Add(new Adapter(ua.Address.ToString(), loopback ? "Loopback (this PC only)" : nic.Name));
        }
        foreach (var ip in configured)
        {
            int idx = _clbBind.Items.Cast<Adapter>().ToList().FindIndex(a => a.Ip == ip);
            // Keep configured addresses that aren't present right now, so saving doesn't drop them.
            if (idx < 0) idx = _clbBind.Items.Add(new Adapter(ip, "(not on this PC right now)"));
            _clbBind.SetItemChecked(idx, true);
        }
    }

    // ---- boot file checks and browsing ----

    void RefreshBootStates()
    {
        foreach (var (box, state) in _bootFields.Values) UpdateBootState(box, state);
    }

    /// <summary>Shows whether a boot file exists in the root folder, so typos show up before a client fails.</summary>
    void UpdateBootState(TextBox box, Label state)
    {
        var v = box.Text.Trim();
        string? rel = v;
        if (v.Length == 0) { state.Text = "not used"; state.ForeColor = SystemColors.GrayText; return; }
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var probe = v.Replace("{server}", "server.local", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || uri.Host != "server.local")
            {
                state.Text = "external URL"; state.ForeColor = SystemColors.GrayText; return;
            }
            rel = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        rel = rel.Replace("{mac}", "", StringComparison.OrdinalIgnoreCase).Replace('\\', '/').TrimStart('/');
        bool ok = rel.Length > 0 && File.Exists(Path.Combine(RootFullPath, rel));
        state.Text = ok ? "✓ found" : "✗ not in root folder";
        state.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
    }

    void BrowseBootFile(TextBox box, bool asUrl)
    {
        using var dlg = new OpenFileDialog { InitialDirectory = Directory.Exists(RootFullPath) ? RootFullPath : ServerDir, Title = "Choose a file inside the root folder" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var root = RootFullPath.TrimEnd('\\') + "\\";
        if (!dlg.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The file must be inside the root folder:\n" + RootFullPath, "SharpPXE", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var rel = dlg.FileName[root.Length..].Replace('\\', '/');
        box.Text = asUrl ? "http://{server}/" + rel : rel;
    }

    void BrowseRoot()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(RootFullPath) ? RootFullPath : ServerDir, ShowNewFolderButton = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        // Store relative to SharpPXE.exe when the folder is beneath it, so the install can be moved.
        var dir = ServerDir.TrimEnd('\\') + "\\";
        _txtRoot.Text = dlg.SelectedPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? dlg.SelectedPath[dir.Length..] : dlg.SelectedPath;
    }

    void EditIpxeScript()
    {
        var path = Path.Combine(RootFullPath, "boot.ipxe");
        var v = _bootFields["ipxe"].Box.Text.Trim().Replace("{server}", "server.local", StringComparison.OrdinalIgnoreCase);
        if (Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Host == "server.local")
            path = Path.Combine(RootFullPath, Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'));
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "Script not found:\n" + path, "SharpPXE", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        OpenInEditor(path);
    }

    static void OpenInEditor(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // =====================================================================================
    //  Running the server in the GUI
    // =====================================================================================

    void ToggleServer()
    {
        if (_server.IsRunning) StopServer();
        else StartServer();
    }

    void StartServer()
    {
        if (!File.Exists(_serverExe))
        {
            MessageBox.Show(this, $"SharpPXE.exe was not found:\n{_serverExe}", "SharpPXE", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (_dirty && !SaveSettings(askRestart: false)) return;
        if (!File.Exists(_iniPath)) _ini.Save();
        if (_fields.First(f => f.Section == "server" && f.Key == "firewall").Get() == "1") EnsureFirewallRule();
        _stopRequested = false;
        _log.Enqueue("-- Starting server");
        try { _server.Start(_serverExe, _iniPath); }
        catch (Exception ex) { _log.Enqueue("ERR Could not start SharpPXE.exe: " + ex.Message); }
        UpdateServerUi();
    }

    /// <summary>
    /// The server started from here runs without admin rights, so it can check the firewall
    /// rule but not create it. When the rule is missing, run "SharpPXE.exe --firewall" elevated
    /// (one UAC prompt). The server's own startup check then finds the rule in place.
    /// </summary>
    void EnsureFirewallRule()
    {
        try
        {
            using var check = Process.Start(new ProcessStartInfo(_serverExe, "--check-firewall") { UseShellExecute = false, CreateNoWindow = true })!;
            check.WaitForExit(20000);
            if (check.ExitCode == 0) return;
        }
        catch (Exception ex) { _log.Enqueue("WRN Could not check the firewall rule: " + ex.Message); return; }

        _log.Enqueue("-- No Windows Firewall rule for SharpPXE.exe yet; asking for administrator rights to create it");
        Cursor = Cursors.WaitCursor;
        try
        {
            _log.Enqueue(ServiceManager.RunElevated(_serverExe, "--firewall")
                ? "-- Firewall rule 'SharpPXE' created"
                : "WRN Firewall rule not created (cancelled or failed). Clients may be blocked.");
        }
        finally { Cursor = Cursors.Default; }
    }

    void StopServer()
    {
        _stopRequested = true;
        _server.Stop();
        UpdateServerUi();
    }

    void OnServerExited(int code)
    {
        if (!_stopRequested) _log.Enqueue($"ERR Server stopped unexpectedly (exit code {code}). See the messages above.");
        else _log.Enqueue("-- Server stopped");
        UpdateServerUi();
    }

    void UpdateServerUi()
    {
        bool running = _server.IsRunning;
        bool serviceRunning = _serviceStatus is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;

        _btnOnline.Text = running ? "■  Stop server" : "▶  Start server";
        _btnOnline.Enabled = running || !serviceRunning;
        if (running)
        {
            _lblStatus.Text = $"Online - {ModeLabel()}";
            _lblStatus.ForeColor = Color.SeaGreen;
        }
        else if (serviceRunning)
        {
            _lblStatus.Text = "The SharpPXE service is running (the log shows its log file)";
            _lblStatus.ForeColor = Color.RoyalBlue;
        }
        else
        {
            _lblStatus.Text = "Offline";
            _lblStatus.ForeColor = SystemColors.GrayText;
        }
        _stServer.Text = running ? $"Server running in this window ({ModeLabel()})" : "Server not running in this window";
    }

    string ModeLabel() => _cbMode.SelectedIndex switch { 1 => "DHCP server", 2 => "TFTP/HTTP only", _ => "proxyDHCP" };

    // =====================================================================================
    //  Windows service
    // =====================================================================================

    void RefreshService()
    {
        var before = _serviceStatus;
        _serviceStatus = ServiceManager.Status();
        var text = ServiceManager.Describe(_serviceStatus);
        _lblService.Text = text;
        _lblService.ForeColor = _serviceStatus == ServiceControllerStatus.Running ? Color.SeaGreen : SystemColors.ControlText;
        _stService.Text = "Service: " + text;

        bool installed = _serviceStatus is not null;
        _btnSvcInstall.Enabled = !installed;
        _btnSvcUninstall.Enabled = installed;
        _btnSvcStart.Enabled = _serviceStatus == ServiceControllerStatus.Stopped;
        _btnSvcStop.Enabled = _serviceStatus is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;

        bool running = _serviceStatus == ServiceControllerStatus.Running;
        if (running && _tailPath is null) StartTail();
        else if (!running && _tailPath is not null) StopTail();
        if (before != _serviceStatus) UpdateServerUi();
    }

    void ServiceAction(string action)
    {
        if (!File.Exists(_serverExe) && action is "install" or "uninstall")
        {
            MessageBox.Show(this, $"SharpPXE.exe was not found:\n{_serverExe}", "SharpPXE", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (action is "install" or "start")
        {
            if (_server.IsRunning)
            {
                if (MessageBox.Show(this, "The server is running in this window. Stop it so the service can use the ports?", "SharpPXE",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                StopServer();
            }
            // The service has no console, so make sure it writes a log file we can follow.
            var logField = _fields.First(f => f.Section == "server" && f.Key == "log");
            if (logField.Get().Length == 0) { logField.Set("SharpPXE.log"); MarkDirty(); }
            if (_dirty && !SaveSettings(askRestart: false)) return;
        }

        Cursor = Cursors.WaitCursor;
        bool ok;
        try
        {
            ok = action switch
            {
                "install" => ServiceManager.Install(_serverExe, _iniPath),
                "uninstall" => ServiceManager.Uninstall(_serverExe),
                "start" => ServiceManager.Start(),
                _ => ServiceManager.Stop(),
            };
        }
        finally { Cursor = Cursors.Default; }

        _log.Enqueue(ok ? $"-- Service: {action} done" : $"WRN Service: {action} failed or was cancelled");
        RefreshService();
    }

    // ---- following the service's log file ----

    void StartTail()
    {
        var log = _fields.First(f => f.Section == "server" && f.Key == "log").Get();
        if (log.Length == 0)
        {
            _log.Enqueue("WRN The service is running but has no log file configured (General tab > Log file), so its log can't be shown.");
            _tailPath = "";
            return;
        }
        _tailPath = Path.IsPathRooted(log) ? log : Path.Combine(ServerDir, log);
        // Start a little before the end so the most recent lines appear right away.
        _tailPos = File.Exists(_tailPath) ? Math.Max(0, new FileInfo(_tailPath).Length - 4096) : 0;
        _tailSkipPartial = _tailPos > 0;
        _log.Enqueue($"-- Following service log {_tailPath}");
        _tailTimer.Start();
    }

    void StopTail()
    {
        _tailTimer.Stop();
        if (!string.IsNullOrEmpty(_tailPath)) _log.Enqueue("-- Service stopped");
        _tailPath = null;
    }

    void TailServiceLog()
    {
        if (string.IsNullOrEmpty(_tailPath) || !File.Exists(_tailPath)) return;
        try
        {
            // FileShare.ReadWrite: the service keeps the file open for writing.
            using var fs = new FileStream(_tailPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _tailPos) _tailPos = 0;          // file was truncated or replaced
            if (fs.Length == _tailPos) return;
            fs.Position = _tailPos;
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            int lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) return;                          // wait for a whole line
            var complete = text[..(lastNl + 1)];
            _tailPos += System.Text.Encoding.UTF8.GetByteCount(complete);
            var lines = complete.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // The first chunk may start mid-line when we began 4 KB before the end: drop that fragment.
            foreach (var l in lines.Skip(_tailSkipPartial ? 1 : 0)) _log.Enqueue(l.TrimEnd('\r'));
            _tailSkipPartial = false;
        }
        catch (IOException) { }
    }

    // =====================================================================================

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_dirty)
        {
            var r = MessageBox.Show(this, "Save changes to SharpPXE.ini before closing?", "SharpPXE", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel || (r == DialogResult.Yes && !SaveSettings(askRestart: false))) { e.Cancel = true; return; }
        }
        if (_server.IsRunning) StopServer();
        _server.Dispose();
    }
}
