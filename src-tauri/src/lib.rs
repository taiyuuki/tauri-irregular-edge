use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use tauri::{Emitter, Manager, State};

static START: LazyLock<Instant> = LazyLock::new(Instant::now);

/// 前端把状态打到标准输出，方便在终端里观察。
/// 两个时间分别是：启动以来的毫秒数、Unix 时间毫秒数（和验证脚本里的时间戳对得上）
#[tauri::command]
fn log(msg: String) {
    let unix_ms = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis()).unwrap_or(0);
    println!("[ui] t=+{:>8.1}ms unix={unix_ms} {msg}", START.elapsed().as_secs_f64() * 1000.0);
}

/// 命中掩码：窗口内每个逻辑像素是不是“实体”。1 表示点击由面板自己接收，0 表示穿透到下面的窗口
struct HitMask {
    width: u32,
    height: u32,
    solid: Vec<u8>,
}

#[derive(Default)]
struct HitState {
    mask: Option<HitMask>,
    version: u64,
}

/// 前端把皮肤图的 alpha 按阈值转成掩码传过来。
/// runs 是游程编码：从 0 开始，依次是“非实体的长度、实体的长度、非实体的长度……”
#[tauri::command]
fn set_hit_mask(state: State<'_, Mutex<HitState>>, width: u32, height: u32, runs: Vec<u32>) -> Result<(), String> {
    let mut solid = Vec::with_capacity((width * height) as usize);
    let mut value = 0u8;
    for run in &runs {
        solid.extend(std::iter::repeat_n(value, *run as usize));
        value ^= 1;
    }
    if solid.len() != (width * height) as usize {
        return Err(format!("掩码长度 {} 与 {}x{} 不符", solid.len(), width, height));
    }
    let mut state = state.lock().unwrap();
    state.mask = Some(HitMask { width, height, solid });
    state.version += 1;
    Ok(())
}

/// 切换穿透有两种做法，用环境变量 IRREGULAR_HIT_MODE 切换，方便对比效果：
///   native（默认）：命中判断和切换都在轮询线程上完成，不经过前端
///   js：轮询线程只把光标位置报给前端，由前端再调回来切换，多一次前后端往返
fn hit_mode() -> &'static str {
    if std::env::var("IRREGULAR_HIT_MODE").as_deref() == Ok("js") {
        "js"
    } else {
        "native"
    }
}

/// 面板窗口一旦开始忽略鼠标事件，网页就收不到 mousemove 了，所以光标位置只能由原生侧自己拿。
///
/// 命中判断也放在这条线程上直接做，切换穿透不经过前后端往返。
/// 这是为了尽量缩短“光标已经移出轮廓、但窗口还没开始穿透”的时间窗：
/// 人在这个时间窗里按下鼠标，点击就会被面板的透明部分吃掉。
fn watch_cursor(app: tauri::AppHandle) {
    std::thread::spawn(move || {
        let state = app.state::<Mutex<HitState>>();
        let mode = hit_mode();
        let mut last = None;
        let mut ignoring = false;

        loop {
            std::thread::sleep(Duration::from_millis(4));

            let Some(panel) = app.get_webview_window("panel") else {
                continue;
            };
            if !panel.is_visible().unwrap_or(false) {
                last = None;
                continue;
            }

            let (Ok(cursor), Ok(origin), Ok(scale)) =
                (app.cursor_position(), panel.outer_position(), panel.scale_factor())
            else {
                continue;
            };
            let x = (cursor.x - f64::from(origin.x)) / scale;
            let y = (cursor.y - f64::from(origin.y)) / scale;

            let (version, solid) = {
                let state = state.lock().unwrap();
                let solid = match &state.mask {
                    Some(mask) => {
                        x >= 0.0
                            && y >= 0.0
                            && x < f64::from(mask.width)
                            && y < f64::from(mask.height)
                            && mask.solid[(y as u32 * mask.width + x as u32) as usize] != 0
                    }
                    None => false,
                };
                (state.version, solid)
            };
            // 光标没动、掩码也没换，就不用重新判断
            if last == Some((x, y, version)) {
                continue;
            }
            last = Some((x, y, version));

            let should_ignore = !solid;
            if mode == "native" && should_ignore != ignoring {
                ignoring = should_ignore;
                if let Err(e) = panel.set_ignore_cursor_events(ignoring) {
                    log(format!("set_ignore_cursor_events 失败：{e}"));
                }
                log(format!("ignore-cursor-events -> {ignoring}"));
            }
            // js 模式下前端靠这个事件切换穿透；两种模式下界面上悬停提示都用它
            let _ = app.emit_to(
                "panel",
                "cursor-move",
                serde_json::json!({ "x": x, "y": y, "solid": solid, "mode": mode }),
            );
        }
    });
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(Mutex::new(HitState::default()))
        .invoke_handler(tauri::generate_handler![log, set_hit_mask])
        .setup(|app| {
            watch_cursor(app.handle().clone());
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
