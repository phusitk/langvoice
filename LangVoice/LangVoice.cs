// LangVoice - announces Thai / English when the keyboard input language changes.
// Written for C# 5 so it compiles with the csc.exe that ships with Windows (.NET Framework 4.x).
using System;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    const int WM_HOTKEY = 0x0312;
    const int HOTKEY_ID = 1;
    const string HOTKEY_TEXT = "Ctrl+Alt+Shift+M";

    // Hidden message-only window that receives the global hotkey (WM_HOTKEY).
    class HotkeyWindow : NativeWindow
    {
        public HotkeyWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID) ToggleMuted(true);
            base.WndProc(ref m);
        }
    }

    const int LANG_THAI = 0x1E;     // primary language id (low 10 bits of LANGID)
    const int LANG_ENGLISH = 0x09;  // covers en-US, en-GB, en-AU, ...

    static string appDir;
    static string settingsPath;
    static SoundPlayer thaiSound;
    static SoundPlayer englishSound;
    static NotifyIcon tray;
    static ToolStripMenuItem muteItem;
    static ToolStripMenuItem windowSwitchItem;
    static Icon currentIcon;
    static IntPtr currentIconHandle = IntPtr.Zero;

    static int lastLang = 0;
    static IntPtr lastWindow = IntPtr.Zero;
    static bool muted = false;
    static bool announceOnWindowSwitch = false; // default: speak only when the language is switched
    static readonly uint ownProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    // Windows shell surfaces (taskbar, Start, search, desktop, tray overflow...). Their keyboard
    // layout often differs from the document you are typing in, so they are ignored entirely.
    static readonly string[] ignoredWindowClasses = {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "Progman", "WorkerW", "TaskListThumbnailWnd",
        "MultitaskingViewFrame", "ForegroundStaging", "#32768" /* menus */
    };
    static bool speakThai = true;
    static bool speakEnglish = true;
    static int lastPrimary = -1;
    static ToolStripMenuItem speakThaiItem, speakEnglishItem;
    static HotkeyWindow hotkeyWindow;
    static bool hotkeyRegistered = false;
    static string voice = "female"; // female | male | custom
    static ToolStripMenuItem femaleItem, maleItem, customItem;

    [STAThread]
    static void Main()
    {
        bool createdNew;
        using (Mutex mutex = new Mutex(true, "LangVoice_SingleInstance_7F3A", out createdNew))
        {
            if (!createdNew) return; // already running

            appDir = AppDomain.CurrentDomain.BaseDirectory;
            settingsPath = Path.Combine(appDir, "settings.ini");
            LoadSettings();

            ReloadSounds();

            Application.EnableVisualStyles();
            BuildTray();

            hotkeyWindow = new HotkeyWindow();
            hotkeyRegistered = RegisterHotKey(hotkeyWindow.Handle, HOTKEY_ID,
                MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.M);
            if (!hotkeyRegistered) muteItem.ShortcutKeyDisplayString = HOTKEY_TEXT + " (ถูกโปรแกรมอื่นใช้อยู่)";

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 150;
            timer.Tick += delegate { CheckLanguage(); };
            timer.Start();

            Application.Run();

            timer.Stop();
            if (hotkeyRegistered) UnregisterHotKey(hotkeyWindow.Handle, HOTKEY_ID);
            hotkeyWindow.DestroyHandle();
            tray.Visible = false;
            tray.Dispose();
            if (currentIconHandle != IntPtr.Zero) DestroyIcon(currentIconHandle);
        }
    }

    static SoundPlayer LoadSound(string fileName)
    {
        string path = Path.Combine(appDir, fileName);
        if (!File.Exists(path)) return null;
        try
        {
            SoundPlayer p = new SoundPlayer(path);
            p.Load(); // preload into memory so it plays instantly
            return p;
        }
        catch { return null; }
    }

    static void ReloadSounds()
    {
        if (voice == "custom")
        {
            thaiSound = LoadSound("custom-thai.wav") ?? LoadSound("thai.wav") ?? LoadSound("thai-female.wav");
            englishSound = LoadSound("custom-english.wav") ?? LoadSound("english-female.wav");
        }
        else
        {
            thaiSound = LoadSound("thai-" + voice + ".wav") ?? LoadSound("thai.wav");
            englishSound = LoadSound("english-" + voice + ".wav") ?? LoadSound("english.wav");
        }
    }

    static bool HasCustomSound()
    {
        return File.Exists(Path.Combine(appDir, "custom-thai.wav")) ||
               File.Exists(Path.Combine(appDir, "custom-english.wav"));
    }

    static void SetVoice(string v)
    {
        voice = v;
        femaleItem.Checked = v == "female";
        maleItem.Checked = v == "male";
        customItem.Checked = v == "custom";
        ReloadSounds();
        SaveSettings();
        Play(lastLang != 0 && (lastLang & 0x3FF) == LANG_ENGLISH ? englishSound : thaiSound);
    }

    static void BuildTray()
    {
        tray = new NotifyIcon();
        tray.Text = "LangVoice";
        SetTrayIcon("..", Color.Gray);

        ContextMenuStrip menu = new ContextMenuStrip();

        muteItem = new ToolStripMenuItem("เปิดเสียงพูด  (หรือคลิกที่ไอคอน)");
        muteItem.Checked = !muted;
        muteItem.ShortcutKeyDisplayString = HOTKEY_TEXT;
        muteItem.Click += delegate { ToggleMuted(false); };
        menu.Items.Add(muteItem);

        ToolStripMenuItem langMenu = new ToolStripMenuItem("พูดเมื่อสลับเป็นภาษา");
        speakThaiItem = new ToolStripMenuItem("ไทย");
        speakEnglishItem = new ToolStripMenuItem("English");
        speakThaiItem.CheckOnClick = true;
        speakEnglishItem.CheckOnClick = true;
        speakThaiItem.Checked = speakThai;
        speakEnglishItem.Checked = speakEnglish;
        speakThaiItem.Click += delegate { speakThai = speakThaiItem.Checked; SaveSettings(); };
        speakEnglishItem.Click += delegate { speakEnglish = speakEnglishItem.Checked; SaveSettings(); };
        langMenu.DropDownItems.Add(speakThaiItem);
        langMenu.DropDownItems.Add(speakEnglishItem);
        menu.Items.Add(langMenu);

        windowSwitchItem = new ToolStripMenuItem("แจ้งเตือนเมื่อสลับหน้าต่างด้วย");
        windowSwitchItem.CheckOnClick = true;
        windowSwitchItem.Checked = announceOnWindowSwitch;
        windowSwitchItem.Click += delegate { announceOnWindowSwitch = windowSwitchItem.Checked; SaveSettings(); };
        menu.Items.Add(windowSwitchItem);

        ToolStripMenuItem voiceMenu = new ToolStripMenuItem("เลือกเสียงพูด");
        femaleItem = new ToolStripMenuItem("เสียงผู้หญิง (เปรมวดี / Jenny)");
        maleItem = new ToolStripMenuItem("เสียงผู้ชาย (นิวัฒน์ / Andrew)");
        customItem = new ToolStripMenuItem("เสียงที่อัด/บันทึกเอง");
        femaleItem.Click += delegate { SetVoice("female"); };
        maleItem.Click += delegate { SetVoice("male"); };
        customItem.Click += delegate { SetVoice("custom"); };
        voiceMenu.DropDownItems.Add(femaleItem);
        voiceMenu.DropDownItems.Add(maleItem);
        voiceMenu.DropDownItems.Add(customItem);
        voiceMenu.DropDownOpening += delegate { customItem.Enabled = HasCustomSound(); };
        femaleItem.Checked = voice == "female";
        maleItem.Checked = voice == "male";
        customItem.Checked = voice == "custom";
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(voiceMenu);
        menu.Items.Add("ทดสอบเสียง: ไทย", null, delegate { Play(thaiSound); });
        menu.Items.Add("ทดสอบเสียง: English", null, delegate { Play(englishSound); });
        menu.Items.Add("เปลี่ยนเสียง / อัดเสียงเอง...", null, delegate { OpenSoundMaker(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ออกจากโปรแกรม", null, delegate { Application.Exit(); });

        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ToggleMuted(false);
        };
        tray.Visible = true;
    }

    // Turn the announcements on/off. When turning on, speak the current language as confirmation.
    static void ToggleMuted(bool fromHotkey)
    {
        muted = !muted;
        muteItem.Checked = !muted;
        SaveSettings();
        RedrawTrayIcon();
        if (!muted)
        {
            if (lastPrimary == LANG_ENGLISH) Play(englishSound);
            else Play(thaiSound);
        }
        if (fromHotkey)
        {
            tray.ShowBalloonTip(1500, "LangVoice",
                muted ? "ปิดเสียงพูดแล้ว (กด " + HOTKEY_TEXT + " เพื่อเปิด)" : "เปิดเสียงพูดแล้ว",
                ToolTipIcon.None);
        }
    }

    static void OpenSoundMaker()
    {
        string script = Path.Combine(appDir, "make-sounds.ps1");
        if (!File.Exists(script)) { MessageBox.Show("ไม่พบไฟล์ make-sounds.ps1 กรุณาติดตั้งใหม่", "LangVoice"); return; }
        try
        {
            System.Diagnostics.Process.Start("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "LangVoice"); }
    }

    static void CheckLanguage()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        uint pid;
        uint threadId = GetWindowThreadProcessId(hwnd, out pid);
        if (pid == ownProcessId || IsShellWindow(hwnd)) return; // keep the previous state untouched
        int langId = (int)((long)GetKeyboardLayout(threadId) & 0xFFFF);
        if (langId == 0) return;

        bool windowChanged = hwnd != lastWindow;
        lastWindow = hwnd;

        if (langId == lastLang) return;

        bool firstRun = lastLang == 0;
        lastLang = langId;

        int primary = langId & 0x3FF;
        UpdateTrayIcon(primary, langId);

        if (firstRun || muted) return;
        if (windowChanged && !announceOnWindowSwitch) return;

        if (primary == LANG_THAI && speakThai) Play(thaiSound);
        else if (primary == LANG_ENGLISH && speakEnglish) Play(englishSound);
    }

    static bool IsShellWindow(IntPtr hwnd)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) <= 0) return false;
        string cls = sb.ToString();
        foreach (string c in ignoredWindowClasses) if (cls == c) return true;
        return false;
    }

    static void Play(SoundPlayer p)
    {
        if (p == null)
        {
            SystemSounds.Asterisk.Play();
            return;
        }
        try { p.Play(); } catch { }
    }

    static int lastLangIdForIcon = 0;

    static void RedrawTrayIcon()
    {
        if (lastPrimary >= 0) UpdateTrayIcon(lastPrimary, lastLangIdForIcon);
        else SetTrayIcon("..", Color.Gray);
    }

    static void UpdateTrayIcon(int primary, int langId)
    {
        lastPrimary = primary;
        lastLangIdForIcon = langId;
        string suffix = muted ? " (ปิดเสียง)" : "";
        if (primary == LANG_THAI)
        {
            SetTrayIcon("TH", Color.FromArgb(200, 30, 45));
            tray.Text = "LangVoice - ภาษาไทย" + suffix;
        }
        else if (primary == LANG_ENGLISH)
        {
            SetTrayIcon("EN", Color.FromArgb(25, 90, 200));
            tray.Text = "LangVoice - English" + suffix;
        }
        else
        {
            SetTrayIcon("??", Color.Gray);
            tray.Text = "LangVoice - 0x" + langId.ToString("X4") + suffix;
        }
    }

    static void SetTrayIcon(string text, Color background)
    {
        if (muted) // faded colour so the muted state is visible at a glance
            background = Color.FromArgb(
                (background.R + 2 * 110) / 3, (background.G + 2 * 110) / 3, (background.B + 2 * 110) / 3);
        using (Bitmap bmp = new Bitmap(32, 32))
        using (Graphics g = Graphics.FromImage(bmp))
        using (Brush bg = new SolidBrush(background))
        using (Font font = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (StringFormat fmt = new StringFormat())
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            g.FillRectangle(bg, 0, 2, 32, 28);
            fmt.Alignment = StringAlignment.Center;
            fmt.LineAlignment = StringAlignment.Center;
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
            if (muted)
            {
                using (Pen outline = new Pen(Color.FromArgb(40, 40, 40), 6f))
                using (Pen slash = new Pen(Color.FromArgb(255, 80, 80), 3f))
                {
                    g.DrawLine(outline, 3, 29, 29, 3);
                    g.DrawLine(slash, 3, 29, 29, 3);
                }
            }

            IntPtr handle = bmp.GetHicon();
            Icon newIcon = Icon.FromHandle(handle);
            tray.Icon = newIcon;

            if (currentIconHandle != IntPtr.Zero) DestroyIcon(currentIconHandle);
            if (currentIcon != null) currentIcon.Dispose();
            currentIcon = newIcon;
            currentIconHandle = handle;
        }
    }

    static void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            foreach (string line in File.ReadAllLines(settingsPath))
            {
                string[] kv = line.Split('=');
                if (kv.Length != 2) continue;
                string key = kv[0].Trim();
                if (key == "voice") { string v = kv[1].Trim(); if (v == "female" || v == "male" || v == "custom") voice = v; continue; }
                bool val = kv[1].Trim() == "1";
                if (key == "muted") muted = val;
                else if (key == "speakOnWindowSwitch") announceOnWindowSwitch = val;
                else if (key == "speakThai") speakThai = val;
                else if (key == "speakEnglish") speakEnglish = val;
            }
        }
        catch { }
    }

    static void SaveSettings()
    {
        try
        {
            File.WriteAllLines(settingsPath, new string[] {
                "muted=" + (muted ? "1" : "0"),
                "speakOnWindowSwitch=" + (announceOnWindowSwitch ? "1" : "0"),
                "speakThai=" + (speakThai ? "1" : "0"),
                "speakEnglish=" + (speakEnglish ? "1" : "0"),
                "voice=" + voice
            });
        }
        catch { }
    }
}
