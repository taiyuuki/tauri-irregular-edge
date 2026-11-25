// 验证用的“背后窗口”：一个独立进程里的原生 AppKit 窗口，画黑白棋盘格，把收到的每次点击打到标准输出。
//
// 之所以放在独立进程里，是为了贴近真实场景：点击穿过面板之后，落到的是别的应用的窗口。
// （如果把它做成同一个应用里的另一个 WKWebView 窗口，非焦点窗口上的第一次点击会被系统吞掉，
//   每次焦点在两个窗口之间切换都要多点一下，测不出真实结果。）
//
// 用法：backdrop <x> <y> <w> <h>
// 坐标是全局坐标（点），原点在主显示器左上角，和 scripts/probe.swift 一致。

import AppKit

setlinebuf(stdout)  // 标准输出被重定向到文件时也要逐行刷出

final class CheckerView: NSView {
  var count = 0
  let cell: CGFloat = 40

  override var isFlipped: Bool { true }  // 坐标原点在左上角，y 向下

  // 这个窗口不是焦点窗口时，第一次点击也照常送达，而不是只被用来切换焦点
  override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

  override func mouseDown(with event: NSEvent) {
    count += 1
    let p = convert(event.locationInWindow, from: nil)
    let key = window?.isKeyWindow ?? false
    print("click \(Int(p.x)) \(Int(p.y)) count=\(count) key=\(key) unix=\(Int(Date().timeIntervalSince1970 * 1000))")
    needsDisplay = true
  }

  override func draw(_ dirtyRect: NSRect) {
    // 左上角那一格是黑的，之后黑白交替
    NSColor.white.setFill()
    bounds.fill()
    NSColor.black.setFill()
    for row in 0..<(Int(bounds.height / cell) + 1) {
      for column in 0..<(Int(bounds.width / cell) + 1) where (row + column) % 2 == 0 {
        NSRect(x: CGFloat(column) * cell, y: CGFloat(row) * cell, width: cell, height: cell).fill()
      }
    }
    let caption = "落到这个窗口上的点击：\(count)   （这是另一个进程里的原生窗口）" as NSString
    caption.draw(
      at: NSPoint(x: 24, y: bounds.height - 52),
      withAttributes: [
        .font: NSFont.systemFont(ofSize: 18, weight: .medium),
        .foregroundColor: NSColor.red,
      ])
  }
}

let arguments = CommandLine.arguments
guard arguments.count == 5,
      let x = Double(arguments[1]), let top = Double(arguments[2]),
      let width = Double(arguments[3]), let height = Double(arguments[4])
else {
  print("用法：backdrop <x> <y> <w> <h>")
  exit(64)
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)

// AppKit 的坐标原点在主显示器左下角，把“距顶边的距离”换算过来
let screenTop = NSScreen.screens.first?.frame.maxY ?? 0
let frame = NSRect(x: x, y: screenTop - top - height, width: width, height: height)
let window = NSWindow(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
window.title = "backdrop"  // probe.swift 靠这个名字找到它
window.isReleasedWhenClosed = false
window.contentView = CheckerView(frame: NSRect(origin: .zero, size: frame.size))
window.makeKeyAndOrderFront(nil)
app.activate(ignoringOtherApps: true)

app.run()
