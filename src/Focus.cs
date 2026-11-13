// Focus: a small window for the FocusBlock site blocker.
// One exe does everything:
//   FocusSetup.exe              (run from anywhere)  -> setup window with an Install button
//   Focus.exe                   (installed copy)     -> the app
//   Focus.exe --install         (elevated)           -> install / update
//   Focus.exe --uninstall                            -> remove (refused during a session)
// Built by src\build.ps1 with the C# compiler that ships with Windows (C# 5, WPF).
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;

[assembly: System.Reflection.AssemblyTitle("Focus")]
[assembly: System.Reflection.AssemblyProduct("Focus")]

public class FocusApp
{
    static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FocusBlock");
    static readonly string ReqPath = System.IO.Path.Combine(Dir, "request.txt");
    static readonly string SitesPath = System.IO.Path.Combine(Dir, "sites.txt");
    static readonly string HostsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
    const string Phrase = "I am choosing to stop this session early";
    static readonly int[] Presets = { 25, 45, 75, 90, 120 };
    const double R = 92, C = 100; // ring radius and centre

    Window win;
    TextBlock big, unit, sub, sitesInfo, phraseText;
    Path arc;
    Ellipse track;
    FrameworkElement idlePanel, activePanel, confirmPanel;
    TextBox phraseBox;
    Button startBtn, extendBtn, stopBtn, keepBtn, confirmStopBtn, editBtn, closeBtn, minBtn;
    FrameworkElement mainView, listView, footer, historyView;
    static readonly string HistoryPath = System.IO.Path.Combine(Dir, "history.csv");
    StackPanel siteRows;
    TextBox addBox;
    TextBlock addHint, listHint;
    int selected = 75;
    bool confirming;

    const string Version = "1.0.0";
    static readonly string InstalledExe = System.IO.Path.Combine(Dir, "Focus.exe");
    static string SelfPath { get { return Process.GetCurrentProcess().MainModule.FileName; } }
    static bool SetupMode { get { return !string.Equals(System.IO.Path.GetFullPath(SelfPath), System.IO.Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); } }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--install")) return Install(args.Contains("--reset-sites"), args.Contains("--no-desktop"));
        if (args.Contains("--uninstall")) return Uninstall();

        Mutex mutex = null;
        if (!SetupMode)
        {
            bool created;
            mutex = new Mutex(true, "Local\\FocusBlockGui", out created);
            if (!created) return 0;
        }
        var app = new Application();
        var f = new FocusApp();
        f.Build();
        app.Run(f.win);
        GC.KeepAlive(mutex);
        return 0;
    }

    // ---------- install / uninstall ----------
    static bool IsAdmin()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static void Extract(string name, string dest)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var f = File.Create(dest)) s.CopyTo(f);
    }

    static int RunHidden(string exe, string arguments)
    {
        var p = Process.Start(new ProcessStartInfo(exe, arguments) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        p.WaitForExit();
        return p.ExitCode;
    }

    static int Install(bool resetSites, bool noDesktop)
    {
        if (!IsAdmin())
        {
            try
            {
                var a = "--install" + (resetSites ? " --reset-sites" : "") + (noDesktop ? " --no-desktop" : "");
                var p = Process.Start(new ProcessStartInfo(SelfPath, a) { Verb = "runas", UseShellExecute = true });
                p.WaitForExit();
                return p.ExitCode;
            }
            catch { return 2; } // UAC declined
        }
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var p in Process.GetProcessesByName("Focus"))
            {
                try { if (p.Id != Process.GetCurrentProcess().Id && string.Equals(p.MainModule.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); } }
                catch { }
            }
            if (SetupMode) File.Copy(SelfPath, InstalledExe, true);
            foreach (var n in new[] { "engine.ps1", "focus.ps1", "register.ps1", "uninstall.ps1" }) Extract(n, System.IO.Path.Combine(Dir, n));
            if (resetSites || !File.Exists(SitesPath)) Extract("sites.txt", SitesPath);
            if (!File.Exists(ReqPath)) File.WriteAllText(ReqPath, "");
            foreach (var old in new[] { "apply.ps1", "focus.ico" }) { try { File.Delete(System.IO.Path.Combine(Dir, old)); } catch { } }

            return RunHidden("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + System.IO.Path.Combine(Dir, "register.ps1") +
                "\" -Dir \"" + Dir + "\" -Version " + Version + (noDesktop ? " -NoDesktop" : ""));
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FocusSetup.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine); } catch { }
            return 1;
        }
    }

    static int Uninstall()
    {
        DateTime end, start;
        if (ReadSession(out end, out start))
        {
            MessageBox.Show("A focus session is running until " + end.ToString("HH:mm") + ".\n\nYou can uninstall Focus after it ends.", "Focus", MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }
        var src = System.IO.Path.Combine(Dir, "uninstall.ps1");
        if (!File.Exists(src)) return 1;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FocusUninstall.ps1");
        File.Copy(src, tmp, true);
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + tmp + "\" -Quiet -Notify")
                { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            return 0;
        }
        catch { return 2; }
    }

    // setup window: shown when FocusSetup.exe is run from anywhere but the install folder
    void BuildSetup()
    {
        mainView.Visibility = Visibility.Collapsed;
        footer.Visibility = Visibility.Collapsed;
        var setupView = (FrameworkElement)win.FindName("SetupView");
        setupView.Visibility = Visibility.Visible;
        var icon = (System.Windows.Controls.Image)win.FindName("SetupIcon");
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.png");
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            icon.Source = bi;
        }
        catch { }
        var note = (TextBlock)win.FindName("SetupNote");
        var btn = (Button)win.FindName("InstallBtn");
        bool installed = File.Exists(InstalledExe);
        if (installed) { btn.Content = "Update"; note.Text = "Focus is already installed. Updating keeps your block list."; }

        btn.Click += (s, e) =>
        {
            btn.IsEnabled = false;
            btn.Content = installed ? "Updating…" : "Installing…";
            var self = SelfPath;
            new Thread(() =>
            {
                int code;
                try
                {
                    var p = Process.Start(new ProcessStartInfo(self, "--install") { Verb = "runas", UseShellExecute = true });
                    p.WaitForExit();
                    code = p.ExitCode;
                }
                catch { code = 2; }
                win.Dispatcher.Invoke(new Action(() =>
                {
                    if (code == 0)
                    {
                        try { Process.Start(InstalledExe); } catch { }
                        win.Close();
                        return;
                    }
                    btn.IsEnabled = true;
                    btn.Content = installed ? "Update" : "Install";
                    note.Foreground = Brushes.IndianRed;
                    note.Text = code == 2
                        ? "Windows needs admin permission to install Focus. Click again and choose Yes."
                        : "Something went wrong. Details are in %TEMP%\\FocusSetup.log.";
                }));
            }) { IsBackground = true }.Start();
        };
    }

    // ---------- theme ----------
    static bool IsDark()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                return v is int && (int)v == 0;
            }
        }
        catch { return false; }
    }

    static string Themed(string xaml)
    {
        bool d = IsDark();
        string[,] t = d
            ? new string[,] {
                {"@BG@","#1C1D1F"},{"@CARD@","#1C1D1F"},{"@LINE@","#2E3034"},{"@FG@","#EDEBE7"},{"@MUTED@","#8F8C86"},
                {"@ACCENT@","#7CC49A"},{"@ACCENTFG@","#0F1B14"},{"@SOFT@","#22352A"},{"@HOVER@","#26282C"},{"@TRACK@","#2A2C30"},{"@SHADOW@","0.55"} }
            : new string[,] {
                {"@BG@","#FBFAF7"},{"@CARD@","#FBFAF7"},{"@LINE@","#E7E4DD"},{"@FG@","#1C1B19"},{"@MUTED@","#8C887F"},
                {"@ACCENT@","#2F6F4F"},{"@ACCENTFG@","#FFFFFF"},{"@SOFT@","#E5EFE8"},{"@HOVER@","#F1EFEA"},{"@TRACK@","#ECE9E2"},{"@SHADOW@","0.16"} };
        for (int i = 0; i < t.GetLength(0); i++) xaml = xaml.Replace(t[i, 0], t[i, 1]);
        return xaml;
    }

    // ---------- UI ----------
    const string Xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='Focus' Width='344' SizeToContent='Height' WindowStyle='None' AllowsTransparency='True'
        Background='Transparent' ResizeMode='NoResize' WindowStartupLocation='CenterScreen'
        FontFamily='Segoe UI Variable Text, Segoe UI' UseLayoutRounding='True' SnapsToDevicePixels='True'>
  <Window.Resources>
    <Style x:Key='Chip' TargetType='RadioButton'>
      <Setter Property='Foreground' Value='@FG@'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Border x:Name='b' Height='34' Margin='3,0' CornerRadius='10' Background='Transparent' BorderBrush='@LINE@' BorderThickness='1'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='@HOVER@'/></Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='b' Property='Background' Value='@SOFT@'/>
              <Setter TargetName='b' Property='BorderBrush' Value='@ACCENT@'/>
              <Setter Property='Foreground' Value='@ACCENT@'/>
              <Setter Property='FontWeight' Value='SemiBold'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='Primary' TargetType='Button'>
      <Setter Property='Foreground' Value='@ACCENTFG@'/>
      <Setter Property='FontSize' Value='14'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Height='44' CornerRadius='12' Background='@ACCENT@'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.9'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.78'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <SolidColorBrush x:Key='HoverBrush' Color='@HOVER@'/>
    <SolidColorBrush x:Key='FgBrush' Color='@FG@'/>
    <SolidColorBrush x:Key='MutedBrush' Color='@MUTED@'/>
    <SolidColorBrush x:Key='AccentBrush' Color='@ACCENT@'/>
    <SolidColorBrush x:Key='SoftBrush' Color='@SOFT@'/>
    <SolidColorBrush x:Key='TrackBrush' Color='@TRACK@'/>
    <SolidColorBrush x:Key='LineBrush' Color='@LINE@'/>
    <Style x:Key='Tile' TargetType='Border'>
      <Setter Property='BorderBrush' Value='@LINE@'/>
      <Setter Property='BorderThickness' Value='1'/>
      <Setter Property='CornerRadius' Value='12'/>
      <Setter Property='Padding' Value='12,10'/>
      <Setter Property='Margin' Value='4'/>
    </Style>
    <Style x:Key='TileValue' TargetType='TextBlock'>
      <Setter Property='FontFamily' Value='Segoe UI Variable Display, Segoe UI'/>
      <Setter Property='FontSize' Value='20'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Foreground' Value='@FG@'/>
    </Style>
    <Style x:Key='TileLabel' TargetType='TextBlock'>
      <Setter Property='FontSize' Value='11'/>
      <Setter Property='Foreground' Value='@MUTED@'/>
    </Style>
    <Style TargetType='ScrollBar'>
      <Setter Property='Width' Value='6'/>
      <Setter Property='MinWidth' Value='6'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>
                <Border CornerRadius='3' Background='@LINE@' Margin='0,4'/>
              </ControlTemplate></Thumb.Template></Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='SmallPrimary' TargetType='Button'>
      <Setter Property='Foreground' Value='@ACCENTFG@'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Width='64' CornerRadius='10' Background='@ACCENT@'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.9'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.78'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='Ghost' TargetType='Button'>
      <Setter Property='Foreground' Value='@FG@'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Height='44' CornerRadius='12' Background='Transparent' BorderBrush='@LINE@' BorderThickness='1'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='@HOVER@'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='Icon' TargetType='Button'>
      <Setter Property='Foreground' Value='@MUTED@'/>
      <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
      <Setter Property='FontSize' Value='10'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Width='28' Height='28' CornerRadius='8' Background='Transparent'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='@HOVER@'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='Link' TargetType='Button'>
      <Setter Property='Foreground' Value='@ACCENT@'/>
      <Setter Property='FontSize' Value='12'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <ContentPresenter x:Name='c'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='c' Property='Opacity' Value='0.75'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='Field' TargetType='TextBox'>
      <Setter Property='Foreground' Value='@FG@'/>
      <Setter Property='CaretBrush' Value='@FG@'/>
      <Setter Property='FontSize' Value='12'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='b' CornerRadius='10' Background='Transparent' BorderBrush='@LINE@' BorderThickness='1' Padding='10,9'>
            <ScrollViewer x:Name='PART_ContentHost'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='@ACCENT@'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
  </Window.Resources>

  <Grid Margin='14'>
    <Border CornerRadius='18' Background='@CARD@' BorderBrush='@LINE@' BorderThickness='1'>
      <Border.Effect><DropShadowEffect BlurRadius='22' ShadowDepth='3' Direction='270' Opacity='@SHADOW@'/></Border.Effect>
    </Border>
    <Grid Margin='22,14,22,20'>
      <Grid.RowDefinitions>
        <RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/>
        <RowDefinition Height='Auto'/><RowDefinition Height='Auto'/>
      </Grid.RowDefinitions>

      <DockPanel Grid.Row='0' x:Name='Header' Background='Transparent'>
        <StackPanel DockPanel.Dock='Right' Orientation='Horizontal' Margin='0,0,-8,0'>
          <Button x:Name='MinBtn' Style='{StaticResource Icon}' Content='&#xE921;'/>
          <Button x:Name='CloseBtn' Style='{StaticResource Icon}' Content='&#xE8BB;'/>
        </StackPanel>
        <TextBlock Text='F O C U S' Foreground='@MUTED@' FontSize='11' FontWeight='SemiBold' VerticalAlignment='Center'/>
      </DockPanel>

      <Grid Grid.Row='1'>
      <StackPanel x:Name='ListView' Visibility='Collapsed' Margin='0,12,0,0'>
        <TextBlock Text='Block list' FontFamily='Segoe UI Variable Display, Segoe UI' FontSize='22' FontWeight='SemiBold' Foreground='@FG@'/>
        <TextBlock x:Name='ListHint' FontSize='12' Foreground='@MUTED@' TextWrapping='Wrap' Margin='0,3,0,14'/>
        <Grid Margin='0,0,0,10'>
          <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <TextBox x:Name='AddBox' Style='{StaticResource Field}'/>
          <TextBlock x:Name='AddHint' Text='Add a site, e.g. youtube.com' FontSize='12' Foreground='@MUTED@'
                     Margin='11,0,0,0' VerticalAlignment='Center' IsHitTestVisible='False'/>
          <Button Grid.Column='1' x:Name='AddBtn' Style='{StaticResource SmallPrimary}' Content='Add' Margin='8,0,0,0'/>
        </Grid>
        <Border BorderBrush='@LINE@' BorderThickness='1' CornerRadius='12' Padding='4'>
          <ScrollViewer MaxHeight='252' VerticalScrollBarVisibility='Auto'>
            <StackPanel x:Name='SiteRows'/>
          </ScrollViewer>
        </Border>
        <Button x:Name='DoneBtn' Style='{StaticResource Primary}' Content='Done' Margin='0,14,0,0'/>
      </StackPanel>
      <StackPanel x:Name='HistoryView' Visibility='Collapsed' Margin='0,12,0,0'>
        <TextBlock Text='History' FontFamily='Segoe UI Variable Display, Segoe UI' FontSize='22' FontWeight='SemiBold' Foreground='@FG@' Margin='0,0,0,10'/>
        <UniformGrid Columns='2' Margin='-4,0,-4,0'>
          <Border Style='{StaticResource Tile}'><StackPanel>
            <TextBlock Text='Today' Style='{StaticResource TileLabel}'/>
            <TextBlock x:Name='HToday' Style='{StaticResource TileValue}'/>
            <TextBlock x:Name='HTodaySub' Style='{StaticResource TileLabel}'/>
          </StackPanel></Border>
          <Border Style='{StaticResource Tile}'><StackPanel>
            <TextBlock Text='This week' Style='{StaticResource TileLabel}'/>
            <TextBlock x:Name='HWeek' Style='{StaticResource TileValue}'/>
            <TextBlock x:Name='HWeekSub' Style='{StaticResource TileLabel}'/>
          </StackPanel></Border>
          <Border Style='{StaticResource Tile}'><StackPanel>
            <TextBlock Text='Streak' Style='{StaticResource TileLabel}'/>
            <TextBlock x:Name='HStreak' Style='{StaticResource TileValue}'/>
            <TextBlock x:Name='HStreakSub' Style='{StaticResource TileLabel}'/>
          </StackPanel></Border>
          <Border Style='{StaticResource Tile}'><StackPanel>
            <TextBlock Text='Stopped early' Style='{StaticResource TileLabel}'/>
            <TextBlock x:Name='HStopped' Style='{StaticResource TileValue}'/>
            <TextBlock x:Name='HStoppedSub' Style='{StaticResource TileLabel}'/>
          </StackPanel></Border>
        </UniformGrid>
        <TextBlock Text='Last 7 days' FontSize='12' Foreground='@MUTED@' Margin='0,14,0,8'/>
        <Grid x:Name='HChart' Height='96'/>
        <TextBlock Text='Recent' FontSize='12' Foreground='@MUTED@' Margin='0,14,0,8'/>
        <Border BorderBrush='@LINE@' BorderThickness='1' CornerRadius='12' Padding='4'>
          <ScrollViewer MaxHeight='150' VerticalScrollBarVisibility='Auto'>
            <StackPanel x:Name='HRecent'/>
          </ScrollViewer>
        </Border>
        <Button x:Name='HistoryDoneBtn' Style='{StaticResource Primary}' Content='Done' Margin='0,14,0,0'/>
      </StackPanel>
      <StackPanel x:Name='SetupView' Visibility='Collapsed' Margin='0,18,0,0'>
        <Image x:Name='SetupIcon' Width='64' Height='64' HorizontalAlignment='Left'/>
        <TextBlock Text='Focus' FontFamily='Segoe UI Variable Display, Segoe UI' FontSize='28' FontWeight='SemiBold' Foreground='@FG@' Margin='0,18,0,6'/>
        <TextBlock Text='Blocks distracting websites for a set time, in every browser. Stopping early takes a deliberate step.'
                   FontSize='13' Foreground='@FG@' TextWrapping='Wrap' LineHeight='20'/>
        <TextBlock x:Name='SetupNote' Text='Installs for everyone on this PC. Windows will ask for admin permission once.'
                   FontSize='12' Foreground='@MUTED@' TextWrapping='Wrap' Margin='0,14,0,20'/>
        <Button x:Name='InstallBtn' Style='{StaticResource Primary}' Content='Install'/>
      </StackPanel>
      <StackPanel x:Name='MainView'>
      <Grid Width='200' Height='200' Margin='0,10,0,0'>
        <Ellipse x:Name='Track' Stroke='@TRACK@' StrokeThickness='8' Margin='4'/>
        <Path x:Name='Arc' Stroke='@ACCENT@' StrokeThickness='8' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
        <StackPanel VerticalAlignment='Center' HorizontalAlignment='Center'>
          <TextBlock x:Name='Big' Text='75' FontFamily='Segoe UI Variable Display, Segoe UI' FontSize='56' FontWeight='SemiBold'
                     Foreground='@FG@' HorizontalAlignment='Center' Margin='0,-6,0,-6'/>
          <TextBlock x:Name='Unit' Text='minutes' FontSize='13' Foreground='@MUTED@' HorizontalAlignment='Center'/>
        </StackPanel>
      </Grid>

      <TextBlock x:Name='Sub' FontSize='13' Foreground='@MUTED@' HorizontalAlignment='Center' Margin='0,14,0,16' TextAlignment='Center'/>

      <Grid>
        <StackPanel x:Name='IdlePanel'>
          <UniformGrid x:Name='Chips' Columns='5' Margin='-3,0,-3,12'/>
          <Button x:Name='StartBtn' Style='{StaticResource Primary}' Content='Start focus'/>
        </StackPanel>
        <UniformGrid x:Name='ActivePanel' Columns='2' Visibility='Collapsed'>
          <Button x:Name='ExtendBtn' Style='{StaticResource Ghost}' Content='+15 min' Margin='0,0,5,0'/>
          <Button x:Name='StopBtn' Style='{StaticResource Ghost}' Content='Stop early' Margin='5,0,0,0'/>
        </UniformGrid>
        <StackPanel x:Name='ConfirmPanel' Visibility='Collapsed'>
          <TextBlock Text='To stop early, type:' FontSize='12' Foreground='@MUTED@'/>
          <TextBlock x:Name='PhraseText' FontSize='12' Foreground='@FG@' FontStyle='Italic' TextWrapping='Wrap' Margin='0,2,0,8'/>
          <TextBox x:Name='PhraseBox' Style='{StaticResource Field}' Margin='0,0,0,10'/>
          <UniformGrid Columns='2'>
            <Button x:Name='KeepBtn' Style='{StaticResource Primary}' Content='Keep going' Margin='0,0,5,0'/>
            <Button x:Name='ConfirmStopBtn' Style='{StaticResource Ghost}' Content='Stop' Margin='5,0,0,0'/>
          </UniformGrid>
        </StackPanel>
      </Grid>
      </StackPanel>
      </Grid>

      <DockPanel Grid.Row='4' x:Name='Footer' Margin='0,16,0,0'>
        <Button DockPanel.Dock='Right' x:Name='EditBtn' Style='{StaticResource Link}' Content='Edit list'/>
        <Button DockPanel.Dock='Right' x:Name='HistoryBtn' Style='{StaticResource Link}' Content='History' Margin='0,0,16,0'/>
        <TextBlock x:Name='SitesInfo' FontSize='12' Foreground='@MUTED@'/>
      </DockPanel>
    </Grid>
  </Grid>
</Window>";

    void Build()
    {
        win = (Window)XamlReader.Parse(Themed(Xaml));
        Func<string, object> F = n => win.FindName(n);
        big = (TextBlock)F("Big"); unit = (TextBlock)F("Unit"); sub = (TextBlock)F("Sub");
        sitesInfo = (TextBlock)F("SitesInfo"); phraseText = (TextBlock)F("PhraseText");
        arc = (Path)F("Arc"); track = (Ellipse)F("Track");
        idlePanel = (FrameworkElement)F("IdlePanel"); activePanel = (FrameworkElement)F("ActivePanel");
        confirmPanel = (FrameworkElement)F("ConfirmPanel"); phraseBox = (TextBox)F("PhraseBox");
        startBtn = (Button)F("StartBtn"); extendBtn = (Button)F("ExtendBtn"); stopBtn = (Button)F("StopBtn");
        keepBtn = (Button)F("KeepBtn"); confirmStopBtn = (Button)F("ConfirmStopBtn"); editBtn = (Button)F("EditBtn");
        closeBtn = (Button)F("CloseBtn"); minBtn = (Button)F("MinBtn");
        phraseText.Text = Phrase;

        var chips = (UniformGrid)F("Chips");
        foreach (int m in Presets)
        {
            int minutes = m;
            var rb = new RadioButton { Content = minutes.ToString(), GroupName = "len", IsChecked = minutes == selected };
            rb.Style = (Style)win.Resources["Chip"];
            rb.Checked += (s, e) => { selected = minutes; Refresh(); };
            chips.Children.Add(rb);
        }

        win.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && !(e.OriginalSource is TextBox)) try { win.DragMove(); } catch { } };
        closeBtn.Click += (s, e) => win.Close();
        minBtn.Click += (s, e) => win.WindowState = WindowState.Minimized;
        startBtn.Click += (s, e) => StartSession(selected);
        extendBtn.Click += (s, e) => Extend(15);
        var phraseFg = phraseText.Foreground;
        stopBtn.Click += (s, e) => { confirming = true; phraseBox.Text = ""; phraseText.Foreground = phraseFg; Refresh(); phraseBox.Focus(); };
        keepBtn.Click += (s, e) => { confirming = false; Refresh(); };
        confirmStopBtn.Click += (s, e) => TryStop();
        phraseBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) TryStop(); if (e.Key == Key.Escape) { confirming = false; Refresh(); } };
        mainView = (FrameworkElement)F("MainView"); listView = (FrameworkElement)F("ListView"); footer = (FrameworkElement)F("Footer");
        siteRows = (StackPanel)F("SiteRows"); addBox = (TextBox)F("AddBox"); addHint = (TextBlock)F("AddHint"); listHint = (TextBlock)F("ListHint");
        var addBtn = (Button)F("AddBtn"); var doneBtn = (Button)F("DoneBtn");
        historyView = (FrameworkElement)F("HistoryView");
        ((Button)F("HistoryBtn")).Click += (s, e) => ShowHistory(true);
        ((Button)F("HistoryDoneBtn")).Click += (s, e) => ShowHistory(false);
        editBtn.Click += (s, e) => ShowList(true);
        doneBtn.Click += (s, e) => ShowList(false);
        addBtn.Click += (s, e) => AddSite();
        addBox.TextChanged += (s, e) => { addHint.Visibility = addBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; SetListHint(null); };
        addBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) AddSite(); };
        win.KeyDown += (s, e) =>
        {
            if (e.Key != Key.Escape || confirming) return;
            if (listView.Visibility == Visibility.Visible) ShowList(false);
            else if (historyView.Visibility == Visibility.Visible) ShowHistory(false);
            else win.Close();
        };

        if (SetupMode) { BuildSetup(); return; }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (s, e) => Refresh();
        timer.Start();
        Refresh();
    }

    // ---------- state ----------
    static bool ReadSession(out DateTime end, out DateTime start)
    {
        end = start = DateTime.MinValue;
        try
        {
            var lines = File.ReadAllLines(ReqPath).Where(l => l.Trim().Length > 0).ToArray();
            if (lines.Length == 0) return false;
            if (!DateTime.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out end)) return false;
            if (lines.Length < 2 || !DateTime.TryParse(lines[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out start))
                start = File.GetLastWriteTime(ReqPath);
            return end > DateTime.Now;
        }
        catch { return false; }
    }

    static bool HostsBlocked()
    {
        try { return File.ReadAllText(HostsPath).Contains("# FocusBlockStart"); } catch { return false; }
    }

    static int SiteCount()
    {
        try
        {
            return File.ReadAllLines(SitesPath)
                .Select(l => { int i = l.IndexOf('#'); return (i >= 0 ? l.Substring(0, i) : l).Trim(); })
                .Count(l => l.Contains("."));
        }
        catch { return 0; }
    }

    static void Write(DateTime end, DateTime start)
    {
        File.WriteAllLines(ReqPath, new[] { end.ToString("s"), start.ToString("s") });
        Kick();
    }

    static void Kick()
    {
        try
        {
            var p = new ProcessStartInfo("schtasks.exe", "/run /tn FocusBlock") { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(p);
        }
        catch { }
    }

    void StartSession(int minutes)
    {
        try { var now = DateTime.Now; Write(now.AddMinutes(minutes), now); }
        catch (Exception ex) { MessageBox.Show(win, "Could not start: " + ex.Message + "\n\nIs Focus installed? Run install.cmd.", "Focus"); }
        Refresh();
    }

    void Extend(int minutes)
    {
        DateTime end, start;
        if (!ReadSession(out end, out start)) return;
        var newEnd = end.AddMinutes(minutes);
        if ((newEnd - DateTime.Now).TotalMinutes > 240) return;
        Write(newEnd, start);
        Refresh();
    }

    void TryStop()
    {
        if (phraseBox.Text.Trim() != Phrase) { phraseText.Foreground = Brushes.IndianRed; phraseBox.SelectAll(); return; }
        try { File.WriteAllText(ReqPath, ""); Kick(); } catch { }
        confirming = false;
        Refresh();
    }

    // ---------- history ----------
    class Session
    {
        public DateTime Start, End;
        public int Planned, Final, Actual;
        public string Outcome; // completed | stopped | running
    }

    static List<Session> LoadHistory()
    {
        var list = new List<Session>();
        try
        {
            foreach (var line in File.ReadAllLines(HistoryPath).Skip(1))
            {
                var p = line.Split(',');
                if (p.Length < 6) continue;
                DateTime st, en; int pl, fi, ac;
                if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out st)) continue;
                DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out en);
                int.TryParse(p[2], out pl); int.TryParse(p[3], out fi); int.TryParse(p[4], out ac);
                list.Add(new Session { Start = st, End = en, Planned = pl, Final = fi, Actual = ac, Outcome = p[5].Trim() });
            }
        }
        catch { }
        DateTime end, start;
        if (ReadSession(out end, out start))
        {
            list.Add(new Session {
                Start = start, End = end, Outcome = "running",
                Final = (int)Math.Round((end - start).TotalMinutes),
                Planned = (int)Math.Round((end - start).TotalMinutes),
                Actual = Math.Max(0, (int)(DateTime.Now - start).TotalMinutes) });
        }
        return list.OrderBy(x => x.Start).ToList();
    }

    static string Dur(int minutes)
    {
        if (minutes < 60) return minutes + "m";
        return (minutes / 60) + "h" + (minutes % 60 == 0 ? "" : " " + (minutes % 60) + "m");
    }

    void ShowHistory(bool show)
    {
        historyView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        mainView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        footer.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) RenderHistory();
        Refresh();
    }

    void SetText(string name, string text) { ((TextBlock)win.FindName(name)).Text = text; }

    void RenderHistory()
    {
        var all = LoadHistory();
        var today = DateTime.Today;
        int dow = ((int)today.DayOfWeek + 6) % 7; // Monday = 0
        var weekStart = today.AddDays(-dow);

        var todays = all.Where(x => x.Start.Date == today).ToList();
        var week = all.Where(x => x.Start.Date >= weekStart).ToList();
        SetText("HToday", Dur(todays.Sum(x => x.Actual)));
        SetText("HTodaySub", todays.Count == 1 ? "1 session" : todays.Count + " sessions");
        SetText("HWeek", Dur(week.Sum(x => x.Actual)));
        SetText("HWeekSub", week.Count == 1 ? "1 session" : week.Count + " sessions");

        var doneDays = new HashSet<DateTime>(all.Where(x => x.Outcome == "completed").Select(x => x.Start.Date));
        var d = doneDays.Contains(today) ? today : today.AddDays(-1);
        int streak = 0;
        while (doneDays.Contains(d)) { streak++; d = d.AddDays(-1); }
        SetText("HStreak", streak == 1 ? "1 day" : streak + " days");
        SetText("HStreakSub", doneDays.Contains(today) ? "done today" : streak > 0 ? "finish one today" : "finish a session to start");

        int stopped = week.Count(x => x.Outcome == "stopped");
        int ended = week.Count(x => x.Outcome != "running");
        SetText("HStopped", stopped.ToString());
        SetText("HStoppedSub", ended == 0 ? "this week" : "of " + ended + " this week");

        // last 7 days chart
        var chart = (Grid)win.FindName("HChart");
        chart.Children.Clear(); chart.ColumnDefinitions.Clear();
        var mins = new int[7];
        for (int i = 0; i < 7; i++) { var day = today.AddDays(i - 6); mins[i] = all.Where(x => x.Start.Date == day).Sum(x => x.Actual); }
        double max = Math.Max(60, mins.Max());
        for (int i = 0; i < 7; i++)
        {
            var day = today.AddDays(i - 6);
            chart.ColumnDefinitions.Add(new ColumnDefinition());
            var col = new Grid();
            col.RowDefinitions.Add(new RowDefinition());
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            bool isToday = i == 6;
            var bar = new Border {
                Width = 24, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Bottom,
                Height = mins[i] == 0 ? 4 : Math.Max(6, 72 * mins[i] / max),
                Background = mins[i] == 0 ? (Brush)win.Resources["TrackBrush"] : (Brush)win.Resources["AccentBrush"],
                Opacity = mins[i] == 0 || isToday ? 1 : 0.5,
                ToolTip = day.ToString("ddd d MMM") + " · " + Dur(mins[i]) };
            col.Children.Add(bar);
            var lbl = new TextBlock {
                Text = isToday ? "Today" : day.ToString("ddd"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Foreground = isToday ? (Brush)win.Resources["FgBrush"] : (Brush)win.Resources["MutedBrush"],
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal };
            Grid.SetRow(lbl, 1);
            col.Children.Add(lbl);
            Grid.SetColumn(col, i);
            chart.Children.Add(col);
        }

        // recent sessions
        var rows = (StackPanel)win.FindName("HRecent");
        rows.Children.Clear();
        var fg = (Brush)win.Resources["FgBrush"]; var muted = (Brush)win.Resources["MutedBrush"]; var accent = (Brush)win.Resources["AccentBrush"];
        var recent = all.AsEnumerable().Reverse().Take(30).ToList();
        if (recent.Count == 0)
        {
            rows.Children.Add(new TextBlock { Text = "No sessions yet. Start one and it shows up here.", FontSize = 12, Foreground = muted, Margin = new Thickness(10, 9, 10, 9), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var x in recent)
        {
            var g = new Grid { Margin = new Thickness(10, 6, 10, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string when = (x.Start.Date == today ? "Today" : x.Start.Date == today.AddDays(-1) ? "Yesterday" : x.Start.ToString("ddd d MMM")) + "  " + x.Start.ToString("HH:mm");
            g.Children.Add(new TextBlock { Text = when, FontSize = 12, Foreground = fg });
            string what; Brush color = fg;
            if (x.Outcome == "running") { what = "now · " + x.Actual + " of " + x.Final + " min"; color = accent; }
            else if (x.Outcome == "stopped") { what = "stopped at " + x.Actual + " of " + x.Final + " min"; color = Brushes.IndianRed; }
            else { what = Dur(x.Actual) + (x.Final > x.Planned ? "  (+" + (x.Final - x.Planned) + ")" : "") + "  ✓"; }
            var right = new TextBlock { Text = what, FontSize = 12, Foreground = color };
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            rows.Children.Add(g);
        }
    }

    // ---------- block list ----------
    static List<string> LoadSites()
    {
        var list = new List<string>();
        try
        {
            foreach (var raw in File.ReadAllLines(SitesPath))
            {
                int i = raw.IndexOf('#');
                var d = (i >= 0 ? raw.Substring(0, i) : raw).Trim().ToLowerInvariant();
                if (d.Contains(".") && !list.Contains(d)) list.Add(d);
            }
        }
        catch { }
        return list;
    }

    static void SaveSites(List<string> sites)
    {
        var lines = new List<string> {
            "# Sites blocked during a focus session. One domain per line.",
            "# \"www.\" is added automatically for bare domains. Edit from the Focus app or here.",
            "" };
        lines.AddRange(sites);
        File.WriteAllLines(SitesPath, lines);
        Kick();
    }

    // "https://www.YouTube.com/watch?v=1" -> "youtube.com"
    static string Normalize(string input)
    {
        var s = (input ?? "").Trim().ToLowerInvariant();
        int k = s.IndexOf("://");
        if (k >= 0) s = s.Substring(k + 3);
        int cut = s.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (cut >= 0) s = s.Substring(0, cut);
        s = s.Trim('.');
        if (s.StartsWith("www.") && s.Split('.').Length == 3) s = s.Substring(4);
        return Regex.IsMatch(s, @"^[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}$") ? s : null;
    }

    void ShowList(bool show)
    {
        listView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        mainView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        footer.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) { addBox.Text = ""; SetListHint(null); RenderSites(); addBox.Focus(); }
        Refresh();
    }

    void SetListHint(string error)
    {
        DateTime end, start;
        if (error != null) { listHint.Text = error; listHint.Foreground = Brushes.IndianRed; return; }
        listHint.Foreground = (Brush)win.Resources["MutedBrush"];
        listHint.Text = ReadSession(out end, out start)
            ? "New sites are blocked right away. Removed ones stay blocked until this session ends."
            : "Blocked in every browser while a session runs.";
    }

    void AddSite()
    {
        var d = Normalize(addBox.Text);
        if (d == null) { SetListHint("That doesn't look like a website. Try something like reddit.com"); return; }
        var sites = LoadSites();
        if (sites.Contains(d)) { SetListHint(d + " is already on the list."); return; }
        sites.Insert(0, d);
        try { SaveSites(sites); } catch (Exception ex) { SetListHint("Couldn't save: " + ex.Message); return; }
        addBox.Text = "";
        RenderSites();
    }

    void RemoveSite(string d)
    {
        var sites = LoadSites();
        sites.Remove(d);
        try { SaveSites(sites); } catch (Exception ex) { SetListHint("Couldn't save: " + ex.Message); return; }
        RenderSites();
    }

    void RenderSites()
    {
        siteRows.Children.Clear();
        var sites = LoadSites();
        if (sites.Count == 0)
        {
            siteRows.Children.Add(new TextBlock { Text = "Nothing on the list yet.", FontSize = 12, Margin = new Thickness(10, 9, 10, 9), Foreground = (Brush)win.Resources["MutedBrush"] });
            return;
        }
        var hover = (Brush)win.Resources["HoverBrush"];
        foreach (var site in sites)
        {
            string d = site;
            var row = new Border { CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = d, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = (Brush)win.Resources["FgBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
            var x = new Button { Content = "", Style = (Style)win.Resources["Icon"], ToolTip = "Remove " + d, Opacity = 0 };
            Grid.SetColumn(x, 1);
            x.Click += (s, e) => RemoveSite(d);
            g.Children.Add(x);
            row.Child = g;
            row.MouseEnter += (s, e) => { row.Background = hover; x.Opacity = 1; };
            row.MouseLeave += (s, e) => { row.Background = Brushes.Transparent; x.Opacity = 0; };
            siteRows.Children.Add(row);
        }
    }

    // ---------- render ----------
    void Refresh()
    {
        DateTime end, start;
        bool active = ReadSession(out end, out start);
        if (!active) confirming = false;

        idlePanel.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        activePanel.Visibility = active && !confirming ? Visibility.Visible : Visibility.Collapsed;
        confirmPanel.Visibility = active && confirming ? Visibility.Visible : Visibility.Collapsed;

        int n = SiteCount();
        if (active)
        {
            var left = end - DateTime.Now;
            double total = Math.Max(1, (end - start).TotalSeconds);
            if (left.TotalMinutes >= 1)
            {
                big.Text = ((int)Math.Ceiling(left.TotalMinutes)).ToString();
                unit.Text = "min left";
            }
            else
            {
                big.Text = Math.Max(0, (int)left.TotalSeconds).ToString();
                unit.Text = "sec left";
            }
            SetArc(left.TotalSeconds / total);
            bool applied = HostsBlocked();
            sub.Text = "Until " + end.ToString("HH:mm") + (applied ? "" : "  ·  applying…");
            sitesInfo.Text = n + " sites blocked";
            extendBtn.IsEnabled = (end.AddMinutes(15) - DateTime.Now).TotalMinutes <= 240;
        }
        else
        {
            big.Text = selected.ToString();
            unit.Text = "minutes";
            SetArc(0);
            sub.Text = "Phone in another room, then start.";
            sitesInfo.Text = n == 0 ? "No block list found" : n + " sites will be blocked";
        }
    }

    void SetArc(double frac)
    {
        frac = Math.Max(0, Math.Min(1, frac));
        if (frac <= 0.0005) { arc.Data = null; return; }
        if (frac >= 0.9995) { arc.Data = new EllipseGeometry(new Point(C, C), R, R); return; }
        double a = frac * 2 * Math.PI;
        var startPt = new Point(C, C - R);
        var endPt = new Point(C + R * Math.Sin(a), C - R * Math.Cos(a));
        var fig = new PathFigure { StartPoint = startPt, IsClosed = false };
        fig.Segments.Add(new ArcSegment(endPt, new Size(R, R), 0, frac > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        arc.Data = g;
    }
}
