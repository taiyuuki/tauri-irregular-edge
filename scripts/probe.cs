// Windows 上的验证工具：定位窗口、截屏、像素测量、合成鼠标事件、启动和结束面板应用。
// 截图、测量、点击这几个子命令和 scripts/probe.swift 一一对应。
//
//   probe launch  <程序> <日志文件> [参数...]   启动面板应用或棋盘格窗口，打印进程号
//   probe list    <pid>
//   probe at      <x> <y>          某个屏幕坐标下叠着哪些窗口（从前往后）
//   probe capture <pid> <输出目录> <标签>
//   probe analyze <输出目录> <标签>
//   probe flicker <pid> <输出目录> <标签>   光标快速进出轮廓时画面有没有闪
//   probe click   <pid> <x> <y> [等待毫秒，默认 300] [local]
//   probe flick   <pid> <起点x> <起点y> <终点x> <终点y> [到达后等几毫秒再按下] [local]
//   probe stop    <pid>            关掉面板应用和棋盘格窗口
//
// x, y 默认是屏幕上的全局坐标（物理像素，原点在主显示器左上角）；
// 鼠标类子命令末尾加上 local，就按面板窗口内的逻辑像素来解释，乘上窗口的缩放比例再换算成物理像素。
//
// 只用系统自带的 .NET Framework 编译（所以只能写 C# 5 的语法），不需要装 SDK。
// 老版 csc 不认路径里的正斜杠，-out 之类带冒号的选项还要写在源文件名前面：
//   csc -nologo -codepage:65001 -r:System.Drawing.dll -out:probe.exe scripts/probe.cs
// （在 Git Bash 里路径要写成反斜杠，见 scripts/verify.sh）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// ---- Win32 ----
static class Native {
  public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }

  [StructLayout(LayoutKind.Sequential)]
  public struct POINT { public int X, Y; }

  // INPUT 是一个联合体：4 字节的类型，之后是对齐过的 MOUSEINPUT（64 位下偏移 8，32 位下偏移 4）
  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, DwFlags, Time; public IntPtr DwExtraInfo; }

  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint Type; public MOUSEINPUT Mi; }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct STARTUPINFO {
    public int cb;
    public string lpReserved, lpDesktop, lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2;
    public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

  [StructLayout(LayoutKind.Sequential)]
  public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

  [DllImport("user32.dll")]
  public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

  [DllImport("user32.dll")]
  public static extern uint GetDpiForWindow(IntPtr hwnd);

  [DllImport("user32.dll")]
  public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

  [DllImport("user32.dll")]
  public static extern bool IsWindowVisible(IntPtr hwnd);

  [DllImport("user32.dll")]
  public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

  [DllImport("user32.dll")]
  public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

  [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
  public static extern int GetWindowLong(IntPtr hwnd, int index);

  [DllImport("user32.dll")]
  public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

  [DllImport("user32.dll")]
  public static extern IntPtr GetForegroundWindow();

  [DllImport("user32.dll")]
  public static extern bool SetForegroundWindow(IntPtr hwnd);

  [DllImport("user32.dll")]
  public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

  [DllImport("user32.dll")]
  public static extern IntPtr WindowFromPoint(POINT point);

  [DllImport("user32.dll")]
  public static extern bool GetCursorPos(out POINT point);

  [DllImport("user32.dll")]
  public static extern bool SetCursorPos(int x, int y);

  [DllImport("user32.dll")]
  public static extern uint SendInput(uint count, INPUT[] inputs, int size);

  [DllImport("user32.dll")]
  public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll")]
  public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

  // 启动信息按指针传（STARTUPINFOEX 的布局是 STARTUPINFO 后面跟一个属性列表指针），C# 的结构体表达不了
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
  public static extern bool CreateProcessExtended(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
    IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
    IntPtr startupInfo, out PROCESS_INFORMATION processInformation);

  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref int size);

  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
    IntPtr previousValue, IntPtr returnSize);

  [DllImport("kernel32.dll")]
  public static extern void DeleteProcThreadAttributeList(IntPtr list);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern IntPtr CreateFile(string name, uint desiredAccess, uint shareMode, ref SECURITY_ATTRIBUTES securityAttributes,
    uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool CloseHandle(IntPtr handle);

  [DllImport("user32.dll")]
  public static extern IntPtr GetDC(IntPtr hwnd);

  [DllImport("user32.dll")]
  public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

  [DllImport("gdi32.dll")]
  public static extern bool BitBlt(IntPtr hdc, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

  [DllImport("kernel32.dll")]
  public static extern uint GetCurrentThreadId();

  [DllImport("dwmapi.dll")]
  public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

class ProbeError : Exception {
  public ProbeError(string message) : base(message) {}
}

sealed class WinInfo {
  public IntPtr Hwnd;
  public uint Pid;
  public string Title = "";
  public string Cls = "";
  public int ExStyle;
  public Native.RECT Rect;

  public WinInfo(IntPtr hwnd) {
    Hwnd = hwnd;
    Native.GetWindowThreadProcessId(hwnd, out Pid);
    Native.GetWindowRect(hwnd, out Rect);
    ExStyle = Native.GetWindowLong(hwnd, -20); // GWL_EXSTYLE
    StringBuilder text = new StringBuilder(256);
    Native.GetWindowText(hwnd, text, text.Capacity);
    Title = text.ToString();
    Native.GetClassName(hwnd, text, text.Capacity);
    Cls = text.ToString();
  }

  public int Width { get { return Rect.Right - Rect.Left; } }
  public int Height { get { return Rect.Bottom - Rect.Top; } }
  public bool Topmost { get { return (ExStyle & 0x00000008) != 0; } } // WS_EX_TOPMOST
  // 正在点击穿透的窗口（带 WS_EX_TRANSPARENT）：Tauri 的 set_ignore_cursor_events(true) 在 Windows 上就是加这个样式
  public bool ClickThrough { get { return (ExStyle & 0x00000020) != 0; } }

  // 在面板范围内它会不会接到鼠标，由 Overlapping 填上
  public bool TakesMouse;

  public bool Contains(int x, int y) {
    return x >= Rect.Left && x < Rect.Right && y >= Rect.Top && y < Rect.Bottom;
  }

  public bool Contains(Native.RECT other) {
    return Rect.Left <= other.Left && Rect.Top <= other.Top && Rect.Right >= other.Right && Rect.Bottom >= other.Bottom;
  }

  public string Describe() {
    string position = string.Format(CultureInfo.InvariantCulture, "({0},{1}) {2}x{3} exstyle=0x{4:X8}", Rect.Left, Rect.Top,
      Width, Height, ExStyle);
    return "pid=" + Pid + " class=" + Cls + "「" + Title + "」" + position;
  }

  public string RectText() {
    return string.Format(CultureInfo.InvariantCulture, "({0},{1}) {2}x{3}", Rect.Left, Rect.Top, Width, Height);
  }
}

sealed class Pair {
  public WinInfo Panel;
  public WinInfo Backdrop;
  public double Scale; // 面板窗口的缩放比例：一个逻辑像素等于几个物理像素

  // 比面板矩形每边缩进 2 个物理像素的范围
  public Native.RECT Inner {
    get {
      Native.RECT r = Panel.Rect;
      r.Left += 2;
      r.Top += 2;
      r.Right -= 2;
      r.Bottom -= 2;
      return r;
    }
  }
}

// 一张截图的像素。内存里是 BGRA 顺序，每行 Stride 个字节
sealed class Raster {
  public int Width;
  public int Height;
  public int Stride;
  public byte[] Data;

  public Raster(Bitmap bmp) {
    Width = bmp.Width;
    Height = bmp.Height;
    BitmapData d = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      Stride = d.Stride;
      Data = new byte[Stride * Height];
      Marshal.Copy(d.Scan0, Data, 0, Data.Length);
    } finally {
      bmp.UnlockBits(d);
    }
  }

  public int R(int x, int y) { return Data[y * Stride + x * 4 + 2]; }
  public int G(int x, int y) { return Data[y * Stride + x * 4 + 1]; }
  public int B(int x, int y) { return Data[y * Stride + x * 4]; }
  public int A(int x, int y) { return Data[y * Stride + x * 4 + 3]; }
}

static class Probe {
  const int WM_SET_PATTERN = 0x8000 + 1; // 和 scripts/backdrop.cs 约好的消息：换棋盘格窗口的图案
  const uint SRCCOPY = 0x00CC0020;
  const uint CAPTUREBLT = 0x40000000;
  const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  const uint MOUSEEVENTF_LEFTUP = 0x0004;
  const uint SWP_NOSIZE = 0x0001;
  const uint SWP_NOMOVE = 0x0002;
  const uint SWP_NOACTIVATE = 0x0010;
  const int DWMWA_CLOAKED = 14;
  const int CheckerCell = 40; // 棋盘格一格的大小（物理像素），和 scripts/backdrop.cs 保持一致

  // ---- 输出 ----

  static long UnixMs() {
    return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
  }

  static uint Pid(string text) {
    uint pid;
    if (!uint.TryParse(text, out pid)) throw new ProbeError("进程号不对：" + text);
    return pid;
  }

  static double Number(string text) {
    return double.Parse(text, CultureInfo.InvariantCulture);
  }

  static int Integer(string text) {
    return int.Parse(text, CultureInfo.InvariantCulture);
  }

  /// 精确到亚毫秒的等待。Sleep(1) 实际会睡十几毫秒，测“到了之后几毫秒内点击”时太粗
  static void SleepPrecise(int ms) {
    if (ms <= 0) return;
    long target = Stopwatch.GetTimestamp() + (long)(ms * (Stopwatch.Frequency / 1000.0));
    while (true) {
      long remain = target - Stopwatch.GetTimestamp();
      if (remain <= 0) return;
      if (remain > Stopwatch.Frequency / 400) Thread.Sleep((int)(remain * 1000.0 / Stopwatch.Frequency) - 1);
      else Thread.SpinWait(50);
    }
  }

  // ---- 窗口 ----

  /// 屏幕上的顶层窗口，按 z-order 从前往后
  static List<WinInfo> OnScreenWindows() {
    List<WinInfo> list = new List<WinInfo>();
    Native.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam) {
      if (!Native.IsWindowVisible(hwnd)) return true;
      int cloaked; // 被 DWM 藏起来的窗口（最小化的 UWP 应用之类）不算在屏幕上
      if (Native.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
      WinInfo win = new WinInfo(hwnd);
      if (win.Width <= 0 || win.Height <= 0) return true;
      list.Add(win);
      return true;
    }, IntPtr.Zero);
    return list;
  }

  /// panel 是面板应用的窗口；backdrop 是 scripts/backdrop.cs 那个独立进程里的棋盘格窗口，靠窗口标题找
  static Pair FindWindows(uint pid) {
    List<WinInfo> all = OnScreenWindows();
    Pair pair = new Pair();
    pair.Panel = all.FirstOrDefault(delegate(WinInfo w) { return w.Pid == pid && w.Title == "panel"; });
    pair.Backdrop = all.FirstOrDefault(delegate(WinInfo w) { return w.Title == "backdrop"; });
    if (pair.Panel == null || pair.Backdrop == null) {
      string mine = string.Join(", ", all.Where(delegate(WinInfo w) { return w.Pid == pid; }).Select(delegate(WinInfo w) { return "「" + w.Title + "」"; }).ToArray());
      int found = all.Count(delegate(WinInfo w) { return w.Title == "backdrop"; });
      throw new ProbeError("找不到窗口：pid " + pid + " 的窗口有 " + mine + "，标题为 backdrop 的窗口有 " + found + " 个");
    }
    pair.Scale = Native.GetDpiForWindow(pair.Panel.Hwnd) / 96.0;
    return pair;
  }

  /// 与面板范围重叠的窗口，从前往后。面板和棋盘格窗口自己一定在名单里，
  /// 其它的窗口只有“在面板范围内会接到鼠标”才算数（见 TakesMouse）。
  /// （不能把开始点击穿透的面板自己也排除掉，所以单独放行）
  static List<WinInfo> Overlapping(Pair pair) {
    Native.RECT area = pair.Inner;
    return OnScreenWindows().Where(delegate(WinInfo w) {
      if (!Intersects(w.Rect, area)) return false;
      w.TakesMouse = w.Hwnd == pair.Panel.Hwnd || w.Hwnd == pair.Backdrop.Hwnd || TakesMouse(w, area);
      return w.TakesMouse;
    }).ToList();
  }

  /// 与面板范围重叠的全部窗口，从前往后，不管它接不接收鼠标。只给 list 用：让人看到哪些浮层被忽略了
  static List<WinInfo> OverlappingIncludingIgnored(Pair pair) {
    Native.RECT area = pair.Inner;
    return OnScreenWindows().Where(delegate(WinInfo w) {
      if (!Intersects(w.Rect, area)) return false;
      w.TakesMouse = w.Hwnd == pair.Panel.Hwnd || w.Hwnd == pair.Backdrop.Hwnd || TakesMouse(w, area);
      return true;
    }).ToList();
  }

  static bool Intersects(Native.RECT a, Native.RECT b) {
    return a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
  }

  /// 这个窗口在 area 范围内会不会接到鼠标。
  ///
  /// 系统浮层、工具软件的覆盖层永远盖在所有窗口上面，却既不接收点击也看不见，例如 Windows 11 的
  /// Shell Handwriting Canvas。macOS 上这类窗口在更高的层级，可以按层级排除；Windows 没有层级，
  /// 也找不到一条能穷举的样式规则（带 WS_EX_TRANSPARENT 的分层窗口、整窗 alpha 为 0 的、被禁用的、
  /// 还有样式中看不出任何异常的），所以直接问系统：
  /// 在它和 area 重叠的范围里取 3x3 个点，WindowFromPoint 一次都没返回它，就当它不接收鼠标。
  static bool TakesMouse(WinInfo w, Native.RECT area) {
    Native.RECT r = w.Rect;
    r.Left = Math.Max(r.Left, area.Left);
    r.Top = Math.Max(r.Top, area.Top);
    r.Right = Math.Min(r.Right, area.Right);
    r.Bottom = Math.Min(r.Bottom, area.Bottom);
    for (int i = 0; i < 3; i++) {
      for (int j = 0; j < 3; j++) {
        Native.POINT p = new Native.POINT();
        p.X = r.Left + (r.Right - r.Left - 1) * i / 2;
        p.Y = r.Top + (r.Bottom - r.Top - 1) * j / 2;
        IntPtr hit = Native.WindowFromPoint(p);
        // 命中的可能是它的子窗口（例如面板里的网页），比较各自的顶层窗口
        if (hit != IntPtr.Zero && Native.GetAncestor(hit, 2) == w.Hwnd) return true; // GA_ROOT
      }
    }
    return false;
  }

  static bool OrderOk(Pair pair, List<WinInfo> overlap) {
    return overlap.Count >= 2 && overlap[0].Hwnd == pair.Panel.Hwnd && overlap[1].Hwnd == pair.Backdrop.Hwnd
      && pair.Backdrop.Contains(pair.Panel.Rect);
  }

  /// 面板矩形范围内，从前往后必须先遇到面板、再遇到棋盘格窗口，并且棋盘格窗口完全盖住这个范围。
  /// 这样截屏和合成点击只会碰到这两个窗口。
  static void AssertOnlyOurWindows(Pair pair) {
    List<WinInfo> overlap = Overlapping(pair);
    if (!OrderOk(pair, overlap)) {
      // 棋盘格窗口不置顶，别的应用在前台时会被挡住：把它提到普通窗口的最前面（不激活）。
      // 面板是置顶窗口，本来就在它上面。
      Native.SetWindowPos(pair.Backdrop.Hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
      Thread.Sleep(100);
      overlap = Overlapping(pair);
    }
    if (!OrderOk(pair, overlap)) {
      string seen = string.Join("; ", overlap.Take(6).Select(delegate(WinInfo w) { return w.Describe(); }).ToArray());
      throw new ProbeError("面板矩形范围内不只是本应用的窗口，停止操作。当前从前往后：" + seen);
    }
    // 让面板成为前台窗口，和刚被唤出来的启动器面板一个样子
    ActivateWindow(pair.Panel.Hwnd);
  }

  static void ActivateWindow(IntPtr hwnd) {
    if (Native.GetForegroundWindow() == hwnd) return;
    Native.SetForegroundWindow(hwnd);
    if (Native.GetForegroundWindow() == hwnd) return;
    // 系统只让前台进程（或者最近收到过输入的进程）把窗口切到前台。
    // 把自己的输入队列临时挂到当前前台线程上，就不受这个限制
    uint me = Native.GetCurrentThreadId();
    uint pid;
    uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
    if (foregroundThread == 0 || foregroundThread == me) return;
    Native.AttachThreadInput(me, foregroundThread, true);
    try {
      Native.SetForegroundWindow(hwnd);
    } finally {
      Native.AttachThreadInput(me, foregroundThread, false);
    }
    Thread.Sleep(60);
  }

  // ---- 截屏 ----

  /// 截取屏幕上一块区域（物理像素）。CAPTUREBLT 让分层窗口也被截进来，不加的话这类窗口在截图里是缺失的
  static Bitmap CaptureScreen(int x, int y, int width, int height) {
    Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
    using (Graphics g = Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc();
      IntPtr screen = Native.GetDC(IntPtr.Zero);
      try {
        if (!Native.BitBlt(hdc, 0, 0, width, height, screen, x, y, SRCCOPY | CAPTUREBLT)) {
          throw new ProbeError("截屏失败（BitBlt），错误码 " + Marshal.GetLastWin32Error());
        }
      } finally {
        Native.ReleaseDC(IntPtr.Zero, screen);
        g.ReleaseHdc(hdc);
      }
    }
    return bmp;
  }

  /// 换棋盘格窗口的图案：0 棋盘格，1 纯黑，2 纯白
  static void SetPattern(Pair pair, int pattern) {
    Native.SendMessage(pair.Backdrop.Hwnd, WM_SET_PATTERN, new IntPtr(pattern), IntPtr.Zero);
    Thread.Sleep(120); // 等 DWM 把新的背景合成到屏幕上
  }

  /// 面板窗口自己的颜色和 alpha。
  ///
  /// Windows 上截单个窗口拿不到 alpha：实测 PrintWindow 截出来的 alpha 通道全是 255，透明的地方变成黑色。
  /// 所以换个办法：让面板分别叠在纯黑、纯白的背景上各截一次屏，
  ///   叠在黑上：  黑 = 面板贡献的颜色（预乘 alpha）
  ///   叠在白上：  白 = 黑 + (1 - alpha) * 255
  /// 两张相减就是 alpha。得到的是 DWM 实际合成出来的结果，和 macOS 上带 alpha 截单个窗口等价。
  static Bitmap DerivePanel(Bitmap overBlack, Bitmap overWhite) {
    Raster black = new Raster(overBlack);
    Raster white = new Raster(overWhite);
    Bitmap panel = new Bitmap(black.Width, black.Height, PixelFormat.Format32bppArgb);
    BitmapData d = panel.LockBits(new Rectangle(0, 0, black.Width, black.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    byte[] data = new byte[d.Stride * black.Height];
    for (int y = 0; y < black.Height; y++) {
      for (int x = 0; x < black.Width; x++) {
        // 三个颜色通道各算一次 alpha 再取平均；纯色区域里三者一样
        int alpha = 255 - (int)Math.Round(((white.R(x, y) - black.R(x, y)) + (white.G(x, y) - black.G(x, y))
          + (white.B(x, y) - black.B(x, y))) / 3.0);
        if (alpha < 0) alpha = 0;
        if (alpha > 255) alpha = 255;
        int i = y * d.Stride + x * 4;
        data[i] = Straight(black.B(x, y), alpha);
        data[i + 1] = Straight(black.G(x, y), alpha);
        data[i + 2] = Straight(black.R(x, y), alpha);
        data[i + 3] = (byte)alpha;
      }
    }
    Marshal.Copy(data, 0, d.Scan0, data.Length);
    panel.UnlockBits(d);
    return panel;
  }

  /// PNG 用的是直通 alpha：颜色 = 预乘颜色 * 255 / alpha
  static byte Straight(int premultiplied, int alpha) {
    if (alpha == 0) return 0;
    return (byte)Math.Min(255, (int)Math.Round(premultiplied * 255.0 / alpha));
  }

  static string F(double value) {
    return value.ToString("0.####", CultureInfo.InvariantCulture);
  }

  /// 截一组图：面板分别叠在棋盘格、纯黑、纯白上各截一次屏，再由后两张反推出面板自己的颜色和 alpha
  static void CaptureSeries(Pair pair, string outDir, string tag) {
    Native.RECT r = pair.Panel.Rect;
    int width = pair.Panel.Width;
    int height = pair.Panel.Height;
    string path = Path.Combine(outDir, tag);
    // 面板矩形范围内的屏幕合成结果：面板 + 它后面的棋盘格
    SetPattern(pair, 0);
    using (Bitmap screen = CaptureScreen(r.Left, r.Top, width, height)) {
      screen.Save(path + "-screen.png", ImageFormat.Png);
    }
    SetPattern(pair, 1);
    using (Bitmap overBlack = CaptureScreen(r.Left, r.Top, width, height)) {
      overBlack.Save(path + "-screen-black.png", ImageFormat.Png);
      SetPattern(pair, 2);
      using (Bitmap overWhite = CaptureScreen(r.Left, r.Top, width, height)) {
        overWhite.Save(path + "-screen-white.png", ImageFormat.Png);
        // 左上角在轮廓之外：背景换了它就得跟着变，否则说明棋盘格窗口没换图案（或者被别的窗口挡住了）
        if (overBlack.GetPixel(0, 0).ToArgb() != Color.Black.ToArgb() || overWhite.GetPixel(0, 0).ToArgb() != Color.White.ToArgb()) {
          throw new ProbeError("棋盘格窗口没有换图案：叠在“黑”上时左上角是 " + overBlack.GetPixel(0, 0)
            + "，叠在“白”上时是 " + overWhite.GetPixel(0, 0));
        }
        using (Bitmap panel = DerivePanel(overBlack, overWhite)) {
          panel.Save(path + "-panel.png", ImageFormat.Png);
        }
      }
    }
    SetPattern(pair, 0);
  }

  static void Capture(uint pid, string outDir, string tag) {
    Pair pair = FindWindows(pid);
    AssertOnlyOurWindows(pair);
    Directory.CreateDirectory(outDir);
    Native.RECT r = pair.Panel.Rect;
    double scale = pair.Scale;

    // 截图期间把光标挪到面板外面固定的地方：面板右下角的状态文字跟着光标变，三次截图之间内容必须一模一样。
    // 截完发现光标不在原地（有人动了鼠标）就重来，最多三次。结束时把光标放回原处
    Native.POINT origin = CursorPos();
    Native.POINT parked = new Native.POINT();
    parked.X = r.Left - 15;
    parked.Y = r.Top + 15;
    bool stable = false;
    try {
      for (int attempt = 1; attempt <= 3 && !stable; attempt++) {
        Move(parked.X, parked.Y);
        Thread.Sleep(200);
        CaptureSeries(pair, outDir, tag);
        Native.POINT now = CursorPos();
        stable = now.X == parked.X && now.Y == parked.Y;
      }
    } finally {
      Move(origin.X, origin.Y);
    }
    if (!stable) {
      throw new ProbeError("截图期间鼠标一直在动，面板的状态文字跟着变，没法比较像素。运行期间请不要动鼠标");
    }

    // 坐标换算成逻辑像素：analyze 按“逻辑像素 -> 截图像素”定位本体和阴影
    StringBuilder json = new StringBuilder();
    json.Append("{\n");
    json.Append("  \"panel\": { \"x\": " + F(r.Left / scale) + ", \"y\": " + F(r.Top / scale) + ", \"w\": " + F(pair.Panel.Width / scale)
      + ", \"h\": " + F(pair.Panel.Height / scale) + " },\n");
    Native.RECT b = pair.Backdrop.Rect;
    json.Append("  \"backdrop\": { \"x\": " + F(b.Left / scale) + ", \"y\": " + F(b.Top / scale) + ", \"w\": " + F(pair.Backdrop.Width / scale)
      + ", \"h\": " + F(pair.Backdrop.Height / scale) + " },\n");
    json.Append("  \"scale\": " + F(scale) + ",\n");
    json.Append("  \"checker\": " + F(CheckerCell / scale) + "\n"); // 棋盘格一格是 40 个物理像素
    json.Append("}\n");
    File.WriteAllText(Path.Combine(outDir, tag + "-geometry.json"), json.ToString(), new UTF8Encoding(false));

    Console.Out.WriteLine("panel " + pair.Panel.RectText() + " 物理像素（缩放 " + F(scale) + "）  backdrop " + pair.Backdrop.RectText());
  }

  // ---- 像素测量 ----

  class Geometry {
    public double PanelX, PanelY, PanelW, PanelH;
    public double BackdropX, BackdropY;
    public double Checker = 40.0; // 棋盘格一格的大小，逻辑像素
    public double Scale = 1.0;
  }

  static double Field(string body, string key) {
    Match m = Regex.Match(body, "\"" + key + "\"\\s*:\\s*(-?[0-9.eE+]+)");
    return m.Success ? Number(m.Groups[1].Value) : 0;
  }

  static Geometry LoadGeometry(string outDir, string tag) {
    string path = Path.Combine(outDir, tag + "-geometry.json");
    string json = File.ReadAllText(path);
    Match panel = Regex.Match(json, "\"panel\"\\s*:\\s*\\{([^}]*)\\}");
    Match backdrop = Regex.Match(json, "\"backdrop\"\\s*:\\s*\\{([^}]*)\\}");
    if (!panel.Success || !backdrop.Success) throw new ProbeError("读不懂 " + path);
    Geometry geo = new Geometry();
    geo.PanelX = Field(panel.Groups[1].Value, "x");
    geo.PanelY = Field(panel.Groups[1].Value, "y");
    geo.PanelW = Field(panel.Groups[1].Value, "w");
    geo.PanelH = Field(panel.Groups[1].Value, "h");
    geo.BackdropX = Field(backdrop.Groups[1].Value, "x");
    geo.BackdropY = Field(backdrop.Groups[1].Value, "y");
    geo.Checker = Field(json, "checker");
    geo.Scale = Field(json, "scale");
    return geo;
  }

  // 皮肤图和界面布局的常量，与 scripts/gen-panel-png.mjs、src/panel.css 保持一致
  const int BodyAlpha = 199; // 本体 alpha：0.78 * 255
  const int ShadowMax = 97; // 阴影 alpha 上限：0.38 * 255
  const double Margin = 36.0; // 皮肤图四周留给阴影的透明边距
  const double BottomBand = 66.0; // 皮肤图底部：尾巴 30 + 边距 36
  const double TailX = 100.0; // 尾巴尖端的 x（边距 36 + 64）
  const double NotchDepth = 10.0; // 采样点离右边缘多远：在缺口里
  const int Tolerance = 6; // 和 macOS 版保持一致。Windows 上实测误差都是 0，这里只是留余量：由两次截图反推 alpha 可能有 1/255 的舍入

  static bool IsWhite(Geometry geo, int x, int y, double k) {
    double gx = geo.PanelX + (x + 0.5) / k;
    double gy = geo.PanelY + (y + 0.5) / k;
    int column = (int)Math.Floor((gx - geo.BackdropX) / geo.Checker);
    int row = (int)Math.Floor((gy - geo.BackdropY) / geo.Checker);
    return (column + row) % 2 == 1; // 左上角那一格是黑的：奇数格是白
  }

  /// 预测值和屏幕上实际的值，一个颜色通道上差几个 1/255
  static int ChannelError(double predicted, int actual) {
    return (int)Math.Round(Math.Abs(predicted - actual), MidpointRounding.AwayFromZero);
  }

  static int Mean(List<int> values) {
    return values.Count == 0 ? 0 : (int)values.Average();
  }

  static Raster LoadRaster(string outDir, string name) {
    try {
      using (Bitmap bmp = new Bitmap(Path.Combine(outDir, name))) {
        return new Raster(bmp);
      }
    } catch (ArgumentException) {
      throw new ProbeError("读不懂截图 " + name);
    } catch (IOException) {
      throw new ProbeError("读不到截图 " + name);
    }
  }

  /// 返回没通过的检查数量
  static int Analyze(string outDir, string tag) {
    Geometry geo = LoadGeometry(outDir, tag);
    Raster screen = LoadRaster(outDir, tag + "-screen.png");
    Raster window = LoadRaster(outDir, tag + "-panel.png");
    Raster overBlack = LoadRaster(outDir, tag + "-screen-black.png");
    Raster overWhite = LoadRaster(outDir, tag + "-screen-white.png");
    if (screen.Width != window.Width || screen.Height != window.Height || screen.Width != overBlack.Width
      || screen.Height != overBlack.Height || screen.Width != overWhite.Width || screen.Height != overWhite.Height) {
      throw new ProbeError(string.Format(CultureInfo.InvariantCulture, "截图尺寸不一致：screen {0}x{1}，panel {2}x{3}，black {4}x{5}，white {6}x{7}",
        screen.Width, screen.Height, window.Width, window.Height, overBlack.Width, overBlack.Height, overWhite.Width, overWhite.Height));
    }

    double w = geo.PanelW;
    double h = geo.PanelH;
    double k = screen.Width / w; // 每个逻辑像素对应几个截图像素
    // 本体在窗口内的范围：四周让开透明边距，底部还要让开尾巴
    double left = Margin;
    double top = Margin;
    double right = w - Margin;
    double bottom = h - BottomBand;
    int failures = 0;
    Action<bool, string> check = delegate(bool ok, string text) { failures += Check(ok, text); };
    // 窗口内逻辑像素 -> 截图像素
    Func<double, int> px = delegate(double v) { return (int)(v * k); };
    Func<double, double, int> alpha = delegate(double x, double y) { return window.A(px(x), px(y)); };

    Console.Out.WriteLine("窗口 " + (int)w + "x" + (int)h + " 逻辑像素，截图中每个逻辑像素 = " + k.ToString("0.00", CultureInfo.InvariantCulture)
      + " 个图像像素（缩放 " + F(geo.Scale) + "）");
    Console.Out.WriteLine("本体在窗口内的范围：x " + (int)left + "..." + (int)right + "，y " + (int)top + "..." + (int)bottom);

    // ---- 一、面板窗口自身的 alpha 通道 ----
    Console.Out.WriteLine("\n[一、面板窗口自身的 alpha 通道]");
    int[] corners = { alpha(2, 2), alpha(w - 3, 2), alpha(2, h - 3), alpha(w - 3, h - 3) };
    check(corners.All(delegate(int a) { return a == 0; }),
      "矩形四角 alpha = [" + string.Join(", ", corners) + "]（期望全 0：窗口是矩形，四角在轮廓之外）");
    int roundCorner = alpha(w - 3, 3);
    check(roundCorner == 0, "右上圆角之外 alpha = " + roundCorner + "（期望 0）");
    int a1 = alpha(left + 8, 156);
    int a2 = alpha(TailX, bottom + 28);
    check(Math.Abs(a1 - BodyAlpha) <= 2 && Math.Abs(a2 - BodyAlpha) <= 2,
      "本体 alpha = " + a1 + "，尾巴尖端 alpha = " + a2 + "（期望 " + BodyAlpha + "）");
    int notch = alpha(right - NotchDepth, top + 34);
    check(notch <= ShadowMax, "右侧缺口内 alpha = " + notch + "（期望 <= " + ShadowMax + "：缺口里最多只有阴影）");

    // 本体下边缘往下 2..28px 这一段：尾巴之外最多只有阴影
    int shadowPeak = 0;
    int besideTailMax = 0;
    int besideTailCount = 0;
    for (int y = px(bottom + 2); y < px(bottom + 28); y++) {
      for (int x = px(right - 400); x < px(right - 20); x++) {
        int a = window.A(x, y);
        if (Math.Abs(x / k - TailX) < 20) continue; // 尾巴自己
        shadowPeak = Math.Max(shadowPeak, a);
        if (Math.Abs(x / k - TailX) < 50) {
          besideTailMax = Math.Max(besideTailMax, a);
          besideTailCount++;
        }
      }
    }
    check(shadowPeak > 0 && shadowPeak <= ShadowMax, "本体正下方 2..28px 的 alpha 峰值 = " + shadowPeak
      + "（期望 1..." + ShadowMax + "：半透明，且低于点击命中的阈值 128）");
    check(besideTailCount > 0 && besideTailMax <= ShadowMax, "尾巴两侧紧挨着的 " + besideTailCount + " 个像素 alpha 峰值 = "
      + besideTailMax + "（期望 <= " + ShadowMax + "：尾巴两侧是透的）");

    // ---- 二、屏幕上的实际合成结果 ----
    // 预测值 = 面板窗口贡献的颜色 + (1 - alpha) * 棋盘格颜色；和屏幕截图逐像素比较
    Console.Out.WriteLine("\n[二、屏幕上的实际合成结果：面板 + 后面的黑白棋盘格]");
    int outsideCount = 0;
    int outsideMax = 0;
    int outsideOff = 0;
    int bodyCount = 0;
    int bodyMax = 0;
    int softCount = 0;
    int softMax = 0;
    int bandMin = 255;
    int bandMax = 0;
    int bandY = px(top + 9); // 顶部留白带里没有内容，alpha 应该处处等于本体 alpha
    int changed = 0; // 三次截图之间内容变了的像素
    List<int> greensOverWhite = new List<int>(); // 顶部留白带里，棋盘格透过本体之后的绿色通道
    List<int> greensOverBlack = new List<int>();
    for (int y = 0; y < screen.Height; y++) {
      for (int x = 0; x < screen.Width; x++) {
        int bg = IsWhite(geo, x, y, k) ? 255 : 0;
        int pa = window.A(x, y);
        int sr = screen.R(x, y);
        int sg = screen.G(x, y);
        int sb = screen.B(x, y);
        // 棋盘格的黑格上，屏幕上的像素应该和“背后是纯黑”时一模一样；白格上和“背后是纯白”时一模一样。
        // 不一样说明三次截图之间面板自己的内容变了
        if (bg == 255 ? (sr != overWhite.R(x, y) || sg != overWhite.G(x, y) || sb != overWhite.B(x, y))
          : (sr != overBlack.R(x, y) || sg != overBlack.G(x, y) || sb != overBlack.B(x, y))) {
          changed++;
        }
        // 面板图存的是直通 alpha，合成之前要先乘上 alpha
        double coverage = pa / 255.0;
        double rest = (255 - pa) / 255.0 * bg;
        int error = ChannelError(window.R(x, y) * coverage + rest, sr);
        error = Math.Max(error, ChannelError(window.G(x, y) * coverage + rest, sg));
        error = Math.Max(error, ChannelError(window.B(x, y) * coverage + rest, sb));
        if (pa == 0) {
          outsideCount++;
          outsideMax = Math.Max(outsideMax, error);
          if (error > Tolerance) outsideOff++;
        } else if (pa >= 190) {
          bodyCount++;
          bodyMax = Math.Max(bodyMax, error);
          if (y == bandY && x > px(left + 14) && x < px(right - 30)) {
            bandMin = Math.Min(bandMin, pa);
            bandMax = Math.Max(bandMax, pa);
            int green = screen.G(x, y);
            if (bg == 255) greensOverWhite.Add(green);
            else greensOverBlack.Add(green);
          }
        } else {
          softCount++;
          softMax = Math.Max(softMax, error);
        }
      }
    }
    double total = screen.Width * screen.Height;
    Func<int, string> share = delegate(int n) { return (n / total * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"; };

    check(changed == 0, "三次截图之间面板内容没变：和“背后是纯黑 / 纯白时”不一样的像素有 " + changed
      + " 个（不为 0 通常是截图时鼠标动了，面板右下角的状态文字跟着变）");
    check(outsideOff == 0, "轮廓之外（alpha = 0）的 " + outsideCount + " 个像素（" + share(outsideCount) + "）在屏幕上就是纯黑或纯白的棋盘格，偏离超过 "
      + Tolerance + "/255 的有 " + outsideOff + " 个");
    check(outsideMax <= Tolerance, "轮廓之外的像素与棋盘格的最大误差 = " + outsideMax + "/255（期望 <= " + Tolerance + "）");
    check(bodyMax <= Tolerance, "本体内部（alpha ≈ " + BodyAlpha + "）的 " + bodyCount + " 个像素（" + share(bodyCount)
      + "）与“面板叠在棋盘格上”的预测值最大误差 = " + bodyMax + "/255（期望 <= " + Tolerance + "）");
    check(softMax <= Tolerance, "阴影和抗锯齿边缘（0 < alpha < 190）的 " + softCount + " 个像素（" + share(softCount) + "）最大误差 = "
      + softMax + "/255（期望 <= " + Tolerance + "）");
    check(bandMin == BodyAlpha && bandMax == BodyAlpha, "顶部留白带里本体 alpha 的范围 = " + bandMin + "..." + bandMax
      + "（期望处处是 " + BodyAlpha + "）");

    // 棋盘格透过本体之后还剩多少对比度：直接反映本体的透明度
    int whiteMean = Mean(greensOverWhite);
    int blackMean = Mean(greensOverBlack);
    int contrast = whiteMean - blackMean;
    Console.Out.WriteLine("     棋盘格透过本体：白格上绿色通道均值 " + whiteMean + "，黑格上 " + blackMean + "，剩余对比度 "
      + contrast + "/255（本体不透明度 ≈ " + (1 - contrast / 255.0).ToString("0.00", CultureInfo.InvariantCulture) + "，设计值 0.78）");

    Console.Out.WriteLine("\n" + (failures == 0 ? "全部通过" : "有 " + failures + " 项没通过"));
    return failures;
  }

  // ---- 切换穿透时的画面 ----

  /// 两张截图在上下高度 top 以内的范围里有几个像素不一样
  static int CountDifferent(Raster a, Raster b, int top) {
    int count = 0;
    for (int y = 0; y < top; y++) {
      for (int x = 0; x < a.Width; x++) {
        int i = y * a.Stride + x * 4;
        int j = y * b.Stride + x * 4;
        if (a.Data[i] != b.Data[j] || a.Data[i + 1] != b.Data[j + 1] || a.Data[i + 2] != b.Data[j + 2]) count++;
      }
    }
    return count;
  }

  /// 光标在轮廓里外之间快速来回，“点击穿透”跟着一开一关，这期间画面有没有闪。
  ///
  /// Windows 上切换穿透是改窗口样式：set_ignore_cursor_events(true) 给窗口加上
  /// WS_EX_LAYERED | WS_EX_TRANSPARENT，设回 false 再去掉（见 tao 的 WindowState::set_window_flags）。
  /// 光标每进出轮廓一次，样式就变一次，所以要确认这一下画面不会跳。
  ///
  /// 做法：让光标在本体内部和一个轮廓外的点之间每秒来回二十多次，同时不停地截屏。
  /// 面板上半部分没有跟着光标变的状态文字，内容是静止的，只要有一帧和静止时不一样，就算闪了。
  /// 返回没通过的检查数量。
  static int Flicker(uint pid, string outDir, string tag) {
    Pair pair = FindWindows(pid);
    AssertOnlyOurWindows(pair);
    Native.RECT r = pair.Panel.Rect;
    int width = pair.Panel.Width;
    int height = pair.Panel.Height;
    int top = height / 2; // 只比较上半部分
    Native.POINT inside = ToGlobal(44, 156, pair, true);
    Native.POINT outside = ToGlobal(4, 4, pair, true);
    Native.POINT origin = CursorPos();
    int failures = 0;
    Bitmap worstFrame = null;
    try {
      Move(inside.X, inside.Y);
      Thread.Sleep(200);
      Raster reference = new Raster(CaptureScreen(r.Left, r.Top, width, top));
      Move(outside.X, outside.Y);
      Thread.Sleep(200);
      int resting = CountDifferent(reference, new Raster(CaptureScreen(r.Left, r.Top, width, top)), top);
      failures += Check(resting == 0, "静止时光标在轮廓内、外两种状态，面板上半部分不一样的像素有 " + resting + " 个（期望 0：开始穿透不应改变画面）");

      int frames = 0, badFrames = 0, worst = 0, toggles = 0;
      bool ignoring = pair.Panel.ClickThrough;
      long end = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
      Move(inside.X, inside.Y);
      while (Stopwatch.GetTimestamp() < end) {
        for (int i = 0; i < 3; i++) {
          using (Bitmap frame = CaptureScreen(r.Left, r.Top, width, height)) {
            frames++;
            bool now = (Native.GetWindowLong(pair.Panel.Hwnd, -20) & 0x00000020) != 0; // WS_EX_TRANSPARENT
            if (now != ignoring) {
              ignoring = now;
              toggles++;
            }
            int different = CountDifferent(reference, new Raster(frame), top);
            if (different > 0) {
              badFrames++;
              worst = Math.Max(worst, different);
              if (worstFrame != null) worstFrame.Dispose();
              worstFrame = frame.Clone(new Rectangle(0, 0, width, top), frame.PixelFormat);
            }
          }
          SleepPrecise(3);
        }
        // 到另一个点去：光标越过轮廓，面板跟着切换一次穿透
        Native.POINT target = ignoring ? inside : outside;
        Move(target.X, target.Y);
      }
      failures += Check(badFrames == 0, "2 秒里截了 " + frames + " 帧，穿透切换了 " + toggles + " 次：和静止时不一样的有 " + badFrames
        + " 帧，单帧最多 " + worst + " 个像素（期望 0 帧：切换穿透不会让画面闪）");
      if (badFrames > 0 && worstFrame != null) {
        worstFrame.Save(Path.Combine(outDir, tag + "-flicker-worst.png"), ImageFormat.Png);
        Console.Out.WriteLine("     差得最多的那一帧存在 " + Path.Combine(outDir, tag + "-flicker-worst.png"));
      }
    } finally {
      if (worstFrame != null) worstFrame.Dispose();
      Move(origin.X, origin.Y);
    }
    return failures;
  }

  static int Check(bool ok, string text) {
    Console.Out.WriteLine((ok ? "ok  " : "FAIL") + " " + text);
    return ok ? 0 : 1;
  }

  // ---- 合成鼠标事件 ----

  static Native.POINT CursorPos() {
    Native.POINT p;
    Native.GetCursorPos(out p);
    return p;
  }

  static void Move(int x, int y) {
    Native.SetCursorPos(x, y);
  }

  static void Button(uint flags) {
    Native.INPUT[] input = new Native.INPUT[1];
    input[0].Type = 0; // INPUT_MOUSE
    input[0].Mi.DwFlags = flags;
    Native.SendInput(1, input, Marshal.SizeOf(typeof(Native.INPUT)));
  }

  /// x, y 是面板窗口内的逻辑像素时，换算成屏幕上的物理像素
  static Native.POINT ToGlobal(double x, double y, Pair pair, bool local) {
    Native.POINT p = new Native.POINT();
    if (local) {
      // 缩放是 1.5 这样的比例时，逻辑像素只对应半个物理像素，四舍五入到整数像素
      p.X = pair.Panel.Rect.Left + (int)Math.Round(x * pair.Scale, MidpointRounding.AwayFromZero);
      p.Y = pair.Panel.Rect.Top + (int)Math.Round(y * pair.Scale, MidpointRounding.AwayFromZero);
    } else {
      p.X = (int)Math.Round(x, MidpointRounding.AwayFromZero);
      p.Y = (int)Math.Round(y, MidpointRounding.AwayFromZero);
    }
    return p;
  }

  static Native.POINT CheckedTarget(double x, double y, Pair pair, bool local) {
    Native.POINT target = ToGlobal(x, y, pair, local);
    if (!pair.Panel.Contains(target.X, target.Y)) throw new ProbeError("目标点 (" + target.X + "," + target.Y + ") 不在面板范围内");
    return target;
  }

  static string PointText(Native.POINT p) {
    return "(" + p.X + "," + p.Y + ")";
  }

  /// 移到目标点，等一会儿，再点一下
  static void Click(uint pid, double x, double y, int waitMs, bool local) {
    Pair pair = FindWindows(pid);
    Native.POINT target = CheckedTarget(x, y, pair, local);
    AssertOnlyOurWindows(pair);
    Native.POINT origin = CursorPos();
    long moved = UnixMs();
    Move(target.X, target.Y);
    SleepPrecise(waitMs); // 等面板根据新的光标位置决定是否忽略鼠标事件
    long down = UnixMs();
    Button(MOUSEEVENTF_LEFTDOWN);
    SleepPrecise(40);
    Button(MOUSEEVENTF_LEFTUP);
    SleepPrecise(250);
    Move(origin.X, origin.Y);
    Console.Out.WriteLine("已把光标移到 " + PointText(target) + "（移动时刻 unix=" + moved + "），等 " + waitMs + "ms 后点击一次（按下时刻 unix="
      + down + "），光标放回 " + PointText(origin));
  }

  /// 先停在 from 处等状态稳定，再“甩”到 to，到达后等 delayMs 毫秒就按下：模拟人快速移动后马上点击
  static void Flick(uint pid, double fromX, double fromY, double toX, double toY, int delayMs, bool local) {
    Pair pair = FindWindows(pid);
    Native.POINT from = CheckedTarget(fromX, fromY, pair, local);
    Native.POINT to = CheckedTarget(toX, toY, pair, local);
    AssertOnlyOurWindows(pair);
    Native.POINT origin = CursorPos();
    Move(from.X, from.Y);
    SleepPrecise(300);
    long moved = UnixMs();
    Move(to.X, to.Y);
    SleepPrecise(delayMs);
    long down = UnixMs();
    Button(MOUSEEVENTF_LEFTDOWN);
    Button(MOUSEEVENTF_LEFTUP);
    SleepPrecise(200);
    Move(origin.X, origin.Y);
    Console.Out.WriteLine("已从 " + PointText(from) + " 甩到 " + PointText(to) + " 并立刻点击（移动时刻 unix=" + moved + "，按下时刻 unix=" + down
      + "），光标放回 " + PointText(origin));
  }

  // ---- 启动 / 结束 ----

  /// 启动一个程序（面板应用或者棋盘格窗口），可以带参数：标准输出和错误输出都写进日志文件，
  /// 不带控制台窗口。返回它的进程号。
  ///
  /// 没有在脚本里写 “$APP > 日志 &”，是因为 Git Bash 给出的进程号可能只是一个中间等待进程的，
  /// 和真正的程序对不上，kill 也结束不了它。
  static uint Launch(string exe, string logPath, string[] arguments) {
    exe = Path.GetFullPath(exe);
    logPath = Path.GetFullPath(logPath);
    // 日志句柄要能被子进程继承，得带上“可继承”的属性
    Native.SECURITY_ATTRIBUTES inheritable = new Native.SECURITY_ATTRIBUTES();
    inheritable.nLength = Marshal.SizeOf(typeof(Native.SECURITY_ATTRIBUTES));
    inheritable.bInheritHandle = 1;
    const uint shareReadWrite = 3, createAlways = 2, openExisting = 3, attributesNormal = 0x80;
    IntPtr log = Native.CreateFile(logPath, 0x40000000, shareReadWrite, ref inheritable, createAlways, attributesNormal, IntPtr.Zero);
    if (log == new IntPtr(-1)) throw new ProbeError("打不开日志文件 " + logPath + "，错误码 " + Marshal.GetLastWin32Error());
    IntPtr nul = Native.CreateFile("NUL", 0x80000000, shareReadWrite, ref inheritable, openExisting, attributesNormal, IntPtr.Zero);
    // 白名单里的句柄。这块内存要一直留到属性列表销毁：系统只是记下指针，不会自己拷贝。
    // 标准输出和标准错误用的是同一个句柄，名单里只能列一次（重复的话 CreateProcess 报“参数错误”）
    IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
    Marshal.WriteIntPtr(handles, 0, nul);
    Marshal.WriteIntPtr(handles, IntPtr.Size, log);
    IntPtr attributes = HandleWhitelist(handles, 2);
    int startupSize = Marshal.SizeOf(typeof(Native.STARTUPINFO));
    IntPtr startup = Marshal.AllocHGlobal(startupSize + IntPtr.Size); // STARTUPINFOEX：多一个属性列表指针
    try {
      StringBuilder commandLine = new StringBuilder("\"" + exe + "\"");
      foreach (string argument in arguments) commandLine.Append(" ").Append(argument);
      Native.STARTUPINFO si = new Native.STARTUPINFO();
      si.cb = startupSize + IntPtr.Size;
      si.dwFlags = 0x100; // STARTF_USESTDHANDLES
      si.hStdInput = nul;
      si.hStdOutput = log;
      si.hStdError = log;
      Marshal.StructureToPtr(si, startup, false);
      Marshal.WriteIntPtr(startup, startupSize, attributes);
      Native.PROCESS_INFORMATION pi;
      // CREATE_NO_WINDOW：调试版的应用是控制台程序，不要给它弹出控制台窗口；
      // EXTENDED_STARTUPINFO_PRESENT：启动信息是 STARTUPINFOEX
      const uint createNoWindow = 0x08000000, extendedStartupInfoPresent = 0x00080000;
      if (!Native.CreateProcessExtended(exe, commandLine, IntPtr.Zero, IntPtr.Zero, true, createNoWindow | extendedStartupInfoPresent,
        IntPtr.Zero, null, startup, out pi)) {
        throw new ProbeError("启动失败：" + exe + "，错误码 " + Marshal.GetLastWin32Error());
      }
      Native.CloseHandle(pi.hThread);
      Native.CloseHandle(pi.hProcess);
      return pi.dwProcessId;
    } finally {
      Native.DeleteProcThreadAttributeList(attributes);
      Marshal.FreeHGlobal(attributes);
      Marshal.FreeHGlobal(handles);
      Marshal.FreeHGlobal(startup);
      Native.CloseHandle(nul);
      Native.CloseHandle(log);
    }
  }

  /// 句柄白名单：CreateProcess 时只让子进程继承列出的句柄，probe 的其它句柄一律不继承。
  /// 不能只靠“句柄自己可不可继承”：probe 的标准输出常常是个管道，调用方用 $(...) 取那一行进程号时，
  /// 程序只要握着这个管道，调用方就一直是“读完了但等不到结束”。
  /// handles 是一段连续存放句柄的内存，在属性列表销毁之前不能释放。
  /// 返回的属性列表用完要调 DeleteProcThreadAttributeList。
  static IntPtr HandleWhitelist(IntPtr handles, int count) {
    int size = 0;
    Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // 先问需要多大
    IntPtr list = Marshal.AllocHGlobal(size);
    if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref size)) {
      Marshal.FreeHGlobal(list);
      throw new ProbeError("初始化进程属性列表失败，错误码 " + Marshal.GetLastWin32Error());
    }
    if (!Native.UpdateProcThreadAttribute(list, 0, new IntPtr(0x00020002), handles, new IntPtr(IntPtr.Size * count),
      IntPtr.Zero, IntPtr.Zero)) { // ProcThreadAttributeHandleList
      Native.DeleteProcThreadAttributeList(list);
      Marshal.FreeHGlobal(list);
      throw new ProbeError("设置句柄白名单失败，错误码 " + Marshal.GetLastWin32Error());
    }
    return list;
  }

  /// 把进程加进待结束的名单，前提是它确实是面板应用或者棋盘格窗口
  /// （进程号可能已经被别的程序用上了）
  static void AddOwnProcess(List<Process> targets, uint pid) {
    try {
      Process p = Process.GetProcessById((int)pid);
      if ((p.ProcessName == "tauri-irregular-edge" || p.ProcessName == "backdrop") && !targets.Contains(p)) targets.Add(p);
      else p.Dispose();
    } catch (ArgumentException) {
      // 进程已经不在了
    } catch (InvalidOperationException) {
      // 取不到进程信息
    }
  }

  /// 关掉面板应用和棋盘格窗口：像用户点关闭一样发 WM_CLOSE，两秒内没退出的再强制结束
  static void Stop(uint pid) {
    List<Process> targets = new List<Process>();
    AddOwnProcess(targets, pid);
    foreach (WinInfo win in OnScreenWindows()) {
      if (win.Title == "backdrop") AddOwnProcess(targets, win.Pid);
    }
    foreach (Process p in targets) p.CloseMainWindow();
    Stopwatch wait = Stopwatch.StartNew();
    while (wait.ElapsedMilliseconds < 2000 && targets.Any(delegate(Process p) { return !p.HasExited; })) Thread.Sleep(50);
    foreach (Process p in targets) {
      if (!p.HasExited) p.Kill();
      p.Dispose();
    }
  }

  // ---- list / at ----

  static void List(uint pid) {
    Pair pair = FindWindows(pid);
    Console.Out.WriteLine("panel    hwnd=" + pair.Panel.Hwnd + " pid=" + pair.Panel.Pid + " " + pair.Panel.RectText()
      + "  置顶:" + (pair.Panel.Topmost ? "是" : "否") + "  点击穿透:" + (pair.Panel.ClickThrough ? "开" : "关") + "  缩放:" + F(pair.Scale));
    Console.Out.WriteLine("backdrop hwnd=" + pair.Backdrop.Hwnd + " pid=" + pair.Backdrop.Pid + " " + pair.Backdrop.RectText());
    Console.Out.WriteLine("与面板范围重叠的窗口（从前往后）：");
    foreach (WinInfo win in OverlappingIncludingIgnored(pair).Take(10)) {
      List<string> marks = new List<string>();
      if (win.Hwnd == pair.Panel.Hwnd || win.Hwnd == pair.Backdrop.Hwnd) marks.Add("<- 这两个窗口");
      else if (!win.TakesMouse) marks.Add("不接收鼠标，忽略");
      if (win.Topmost && win.Hwnd != pair.Panel.Hwnd) marks.Add("置顶窗口");
      if (win.ClickThrough) marks.Add("点击穿透");
      Console.Out.WriteLine("  " + win.Describe() + (marks.Count > 0 ? "  " + string.Join("，", marks.ToArray()) : ""));
    }
  }

  /// 某个屏幕坐标下叠着哪些窗口，从前往后
  static void At(int x, int y) {
    Native.POINT p = new Native.POINT();
    p.X = x;
    p.Y = y;
    WinInfo hit = new WinInfo(Native.WindowFromPoint(p));
    Console.Out.WriteLine("全局坐标 (" + x + "," + y + ") 下系统命中的窗口：" + hit.Describe() + (hit.ClickThrough ? "  [点击穿透]" : ""));
    Console.Out.WriteLine("  这个点上叠着的顶层窗口（从前往后）：");
    foreach (WinInfo win in OnScreenWindows()) {
      if (win.Contains(x, y)) {
        Console.Out.WriteLine("  " + win.Describe() + (win.ClickThrough ? "  [点击穿透]" : ""));
      }
    }
  }

  // ---- 入口 ----

  const string Usage = @"用法：
  probe launch  <应用> <日志文件>   启动面板应用，打印它的进程号
  probe list    <pid>
  probe at      <x> <y>
  probe capture <pid> <输出目录> <标签>
  probe analyze <输出目录> <标签>
  probe flicker <pid> <输出目录> <标签>   光标快速进出轮廓时画面有没有闪
  probe click   <pid> <x> <y> [等待毫秒]
  probe flick   <pid> <起点x> <起点y> <终点x> <终点y> [到达后等几毫秒再按下]
  probe stop    <pid>              关掉面板应用和棋盘格窗口
";

  static int Run(string[] args) {
    string command = args.Length > 0 ? args[0] : "";
    bool isMouse = command == "click" || command == "flick";
    // 鼠标类子命令的坐标默认是全局坐标，末尾加上 local 则是面板窗口内的逻辑像素
    List<string> rest = new List<string>();
    bool local = false;
    for (int i = 1; i < args.Length; i++) {
      if (isMouse && args[i] == "local") local = true;
      else rest.Add(args[i]);
    }
    switch (command) {
      case "list":
        if (rest.Count == 1) {
          List(Pid(rest[0]));
          return 0;
        }
        break;
      case "at":
        if (rest.Count == 2) {
          At(Integer(rest[0]), Integer(rest[1]));
          return 0;
        }
        break;
      case "launch":
        if (rest.Count >= 2) {
          Console.Out.WriteLine(Launch(rest[0], rest[1], rest.Skip(2).ToArray()));
          return 0;
        }
        break;
      case "stop":
        if (rest.Count == 1) {
          Stop(Pid(rest[0]));
          return 0;
        }
        break;
      case "capture":
        if (rest.Count == 3) {
          Capture(Pid(rest[0]), rest[1], rest[2]);
          return 0;
        }
        break;
      case "flicker":
        if (rest.Count == 3) return Flicker(Pid(rest[0]), rest[1], rest[2]) == 0 ? 0 : 1;
        break;
      case "analyze":
        if (rest.Count == 2) return Analyze(rest[0], rest[1]) == 0 ? 0 : 1;
        break;
      case "click":
        if (rest.Count == 3 || rest.Count == 4) {
          Click(Pid(rest[0]), Number(rest[1]), Number(rest[2]), rest.Count == 4 ? Integer(rest[3]) : 300, local);
          return 0;
        }
        break;
      case "flick":
        if (rest.Count == 5 || rest.Count == 6) {
          Flick(Pid(rest[0]), Number(rest[1]), Number(rest[2]), Number(rest[3]), Number(rest[4]),
            rest.Count == 6 ? Integer(rest[5]) : 0, local);
          return 0;
        }
        break;
    }
    Console.Error.WriteLine(Usage);
    return 64;
  }

  static int Main(string[] args) {
    // 这个工具按物理像素报坐标、截屏，所以告诉系统“DPI 缩放我自己处理”。必须在调用任何窗口接口之前
    try {
      Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
    } catch (EntryPointNotFoundException) {
      // Windows 10 1703 之前没有这个接口
    }
    // 固定用 UTF-8 写标准输出和错误输出：验证脚本按中文找输出行，不能跟着控制台的代码页变
    try {
      Console.OutputEncoding = new UTF8Encoding(false);
      Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    } catch (IOException) {
    } catch (System.ComponentModel.Win32Exception) {
    }

    try {
      return Run(args);
    } catch (ProbeError e) {
      Console.Error.WriteLine("error: " + e.Message);
      return 2;
    }
  }
}
