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
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

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
    static bool announceOnWindowSwitch = true;
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

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 150;
            timer.Tick += delegate { CheckLanguage(); };
            timer.Start();

            Application.Run();

            timer.Stop();
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

        muteItem = new ToolStripMenuItem("ปิดเสียงชั่วคราว");
        muteItem.CheckOnClick = true;
        muteItem.Checked = muted;
        muteItem.Click += delegate { muted = muteItem.Checked; SaveSettings(); };
        menu.Items.Add(muteItem);

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
        tray.Visible = true;
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

        uint threadId = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
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

        if (primary == LANG_THAI) Play(thaiSound);
        else if (primary == LANG_ENGLISH) Play(englishSound);
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

    static void UpdateTrayIcon(int primary, int langId)
    {
        if (primary == LANG_THAI)
        {
            SetTrayIcon("TH", Color.FromArgb(200, 30, 45));
            tray.Text = "LangVoice - ภาษาไทย";
        }
        else if (primary == LANG_ENGLISH)
        {
            SetTrayIcon("EN", Color.FromArgb(25, 90, 200));
            tray.Text = "LangVoice - English";
        }
        else
        {
            SetTrayIcon("??", Color.Gray);
            tray.Text = "LangVoice - 0x" + langId.ToString("X4");
        }
    }

    static void SetTrayIcon(string text, Color background)
    {
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
                else if (key == "announceOnWindowSwitch") announceOnWindowSwitch = val;
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
                "announceOnWindowSwitch=" + (announceOnWindowSwitch ? "1" : "0"),
                "voice=" + voice
            });
        }
        catch { }
    }
}
