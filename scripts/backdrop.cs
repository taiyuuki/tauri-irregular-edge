// Windows 上的“背后窗口”：一个独立进程里的原生 WinForms 窗口，画黑白棋盘格，把收到的每次点击打到标准输出。
//
// 之所以放在独立进程里，是为了贴近真实场景：点击穿过面板之后，落到的是别的应用的窗口。
//
// 用法：backdrop <x> <y> <w> <h>
// 坐标是屏幕上的物理像素，原点在主显示器左上角，和 scripts/probe.cs 一致。
//
// 收到 WM_APP + 1 时换图案（wParam）：0 = 棋盘格（默认），1 = 纯黑，2 = 纯白。
// probe 截图时让面板分别叠在纯黑、纯白上各截一次屏，就能反推出面板窗口自己的颜色和 alpha
// （Windows 上截单个窗口拿不到可靠的 alpha 通道，见 scripts/probe.cs 的 capture）。
//
// 只用系统自带的 .NET Framework 编译（所以只能写 C# 5 的语法），不需要装 SDK。
// 用 winexe 编译，运行时不带控制台窗口；输出照样写进调用方重定向的文件：
//   csc -nologo -codepage:65001 -target:winexe -r:System.Drawing.dll -r:System.Windows.Forms.dll
//       -out:backdrop.exe scripts/backdrop.cs

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Backdrop {
  const int WM_SET_PATTERN = 0x8000 + 1;
  const int WM_LBUTTONDOWN = 0x0201;
  const int Cell = 40; // 棋盘格一格的大小（物理像素）。scripts/probe.cs 的 capture 里也有这个数

  // 这个进程要按物理像素画图、报坐标，所以告诉系统“DPI 缩放我自己处理”。必须在创建任何窗口之前调用
  [DllImport("user32.dll")]
  static extern bool SetProcessDpiAwarenessContext(IntPtr context);

  [DllImport("user32.dll")]
  static extern IntPtr GetForegroundWindow();

  static long UnixMs() {
    return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
  }

  sealed class CheckerForm : Form {
    int pattern; // 0 棋盘格，1 纯黑，2 纯白
    int count;
    readonly Font captionFont = new Font(FontFamily.GenericSansSerif, 18f, FontStyle.Regular, GraphicsUnit.Point);

    public CheckerForm(int x, int y, int width, int height) {
      Text = "backdrop"; // probe 靠这个标题找到它
      FormBorderStyle = FormBorderStyle.None;
      StartPosition = FormStartPosition.Manual;
      AutoScaleMode = AutoScaleMode.None; // 尺寸由调用方按物理像素给定，不要让 WinForms 再按 DPI 换算
      Bounds = new Rectangle(x, y, width, height);
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
        | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      Graphics g = e.Graphics;
      Rectangle b = ClientRectangle;
      g.Clear(pattern == 1 ? Color.Black : Color.White);
      if (pattern == 0) {
        // 左上角那一格是黑的，之后黑白交替
        using (Brush black = new SolidBrush(Color.Black)) {
          for (int row = 0; row * Cell < b.Height; row++) {
            for (int col = 0; col * Cell < b.Width; col++) {
              if ((row + col) % 2 == 0) g.FillRectangle(black, col * Cell, row * Cell, Cell, Cell);
            }
          }
        }
      }
      string caption = "落到这个窗口上的点击：" + count + "   （这是另一个进程里的原生窗口）";
      g.DrawString(caption, captionFont, Brushes.Red, 24, b.Height - 52);
    }

    protected override void WndProc(ref Message m) {
      if (m.Msg == WM_SET_PATTERN) {
        int next = (int)m.WParam;
        if (next != pattern) {
          pattern = next;
          Invalidate();
          Update(); // 同步画完才返回，probe 接着会再等一会儿，让 DWM 把这一帧合成到屏幕上
        }
        m.Result = new IntPtr(1);
        return;
      }
      if (m.Msg == WM_LBUTTONDOWN) {
        count++;
        long p = m.LParam.ToInt64();
        int x = (short)(p & 0xffff);
        int y = (short)((p >> 16) & 0xffff);
        bool key = GetForegroundWindow() == Handle;
        Console.Out.WriteLine("click " + x + " " + y + " count=" + count + " key=" + (key ? "true" : "false")
          + " unix=" + UnixMs());
        Invalidate(); // 让底部的计数文字更新
      }
      base.WndProc(ref m);
    }
  }

  [STAThread]
  static int Main(string[] args) {
    int x, y, width, height;
    if (args.Length != 4 || !int.TryParse(args[0], out x) || !int.TryParse(args[1], out y)
      || !int.TryParse(args[2], out width) || !int.TryParse(args[3], out height)) {
      Console.Error.WriteLine("用法：backdrop <x> <y> <w> <h>");
      return 64;
    }
    try {
      SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
    } catch (EntryPointNotFoundException) {
      // Windows 10 1703 之前没有这个接口，那时按系统 DPI 感知就够了
    }

    Application.EnableVisualStyles();
    using (CheckerForm form = new CheckerForm(x, y, width, height)) {
      form.Shown += delegate { form.Activate(); };
      Application.Run(form);
    }
    return 0;
  }
}
