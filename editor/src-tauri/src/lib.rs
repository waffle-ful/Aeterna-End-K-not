use std::fs;
use std::path::Path;
use std::time::UNIX_EPOCH;

/// ゲームが読む EKMaps フォルダの一覧に載せる 1 件。
#[derive(serde::Serialize)]
struct EkmapEntry {
    /// 表示名 (拡張子 .ekmap.json を除いたもの)
    name: String,
    /// 絶対パス
    path: String,
    /// 更新時刻 (UNIX 秒)。新しい順に並べるため
    modified: u64,
}

// 以下の 3 つはアプリ固有コマンド。tauri-plugin-fs のスコープは
// Documents/EndKnot/EKMaps 配下だけに絞ってあるので、ファイルダイアログで
// ユーザーが選んだ任意パスの読み書きはここで受ける。
// (アプリ固有コマンドはプラグインと違い capability の許可列挙が要らない)

#[tauri::command]
fn read_text_file_abs(path: String) -> Result<String, String> {
    fs::read_to_string(&path).map_err(|e| e.to_string())
}

#[tauri::command]
fn write_text_file_abs(path: String, contents: String) -> Result<(), String> {
    fs::write(&path, contents).map_err(|e| e.to_string())
}

/// `dir` 直下の `*.ekmap.json` を列挙する。フォルダが無い場合は空を返す
/// (まだ一度もゲームで試していないだけなので、エラーにはしない)。
#[tauri::command]
fn list_ekmaps(dir: String) -> Result<Vec<EkmapEntry>, String> {
    let path = Path::new(&dir);
    if !path.is_dir() {
        return Ok(Vec::new());
    }

    let mut out = Vec::new();
    for entry in fs::read_dir(path).map_err(|e| e.to_string())? {
        let entry = match entry {
            Ok(e) => e,
            Err(_) => continue,
        };
        let file_name = entry.file_name().to_string_lossy().to_string();
        if !file_name.ends_with(".ekmap.json") {
            continue;
        }
        let modified = entry
            .metadata()
            .ok()
            .and_then(|m| m.modified().ok())
            .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
            .map(|d| d.as_secs())
            .unwrap_or(0);
        out.push(EkmapEntry {
            name: file_name.trim_end_matches(".ekmap.json").to_string(),
            path: entry.path().to_string_lossy().to_string(),
            modified,
        });
    }
    out.sort_by(|a, b| b.modified.cmp(&a.modified));
    Ok(out)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
  tauri::Builder::default()
    .plugin(tauri_plugin_fs::init())
    .plugin(tauri_plugin_dialog::init())
    .invoke_handler(tauri::generate_handler![
      read_text_file_abs,
      write_text_file_abs,
      list_ekmaps
    ])
    .setup(|app| {
      if cfg!(debug_assertions) {
        app.handle().plugin(
          tauri_plugin_log::Builder::default()
            .level(log::LevelFilter::Info)
            .build(),
        )?;
      }
      Ok(())
    })
    .run(tauri::generate_context!())
    .expect("error while running tauri application");
}
