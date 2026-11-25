// macOS 上的验证工具：定位窗口、截屏、像素测量、合成鼠标事件。
//
//   swift scripts/probe.swift list    <pid>
//   swift scripts/probe.swift at      <x> <y>          某个全局坐标下叠着哪些窗口
//   swift scripts/probe.swift key     <pid>            这个应用当前的焦点窗口
//   swift scripts/probe.swift capture <pid> <输出目录> <标签>
//   swift scripts/probe.swift analyze <输出目录> <标签>
//   swift scripts/probe.swift hover   <pid> <x> <y> [停留毫秒，默认 500]
//   swift scripts/probe.swift click   <pid> <x> <y> [等待毫秒，默认 300]
//   swift scripts/probe.swift flick   <pid> <起点x> <起点y> <终点x> <终点y> [到达后等几毫秒再按下]
//
// x, y 默认是全局坐标（点，和 CGWindowBounds 同一坐标系，原点在主显示器左上角）；
// 鼠标类子命令末尾加上 local，就按面板窗口内的坐标来解释。

import AppKit
import CoreGraphics
import Foundation

struct ProbeError: Error, CustomStringConvertible {
  let description: String
  init(_ description: String) { self.description = description }
}

struct WinInfo {
  let id: CGWindowID
  let pid: pid_t
  let name: String
  let owner: String
  let layer: Int
  let alpha: Double
  let bounds: CGRect
}

struct Pair {
  let panel: WinInfo
  let backdrop: WinInfo
}

func unixMs() -> Int { Int(Date().timeIntervalSince1970 * 1000) }

/// 屏幕上的窗口，按 z-order 从前往后
func onScreenWindows() -> [WinInfo] {
  let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
  guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
    return []
  }
  return list.compactMap { info in
    guard let id = info[kCGWindowNumber as String] as? CGWindowID,
          let pid = info[kCGWindowOwnerPID as String] as? pid_t,
          let dict = info[kCGWindowBounds as String] as? NSDictionary,
          let bounds = CGRect(dictionaryRepresentation: dict as CFDictionary)
    else { return nil }
    return WinInfo(
      id: id,
      pid: pid,
      name: info[kCGWindowName as String] as? String ?? "",
      owner: info[kCGWindowOwnerName as String] as? String ?? "",
      layer: info[kCGWindowLayer as String] as? Int ?? 0,
      alpha: info[kCGWindowAlpha as String] as? Double ?? 1,
      bounds: bounds
    )
  }
}

/// panel 是面板应用的窗口；backdrop 是 scripts/backdrop.swift 那个独立进程里的棋盘格窗口，靠窗口标题找
func appWindows(_ pid: pid_t) throws -> Pair {
  let all = onScreenWindows()
  guard let panel = all.first(where: { $0.pid == pid && $0.name == "panel" }),
        let backdrop = all.first(where: { $0.name == "backdrop" })
  else {
    let mine = all.filter { $0.pid == pid }.map(\.name)
    let found = all.filter { $0.name == "backdrop" }.count
    throw ProbeError("找不到窗口：pid \(pid) 的窗口有 \(mine)，标题为 backdrop 的窗口有 \(found) 个")
  }
  return Pair(panel: panel, backdrop: backdrop)
}

func windowsOverlappingPanel(_ pair: Pair) -> [WinInfo] {
  let rect = pair.panel.bounds.insetBy(dx: 2, dy: 2)
  // 程序坞这类系统浮层上报的范围是整个屏幕，实际只占屏幕边缘，所以只看层级不高于面板的窗口
  return onScreenWindows().filter { $0.layer <= pair.panel.layer && $0.bounds.intersects(rect) }
}

func zOrderOk(_ pair: Pair) -> Bool {
  let overlap = windowsOverlappingPanel(pair)
  return overlap.count >= 2 && overlap[0].id == pair.panel.id && overlap[1].id == pair.backdrop.id
    && pair.backdrop.bounds.contains(pair.panel.bounds)
}

/// 把应用切到前台。macOS 14 起推荐用 activate(from:options:)，但那是给有自己窗口的应用用的，
/// 命令行工具只能继续用这个旧接口（实测在 macOS 27 上仍然有效）。
/// 为了不产生弃用警告，这个文件按 macOS 13 作为目标编译，见 verify.sh。
func activate(_ app: NSRunningApplication) {
  app.activate(options: [.activateIgnoringOtherApps])
}

/// 面板矩形范围内，从前往后必须先遇到面板、再遇到棋盘格窗口，并且棋盘格窗口完全盖住这个范围。
/// 这样截屏和合成点击只会碰到这两个窗口。
///
/// 调用前会把两个应用都切到前台，原因有两个：
///   - 棋盘格窗口不置顶，别的应用在前台时会被挡住；
///   - 面板要成为焦点窗口，落在面板上的点击才会被网页收到（WKWebView 不接受非焦点窗口上的第一次点击）。
func assertOnlyOurWindows(_ pair: Pair) throws {
  for pid in [pair.backdrop.pid, pair.panel.pid] {
    if let app = NSRunningApplication(processIdentifier: pid) {
      activate(app)
      usleep(300_000)
    }
  }
  guard zOrderOk(pair) else {
    let seen = windowsOverlappingPanel(pair).prefix(6)
      .map { "\($0.owner)「\($0.name)」\($0.bounds)" }.joined(separator: "; ")
    throw ProbeError("面板矩形范围内不只是本应用的窗口，停止操作。当前从前往后：\(seen)")
  }
}

func run(_ launch: String, _ arguments: [String]) throws {
  let process = Process()
  process.executableURL = URL(fileURLWithPath: launch)
  process.arguments = arguments
  let stderr = Pipe()
  process.standardError = stderr
  try process.run()
  process.waitUntilExit()
  guard process.terminationStatus == 0 else {
    let message = String(data: stderr.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
    throw ProbeError("\(launch) 退出码 \(process.terminationStatus)：\(message)")
  }
}

// MARK: - list / capture

func list(_ pid: pid_t) throws {
  let pair = try appWindows(pid)
  print("panel    id=\(pair.panel.id) layer=\(pair.panel.layer) pid=\(pair.panel.pid) \(pair.panel.bounds)")
  print("backdrop id=\(pair.backdrop.id) layer=\(pair.backdrop.layer) pid=\(pair.backdrop.pid) \(pair.backdrop.bounds)")
  print("与面板范围重叠的窗口（从前往后）：")
  for win in onScreenWindows().filter({ $0.bounds.intersects(pair.panel.bounds.insetBy(dx: 2, dy: 2)) }).prefix(10) {
    let marks = [
      win.id == pair.panel.id || win.id == pair.backdrop.id ? "<- 这两个窗口" : "",
      win.layer > pair.panel.layer ? "层级比面板高" : "",
    ].filter { !$0.isEmpty }.joined(separator: "，")
    print("  layer=\(win.layer) pid=\(win.pid) \(win.owner)「\(win.name)」\(win.bounds)  \(marks)")
  }
}

/// 某个全局坐标下叠着哪些窗口，从前往后
func at(_ x: Double, _ y: Double) {
  let point = CGPoint(x: x, y: y)
  print("全局坐标 \(point) 下的窗口（从前往后）：")
  for win in onScreenWindows().filter({ $0.bounds.contains(point) }).prefix(12) {
    print("  layer=\(win.layer) alpha=\(win.alpha) pid=\(win.pid) \(win.owner)「\(win.name)」\(win.bounds)")
  }
}

/// 某个应用当前的焦点（键盘）窗口是哪个
func keyWindow(_ pid: pid_t) {
  let app = AXUIElementCreateApplication(pid)
  var value: CFTypeRef?
  let error = AXUIElementCopyAttributeValue(app, kAXFocusedWindowAttribute as CFString, &value)
  guard error == .success, let value else {
    print("焦点窗口：读不到（AXError \(error.rawValue)）")
    return
  }
  let window = unsafeBitCast(value, to: AXUIElement.self)
  var title: CFTypeRef?
  AXUIElementCopyAttributeValue(window, kAXTitleAttribute as CFString, &title)
  let active = NSWorkspace.shared.frontmostApplication?.processIdentifier == pid
  print("焦点窗口：「\(title as? String ?? "?")」  本应用\(active ? "是" : "不是")前台应用")
}

func rectDict(_ r: CGRect) -> [String: Double] {
  ["x": r.minX, "y": r.minY, "w": r.width, "h": r.height]
}

func capture(_ pid: pid_t, _ outDir: String, _ tag: String) throws {
  let pair = try appWindows(pid)
  try assertOnlyOurWindows(pair)
  try FileManager.default.createDirectory(atPath: outDir, withIntermediateDirectories: true)

  let r = pair.panel.bounds
  let rect = "\(Int(r.minX)),\(Int(r.minY)),\(Int(r.width)),\(Int(r.height))"
  // 面板矩形范围内的屏幕合成结果：面板 + 它后面的棋盘格
  try run("/usr/sbin/screencapture", ["-x", "-R", rect, "\(outDir)/\(tag)-screen.png"])
  // 只截面板窗口本身，保留 alpha 通道，不带窗口阴影
  try run("/usr/sbin/screencapture", ["-x", "-o", "-l", "\(pair.panel.id)", "\(outDir)/\(tag)-panel.png"])

  let geometry: [String: Any] = ["panel": rectDict(r), "backdrop": rectDict(pair.backdrop.bounds)]
  let json = try JSONSerialization.data(withJSONObject: geometry, options: [.prettyPrinted, .sortedKeys])
  try json.write(to: URL(fileURLWithPath: "\(outDir)/\(tag)-geometry.json"))
  print("panel \(r)  backdrop \(pair.backdrop.bounds)")
}

// MARK: - 像素测量

struct Bitmap {
  let width: Int
  let height: Int
  let row: Int
  let data: [UInt8]

  init?(path: String) {
    guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil)
    else { return nil }
    width = image.width
    height = image.height
    row = width * 4
    var bytes = [UInt8](repeating: 0, count: row * height)
    // 沿用图片自己的色彩空间，不做颜色转换，读到的就是文件里的原始数值
    let space = image.colorSpace ?? CGColorSpaceCreateDeviceRGB()
    let info = CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue
    guard let context = CGContext(
      data: &bytes, width: width, height: height, bitsPerComponent: 8, bytesPerRow: row,
      space: space, bitmapInfo: info)
    else { return nil }
    context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
    data = bytes
  }

  /// 预乘 alpha 的 RGBA
  func rgba(_ x: Int, _ y: Int) -> (Int, Int, Int, Int) {
    let i = y * row + x * 4
    return (Int(data[i]), Int(data[i + 1]), Int(data[i + 2]), Int(data[i + 3]))
  }
}

func loadGeometry(_ outDir: String, _ tag: String) throws -> (panel: CGRect, backdrop: CGRect) {
  let url = URL(fileURLWithPath: "\(outDir)/\(tag)-geometry.json")
  let object = try JSONSerialization.jsonObject(with: Data(contentsOf: url))
  guard let dict = object as? [String: [String: Double]], let p = dict["panel"], let b = dict["backdrop"] else {
    throw ProbeError("读不懂 \(url.path)")
  }
  func rect(_ d: [String: Double]) -> CGRect {
    CGRect(x: d["x"] ?? 0, y: d["y"] ?? 0, width: d["w"] ?? 0, height: d["h"] ?? 0)
  }
  return (rect(p), rect(b))
}

// 皮肤图和界面布局的常量，与 scripts/gen-panel-png.mjs、src/panel.css 保持一致
let bodyAlpha = 199  // 本体 alpha：0.78 * 255
let shadowMax = 97  // 阴影 alpha 上限：0.38 * 255
let margin = 36.0  // 皮肤图四周留给阴影的透明边距
let bottomBand = 66.0  // 皮肤图底部：尾巴 30 + 边距 36
let tailX = 100.0  // 尾巴尖端的 x（边距 36 + 64）
let notchDepth = 10.0  // 采样点离右边缘多远：在缺口里
let checkerCell = 40.0  // 棋盘格一格的大小
let tolerance = 6  // 棋盘格自身的瓦片接缝会带来不超过 4/255 的误差

/// 返回没通过的检查数量
func analyze(_ outDir: String, _ tag: String) throws -> Int {
  let geo = try loadGeometry(outDir, tag)
  guard let screen = Bitmap(path: "\(outDir)/\(tag)-screen.png"),
        let window = Bitmap(path: "\(outDir)/\(tag)-panel.png")
  else { throw ProbeError("读不到截图") }
  guard screen.width == window.width, screen.height == window.height else {
    throw ProbeError(
      "两张截图尺寸不一致：screen \(screen.width)x\(screen.height)，panel \(window.width)x\(window.height)")
  }

  let w = geo.panel.width
  let h = geo.panel.height
  let k = Double(screen.width) / w  // 每个逻辑像素对应几个截图像素
  // 本体在窗口内的范围：四周让开透明边距，底部还要让开尾巴
  let left = margin
  let top = margin
  let right = w - margin
  let bottom = h - bottomBand
  var failures = 0
  func check(_ ok: Bool, _ text: String) {
    if !ok { failures += 1 }
    print("\(ok ? "ok  " : "FAIL") \(text)")
  }
  // 窗口内逻辑像素 -> 截图像素
  func px(_ v: Double) -> Int { Int(v * k) }
  func alpha(_ x: Double, _ y: Double) -> Int { window.rgba(px(x), px(y)).3 }

  print("窗口 \(Int(w))x\(Int(h)) 逻辑像素，截图中每个逻辑像素 = \(String(format: "%.2f", k)) 个图像像素")
  print("本体在窗口内的范围：x \(Int(left))...\(Int(right))，y \(Int(top))...\(Int(bottom))")

  // ---- 一、面板窗口自身的 alpha 通道 ----
  print("\n[一、面板窗口自身的 alpha 通道]")
  let corners = [alpha(2, 2), alpha(w - 3, 2), alpha(2, h - 3), alpha(w - 3, h - 3)]
  check(corners.allSatisfy { $0 == 0 }, "矩形四角 alpha = \(corners)（期望全 0：窗口是矩形，四角在轮廓之外）")
  let roundCorner = alpha(w - 3, 3)
  check(roundCorner == 0, "右上圆角之外 alpha = \(roundCorner)（期望 0）")
  let a1 = alpha(left + 8, 156), a2 = alpha(tailX, bottom + 28)
  check(abs(a1 - bodyAlpha) <= 2 && abs(a2 - bodyAlpha) <= 2, "本体 alpha = \(a1)，尾巴尖端 alpha = \(a2)（期望 \(bodyAlpha)）")
  let notch = alpha(right - notchDepth, top + 34)
  check(notch <= shadowMax, "右侧缺口内 alpha = \(notch)（期望 <= \(shadowMax)：缺口里最多只有阴影）")

  // 本体下边缘往下 2..28px 这一段：尾巴之外最多只有阴影
  var shadowPeak = 0
  var besideTailMax = 0
  var besideTailCount = 0
  for y in px(bottom + 2)..<px(bottom + 28) {
    for x in px(right - 400)..<px(right - 20) {
      let a = window.rgba(x, y).3
      if abs(Double(x) / k - tailX) < 20 { continue }  // 尾巴自己
      shadowPeak = max(shadowPeak, a)
      if abs(Double(x) / k - tailX) < 50 {
        besideTailMax = max(besideTailMax, a)
        besideTailCount += 1
      }
    }
  }
  check(shadowPeak > 0 && shadowPeak <= shadowMax, "本体正下方 2..28px 的 alpha 峰值 = \(shadowPeak)（期望 1...\(shadowMax)：半透明，且低于点击命中的阈值 128）")
  check(besideTailCount > 0 && besideTailMax <= shadowMax, "尾巴两侧紧挨着的 \(besideTailCount) 个像素 alpha 峰值 = \(besideTailMax)（期望 <= \(shadowMax)：尾巴两侧是透的）")

  // ---- 二、屏幕上的实际合成结果 ----
  // 预测值 = 面板窗口贡献的颜色 + (1 - alpha) * 棋盘格颜色；和屏幕截图逐像素比较
  print("\n[二、屏幕上的实际合成结果：面板 + 后面的黑白棋盘格]")
  var outside = (count: 0, maxError: 0, offColor: 0)
  var body = (count: 0, maxError: 0)
  var soft = (count: 0, maxError: 0)
  var bandMin = 255
  var bandMax = 0
  let bandY = px(top + 9)  // 顶部留白带里没有内容，alpha 应该处处等于本体 alpha
  var overWhite = [Int]()
  var overBlack = [Int]()
  func isWhite(_ pxX: Int, _ pxY: Int) -> Bool {
    let gx = geo.panel.minX + (Double(pxX) + 0.5) / k
    let gy = geo.panel.minY + (Double(pxY) + 0.5) / k
    let ix = Int(floor((gx - geo.backdrop.minX) / checkerCell))
    let iy = Int(floor((gy - geo.backdrop.minY) / checkerCell))
    return (ix + iy) % 2 == 1  // repeating-conic-gradient(#fff 0 25%, #000 0 50%)：奇数格是白
  }
  for py in 0..<screen.height {
    for pxX in 0..<screen.width {
      let bg = isWhite(pxX, py) ? 255 : 0
      let p = window.rgba(pxX, py)
      let s = screen.rgba(pxX, py)
      var error = 0
      for (pc, sc) in [(p.0, s.0), (p.1, s.1), (p.2, s.2)] {
        let predicted = Double(pc) + Double(255 - p.3) / 255 * Double(bg)
        error = max(error, Int(abs(predicted - Double(sc)).rounded()))
      }
      if p.3 == 0 {
        outside.count += 1
        outside.maxError = max(outside.maxError, error)
        if error > tolerance { outside.offColor += 1 }
      } else if p.3 >= 190 {
        body.count += 1
        body.maxError = max(body.maxError, error)
        if py == bandY && pxX > px(left + 14) && pxX < px(right - 30) {
          bandMin = min(bandMin, p.3)
          bandMax = max(bandMax, p.3)
          let green = s.1
          if bg == 255 { overWhite.append(green) } else { overBlack.append(green) }
        }
      } else {
        soft.count += 1
        soft.maxError = max(soft.maxError, error)
      }
    }
  }
  let total = Double(screen.width * screen.height)
  func share(_ n: Int) -> String { String(format: "%.1f%%", Double(n) / total * 100) }

  check(outside.offColor == 0, "轮廓之外（alpha = 0）的 \(outside.count) 个像素（\(share(outside.count))）在屏幕上就是纯黑或纯白的棋盘格，偏离超过 \(tolerance)/255 的有 \(outside.offColor) 个")
  check(outside.maxError <= tolerance, "轮廓之外的像素与棋盘格的最大误差 = \(outside.maxError)/255（期望 <= \(tolerance)）")
  check(body.maxError <= tolerance, "本体内部（alpha ≈ \(bodyAlpha)）的 \(body.count) 个像素（\(share(body.count))）与“面板叠在棋盘格上”的预测值最大误差 = \(body.maxError)/255（期望 <= \(tolerance)）")
  check(soft.maxError <= tolerance, "阴影和抗锯齿边缘（0 < alpha < 190）的 \(soft.count) 个像素（\(share(soft.count))）最大误差 = \(soft.maxError)/255（期望 <= \(tolerance)）")
  check(bandMin == bodyAlpha && bandMax == bodyAlpha, "顶部留白带里本体 alpha 的范围 = \(bandMin)...\(bandMax)（期望处处是 \(bodyAlpha)）")

  // 棋盘格透过本体之后还剩多少对比度：直接反映本体的透明度
  let mean = { (v: [Int]) in v.isEmpty ? 0 : v.reduce(0, +) / v.count }
  let contrast = mean(overWhite) - mean(overBlack)
  print("     棋盘格透过本体：白格上绿色通道均值 \(mean(overWhite))，黑格上 \(mean(overBlack))，剩余对比度 \(contrast)/255（本体不透明度 ≈ \(String(format: "%.2f", 1 - Double(contrast) / 255))，设计值 0.78）")

  print("\n\(failures == 0 ? "全部通过" : "有 \(failures) 项没通过")")
  return failures
}

// MARK: - 合成鼠标事件

func post(_ type: CGEventType, at point: CGPoint) {
  CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: point, mouseButton: .left)?
    .post(tap: .cghidEventTap)
}

/// x, y 是面板窗口内的坐标时，换算成全局坐标
func toGlobal(_ point: CGPoint, in pair: Pair, local: Bool) -> CGPoint {
  local ? CGPoint(x: pair.panel.bounds.minX + point.x, y: pair.panel.bounds.minY + point.y) : point
}

func checkedTarget(_ point: CGPoint, in pair: Pair, local: Bool) throws -> CGPoint {
  let target = toGlobal(point, in: pair, local: local)
  guard pair.panel.bounds.contains(target) else { throw ProbeError("目标点 \(target) 不在面板范围内") }
  return target
}

func cursorOrigin() -> CGPoint {
  CGEvent(source: nil)?.location ?? NSEvent.mouseLocation
}

/// 把光标移到目标点并停留。只用鼠标移动事件；结束时把光标放回原处。
func hover(_ pid: pid_t, _ target: CGPoint, _ dwellMs: Int, local: Bool) throws {
  let pair = try appWindows(pid)
  let target = try checkedTarget(target, in: pair, local: local)
  try assertOnlyOurWindows(pair)
  let origin = cursorOrigin()
  post(.mouseMoved, at: target)
  usleep(useconds_t(dwellMs) * 1000)
  post(.mouseMoved, at: origin)
  print("已把光标移到 \(target) 停留 \(dwellMs)ms，并放回 \(origin)")
}

/// 移到目标点，等一会儿，再点一下
func click(_ pid: pid_t, _ target: CGPoint, _ waitMs: Int, local: Bool) throws {
  let pair = try appWindows(pid)
  let target = try checkedTarget(target, in: pair, local: local)
  try assertOnlyOurWindows(pair)
  let origin = cursorOrigin()
  let moved = unixMs()
  post(.mouseMoved, at: target)
  usleep(useconds_t(waitMs) * 1000)  // 等面板根据新的光标位置决定是否忽略鼠标事件
  let down = unixMs()
  post(.leftMouseDown, at: target)
  usleep(40_000)
  post(.leftMouseUp, at: target)
  usleep(250_000)
  post(.mouseMoved, at: origin)
  print("已把光标移到 \(target)（移动时刻 unix=\(moved)），等 \(waitMs)ms 后点击一次（按下时刻 unix=\(down)），光标放回 \(origin)")
}

/// 先停在 from 处等状态稳定，再“甩”到 to，到达后等 delayMs 毫秒就按下：模拟人快速移动后马上点击
func flick(_ pid: pid_t, from: CGPoint, to: CGPoint, delayMs: UInt32, local: Bool) throws {
  let pair = try appWindows(pid)
  let from = try checkedTarget(from, in: pair, local: local)
  let to = try checkedTarget(to, in: pair, local: local)
  try assertOnlyOurWindows(pair)
  let origin = cursorOrigin()
  post(.mouseMoved, at: from)
  usleep(300_000)
  let moved = unixMs()
  post(.mouseMoved, at: to)
  usleep(delayMs * 1000)
  let down = unixMs()
  post(.leftMouseDown, at: to)
  post(.leftMouseUp, at: to)
  usleep(200_000)
  post(.mouseMoved, at: origin)
  print("已从 \(from) 甩到 \(to) 并立刻点击（移动时刻 unix=\(moved)，按下时刻 unix=\(down)），光标放回 \(origin)")
}

func point(_ x: String, _ y: String) -> CGPoint {
  CGPoint(x: Double(x) ?? 0, y: Double(y) ?? 0)
}

// MARK: - 入口

let usage = """
用法：
  swift scripts/probe.swift list    <pid>
  swift scripts/probe.swift capture <pid> <输出目录> <标签>
  swift scripts/probe.swift analyze <输出目录> <标签>
  swift scripts/probe.swift hover   <pid> <x> <y> [停留毫秒]
  swift scripts/probe.swift click   <pid> <x> <y> [等待毫秒]
  swift scripts/probe.swift flick   <pid> <起点x> <起点y> <终点x> <终点y> [到达后等几毫秒再按下]
"""

var exitCode: Int32 = 0
do {
  let args = CommandLine.arguments
  // 鼠标类子命令的坐标默认是全局坐标，末尾加上 local 则是面板窗口内的坐标
  let isMouse = args.count > 1 && ["hover", "click", "flick"].contains(args[1])
  let rest = isMouse ? Array(args.dropFirst(2).filter { $0 != "local" }) : []
  let local = args.contains("local")
  switch isMouse ? args[1] : (args.count > 1 ? args[1] : "") {
  case "list" where args.count == 3:
    try list(pid_t(args[2])!)
  case "at" where args.count == 4:
    at(Double(args[2]) ?? 0, Double(args[3]) ?? 0)
  case "key" where args.count == 3:
    keyWindow(pid_t(args[2])!)
  case "capture" where args.count == 5:
    try capture(pid_t(args[2])!, args[3], args[4])
  case "analyze" where args.count == 4:
    exitCode = try analyze(args[2], args[3]) == 0 ? 0 : 1
  case "hover" where (3...4).contains(rest.count):
    try hover(pid_t(rest[0])!, point(rest[1], rest[2]), rest.count == 4 ? Int(rest[3])! : 500, local: local)
  case "click" where (3...4).contains(rest.count):
    try click(pid_t(rest[0])!, point(rest[1], rest[2]), rest.count == 4 ? Int(rest[3])! : 300, local: local)
  case "flick" where (5...6).contains(rest.count):
    try flick(
      pid_t(rest[0])!, from: point(rest[1], rest[2]), to: point(rest[3], rest[4]),
      delayMs: rest.count == 6 ? UInt32(rest[5]) ?? 0 : 0, local: local)
  default:
    print(usage)
    exitCode = 64
  }
} catch let error as ProbeError {
  FileHandle.standardError.write("error: \(error.description)\n".data(using: .utf8)!)
  exitCode = 2
} catch {
  FileHandle.standardError.write("error: \(error)\n".data(using: .utf8)!)
  exitCode = 2
}
exit(exitCode)
