// MixDial - makes the hardware game/chat mix dial on SteelSeries Arctis 7+ family headsets work without
// SteelSeries GG. Unofficial; not affiliated with or endorsed by SteelSeries.
//
// The dongle reports the dial as two levels (game 0-100, chat 0-100). Windows only exposes a single
// output device for the headset, so the mix is applied per application: apps on the chat list follow
// the chat level, every other app playing on the headset follows the game level.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("MixDial")]
[assembly: AssemblyProduct("MixDial")]
[assembly: AssemblyDescription("Game/chat mix dial for SteelSeries Arctis 7+ headsets (unofficial)")]
[assembly: AssemblyCopyright("Copyright (c) 2026 dialworks. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

[assembly: AssemblyTrademark("SteelSeries and Arctis are trademarks of SteelSeries ApS; used only to describe compatibility.")]

public static class Program
{
    // Command line written to the Windows Run key for "Start with Windows". Start-MixDial.ps1
    // overrides this, since when it hosts the app the executable is powershell.exe.
    public static string LaunchCommand = "\"" + Application.ExecutablePath + "\"";

    [STAThread]
    public static void Main()
    {
        bool created;
        using (new Mutex(true, "MixDial.SingleInstance", out created))
        {
            if (!created) return;
            SetProcessDPIAware(); // crisp popup and tray icon on scaled displays
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

// ---------------------------------------------------------------------------------------------
// Tray UI
// ---------------------------------------------------------------------------------------------

class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly ToolStripMenuItem mixLabel, headsetLabel, deviceLabel, chatAppsMenu, enabledItem, popupItem, notifyItem, startupItem;
    readonly MixPopup popup = new MixPopup();
    bool firstMix = true;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
    // The connect notification waits briefly so the battery level pushed right after connecting is included.
    readonly System.Windows.Forms.Timer connectedTimer = new System.Windows.Forms.Timer { Interval = 1500 };
    bool firstStatus = true;
    readonly AudioMixer mixer = new AudioMixer();
    readonly ChatAppList chatApps = new ChatAppList();
    readonly ArctisHid hid;

    int game = 100, chat = 100;
    HeadsetStatus status;
    IntPtr iconHandle;

    public TrayApp()
    {
        if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        mixLabel = new ToolStripMenuItem { Enabled = false };
        headsetLabel = new ToolStripMenuItem { Enabled = false };
        deviceLabel = new ToolStripMenuItem { Enabled = false };

        chatAppsMenu = new ToolStripMenuItem("Chat apps");
        chatAppsMenu.DropDownItems.Add("...");
        chatAppsMenu.DropDownOpening += (s, e) => BuildChatAppsMenu();

        enabledItem = new ToolStripMenuItem("Mix enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (s, e) => { if (!enabledItem.Checked) mixer.Restore(); Apply(); };

        popupItem = new ToolStripMenuItem("Show popup when turning dial") { Checked = Settings.ShowPopup, CheckOnClick = true };
        popupItem.CheckedChanged += (s, e) =>
        {
            Settings.ShowPopup = popupItem.Checked;
            if (popupItem.Checked) popup.ShowMix(game, chat); // preview
        };

        notifyItem = new ToolStripMenuItem("Notifications") { Checked = Settings.Notifications, CheckOnClick = true };
        notifyItem.CheckedChanged += (s, e) => Settings.Notifications = notifyItem.Checked;

        startupItem = new ToolStripMenuItem("Start with Windows") { Checked = Startup.IsEnabled };
        startupItem.Click += (s, e) => { Startup.Set(!Startup.IsEnabled); startupItem.Checked = Startup.IsEnabled; };

        menu.Items.AddRange(new ToolStripItem[] {
            mixLabel, headsetLabel, deviceLabel, new ToolStripSeparator(),
            chatAppsMenu, enabledItem, popupItem, notifyItem, startupItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (s, e) => ExitThread())
        });
        tray.ContextMenuStrip = menu;
        tray.Visible = true;

        hid = new ArctisHid(SynchronizationContext.Current);
        hid.MixChanged += (g, c) =>
        {
            game = g; chat = c;
            Apply();
            if (popupItem.Checked && !firstMix) popup.ShowMix(g, c); // the first report is the startup state, not a turn
            firstMix = false;
        };
        hid.StatusChanged += OnStatus;
        hid.Start();

        connectedTimer.Tick += (s, e) =>
        {
            connectedTimer.Stop();
            if (status.Battery <= 1) Notify("Headset connected", "Battery low: " + BatteryText, ToolTipIcon.Warning);
            else Notify("Headset connected", "Battery " + BatteryText, ToolTipIcon.Info);
        };

        SystemEvents.SessionEnding += (s, e) => mixer.Restore();
        timer.Tick += (s, e) => Apply();
        timer.Start();
        Apply();
    }

    void Apply()
    {
        chatApps.ReloadIfChanged();
        if (enabledItem.Checked)
            mixer.Apply(game / 100f, chat / 100f, chatApps.Contains);
        else
            mixer.Sessions(); // keeps the device lookup current for the menu
        RefreshUi();
    }

    void OnStatus(HeadsetStatus st)
    {
        var prev = status;
        status = st;
        RefreshUi();
        if (firstStatus) { firstStatus = false; return; } // nothing to announce on startup

        if (st.Online && !prev.Online)
        {
            connectedTimer.Stop();
            connectedTimer.Start();
        }
        else if (!st.Online && prev.Online)
        {
            if (connectedTimer.Enabled) connectedTimer.Stop(); // connected and dropped again: say nothing
            else Notify("Headset disconnected", st.Known ? "Turned off or out of range" : "Dongle unplugged", ToolTipIcon.Info);
        }
        else if (st.Online && prev.Online && st.Battery < prev.Battery && st.Battery <= 1 && !connectedTimer.Enabled)
        {
            Notify(st.Battery == 0 ? "Battery almost empty" : "Battery low", "Battery " + BatteryText + " - time to charge", ToolTipIcon.Warning);
        }
    }

    // The headset only reports 5 battery steps (0-4), so show it as such rather than a fake percentage.
    string BatteryText { get { return status.Battery + "/4"; } }

    void Notify(string title, string text, ToolTipIcon icon)
    {
        if (notifyItem.Checked) tray.ShowBalloonTip(5000, title, text, icon);
    }

    void RefreshUi()
    {
        mixLabel.Text = string.Format("Game {0}%  ·  Chat {1}%", game, chat);
        headsetLabel.Text = !status.Known ? "Headset: looking for dongle..."
            : !status.Online ? "Headset: off / out of range"
            : string.Format("Headset: on  ·  battery {0}{1}", BatteryText, status.Charging ? " (charging)" : "");
        deviceLabel.Text = mixer.DeviceName != null ? "Output: " + mixer.DeviceName : "Output: Arctis audio device not found";

        string tip = string.Format("MixDial  ·  Game {0}%  ·  Chat {1}%", game, chat);
        if (status.Online) tip += "\nBattery " + BatteryText;
        if (!enabledItem.Checked) tip += "\n(paused)";
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        UpdateIcon();
    }

    void BuildChatAppsMenu()
    {
        chatAppsMenu.DropDownItems.Clear();
        var running = mixer.Sessions().Select(s => s.Name).Where(n => n != AudioMixer.SystemSoundsName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var header = new ToolStripMenuItem("Apps playing on the headset (checked = chat):") { Enabled = false };
        chatAppsMenu.DropDownItems.Add(header);
        if (running.Count == 0)
            chatAppsMenu.DropDownItems.Add(new ToolStripMenuItem("(none right now)") { Enabled = false });
        foreach (var name in running)
        {
            var app = name;
            var item = new ToolStripMenuItem(app) { Checked = chatApps.Contains(app) };
            item.Click += (s, e) => { chatApps.Toggle(app); Apply(); };
            chatAppsMenu.DropDownItems.Add(item);
        }
        chatAppsMenu.DropDownItems.Add(new ToolStripSeparator());
        chatAppsMenu.DropDownItems.Add(new ToolStripMenuItem("Edit chat app list...", null,
            (s, e) => Process.Start("notepad.exe", "\"" + chatApps.FilePath + "\"")));
    }

    void UpdateIcon()
    {
        int size = SystemInformation.SmallIconSize.Width;
        bool live = status.Online;
        Color gameColor = live ? Color.FromArgb(70, 200, 110) : Color.FromArgb(150, 150, 150);
        Color chatColor = live ? Color.FromArgb(80, 160, 255) : Color.FromArgb(150, 150, 150);

        using (var bmp = new Bitmap(size, size))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);
            float w = size * 0.36f, gap = size * 0.14f, x1 = (size - 2 * w - gap) / 2, x2 = x1 + w + gap;
            DrawBar(g, x1, w, size, game, gameColor);
            DrawBar(g, x2, w, size, chat, chatColor);

            IntPtr old = iconHandle;
            iconHandle = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(iconHandle);
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
    }

    static void DrawBar(Graphics g, float x, float w, int size, int value, Color color)
    {
        float top = 1, full = size - 2, h = full * value / 100f;
        using (var track = new SolidBrush(Color.FromArgb(70, color))) g.FillRectangle(track, x, top, w, full);
        using (var fill = new SolidBrush(color)) g.FillRectangle(fill, x, top + full - h, w, h);
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        mixer.Restore();
        popup.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}

// ---------------------------------------------------------------------------------------------
// Chat app list (one process name per line in %APPDATA%\MixDial\chat-apps.txt)
// ---------------------------------------------------------------------------------------------

class ChatAppList
{
    const string Defaults =
@"# MixDial chat apps
# One process name per line (with or without .exe). Lines starting with # are ignored.
# These apps follow the CHAT side of the dial; every other app follows the GAME side.
Discord
DiscordPTB
DiscordCanary
ts3client_win64
TeamSpeak
Mumble
Skype
ms-teams
Teams
Zoom
Slack
Guilded
Element
WhatsApp
Telegram
";

    public readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixDial", "chat-apps.txt");

    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    DateTime loadedStamp;

    public ChatAppList()
    {
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, Defaults);
        }
        ReloadIfChanged();
    }

    static string Normalize(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
    }

    public bool Contains(string processName) { return names.Contains(processName); }

    public void ReloadIfChanged()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(FilePath);
            if (stamp == loadedStamp) return;
            names = new HashSet<string>(File.ReadAllLines(FilePath).Select(Normalize)
                .Where(l => l.Length > 0 && !l.StartsWith("#")), StringComparer.OrdinalIgnoreCase);
            loadedStamp = stamp;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Toggle(string processName)
    {
        try
        {
            var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : new List<string>();
            if (Contains(processName))
                lines.RemoveAll(l => string.Equals(Normalize(l), processName, StringComparison.OrdinalIgnoreCase));
            else
                lines.Add(processName);
            File.WriteAllLines(FilePath, lines);
            ReloadIfChanged();
        }
        catch (IOException) { }               // file locked by an editor: the click is simply ignored
        catch (UnauthorizedAccessException) { }
    }
}

// ---------------------------------------------------------------------------------------------
// On-screen popup shown while the dial turns
// ---------------------------------------------------------------------------------------------

class MixPopup : Form
{
    static readonly Color GameColor = Color.FromArgb(70, 200, 110), ChatColor = Color.FromArgb(80, 160, 255);
    readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer { Interval = 1500 };
    readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
    readonly float scale;
    int game = 100, chat = 100;

    public MixPopup()
    {
        using (var screen = Graphics.FromHwnd(IntPtr.Zero)) scale = screen.DpiX / 96f;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 10f);
        Opacity = 0.95; // also makes the window layered, which click-through needs
        Size = new Size((int)(300 * scale), (int)(84 * scale));

        hideTimer.Tick += (s, e) => { hideTimer.Stop(); fadeTimer.Start(); };
        fadeTimer.Tick += (s, e) =>
        {
            if (Opacity > 0.1) Opacity -= 0.08;
            else { fadeTimer.Stop(); Hide(); Opacity = 0.95; }
        };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x20 /* TRANSPARENT */ | 0x8 /* TOPMOST */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND: Windows 11 rounded corners
        DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    public void ShowMix(int game, int chat)
    {
        this.game = game;
        this.chat = chat;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - (int)(72 * scale));
        fadeTimer.Stop();
        Opacity = 0.95;
        if (!Visible) Show();
        SetWindowPos(Handle, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 /* NOSIZE | NOMOVE | NOACTIVATE */);
        Invalidate();
        hideTimer.Stop();
        hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float pad = 18 * scale, rowH = 20 * scale, gap = 8 * scale;
        DrawRow(g, "Game", game, GameColor, pad);
        DrawRow(g, "Chat", chat, ChatColor, pad + rowH + gap);
    }

    void DrawRow(Graphics g, string label, int value, Color color, float top)
    {
        float pad = 18 * scale, rowH = 20 * scale, labelW = 52 * scale, pctW = 50 * scale, barH = 6 * scale;
        float barX = pad + labelW, barW = Width - 2 * pad - labelW - pctW, barY = top + (rowH - barH) / 2;
        var left = new StringFormat { LineAlignment = StringAlignment.Center };
        var right = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far };

        using (var text = new SolidBrush(Color.FromArgb(235, 235, 235)))
        {
            g.DrawString(label, Font, text, new RectangleF(pad, top, labelW, rowH), left);
            g.DrawString(value + "%", Font, text, new RectangleF(Width - pad - pctW, top, pctW, rowH), right);
        }
        using (var track = new SolidBrush(Color.FromArgb(70, 70, 70))) FillPill(g, track, barX, barY, barW, barH);
        using (var fill = new SolidBrush(color)) FillPill(g, fill, barX, barY, barW * value / 100f, barH);
    }

    static void FillPill(Graphics g, Brush brush, float x, float y, float w, float h)
    {
        if (w <= 0) return;
        w = Math.Max(w, h);
        using (var path = new GraphicsPath())
        {
            path.AddArc(x, y, h, h, 90, 180);
            path.AddArc(x + w - h, y, h, h, 270, 180);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { hideTimer.Dispose(); fadeTimer.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

static class Settings
{
    const string Key = @"Software\MixDial";

    public static bool Notifications { get { return Get("Notifications"); } set { Set("Notifications", value); } }
    public static bool ShowPopup { get { return Get("ShowPopup"); } set { Set("ShowPopup", value); } }

    // Options default to on until switched off.
    static bool Get(string name)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null || !0.Equals(k.GetValue(name));
    }

    static void Set(string name, bool value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "MixDial";

    public static bool IsEnabled
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(ValueName) != null; }
    }

    public static void Set(bool enabled)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) k.SetValue(ValueName, Program.LaunchCommand);
            else k.DeleteValue(ValueName, false);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Per-application volume on the Arctis output device (Windows Core Audio)
// ---------------------------------------------------------------------------------------------

class AppSession
{
    public string Name, Id;
    public ISimpleAudioVolume Volume;
}

class AudioMixer
{
    public const string SystemSoundsName = "System sounds";

    readonly IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
    readonly Dictionary<uint, string> processNames = new Dictionary<uint, string>();
    readonly HashSet<string> touched = new HashSet<string>();
    IAudioSessionManager2 manager;
    SessionNotifier notifier;
    DateTime nextLookup, namesClearedAt = DateTime.Now;

    public string DeviceName { get; private set; }

    bool EnsureDevice()
    {
        if (manager != null) return true;
        if (DateTime.Now < nextLookup) return false;
        nextLookup = DateTime.Now.AddSeconds(3);
        try
        {
            IMMDeviceCollection devices;
            enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out devices);
            uint count;
            devices.GetCount(out count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice device;
                devices.Item(i, out device);
                string name = FriendlyName(device);
                if (name == null || name.IndexOf("Arctis", StringComparison.OrdinalIgnoreCase) < 0) continue;

                object obj;
                Guid iid = typeof(IAudioSessionManager2).GUID;
                device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out obj);
                manager = (IAudioSessionManager2)obj;
                // Registering for notifications also makes the session enumerator see newly started apps.
                notifier = new SessionNotifier();
                manager.RegisterSessionNotification(notifier);
                DeviceName = name;
                touched.Clear();
                return true;
            }
        }
        catch (COMException) { Drop(); }
        DeviceName = null;
        return false;
    }

    void Drop()
    {
        try { if (manager != null) manager.UnregisterSessionNotification(notifier); } catch (COMException) { }
        manager = null;
        notifier = null;
        DeviceName = null;
    }

    public List<AppSession> Sessions()
    {
        var list = new List<AppSession>();
        if (!EnsureDevice()) return list;
        if (DateTime.Now - namesClearedAt > TimeSpan.FromMinutes(1)) { processNames.Clear(); namesClearedAt = DateTime.Now; }
        try
        {
            IAudioSessionEnumerator sessions;
            manager.GetSessionEnumerator(out sessions);
            int count;
            sessions.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2 control;
                sessions.GetSession(i, out control);
                int state;
                control.GetState(out state);
                if (state == 2 /* AudioSessionStateExpired */) continue;
                uint pid;
                control.GetProcessId(out pid);
                string id;
                control.GetSessionInstanceIdentifier(out id);
                list.Add(new AppSession { Name = ProcessName(pid), Id = id, Volume = (ISimpleAudioVolume)control });
            }
        }
        catch (COMException) { Drop(); }        // device unplugged or audio service restarted
        catch (InvalidCastException) { Drop(); }
        return list;
    }

    string ProcessName(uint pid)
    {
        if (pid == 0) return SystemSoundsName;
        string name;
        if (processNames.TryGetValue(pid, out name)) return name;
        try { name = Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception) { name = "pid " + pid; }
        processNames[pid] = name;
        return name;
    }

    public void Apply(float game, float chat, Func<string, bool> isChat)
    {
        foreach (var s in Sessions())
        {
            float target = isChat(s.Name) ? chat : game;
            if (SetVolume(s, target)) touched.Add(s.Id);
        }
    }

    // Windows remembers per-app volumes, so put everything we changed back to full on exit.
    public void Restore()
    {
        foreach (var s in Sessions())
            if (touched.Contains(s.Id)) SetVolume(s, 1f);
        touched.Clear();
    }

    static bool SetVolume(AppSession s, float target)
    {
        try
        {
            float current;
            s.Volume.GetMasterVolume(out current);
            if (Math.Abs(current - target) > 0.004f)
            {
                Guid context = Guid.Empty;
                s.Volume.SetMasterVolume(target, ref context);
            }
            return true;
        }
        catch (COMException) { return false; }
    }

    static string FriendlyName(IMMDevice device)
    {
        IPropertyStore store;
        device.OpenPropertyStore(0 /* STGM_READ */, out store);
        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        PROPVARIANT value;
        store.GetValue(ref key, out value);
        try { return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.p) : null; }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
}

class SessionNotifier : IAudioSessionNotification
{
    public int OnSessionCreated(IAudioSessionControl newSession) { return 0; }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorComObject { }

[StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
[StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr client);
    void UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PROPERTYKEY key);
    void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    void Commit();
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    void GetAudioSessionControl(IntPtr sessionGuid, int flags, out IAudioSessionControl control);
    void GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out ISimpleAudioVolume volume);
    void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    void RegisterSessionNotification(IAudioSessionNotification notification);
    void UnregisterSessionNotification(IAudioSessionNotification notification);
    void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
    void UnregisterDuckNotification(IntPtr notification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl
{
    void GetState(out int state);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
    void GetGroupingParam(out Guid param);
    void SetGroupingParam(ref Guid param, ref Guid context);
    void RegisterAudioSessionNotification(IntPtr client);
    void UnregisterAudioSessionNotification(IntPtr client);
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid); // returns a success code for multi-process sessions
    [PreserveSig] int IsSystemSoundsSession();
    void SetDuckingPreference(bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid context);
    void GetMasterVolume(out float level);
    void SetMute(bool mute, ref Guid context);
    void GetMute(out bool mute);
}

[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated(IAudioSessionControl newSession);
}

// ---------------------------------------------------------------------------------------------
// Dongle protocol (USB HID)
//
//   vendor interface, usage page 0xFF00: pushes  [00] 45 <game> <chat>  whenever the dial moves
//                                        pushes  [00] BB <03 connected / 00 off>  on connection changes
//                                        pushes  [00] B7 <battery 0-4>  when the battery level changes
//   vendor interface, usage page 0xFFC0: request [00] B0  ->  [00] B0 <online 03 / off 01> <battery 0-4>
//                                                              <charging 01> <game> <chat>
// ---------------------------------------------------------------------------------------------

struct HeadsetStatus
{
    public bool Known, Online, Charging;
    public int Battery;
}

class ArctisHid
{
    const ushort VendorId = 0x1038;
    // Arctis 7+, 7P+, 7X+, 7+ Destiny edition
    static readonly ushort[] ProductIds = { 0x220E, 0x2212, 0x2216, 0x2236 };

    public event Action<int, int> MixChanged;
    public event Action<HeadsetStatus> StatusChanged;

    readonly SynchronizationContext ui;
    readonly object statusLock = new object();
    volatile bool deviceLost, requestStatus;
    HeadsetStatus lastStatus;
    int lastGame = -1, lastChat = -1;

    public ArctisHid(SynchronizationContext ui) { this.ui = ui; }

    public void Start()
    {
        new Thread(Run) { IsBackground = true, Name = "Arctis HID" }.Start();
    }

    void Run()
    {
        while (true)
        {
            try { RunSession(); } catch (Exception) { }
            if (lastStatus.Known) Publish(new HeadsetStatus());
            Thread.Sleep(2000);
        }
    }

    void RunSession()
    {
        string statusPath = null, eventPath = null;
        foreach (var dev in Hid.Enumerate(VendorId, ProductIds))
        {
            if (dev.UsagePage == 0xFFC0) statusPath = dev.Path;
            else if (dev.UsagePage == 0xFF00) eventPath = dev.Path;
        }
        if (statusPath == null) return;

        deviceLost = false;
        var readers = new List<FileStream>();
        try
        {
            readers.Add(StartReader(statusPath));
            if (eventPath != null) readers.Add(StartReader(eventPath));

            // A separate handle for writing: synchronous HID handles serialize I/O, so a pending
            // read on the same handle would block the status request.
            using (var writer = Hid.Open(statusPath))
            {
                var request = new byte[65];
                request[1] = 0xB0;
                int tick = 0;
                while (!deviceLost)
                {
                    if (tick++ % 50 == 0 || requestStatus) // every 5 s, or right after a connection change
                    {
                        requestStatus = false;
                        writer.Write(request, 0, request.Length);
                    }
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
        }
    }

    FileStream StartReader(string path)
    {
        var stream = Hid.Open(path);
        new Thread(() =>
        {
            var buf = new byte[65];
            try
            {
                while (true)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    Parse(buf, n);
                }
            }
            catch (Exception) { }
            deviceLost = true;
        }) { IsBackground = true }.Start();
        return stream;
    }

    void Parse(byte[] r, int n)
    {
        if (n >= 4 && r[1] == 0x45 && r[2] <= 100 && r[3] <= 100)
        {
            PublishMix(r[2], r[3]);
        }
        else if (n >= 7 && r[1] == 0xB0)
        {
            bool online = r[2] != 0x01 && r[5] <= 100 && r[6] <= 100;
            Publish(new HeadsetStatus { Known = true, Online = online, Battery = Math.Min((int)r[3], 4), Charging = r[4] == 0x01 });
            if (online) PublishMix(r[5], r[6]);
        }
        else if (n >= 3 && r[1] == 0xBB)
        {
            lock (statusLock) { var st = lastStatus; st.Known = true; st.Online = r[2] == 0x03; Publish(st); }
            requestStatus = true; // fetch charging state and dial position right away
        }
        else if (n >= 3 && r[1] == 0xB7 && r[2] <= 4)
        {
            lock (statusLock) { var st = lastStatus; st.Known = true; st.Battery = r[2]; Publish(st); }
        }
    }

    void PublishMix(int game, int chat)
    {
        if (game == lastGame && chat == lastChat) return;
        lastGame = game; lastChat = chat;
        ui.Post(_ => { if (MixChanged != null) MixChanged(game, chat); }, null);
    }

    void Publish(HeadsetStatus status)
    {
        lock (statusLock) lastStatus = status;
        ui.Post(_ => { if (StatusChanged != null) StatusChanged(status); }, null);
    }
}

static class Hid
{
    public class DeviceInfo { public string Path; public ushort UsagePage, Usage; }

    public static List<DeviceInfo> Enumerate(ushort vid, ushort[] pids)
    {
        var result = new List<DeviceInfo>();
        var wanted = pids.Select(p => string.Format("vid_{0:x4}&pid_{1:x4}", vid, p)).ToArray();
        Guid hidGuid;
        HidD_GetHidGuid(out hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
            {
                int size;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(size);
                string path;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out size, IntPtr.Zero)) continue;
                    path = Marshal.PtrToStringUni(detail + 4);
                }
                finally { Marshal.FreeHGlobal(detail); }

                string lower = path.ToLowerInvariant();
                if (!wanted.Any(lower.Contains)) continue;

                using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    IntPtr preparsed;
                    if (!HidD_GetPreparsedData(h, out preparsed)) continue;
                    HIDP_CAPS caps;
                    HidP_GetCaps(preparsed, out caps);
                    HidD_FreePreparsedData(preparsed);
                    result.Add(new DeviceInfo { Path = path, UsagePage = caps.UsagePage, Usage = caps.Usage });
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static FileStream Open(string path)
    {
        var h = CreateFile(path, 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException("Cannot open " + path);
        return new FileStream(h, FileAccess.ReadWrite, 65, false);
    }

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid guid; public int flags; public IntPtr reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
