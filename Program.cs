using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CpuCoreManager;

// ponytail: ulong mask caps at 64 logical processors (any current PC is far below).
public record Profile(string Exe, ulong Mask);

static class Cores
{
    public static ulong ToMask(IEnumerable<int> cores) =>
        cores.Select(c => 1UL << c).Aggregate(0UL, (a, b) => a | b);

    public static List<int> FromMask(ulong mask)
    {
        var l = new List<int>();
        for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) l.Add(i);
        return l;
    }

    // "0-7, 12" style summary
    public static string Fmt(ulong mask)
    {
        var cs = FromMask(mask);
        if (cs.Count == 0) return "none";
        var parts = new List<string>();
        int s = cs[0], e = cs[0];
        foreach (var c in cs.Skip(1))
            if (c == e + 1) e = c;
            else { parts.Add(s == e ? $"{s}" : $"{s}-{e}"); s = e = c; }
        parts.Add(s == e ? $"{s}" : $"{s}-{e}");
        return string.Join(", ", parts);
    }
}

// Hardware lookup via the registry (no WMI, no FFI): vendor string + Intel E-core count.
static class CpuId
{
    const string CpRoot = @"HARDWARE\DESCRIPTION\System\CentralProcessor";

    public static string Vendor()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(CpRoot + @"\0");
            var v = k?.GetValue("VendorIdentifier") as string;
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        catch { /* fall through */ }
        return "Unknown";
    }

    // E-core count. Only Intel exposes a Windows-visible P/E split, so any other
    // vendor returns 0 (nothing to lie about). On hybrid Intel each logical
    // processor has its own CentralProcessor\<n> key; E-core LPs run at a lower
    // base clock than the P-core cluster, so count every LP whose ~MHz is below
    // the max observed. Heuristic but crash-free and correct for current SKUs.
    public static int ECores()
    {
        if (!Vendor().Contains("Intel", StringComparison.OrdinalIgnoreCase)) return 0;

        var mhz = new List<int>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(CpRoot);
            if (root == null) return 0;
            for (int i = 0; ; i++)
            {
                string name = i.ToString();
                using var k = root.OpenSubKey(name);
                if (k == null) break;
                if (k.GetValue("~MHz") is int m) mhz.Add(m);
            }
        }
        catch { return 0; }

        if (mhz.Count < 2) return 0;
        int max = int.MinValue, e = 0;
        foreach (var f in mhz) if (f > max) max = f;
        foreach (var f in mhz) if (f < max) e++;
        return e; // LPs below the top base-clock cluster are E-cores
    }
}

public readonly record struct CpuInfo(string Vendor, int Logical, int ECores)
{
    public bool IsHybridIntel => Vendor == "GenuineIntel" && ECores > 0;
    // ponytail: assumes Windows assigns P-core logical IDs first (true on Alder Lake+); per-LP topology API is the upgrade path.
    public int PThreads => IsHybridIntel ? Logical - ECores : 0;

    // ponytail: bool+value cache instead of `static CpuInfo?` — a struct holding a static field of its own Nullable type throws TypeLoadException at runtime on .NET 8.
    static bool _hasCache; static CpuInfo _cacheVal;
    public static CpuInfo Detect()
    {
        if (_hasCache) return _cacheVal;
        try { _cacheVal = Create(); }
        catch { _cacheVal = new CpuInfo("Unknown", Environment.ProcessorCount, 0); } // FFI failed: plain thread view
        _hasCache = true;
        return _cacheVal;
    }
    static CpuInfo Create()
    {
        var v = CpuId.Vendor();
        return new(v, Environment.ProcessorCount, v == "GenuineIntel" ? CpuId.ECores() : 0);
    }

    public override string ToString() => IsHybridIntel
        ? $"{Vendor} hybrid: {PThreads / 2} P-cores + {ECores} E-cores ({Logical} threads)"
        : $"{Vendor}: {Logical} threads";
}

static class Store
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CpuCoreManager");
    public static string ConfigFile => Path.Combine(Dir, "profiles.json");

    public static List<Profile> Load()
    {
        try
        {
            if (System.IO.File.Exists(ConfigFile))
                return JsonSerializer.Deserialize<List<Profile>>(System.IO.File.ReadAllText(ConfigFile)) ?? new();
        }
        catch { /* corrupt file -> start fresh rather than crash */ }
        return new();
    }

    public static void Save(List<Profile> profiles)
    {
        Directory.CreateDirectory(Dir);
        var tmp = ConfigFile + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
        if (System.IO.File.Exists(ConfigFile)) System.IO.File.Replace(tmp, ConfigFile, null, true);
        else System.IO.File.Move(tmp, ConfigFile);
    }

    // Global settings for the "reported processor count" rows.
    public record AppSettings(int ProcCountLie = 16, int ProcCountLieHybrid = 16);
    public static string SettingsFile => Path.Combine(Dir, "settings.json");

    public static AppSettings LoadSettings()
    {
        try
        {
            if (System.IO.File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(SettingsFile)) ?? new();
        }
        catch { /* corrupt file -> defaults */ }
        return new();
    }

    public static void SaveSettings(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}

public class MainForm : Form
{
    readonly TextBox exeBox;
    static readonly Color PColor = Color.FromArgb(219, 236, 252); // light blue
    static readonly Color EColor = Color.FromArgb(255, 238, 205); // light amber
    readonly FlowLayoutPanel corePanel;
    readonly Label countLabel;
    readonly ListBox list;
    readonly CheckBox startWithWin;
    readonly NumericUpDown numLie, numLieHybrid;
    readonly StatusStrip status;
    readonly NotifyIcon tray;
    readonly object sync = new();
    List<Profile> profiles = Store.Load();

    public MainForm()
    {
        Text = "CPU Core Manager";
        Icon = AppIcon();
        ClientSize = new Size(584, 532);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var lblApp = new Label { Text = "Application:", Location = new Point(12, 16), AutoSize = true };
        exeBox = new TextBox { Location = new Point(90, 12), Width = 384 };
        var browse = new Button { Text = "Browse...", Location = new Point(480, 10), Width = 90 };
        browse.Click += (s, e) =>
        {
            using var d = new OpenFileDialog { Title = "Select application", Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) exeBox.Text = d.FileName;
        };

        int by = 42, bx = 12;
        Button Preset(string text, Func<ulong> f)
        {
            var b = new Button { Text = text, Location = new Point(bx, by), Width = 78, Height = 26 };
            b.Click += (s, e) => SetMask(f());
            bx += 84;
            return b;
        }
        int n = Environment.ProcessorCount;
        var cpu = CpuInfo.Detect();
        Controls.Add(Preset("All cores", () => Cores.ToMask(Enumerable.Range(0, n))));
        Controls.Add(Preset($"First {Math.Min(8, n)}", () => Cores.ToMask(Enumerable.Range(0, Math.Min(8, n)))));
        Controls.Add(Preset($"First {Math.Min(16, n)}", () => Cores.ToMask(Enumerable.Range(0, Math.Min(16, n)))));
        Controls.Add(Preset("Clear all", () => 0UL));

        countLabel = new Label { Text = "", Location = new Point(452, by + 7), AutoSize = true };
        Controls.Add(countLabel);

        var cpuLabel = new Label
        {
            Text = cpu.IsHybridIntel
                ? $"{cpu}   |   P-cores {0}-{cpu.PThreads - 1} (blue) · E-cores {cpu.PThreads}-{n - 1} (amber)"
                : cpu.ToString(),
            Location = new Point(12, 70), AutoSize = true
        };
        Controls.Add(cpuLabel);

        corePanel = new FlowLayoutPanel
        {
            Location = new Point(12, 94), Size = new Size(560, 96),
            BorderStyle = BorderStyle.FixedSingle, AutoScroll = true, WrapContents = true
        };
        for (int i = 0; i < n; i++)
        {
            var cb = new CheckBox { Text = $"{i}", Width = 46, Height = 24, Margin = new Padding(3) };
            if (cpu.IsHybridIntel) // P/E highlighting only when the hardware has it
                cb.BackColor = i < cpu.PThreads ? PColor : EColor;
            cb.CheckedChanged += (s, e) => UpdateCount();
            corePanel.Controls.Add(cb);
        }

        // ---- reported processor count rows ---------------------------------
        var lblLie = new Label { Text = "Reported processor count:", Location = new Point(12, 200), AutoSize = true };
        numLie = new NumericUpDown { Minimum = 1, Maximum = 16, Value = 16, Location = new Point(178, 196), Width = 54 };
        var lblLieHybrid = new Label { Text = "hybrid CPU:", Location = new Point(246, 200), AutoSize = true };
        numLieHybrid = new NumericUpDown { Minimum = 1, Maximum = 16, Value = 16, Location = new Point(322, 196), Width = 54 };
        var lieHint = new Label
        {
            Text = "apps see this many CPUs",
            Location = new Point(390, 200), AutoSize = true, ForeColor = SystemColors.GrayText
        };
        void LieChanged() => Store.SaveSettings(new Store.AppSettings((int)numLie.Value, (int)numLieHybrid.Value));
        numLie.ValueChanged += (s, e) => LieChanged();
        numLieHybrid.ValueChanged += (s, e) => LieChanged();

        var save = new Button { Text = "Save profile", Location = new Point(12, 232), Width = 110, Height = 30 };
        save.Click += (s, e) => SaveProfile();
        var launch = new Button { Text = "Launch with selected cores", Location = new Point(130, 232), Width = 170, Height = 30 };
        launch.Click += (s, e) => Launch();

        var lblList = new Label { Text = "Saved profiles (auto-applied when the app starts):", Location = new Point(12, 274), AutoSize = true };
        list = new ListBox { Location = new Point(12, 296), Size = new Size(558, 96) };
        list.SelectedIndexChanged += (s, e) => LoadSelected();
        var remove = new Button { Text = "Remove", Location = new Point(12, 400), Width = 90, Height = 28 };
        remove.Click += (s, e) => RemoveProfile();

        startWithWin = new CheckBox
        {
            Text = "Start CPU Core Manager with Windows (keeps auto-apply active after reboot)",
            Location = new Point(12, 434), AutoSize = true, Checked = Startup.Get()
        };
        startWithWin.CheckedChanged += (s, e) => Startup.Set(startWithWin.Checked);

        status = new StatusStrip();
        status.Items.Add(new ToolStripStatusLabel("Ready"));

        tray = new NotifyIcon
        {
            Icon = AppIcon(), Text = "CPU Core Manager", Visible = true
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (s, e) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => ShowFromTray();

        // Load persisted "reported processor count" values (clamped to 1..16)
        var st = Store.LoadSettings();
        numLie.Value = Math.Clamp(st.ProcCountLie, 1, 16);
        numLieHybrid.Value = Math.Clamp(st.ProcCountLieHybrid, 1, 16);

        Controls.AddRange(new Control[] { lblApp, exeBox, browse, countLabel, corePanel, lblLie, numLie, lblLieHybrid, numLieHybrid, lieHint, save, launch, lblList, list, remove, startWithWin, status });
        RefreshList();
        UpdateCount();

        var t = new Thread(WatchLoop) { IsBackground = true };
        t.Start();
    }

    // ---- core selection -------------------------------------------------

    ulong CurrentMask() => Cores.ToMask(Enumerable.Range(0, corePanel.Controls.Count).Where(i => ((CheckBox)corePanel.Controls[i]).Checked));

    void SetMask(ulong mask)
    {
        for (int i = 0; i < corePanel.Controls.Count; i++)
            ((CheckBox)corePanel.Controls[i]).Checked = (mask & (1UL << i)) != 0;
        UpdateCount();
    }

    void UpdateCount()
    {
        int sel = Cores.FromMask(CurrentMask()).Count;
        countLabel.Text = $"{sel}/{Environment.ProcessorCount} cores";
    }

    static Icon AppIcon() // the exe's own icon (set via <ApplicationIcon>) for tray/title bar
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    // ---- profiles -------------------------------------------------------

    void SaveProfile()
    {
        var exe = Path.GetFullPath(exeBox.Text.Trim().Trim('"'));
        if (exe == "" || !File.Exists(exe)) { SetStatus("Enter a valid .exe path first."); return; }
        ulong mask = CurrentMask();
        lock (sync)
        {
            profiles.RemoveAll(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
            profiles.Add(new Profile(exe, mask));
            Store.Save(profiles);
        }
        RefreshList();
        SetStatus($"Saved: {Path.GetFileName(exe)} -> cores [{Cores.Fmt(mask)}]");
    }

    void RemoveProfile()
    {
        if (list.SelectedIndex < 0) return;
        lock (sync) { profiles.RemoveAt(list.SelectedIndex); Store.Save(profiles); }
        RefreshList();
        SetStatus("Profile removed.");
    }

    void LoadSelected()
    {
        if (list.SelectedIndex < 0) return;
        Profile p;
        lock (sync) p = profiles[list.SelectedIndex];
        exeBox.Text = p.Exe;
        var cores = Cores.FromMask(p.Mask);
        for (int i = 0; i < corePanel.Controls.Count; i++)
            ((CheckBox)corePanel.Controls[i]).Checked = cores.Contains(i);
        UpdateCount();
    }

    void RefreshList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        List<Profile> snap;
        lock (sync) snap = new List<Profile>(profiles);
        foreach (var p in snap)
            list.Items.Add($"{Path.GetFileName(p.Exe)}   ->   [{Cores.Fmt(p.Mask)}]");
        list.EndUpdate();
    }

    // ---- launch / apply ---------------------------------------------------

    int CurrentLie() => CpuInfo.Detect().IsHybridIntel ? (int)numLieHybrid.Value : (int)numLie.Value;

    void Launch()
    {
        var exe = Path.GetFullPath(exeBox.Text.Trim().Trim('"'));
        if (!File.Exists(exe)) { SetStatus("Executable not found."); return; }
        ulong mask = CurrentMask();
        int lie = CurrentLie();
        try
        {
            LieLauncher.Launch(exe, mask, lie);
            SetStatus($"Launched {Path.GetFileName(exe)} on cores [{Cores.Fmt(mask)}], reporting {lie} processors");
        }
        catch (Exception ex) { SetStatus("Launch failed: " + ex.Message); }
    }

    static void ApplyAffinity(Process p, ulong mask)
    {
        for (int i = 0; i < 20 && !p.HasExited; i++)
        {
            try { p.ProcessorAffinity = unchecked((nint)mask); return; }
            catch { Thread.Sleep(50); } // process may still be initializing, or elevated (access denied)
        }
    }

    // Background watcher: re-apply saved affinity whenever a profiled app starts.
    void WatchLoop()
    {
        var handled = new HashSet<int>();
        while (true)
        {
            try
            {
                List<Profile> snap;
                lock (sync) snap = new List<Profile>(profiles);
                if (snap.Count > 0)
                {
                    if (handled.Count > 4096) handled.Clear(); // ponytail: coarse reset, worst case re-applies once
                    foreach (var p in Process.GetProcesses())
                        using (p)
                        {
                            string name;
                            try { name = p.ProcessName + ".exe"; } catch { continue; }
                            var prof = snap.FirstOrDefault(x => x.Exe != null &&
                                Path.GetFileName(x.Exe).Equals(name, StringComparison.OrdinalIgnoreCase));
                            if (prof == null || !handled.Add(p.Id)) continue;
                            ApplyAffinity(p, prof.Mask); // env-var lie needs launch time; watcher can only pin cores
                        }
                }
            }
            catch { /* snapshot can fail mid-enumeration */ }
            Thread.Sleep(300);
        }
    }

    // ---- chrome -----------------------------------------------------------

    void SetStatus(string msg) => status.Items[0].Text = msg;

    void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide(); // minimize -> tray
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tray.Visible = false;
        base.OnFormClosed(e);
    }
}

static class Startup
{
    const string SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "CpuCoreManager";

    public static bool Get()
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey);
        return k?.GetValue(ValueName) != null;
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(SubKey);
            if (on) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(ValueName, false);
        }
        catch { /* registry write failed; checkbox state is best-effort */ }
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Crash fix: never die silently. Log next to the exe and show a dialog so
        // startup failures (TypeLoadException, missing deps, ...) are visible.
        void Dump(Exception? ex)
        {
            try
            {
                var log = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.AppendAllText(log, $"[{DateTime.Now:O}] {(ex?.ToString() ?? "unknown")}\n\n");
            }
            catch { }
        }
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Dump(ex);
            MessageBox.Show("CPU Core Manager crashed:\n" + (ex?.Message ?? "unknown"), "CPU Core Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Application.ThreadException += (s, e) =>
        {
            Dump(e.Exception);
            MessageBox.Show("CPU Core Manager error:\n" + e.Exception.Message, "CPU Core Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        if (args.Contains("--selftest"))
        {
            AttachParentConsole(); // make SELFTEST output visible when run from cmd/PowerShell
            return SelfTest.Run();
        }
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Dump(ex);
            MessageBox.Show("CPU Core Manager failed to start:\n" + ex, "CPU Core Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }

    // WinExe has no console of its own — attach to the parent's so --selftest output shows in cmd/PowerShell.
    delegate bool AttachConsoleFn(uint id);

    static void AttachParentConsole()
    {
        try
        {
            var k32 = NativeLibrary.Load("kernel32.dll");
            var attach = (AttachConsoleFn)Marshal.GetDelegateForFunctionPointer(NativeLibrary.GetExport(k32, "AttachConsole")!, typeof(AttachConsoleFn))!;
            attach(0xFFFFFFFFu); // ATTACH_PARENT_PROCESS
        }
        catch { /* launched from Explorer: no parent console */ }
    }
}

// One runnable check: mask math, JSON round-trip, and a real process affinity set/read.
static class SelfTest
{
    public static int Run()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }

        // mask math
        Check(Cores.ToMask(new[] { 0, 1 }) == 3UL, "ToMask basic");
        Check(string.Join(",", Cores.FromMask(0b1100)) == "2,3", "FromMask basic");
        Check(Cores.Fmt(0b1111_0000_1111) == "0-3, 8-11", "Fmt ranges");

        // hardware lookup (CPUID stubs): vendor string + hybrid sanity
        CpuInfo ci;
        try { ci = CpuInfo.Detect(); }
        catch (Exception ex) { Console.WriteLine("DETECT EX:\n" + ex); throw; }
        Console.WriteLine("CPU: " + ci);
        Check(ci.Vendor is "GenuineIntel" or "AuthenticAMD", $"cpu vendor ({ci.Vendor})");
        Check(ci.Logical == Environment.ProcessorCount, "logical count");
        Check(!ci.IsHybridIntel || (ci.ECores > 0 && ci.PThreads >= 2), "hybrid sanity");

        // JSON round-trip in a temp dir
        var tmp = Path.Combine(Path.GetTempPath(), $"ccm_selftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var file = Path.Combine(tmp, "profiles.json");
            var list = new List<Profile> { new("C:\\Games\\ChildofLight.exe", 0xFFUL) };
            File.WriteAllText(file, JsonSerializer.Serialize(list));
            var back = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(file))!;
            Check(back.Count == 1 && back[0].Mask == 0xFFUL && back[0].Exe.EndsWith("ChildofLight.exe"), "JSON round-trip");

            // real process: spawn, pin to cores 0-1, read back
            var p = Process.Start(new ProcessStartInfo("cmd", "/c ping -n 6 127.0.0.1 > nul") { UseShellExecute = false });
            if (p != null)
            {
                using (p)
                {
                    ApplyAffinity(p, 3UL);
                    Check((ulong)p.ProcessorAffinity == 3UL, "affinity set/read");
                    p.WaitForExit(10000);
                }
            }
            else fails.Add("could not spawn test process");

            // count-lie path: child must see our NUMBER_OF_PROCESSORS value
            var psi = new ProcessStartInfo("cmd", "/c echo %NUMBER_OF_PROCESSORS%")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            psi.Environment["NUMBER_OF_PROCESSORS"] = "7";
            using (var c = Process.Start(psi)!)
            {
                string outp = c.StandardOutput.ReadToEnd();
                c.WaitForExit(10000);
                Check(outp.Trim() == "7", $"env lie got '{outp.Trim()}'.");
            }
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        Console.WriteLine(fails.Count == 0 ? "SELFTEST PASS" : "SELFTEST FAIL: " + string.Join("; ", fails));
        return fails.Count == 0 ? 0 : 1;
    }

    static void ApplyAffinity(Process p, ulong mask) // same retry logic as the app
    {
        for (int i = 0; i < 20 && !p.HasExited; i++)
        {
            try { p.ProcessorAffinity = unchecked((nint)mask); return; } catch { Thread.Sleep(50); }
        }
    }
}

// Launches an app so it sees N processors: NUMBER_OF_PROCESSORS env var for early readers,
// then pin the process to the selected cores. Simplest reliable combo — no DLL injection.
static class LieLauncher
{
    public static void Launch(string exe, ulong mask, int lie)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, // required to set the child's env vars
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)),
        };
        psi.Environment["NUMBER_OF_PROCESSORS"] = lie.ToString();
        var p = Process.Start(psi);
        if (p == null) return;
        using (p)
        {
            for (int i = 0; i < 40 && !p.HasExited; i++) // retry: process may still be initializing, or elevated
            {
                try { p.ProcessorAffinity = unchecked((nint)mask); return; }
                catch { Thread.Sleep(50); }
            }
        }
    }
}
