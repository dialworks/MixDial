// MixDial - makes the hardware game/chat mix dial on SteelSeries Arctis 7+ and Arctis Nova Elite
// headsets work without SteelSeries GG. Unofficial; not affiliated with or endorsed by SteelSeries.
//
// The dongle reports the dial as two levels (game 0-100, chat 0-100). Windows only exposes a single
// output device for the headset, so the mix is applied per application: apps on the chat list follow
// the chat level, apps on the media list stay at full volume, and every other app follows the game
// level. An app on both lists follows chat. This split is done in Windows and applies to every headset.

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
[assembly: AssemblyDescription("Game/chat mix dial for SteelSeries Arctis 7+ and Nova Elite headsets (unofficial)")]
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
    readonly ToolStripMenuItem mixLabel, headsetLabel, deviceLabel, chatAppsMenu, mediaAppsMenu, ancMenu, sidetoneMenu, screenMenu, eqMenu, enabledItem, popupItem, notifyItem, startupItem;
    static readonly string[] Durations = { "Never", "1 minute", "5 minutes", "10 minutes", "15 minutes", "30 minutes", "60 minutes" };
    readonly MixPopup popup = new MixPopup();
    bool firstMix = true;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
    // The connect notification waits briefly so the battery level pushed right after connecting is included.
    readonly System.Windows.Forms.Timer connectedTimer = new System.Windows.Forms.Timer { Interval = 1500 };
    bool firstStatus = true;
    readonly AudioMixer mixer = new AudioMixer();
    readonly AppNameList chatApps = new AppNameList("chat-apps.txt", AppNameList.ChatDefaults);
    readonly AppNameList mediaApps = new AppNameList("media-apps.txt", AppNameList.MediaDefaults);
    readonly ArctisHid hid;

    int game = 100, chat = 100;
    HeadsetStatus status;
    EqSnapshot eqSnapshot;
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
        chatAppsMenu.DropDownOpening += (s, e) => BuildAppMenu(chatAppsMenu, chatApps, "checked = chat", "Edit chat app list...");

        mediaAppsMenu = new ToolStripMenuItem("Media apps");
        mediaAppsMenu.DropDownItems.Add("...");
        mediaAppsMenu.DropDownOpening += (s, e) => BuildAppMenu(mediaAppsMenu, mediaApps, "checked = full volume", "Edit media app list...");

        ancMenu = new ToolStripMenuItem("ANC") { Visible = false };
        ancMenu.DropDownOpening += (s, e) => BuildAncMenu();
        sidetoneMenu = new ToolStripMenuItem("Sidetone") { Visible = false };
        sidetoneMenu.DropDownOpening += (s, e) => BuildSidetoneMenu();
        screenMenu = new ToolStripMenuItem("Screen") { Visible = false };
        screenMenu.DropDownOpening += (s, e) => BuildScreenMenu();
        eqMenu = new ToolStripMenuItem("Onboard EQ") { Visible = false };
        eqMenu.DropDownOpening += (s, e) => { hid.RequestEq(); BuildEqMenu(); };

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
            chatAppsMenu, mediaAppsMenu, ancMenu, sidetoneMenu, screenMenu, eqMenu, enabledItem, popupItem, notifyItem, startupItem, new ToolStripSeparator(),
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
        hid.EqChanged += snap => { eqSnapshot = snap; if (eqMenu.DropDown.Visible) BuildEqMenu(); };
        hid.Start();

        connectedTimer.Tick += (s, e) =>
        {
            connectedTimer.Stop();
            if (BatteryIsLow) Notify("Headset connected", "Battery low: " + BatteryText, ToolTipIcon.Warning);
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
        mediaApps.ReloadIfChanged();
        if (enabledItem.Checked)
            mixer.Apply(game / 100f, chat / 100f, chatApps.Contains, mediaApps.Contains);
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
            if (st.Elite) hid.RequestEq();
        }
        else if (!st.Online && prev.Online)
        {
            if (connectedTimer.Enabled) connectedTimer.Stop(); // connected and dropped again: say nothing
            else Notify("Headset disconnected", st.Known ? "Turned off or out of range" : "Dongle unplugged", ToolTipIcon.Info);
        }
        else if (st.Online && prev.Online && st.Battery < prev.Battery && BatteryIsLow && !connectedTimer.Enabled)
        {
            Notify(st.Battery == 0 ? "Battery almost empty" : "Battery low", "Battery " + BatteryText + " - time to charge", ToolTipIcon.Warning);
        }
    }

    // Arctis 7+ reports five steps (0-4). Nova Elite reports a percentage, plus the spare battery in the base.
    string BatteryText
    {
        get
        {
            if (status.BatteryMax >= 100)
            {
                string text = status.Battery + "%";
                if (status.HasDockBattery) text += "  ·  spare " + status.DockBattery + "%";
                return text;
            }
            return status.Battery + "/4";
        }
    }

    bool BatteryIsLow { get { return status.BatteryMax >= 100 ? status.Battery <= 20 : status.Battery <= 1; } }

    void Notify(string title, string text, ToolTipIcon icon)
    {
        if (notifyItem.Checked) tray.ShowBalloonTip(5000, title, text, icon);
    }

    void RefreshUi()
    {
        mixLabel.Text = string.Format("Game {0}%  ·  Chat {1}%  ·  Media full", game, chat);
        headsetLabel.Text = HeadsetLine();
        bool elite = status.Elite && status.Known;
        ancMenu.Visible = sidetoneMenu.Visible = screenMenu.Visible = eqMenu.Visible = elite;
        deviceLabel.Text = mixer.DeviceName != null ? "Output: " + mixer.DeviceName : "Output: Arctis audio device not found";

        string tip = string.Format("MixDial  ·  Game {0}%  ·  Chat {1}%", game, chat);
        if (status.Online) tip += "\nBattery " + BatteryText;
        if (!enabledItem.Checked) tip += "\n(paused)";
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        UpdateIcon();
    }

    string HeadsetLine()
    {
        if (!status.Known) return "Headset: looking for dongle...";
        if (!status.Online) return "Headset: off / out of range";
        string text = string.Format("Headset: on  ·  battery {0}{1}", BatteryText, status.Charging ? " (charging)" : "");
        if (status.Elite && status.MicKnown)
            text += (status.MicMuted ? "  ·  mic muted" : "  ·  mic open") + "  ·  " + AncLabel(status.AncMode);
        return text;
    }

    static string AncLabel(int mode)
    {
        if (mode == 1) return "transparency";
        if (mode == 2) return "ANC";
        return "ANC off";
    }

    void BuildAncMenu()
    {
        ancMenu.DropDownItems.Clear();
        if (!status.MicKnown)
            ancMenu.DropDownItems.Add(new ToolStripMenuItem("Waiting for the headset") { Enabled = false });
        string[] labels = { "Off", "Transparency", "ANC" };
        for (int i = 0; i < labels.Length; i++)
        {
            int mode = i;
            var item = new ToolStripMenuItem(labels[i]) { Checked = status.MicKnown && status.AncMode == mode };
            item.Click += (s, e) => hid.SendElite(0xBD, (byte)mode);
            ancMenu.DropDownItems.Add(item);
        }
    }

    void BuildSidetoneMenu()
    {
        sidetoneMenu.DropDownItems.Clear();
        if (!status.SidetoneKnown)
            sidetoneMenu.DropDownItems.Add(new ToolStripMenuItem("Level unknown until the headset reports it") { Enabled = false });
        var on = new ToolStripMenuItem("On") { Checked = status.SidetoneKnown && status.SidetoneOn };
        on.Click += (s, e) =>
        {
            int level = status.SidetoneKnown ? status.SidetoneLevel : 5;
            hid.SendElite(0x38, (byte)(on.Checked ? 0 : 1), (byte)level);
        };
        sidetoneMenu.DropDownItems.Add(on);
        sidetoneMenu.DropDownItems.Add(new ToolStripSeparator());
        for (int level = 1; level <= 10; level++)
        {
            int n = level;
            var item = new ToolStripMenuItem("Level " + n) { Checked = status.SidetoneKnown && status.SidetoneLevel == n };
            item.Click += (s, e) => hid.SendElite(0x38, (byte)(!status.SidetoneKnown || status.SidetoneOn ? 1 : 0), (byte)n);
            sidetoneMenu.DropDownItems.Add(item);
        }
    }

    void BuildScreenMenu()
    {
        screenMenu.DropDownItems.Clear();
        var brightness = new ToolStripMenuItem("Brightness");
        FillRange(brightness, 1, 10, status.BrightnessKnown, status.Brightness, n => hid.SendElite(0x85, (byte)n), "Level ");
        var dim = new ToolStripMenuItem("Dim when idle") { Checked = status.DimModeKnown && status.DimMode == 1 };
        dim.Click += (s, e) => hid.SendElite(0x88, (byte)(dim.Checked ? 0 : 1));
        var dimAfter = new ToolStripMenuItem("Dim after");
        FillChoices(dimAfter, Durations, status.DimTimerKnown, status.DimTimer, n => hid.SendElite(0x83, (byte)n));
        var autoOff = new ToolStripMenuItem("Headset off after");
        FillChoices(autoOff, Durations, status.AutoOffKnown, status.AutoOff, n => hid.SendElite(0xC1, (byte)n));
        screenMenu.DropDownItems.AddRange(new ToolStripItem[] { brightness, dim, dimAfter, autoOff });
    }

    void BuildEqMenu()
    {
        eqMenu.DropDownItems.Clear();
        var edit = new ToolStripMenuItem("Edit wireless curve…");
        edit.Click += (s, e) => OpenEqEditor();
        eqMenu.DropDownItems.Add(edit);
        eqMenu.DropDownItems.Add(new ToolStripSeparator());
        if (eqSnapshot == null)
        {
            eqMenu.DropDownItems.Add(new ToolStripMenuItem("Reading the headset…") { Enabled = false });
            return;
        }
        eqMenu.DropDownItems.Add(CurveMenu("Wireless", eqSnapshot.WirelessPreset, eqSnapshot.Wireless));
        var measured = eqSnapshot.MeasuredDiffers ? CurveMenu("Measured", -1, eqSnapshot.Measured) : null;
        eqMenu.DropDownItems.Add(GainsMenu("Microphone", eqSnapshot.MicPreset, eqSnapshot.MicGains));
        eqMenu.DropDownItems.Add(GainsMenu("Bluetooth", eqSnapshot.BtPreset, eqSnapshot.BtGains));
        if (measured != null) eqMenu.DropDownItems.Add(measured);
    }

    EqEditor eqEditor;

    void OpenEqEditor()
    {
        if (eqEditor == null || eqEditor.IsDisposed)
        {
            eqEditor = new EqEditor(hid, eqSnapshot);
            eqEditor.Show();
        }
        else eqEditor.Activate();
    }

    static ToolStripMenuItem CurveMenu(string title, int preset, EqBand[] bands)
    {
        var menu = new ToolStripMenuItem(preset >= 0 ? title + "  ·  preset " + preset : title);
        if (bands == null)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("No curve") { Enabled = false });
            return menu;
        }
        foreach (var band in bands)
            menu.DropDownItems.Add(new ToolStripMenuItem(band.Describe()) { Enabled = false });
        return menu;
    }

    static ToolStripMenuItem GainsMenu(string title, int preset, int[] tenths)
    {
        var menu = new ToolStripMenuItem(title + "  ·  preset " + preset);
        if (tenths == null)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("No curve") { Enabled = false });
            return menu;
        }
        bool flat = true;
        for (int i = 0; i < tenths.Length; i++) if (tenths[i] != tenths[0]) flat = false;
        if (flat)
            menu.DropDownItems.Add(new ToolStripMenuItem("All bands " + EqBand.Db(tenths[0])) { Enabled = false });
        else
            for (int i = 0; i < tenths.Length; i++)
                menu.DropDownItems.Add(new ToolStripMenuItem("Band " + (i + 1) + "  " + EqBand.Db(tenths[i])) { Enabled = false });
        return menu;
    }

    static void FillRange(ToolStripMenuItem menu, int first, int last, bool known, int selected, Action<int> choose, string prefix)
    {
        if (!known) menu.DropDownItems.Add(new ToolStripMenuItem("Current value unknown") { Enabled = false });
        for (int n = first; n <= last; n++)
        {
            int value = n;
            var item = new ToolStripMenuItem(prefix + value) { Checked = known && selected == value };
            item.Click += (s, e) => choose(value);
            menu.DropDownItems.Add(item);
        }
    }

    static void FillChoices(ToolStripMenuItem menu, string[] labels, bool known, int selected, Action<int> choose)
    {
        if (!known) menu.DropDownItems.Add(new ToolStripMenuItem("Current value unknown") { Enabled = false });
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(labels[i]) { Checked = known && selected == index };
            item.Click += (s, e) => choose(index);
            menu.DropDownItems.Add(item);
        }
    }

    void BuildAppMenu(ToolStripMenuItem menu, AppNameList list, string checkedMeans, string editLabel)
    {
        menu.DropDownItems.Clear();
        var running = mixer.Sessions().Select(s => s.Name).Where(n => n != AudioMixer.SystemSoundsName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        menu.DropDownItems.Add(new ToolStripMenuItem("Apps playing on the headset (" + checkedMeans + "):") { Enabled = false });
        if (running.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem("(none right now)") { Enabled = false });
        foreach (var name in running)
        {
            var app = name;
            var item = new ToolStripMenuItem(app) { Checked = list.Contains(app) };
            item.Click += (s, e) => { list.Toggle(app); Apply(); };
            menu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(editLabel, null,
            (s, e) => Process.Start("notepad.exe", "\"" + list.FilePath + "\"")));
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
        if (hid != null) hid.Shutdown();
        mixer.Restore();
        popup.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}

// ---------------------------------------------------------------------------------------------
// App lists (one process name per line in %APPDATA%\MixDial\)
// ---------------------------------------------------------------------------------------------

class AppNameList
{
    public const string ChatDefaults =
@"# MixDial chat apps
# One process name per line (with or without .exe). Lines starting with # are ignored.
# These apps follow the CHAT side of the dial. Chat wins over the media list.
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

    public const string MediaDefaults =
@"# MixDial media apps
# One process name per line (with or without .exe). Lines starting with # are ignored.
# These apps stay at full volume on every supported headset. Chat wins if an app is on both lists.
firefox
chrome
msedge
brave
opera
spotify
vlc
mpv
wmplayer
Music.UI
foobar2000
MusicBee
iTunes
";

    public readonly string FilePath;
    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    DateTime loadedStamp;

    public AppNameList(string fileName, string defaults)
    {
        FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixDial", fileName);
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, defaults);
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
// Onboard EQ editor (Nova Elite wireless curve, Custom slot)
// ---------------------------------------------------------------------------------------------

class EqEditor : Form
{
    static readonly int[] StandardFreqs = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    // Scores are AutoEq's Harman preference scores for that headphone (oratory1990 over-ear list).
    // Gains are that model's published 10-band correction, in tenths of a dB, at 31/62 Hz mapped onto 32/64 Hz.
    static readonly string[] PresetNames = {
        "E3  101", "SRH440  96", "Sundara  95", "Ananda  94", "X2HR  94", "HD 550  94",
        "HD 560S  93", "Noire X  92", "HD 600  91",
        "Flat", "Bass", "Treble", "Vocal", "V shape", "Warm"
    };
    static readonly int[] PresetScores = { 101, 96, 95, 94, 94, 94, 93, 92, 91, 0, 0, 0, 0, 0, 0 };
    static readonly int[][] PresetGains = {
        new[] { -3, -3, -1, 3, -7, 13, 0, -9, 1, -37 },
        new[] { 52, 3, -18, 11, -3, 4, -4, 26, -19, -20 },
        new[] { 65, 17, 5, -19, 1, -20, 21, -2, 18, -62 },
        new[] { 44, 13, -3, -6, -4, -1, 23, -10, -5, -120 },
        new[] { 68, -18, -7, -9, 0, 0, 6, 0, 10, -19 },
        new[] { 66, 22, 1, -19, 2, -16, 15, 2, 26, -72 },
        new[] { 59, 16, 0, -10, 8, -16, 8, -13, 35, -81 },
        new[] { -10, 4, -18, -3, 8, 4, -11, -2, 25, -87 },
        new[] { 69, 33, -11, -16, 6, -8, 1, -10, 39, -65 },
        new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new[] { 60, 45, 25, 10, 0, 0, -5, -10, -10, -15 },
        new[] { -10, -5, 0, 0, 0, 10, 25, 40, 45, 40 },
        new[] { -20, -10, 0, 15, 35, 40, 25, 5, -10, -15 },
        new[] { 45, 30, 10, 0, -15, -20, -5, 20, 40, 50 },
        new[] { 30, 25, 15, 5, 0, -5, -15, -20, -25, -20 }
    };

    readonly ArctisHid hid;
    readonly EqGraph graph;
    readonly Label statusLabel;
    readonly Button[] presetButtons;
    string chosen = "Custom";
    bool dirty;

    public EqEditor(ArctisHid hid, EqSnapshot snapshot)
    {
        this.hid = hid;
        Text = "Onboard EQ";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(22, 22, 26);
        ForeColor = Color.FromArgb(236, 236, 236);
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(760, 520);
        DoubleBuffered = true;

        var presets = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 96,
            Padding = new Padding(16, 12, 16, 0),
            BackColor = Color.FromArgb(22, 22, 26),
            WrapContents = true,
            AutoScroll = true
        };
        presetButtons = new Button[PresetNames.Length];
        for (int i = 0; i < PresetNames.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Text = PresetNames[i],
                AutoSize = true,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 8, 0),
                ForeColor = Color.FromArgb(236, 236, 236),
                BackColor = Color.FromArgb(42, 42, 48),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (s, e) => ApplyPreset(index);
            presetButtons[i] = button;
            presets.Controls.Add(button);
        }

        graph = new EqGraph { Dock = DockStyle.Fill };
        graph.Edited += (s, e) => { dirty = true; chosen = "Custom"; HighlightPreset(-1); statusLabel.Text = "Not applied"; };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Color.FromArgb(22, 22, 26), Padding = new Padding(16, 12, 16, 16) };
        statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(160, 160, 168),
            Text = "Custom slot on the headset. Drag a point, or pick a preset."
        };
        var apply = new Button
        {
            Text = "Apply",
            Dock = DockStyle.Right,
            Width = 120,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(70, 200, 110),
            ForeColor = Color.FromArgb(14, 28, 18),
            Cursor = Cursors.Hand
        };
        apply.FlatAppearance.BorderSize = 0;
        apply.Click += (s, e) =>
        {
            statusLabel.Text = "Sending…";
            hid.WriteWirelessEq(graph.Bands, chosen);
        };
        footer.Controls.Add(statusLabel);
        footer.Controls.Add(apply);

        Controls.Add(graph);
        Controls.Add(presets);
        Controls.Add(footer);
        AcceptButton = apply;

        hid.EqChanged += OnEq;
        FormClosed += (s, e) => hid.EqChanged -= OnEq;
        if (snapshot != null) LoadSnapshot(snapshot, false);
        else graph.SetBands(StandardBands());
    }

    void ApplyPreset(int index)
    {
        var bands = graph.Bands;
        var gains = PresetGains[index];
        for (int i = 0; i < bands.Length && i < gains.Length; i++)
        {
            bands[i].GainTenths = gains[i];
            if (bands[i].Filter < 1 || bands[i].Filter > 5) bands[i].Filter = 1;
            if (bands[i].QThousandths < 200) bands[i].QThousandths = 1414;
        }
        graph.SetBands(bands);
        chosen = PresetNames[index];
        dirty = true;
        HighlightPreset(index);
        statusLabel.Text = PresetScores[index] > 0
            ? PresetNames[index] + " · AutoEq Harman score " + PresetScores[index] + " · 10-band correction for that headphone. Not applied yet."
            : chosen + "  ·  not applied";
    }

    void HighlightPreset(int index)
    {
        for (int i = 0; i < presetButtons.Length; i++)
            presetButtons[i].BackColor = i == index ? Color.FromArgb(70, 200, 110) : Color.FromArgb(42, 42, 48);
        if (index >= 0) presetButtons[index].ForeColor = Color.FromArgb(14, 28, 18);
        for (int i = 0; i < presetButtons.Length; i++)
            if (i != index) presetButtons[i].ForeColor = Color.FromArgb(236, 236, 236);
    }

    void OnEq(EqSnapshot snapshot)
    {
        if (IsDisposed) return;
        if (!dirty) LoadSnapshot(snapshot, false);
        else if (Matches(snapshot)) statusLabel.Text = "On the headset  ·  " + chosen;
    }

    void LoadSnapshot(EqSnapshot snapshot, bool fromUser)
    {
        if (snapshot == null || snapshot.Wireless == null) return;
        graph.SetBands(snapshot.Wireless.OrderBy(b => b.Frequency).ToArray());
        if (!fromUser) { dirty = false; chosen = "Headset"; HighlightPreset(-1); statusLabel.Text = "Headset curve  ·  preset " + snapshot.WirelessPreset; }
    }

    bool Matches(EqSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Wireless == null) return false;
        var live = snapshot.Wireless.OrderBy(b => b.Frequency).ToArray();
        var mine = graph.Bands;
        if (live.Length != mine.Length) return false;
        for (int i = 0; i < mine.Length; i++)
            if (live[i].Frequency != mine[i].Frequency || Math.Abs(live[i].GainTenths - mine[i].GainTenths) > 1) return false;
        return true;
    }

    static EqBand[] StandardBands()
    {
        var bands = new EqBand[StandardFreqs.Length];
        for (int i = 0; i < bands.Length; i++)
            bands[i] = new EqBand { Frequency = StandardFreqs[i], Filter = 1, QThousandths = 1414 };
        return bands;
    }

    class EqGraph : Control
    {
        EqBand[] bands = new EqBand[0];
        int hot = -1, drag = -1;
        public event EventHandler Edited;
        public EqBand[] Bands { get { return bands; } }

        public EqGraph()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(22, 22, 26);
            Cursor = Cursors.Hand;
        }

        public void SetBands(EqBand[] next)
        {
            bands = next ?? new EqBand[0];
            Invalidate();
        }

        RectangleF Plot()
        {
            return new RectangleF(48, 18, Math.Max(10, Width - 72), Math.Max(10, Height - 48));
        }

        float X(int i, RectangleF plot)
        {
            if (bands.Length <= 1) return plot.Left;
            return plot.Left + plot.Width * i / (bands.Length - 1);
        }

        float Y(int tenths, RectangleF plot)
        {
            tenths = Math.Max(-120, Math.Min(120, tenths));
            return plot.Top + plot.Height * (120 - tenths) / 240f;
        }

        int Hit(Point p)
        {
            var plot = Plot();
            for (int i = 0; i < bands.Length; i++)
            {
                float dx = p.X - X(i, plot), dy = p.Y - Y(bands[i].GainTenths, plot);
                if (dx * dx + dy * dy <= 16 * 16) return i;
            }
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            drag = Hit(e.Location);
            if (drag >= 0) { hot = drag; UpdateGain(e.Y); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag >= 0) UpdateGain(e.Y);
            else
            {
                int next = Hit(e.Location);
                if (next != hot) { hot = next; Invalidate(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            drag = -1;
            base.OnMouseUp(e);
        }

        void UpdateGain(int y)
        {
            var plot = Plot();
            float t = (y - plot.Top) / plot.Height;
            int tenths = (int)Math.Round(120 - t * 240);
            tenths = Math.Max(-120, Math.Min(120, tenths));
            if (bands[drag].GainTenths == tenths) return;
            bands[drag].GainTenths = tenths;
            if (Edited != null) Edited(this, EventArgs.Empty);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var plot = Plot();
            using (var fill = new SolidBrush(Color.FromArgb(32, 32, 38)))
                g.FillRectangle(fill, plot);
            using (var grid = new Pen(Color.FromArgb(38, 255, 255, 255)))
            using (var zero = new Pen(Color.FromArgb(90, 255, 255, 255)))
            using (var font = new Font("Segoe UI", 8f))
            using (var muted = new SolidBrush(Color.FromArgb(140, 140, 148)))
            {
                for (int db = -12; db <= 12; db += 6)
                {
                    float y = Y(db * 10, plot);
                    g.DrawLine(db == 0 ? zero : grid, plot.Left, y, plot.Right, y);
                    string label = db > 0 ? "+" + db : db.ToString();
                    g.DrawString(label, font, muted, 8, y - 7);
                }
                if (bands.Length == 0) return;
                var points = new PointF[bands.Length];
                for (int i = 0; i < bands.Length; i++) points[i] = new PointF(X(i, plot), Y(bands[i].GainTenths, plot));
                using (var area = new GraphicsPath())
                {
                    area.AddLine(points[0].X, Y(0, plot), points[0].X, points[0].Y);
                    area.AddLines(points);
                    area.AddLine(points[points.Length - 1].X, points[points.Length - 1].Y, points[points.Length - 1].X, Y(0, plot));
                    area.CloseFigure();
                    using (var brush = new SolidBrush(Color.FromArgb(56, 70, 200, 110))) g.FillPath(brush, area);
                }
                using (var pen = new Pen(Color.FromArgb(70, 200, 110), 2.2f)) g.DrawLines(pen, points);
                for (int i = 0; i < bands.Length; i++)
                {
                    bool on = i == hot || i == drag;
                    float r = on ? 7f : 5f;
                    var c = points[i];
                    using (var brush = new SolidBrush(on ? Color.White : Color.FromArgb(70, 200, 110)))
                        g.FillEllipse(brush, c.X - r, c.Y - r, r * 2, r * 2);
                    var freq = FreqLabel(bands[i].Frequency);
                    var size = g.MeasureString(freq, font);
                    g.DrawString(freq, font, muted, c.X - size.Width / 2, plot.Bottom + 6);
                }
                int show = drag >= 0 ? drag : hot;
                if (show >= 0)
                {
                    string tip = FreqLabel(bands[show].Frequency) + "   " + EqBand.Db(bands[show].GainTenths);
                    var size = g.MeasureString(tip, Font);
                    float x = Math.Min(points[show].X + 12, plot.Right - size.Width - 8);
                    float y = Math.Max(plot.Top + 4, points[show].Y - 26);
                    var box = new RectangleF(x - 6, y - 2, size.Width + 12, size.Height + 4);
                    using (var brush = new SolidBrush(Color.FromArgb(230, 18, 18, 22))) g.FillRectangle(brush, box);
                    g.DrawString(tip, Font, Brushes.White, x, y);
                }
            }
        }

        static string FreqLabel(int hz)
        {
            if (hz >= 1000) return (hz / 1000.0).ToString(hz % 1000 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";
            return hz.ToString();
        }
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

    public void Apply(float game, float chat, Func<string, bool> isChat, Func<string, bool> isMedia)
    {
        foreach (var s in Sessions())
        {
            float target = isChat(s.Name) ? chat : isMedia(s.Name) ? 1f : game;
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
// Dongle protocol (USB HID). Both families use usage page 0xFFC0 to send and 0xFF00 to receive.
//
// Arctis 7+ family, report id 0x00:
//   pushes  [00] 45 <game> <chat>                 dial moved (0-100 each)
//   pushes  [00] BB <03 connected / 00 off>       connection change
//   pushes  [00] B7 <battery 0-4>                 battery changed
//   request [00] B0 -> [00] B0 <online 03 / off 01> <battery 0-4> <charging 01> <game> <chat>
//
// Arctis Nova Elite, report id 0x01 out / 0x07 in:
//   write   [01] 49 <01 on / 00 off>              software ChatMix; the dial emits 45 only while this is on
//   write   [01] B0                               status query
//   answer  [01] B0 .. headset % at byte 6, spare % at byte 7, radio at byte 14 (08 = online), charging at byte 15 (02 = charging)
//   pushes  [07] 45 <game> <chat>
//   pushes  [07] B7 <headset %> <spare %> <charging>
//   pushes  [07] B5 .. <radio at byte 4>
// ---------------------------------------------------------------------------------------------

struct HeadsetStatus
{
    public bool Known, Online, Charging, HasDockBattery, Elite, MicKnown, MicMuted, SidetoneKnown, SidetoneOn;
    public bool BrightnessKnown, DimTimerKnown, DimModeKnown, AutoOffKnown;
    public int Battery, BatteryMax, DockBattery, AncMode, SidetoneLevel, Brightness, DimTimer, DimMode, AutoOff;
}

class EqBand
{
    public int Frequency, Filter, GainTenths, QThousandths;

    public string Describe()
    {
        return Frequency + " Hz  ·  " + FilterName(Filter) + "  ·  " + Db(GainTenths) + "  ·  Q " + (QThousandths / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Db(int tenths)
    {
        return (tenths / 10.0).ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
    }

    static string FilterName(int type)
    {
        switch (type)
        {
            case 1: return "peak";
            case 2: return "low pass";
            case 3: return "high pass";
            case 4: return "low shelf";
            case 5: return "high shelf";
            default: return "type " + type;
        }
    }
}

class EqSnapshot
{
    public EqBand[] Wireless, Measured;
    public int[] MicGains, BtGains;
    public int WirelessPreset, MicPreset, BtPreset;
    public bool MeasuredDiffers;

    public static EqSnapshot Parse(byte[] buf)
    {
        if (buf == null || buf.Length < 161) return null;
        int o = buf[0] == 0x01 && buf[1] == 0x20 ? 1 : buf[0] == 0x20 ? 0 : -1;
        if (o < 0 || buf.Length < o + 161) return null;
        var wireless = ReadBands(buf, o + 1);
        var measured = ReadBands(buf, o + 61);
        if (wireless == null) return null;
        return new EqSnapshot
        {
            Wireless = wireless,
            Measured = measured,
            MeasuredDiffers = measured != null && Differs(wireless, measured),
            BtGains = ReadGains(buf, o + 121),
            MicGains = ReadGains(buf, o + 131),
            WirelessPreset = buf[o + 148],
            MicPreset = buf[o + 149],
            BtPreset = buf[o + 150]
        };
    }

    static bool Differs(EqBand[] a, EqBand[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i].Frequency != b[i].Frequency || a[i].Filter != b[i].Filter || a[i].GainTenths != b[i].GainTenths || a[i].QThousandths != b[i].QThousandths)
                return true;
        return false;
    }

    static EqBand[] ReadBands(byte[] buf, int start)
    {
        var bands = new EqBand[10];
        int plausible = 0;
        for (int i = 0; i < 10; i++)
        {
            int p = start + i * 6;
            if (p + 6 > buf.Length) return null;
            int freq = buf[p] | (buf[p + 1] << 8);
            int q = buf[p + 4] | (buf[p + 5] << 8);
            if (freq >= 20 && freq <= 20000) plausible++;
            bands[i] = new EqBand { Frequency = freq, Filter = buf[p + 2], GainTenths = (sbyte)buf[p + 3], QThousandths = q };
        }
        return plausible >= 8 ? bands : null;
    }

    static int[] ReadGains(byte[] buf, int start)
    {
        var gains = new int[10];
        for (int i = 0; i < 10; i++) gains[i] = (sbyte)buf[start + i];
        return gains;
    }

    public static byte[] BuildWirelessSlot(EqBand[] bands, string name, int length)
    {
        if (length < 140) length = 1036;
        var msg = new byte[length];
        msg[0] = 0x01;
        msg[1] = 0x1B;
        msg[2] = 4;
        WriteAscii(msg, 3, 6, name);
        WriteAscii(msg, 9, 61, name);
        var ordered = (bands ?? new EqBand[0]).OrderBy(b => b.Frequency).Take(10).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            var band = ordered[i];
            int freq = Math.Max(20, Math.Min(20000, band.Frequency));
            int gain = Math.Max(-120, Math.Min(120, band.GainTenths));
            int q = band.QThousandths <= 0 ? 1414 : Math.Max(200, Math.Min(10000, band.QThousandths));
            int type = band.Filter >= 1 && band.Filter <= 5 ? band.Filter : 1;
            int d = 70 + i * 6;
            msg[d] = (byte)freq;
            msg[d + 1] = (byte)(freq >> 8);
            msg[d + 2] = (byte)type;
            msg[d + 3] = unchecked((byte)gain);
            msg[d + 4] = (byte)q;
            msg[d + 5] = (byte)(q >> 8);
        }
        return msg;
    }

    static void WriteAscii(byte[] buf, int at, int len, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        for (int i = 0; i < len && i < bytes.Length; i++)
            if (bytes[i] >= 32 && bytes[i] < 127) buf[at + i] = bytes[i];
    }
}

enum DialProtocol { Arctis7Plus, NovaElite }

class HeadsetModel
{
    public DialProtocol Protocol;
    public ushort[] ProductIds;
}

class ArctisHid
{
    const ushort VendorId = 0x1038;
    static readonly HeadsetModel[] Models = {
        new HeadsetModel { Protocol = DialProtocol.Arctis7Plus, ProductIds = new ushort[] { 0x220E, 0x2212, 0x2216, 0x2236 } },
        new HeadsetModel { Protocol = DialProtocol.NovaElite, ProductIds = new ushort[] { 0x2244, 0x2270 } },
    };

    public event Action<int, int> MixChanged;
    public event Action<HeadsetStatus> StatusChanged;
    public event Action<EqSnapshot> EqChanged;

    readonly SynchronizationContext ui;
    readonly object statusLock = new object();
    readonly Queue<byte[]> outbound = new Queue<byte[]>();
    volatile bool deviceLost, requestStatus, stopping, rearmChatMix, wantEq;
    volatile string eliteCommandPath;
    int eliteOutLen = 64;
    int featureLength = 1036;
    byte[] pendingEq;
    DialProtocol protocol;
    HeadsetStatus lastStatus;
    int lastGame = -1, lastChat = -1;

    public ArctisHid(SynchronizationContext ui) { this.ui = ui; }

    public void Start()
    {
        new Thread(Run) { IsBackground = true, Name = "Arctis HID" }.Start();
    }

    public void SendElite(byte command, params byte[] payload)
    {
        if (payload == null || payload.Length > 2) return;
        var item = new byte[1 + payload.Length];
        item[0] = command;
        Buffer.BlockCopy(payload, 0, item, 1, payload.Length);
        lock (outbound) outbound.Enqueue(item);
    }

    public void RequestEq() { wantEq = true; }

    public void WriteWirelessEq(EqBand[] bands, string name)
    {
        lock (statusLock) pendingEq = EqSnapshot.BuildWirelessSlot(bands, name, featureLength);
    }

    public void Shutdown()
    {
        string path = eliteCommandPath;
        int length = eliteOutLen;
        stopping = true;
        deviceLost = true;
        if (path == null || length < 64) return;
        try
        {
            using (var writer = Hid.Open(path, length))
                writer.Write(EliteCommand(length, 0x49, 0x00), 0, length);
        }
        catch (Exception) { }
    }

    void Run()
    {
        while (!stopping)
        {
            try { RunSession(); } catch (Exception) { }
            if (lastStatus.Known) Publish(new HeadsetStatus());
            if (stopping) break;
            Thread.Sleep(2000);
        }
    }

    void RunSession()
    {
        string statusPath = null, eventPath = null;
        HeadsetModel model = null;
        int outLen = 64, commandReportIn = 64, eventReportIn = 64, featureLen = 1036;
        foreach (var candidate in Models)
        {
            string command = null, events = null;
            int length = 64, commandIn = 64, eventIn = 64, feature = 1036;
            foreach (var dev in Hid.Enumerate(VendorId, candidate.ProductIds))
            {
                if (dev.UsagePage == 0xFFC0)
                {
                    command = dev.Path;
                    if (dev.OutputLength >= 64) length = dev.OutputLength;
                    if (dev.InputLength >= 64) commandIn = dev.InputLength;
                    if (dev.FeatureLength >= 64) feature = dev.FeatureLength;
                }
                else if (dev.UsagePage == 0xFF00)
                {
                    events = dev.Path;
                    if (dev.InputLength >= 64) eventIn = dev.InputLength;
                }
            }
            if (command == null) continue;
            model = candidate;
            statusPath = command;
            eventPath = events;
            outLen = length;
            commandReportIn = commandIn;
            eventReportIn = eventIn;
            featureLen = feature;
            featureLength = feature;
            break;
        }
        if (statusPath == null) return;

        protocol = model.Protocol;
        deviceLost = false;
        if (protocol == DialProtocol.NovaElite)
        {
            eliteCommandPath = statusPath;
            eliteOutLen = outLen;
        }
        var readers = new List<FileStream>();
        try
        {
            int readLength = protocol == DialProtocol.NovaElite ? commandReportIn : 65;
            readers.Add(StartReader(statusPath, readLength));
            if (eventPath != null) readers.Add(StartReader(eventPath, protocol == DialProtocol.NovaElite ? eventReportIn : 65));

            using (var writer = Hid.Open(statusPath, protocol == DialProtocol.NovaElite ? outLen : 65))
            {
                int reportLength = protocol == DialProtocol.NovaElite ? outLen : 65;
                var request = new byte[reportLength];
                if (protocol == DialProtocol.NovaElite)
                {
                    request[0] = 0x01;
                    request[1] = 0xB0;
                    writer.Write(EliteCommand(reportLength, 0x49, 0x01), 0, reportLength);
                }
                else request[1] = 0xB0;

                int tick = 0;
                while (!deviceLost && !stopping)
                {
                    if (tick++ % 50 == 0 || requestStatus)
                    {
                        requestStatus = false;
                        writer.Write(request, 0, request.Length);
                    }
                    if (rearmChatMix && protocol == DialProtocol.NovaElite)
                    {
                        rearmChatMix = false;
                        writer.Write(EliteCommand(reportLength, 0x49, 0x01), 0, reportLength);
                    }
                    if (protocol == DialProtocol.NovaElite) DrainOutbound(writer, reportLength);
                    if (protocol == DialProtocol.NovaElite) WritePendingEq(statusPath);
                    if (wantEq && protocol == DialProtocol.NovaElite)
                    {
                        wantEq = false;
                        try
                        {
                            writer.Write(EliteCommand(reportLength, 0x20, 0), 0, reportLength);
                            var snap = EqSnapshot.Parse(Hid.GetFeature(statusPath, featureLen));
                            if (snap != null) PublishEq(snap);
                        }
                        catch (Exception) { }
                    }
                    Thread.Sleep(100);
                }
                if (stopping && protocol == DialProtocol.NovaElite)
                {
                    try { writer.Write(EliteCommand(reportLength, 0x49, 0x00), 0, reportLength); }
                    catch (Exception) { }
                }
            }
        }
        finally
        {
            eliteCommandPath = null;
            foreach (var r in readers) r.Dispose();
        }
    }

    void DrainOutbound(FileStream writer, int reportLength)
    {
        byte[][] batch;
        lock (outbound)
        {
            if (outbound.Count == 0) return;
            batch = outbound.ToArray();
            outbound.Clear();
        }
        foreach (var item in batch)
            writer.Write(EliteCommand(reportLength, item[0], item.Length > 1 ? item[1] : (byte)0, item.Length > 2 ? item[2] : (byte)0, item.Length - 1), 0, reportLength);
    }

    static byte[] EliteCommand(int reportLength, byte command, byte a, byte b, int payloadLength)
    {
        var buf = new byte[reportLength];
        buf[0] = 0x01;
        buf[1] = command;
        if (payloadLength >= 1) buf[2] = a;
        if (payloadLength >= 2) buf[3] = b;
        return buf;
    }

    static byte[] EliteCommand(int reportLength, byte command, byte value)
    {
        return EliteCommand(reportLength, command, value, 0, 1);
    }

    FileStream StartReader(string path, int reportLength)
    {
        var stream = Hid.Open(path, reportLength);
        new Thread(() =>
        {
            var buf = new byte[reportLength];
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
        if (protocol == DialProtocol.NovaElite) ParseElite(r, n);
        else ParseArctis7Plus(r, n);
    }

    void ParseArctis7Plus(byte[] r, int n)
    {
        if (n >= 4 && r[1] == 0x45 && r[2] <= 100 && r[3] <= 100) PublishMix(r[2], r[3]);
        else if (n >= 7 && r[1] == 0xB0)
        {
            bool online = r[2] != 0x01 && r[5] <= 100 && r[6] <= 100;
            Publish(new HeadsetStatus { Known = true, Online = online, Battery = Math.Min((int)r[3], 4), BatteryMax = 4, Charging = r[4] == 0x01 });
            if (online) PublishMix(r[5], r[6]);
        }
        else if (n >= 3 && r[1] == 0xBB)
        {
            lock (statusLock) { var st = lastStatus; st.Known = true; st.Online = r[2] == 0x03; st.BatteryMax = 4; Publish(st); }
            requestStatus = true;
        }
        else if (n >= 3 && r[1] == 0xB7 && r[2] <= 4)
        {
            lock (statusLock) { var st = lastStatus; st.Known = true; st.Battery = r[2]; st.BatteryMax = 4; Publish(st); }
        }
    }

    void ParseElite(byte[] r, int n)
    {
        if (n < 3) return;
        if (r[0] == 0x07 && r[1] == 0x45 && n >= 4 && r[2] <= 100 && r[3] <= 100) PublishMix(r[2], r[3]);
        else if (r[0] == 0x01 && r[1] == 0xB0 && n >= 16)
        {
            HeadsetStatus prev;
            lock (statusLock) prev = lastStatus;
            bool online = r[14] == 0x08;
            Publish(new HeadsetStatus
            {
                Known = true,
                Online = online,
                Elite = true,
                Battery = Math.Min((int)r[6], 100),
                BatteryMax = 100,
                DockBattery = Math.Min((int)r[7], 100),
                HasDockBattery = true,
                Charging = r[15] == 0x02,
                MicKnown = true,
                MicMuted = r[9] != 0,
                AncMode = r[10] <= 2 ? r[10] : 0,
                SidetoneKnown = prev.SidetoneKnown,
                SidetoneOn = prev.SidetoneOn,
                SidetoneLevel = prev.SidetoneLevel,
                BrightnessKnown = prev.BrightnessKnown,
                Brightness = prev.Brightness,
                DimTimerKnown = prev.DimTimerKnown,
                DimTimer = prev.DimTimer,
                DimModeKnown = prev.DimModeKnown,
                DimMode = prev.DimMode,
                AutoOffKnown = r[12] <= 6,
                AutoOff = r[12] <= 6 ? r[12] : prev.AutoOff
            });
            if (online && !prev.Online) rearmChatMix = true;
        }
        else if (r[0] == 0x07 && r[1] == 0xB7 && n >= 5 && r[2] <= 100 && r[3] <= 100)
        {
            lock (statusLock)
            {
                if (!lastStatus.Known) return;
                var st = lastStatus;
                st.Battery = r[2];
                st.BatteryMax = 100;
                st.DockBattery = r[3];
                st.HasDockBattery = true;
                st.Elite = true;
                st.Charging = r[4] == 0x02;
                Publish(st);
            }
        }
        else if (r[0] == 0x07 && r[1] == 0xB5 && n >= 5)
        {
            HeadsetStatus prev;
            lock (statusLock) prev = lastStatus;
            if (!prev.Known) { requestStatus = true; return; }
            bool online = r[4] == 0x08;
            if (online != prev.Online)
            {
                var st = prev;
                st.Online = online;
                Publish(st);
                if (online) rearmChatMix = true;
            }
            requestStatus = true;
        }
        else if (r[0] == 0x07 && r[1] == 0xBB && n >= 3)
        {
            lock (statusLock)
            {
                if (!lastStatus.Known) return;
                var st = lastStatus;
                st.Elite = true;
                st.MicKnown = true;
                st.MicMuted = r[2] != 0;
                Publish(st);
            }
        }
        else if (r[0] == 0x07 && r[1] == 0xBD && n >= 3 && r[2] <= 2)
        {
            lock (statusLock)
            {
                if (!lastStatus.Known) return;
                var st = lastStatus;
                st.Elite = true;
                st.MicKnown = true;
                st.AncMode = r[2];
                Publish(st);
            }
        }
        else if (r[0] == 0x07 && r[1] == 0x38 && n >= 4 && r[3] >= 1 && r[3] <= 10)
        {
            lock (statusLock)
            {
                if (!lastStatus.Known) return;
                var st = lastStatus;
                st.Elite = true;
                st.SidetoneKnown = true;
                st.SidetoneOn = r[2] != 0;
                st.SidetoneLevel = r[3];
                Publish(st);
            }
        }
        else if (r[0] == 0x07 && r[1] == 0x85 && n >= 3 && r[2] >= 1 && r[2] <= 10) PatchScreen(st => { st.BrightnessKnown = true; st.Brightness = r[2]; return st; });
        else if (r[0] == 0x07 && r[1] == 0x83 && n >= 3 && r[2] <= 6) PatchScreen(st => { st.DimTimerKnown = true; st.DimTimer = r[2]; return st; });
        else if (r[0] == 0x07 && r[1] == 0x88 && n >= 3 && r[2] <= 1) PatchScreen(st => { st.DimModeKnown = true; st.DimMode = r[2]; return st; });
        else if (r[0] == 0x07 && r[1] == 0xC1 && n >= 3 && r[2] <= 6) PatchScreen(st => { st.AutoOffKnown = true; st.AutoOff = r[2]; return st; });
    }

    void PatchScreen(Func<HeadsetStatus, HeadsetStatus> edit)
    {
        lock (statusLock)
        {
            if (!lastStatus.Known) return;
            var st = edit(lastStatus);
            st.Elite = true;
            Publish(st);
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

    void PublishEq(EqSnapshot snapshot)
    {
        ui.Post(_ => { if (EqChanged != null) EqChanged(snapshot); }, null);
    }

    void WritePendingEq(string path)
    {
        byte[] msg;
        lock (statusLock) { msg = pendingEq; pendingEq = null; }
        if (msg == null) return;
        try
        {
            Hid.SetFeature(path, msg);
            wantEq = true;
        }
        catch (Exception) { }
    }
}

static class Hid
{
    public class DeviceInfo { public string Path; public ushort UsagePage, Usage, InputLength, OutputLength, FeatureLength; }

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
                    result.Add(new DeviceInfo { Path = path, UsagePage = caps.UsagePage, Usage = caps.Usage, InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength, FeatureLength = caps.FeatureReportByteLength });
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static FileStream Open(string path) { return Open(path, 65); }

    public static FileStream Open(string path, int reportLength)
    {
        var h = CreateFile(path, 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException("Cannot open " + path);
        return new FileStream(h, FileAccess.ReadWrite, reportLength, false);
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

    public static byte[] GetFeature(string path, int length)
    {
        if (length < 16 || length > 4096) length = 1036;
        using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) return null;
            var buf = new byte[length];
            buf[0] = 0x01;
            if (!HidD_GetFeature(h, buf, buf.Length)) return null;
            return buf;
        }
    }

    public static void SetFeature(string path, byte[] buf)
    {
        if (buf == null || buf.Length < 16) throw new IOException("Bad EQ report");
        using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) throw new IOException("Cannot open " + path);
            if (!HidD_SetFeature(h, buf, buf.Length)) throw new IOException("EQ write failed");
        }
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int length);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int length);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
