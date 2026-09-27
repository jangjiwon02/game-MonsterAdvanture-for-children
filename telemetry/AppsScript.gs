// 몬스터 어드벤처 — 설치/접속/진행상황 수집기 (Google Apps Script 웹 앱)
// 배포 방법은 telemetry/README.md 참고. 이 파일은 스프레드시트에 바인딩된
// Apps Script 프로젝트에 그대로 붙여넣는다.

function doPost(e) {
  try {
    var data = JSON.parse(e.postData.contents);
    var ss = SpreadsheetApp.getActiveSpreadsheet();

    if (data.event === 'install') {
      appendRow_(ss, '설치기록', ['시각', '기기ID', '앱버전'],
        [nowStr_(), data.device_id, data.app_version]);
    } else if (data.event === 'session_start') {
      appendRow_(ss, '접속기록', ['시각', '이름', '기기ID', '종류', '이번접속(분)'],
        [nowStr_(), data.player_name, data.device_id, '시작', '']);
    } else if (data.event === 'session_end') {
      appendRow_(ss, '접속기록', ['시각', '이름', '기기ID', '종류', '이번접속(분)'],
        [nowStr_(), data.player_name, data.device_id, '종료', round1_(data.duration_seconds / 60)]);
      upsertLatest_(ss, data.player_name, data.device_id, {
        '누적플레이(분)': round1_(data.total_play_seconds / 60),
        '마지막접속': nowStr_(),
      });
    } else if (data.event === 'progress') {
      upsertLatest_(ss, data.player_name, data.device_id, {
        '레벨': data.level,
        '도감수': data.dex_count,
        '소지금': data.money,
        '누적플레이(분)': round1_(data.total_play_seconds / 60),
        '마지막접속': nowStr_(),
      });
    }

    return ContentService.createTextOutput(JSON.stringify({ ok: true }))
      .setMimeType(ContentService.MimeType.JSON);
  } catch (err) {
    return ContentService.createTextOutput(JSON.stringify({ ok: false, error: String(err) }))
      .setMimeType(ContentService.MimeType.JSON);
  }
}

/** 스프레드시트를 새로 만든 뒤 딱 한 번 실행 — 시트 4개(설치기록/접속기록/최신현황/요약)를 미리 만들어 둔다. */
function setup() {
  var ss = SpreadsheetApp.getActiveSpreadsheet();
  ensureHeaders_(ss, '설치기록', ['시각', '기기ID', '앱버전']);
  ensureHeaders_(ss, '접속기록', ['시각', '이름', '기기ID', '종류', '이번접속(분)']);
  ensureHeaders_(ss, '최신현황', ['이름', '기기ID', '레벨', '도감수', '소지금', '누적플레이(분)', '마지막접속']);

  var summary = ss.getSheetByName('요약') || ss.insertSheet('요약');
  summary.clear();
  summary.getRange('A1').setValue('총 설치 수');
  summary.getRange('B1').setFormula('=COUNTA(설치기록!A2:A)');
  summary.getRange('A2').setValue('접속한 플레이어 수');
  summary.getRange('B2').setFormula('=COUNTA(최신현황!A2:A)');
  summary.getRange('A3').setValue('전체 누적 플레이(분)');
  summary.getRange('B3').setFormula('=SUM(최신현황!F2:F)');
  summary.getRange('A1:A3').setFontWeight('bold');
  ss.setActiveSheet(summary);
  ss.moveActiveSheet(1);
}

function nowStr_() {
  return Utilities.formatDate(new Date(), 'Asia/Seoul', 'yyyy-MM-dd HH:mm:ss');
}

function round1_(n) {
  return Math.round((n || 0) * 10) / 10;
}

function ensureHeaders_(ss, sheetName, headers) {
  var sheet = ss.getSheetByName(sheetName) || ss.insertSheet(sheetName);
  if (sheet.getLastRow() === 0) {
    sheet.appendRow(headers);
    sheet.getRange(1, 1, 1, headers.length).setFontWeight('bold');
    sheet.setFrozenRows(1);
  }
  return sheet;
}

function appendRow_(ss, sheetName, headers, row) {
  var sheet = ensureHeaders_(ss, sheetName, headers);
  sheet.appendRow(row);
}

/** 이름(또는 이름+기기ID)별 최신 상태 한 줄만 유지한다 — 로그가 아니라 "지금 상태" 스냅샷용 탭. */
function upsertLatest_(ss, playerName, deviceId, fields) {
  var headers = ['이름', '기기ID', '레벨', '도감수', '소지금', '누적플레이(분)', '마지막접속'];
  var sheet = ensureHeaders_(ss, '최신현황', headers);
  var name = playerName || '(이름 없음)';

  var data = sheet.getDataRange().getValues();
  var rowIndex = -1;
  for (var i = 1; i < data.length; i++) {
    if (data[i][0] === name) { rowIndex = i + 1; break; }
  }
  if (rowIndex === -1) {
    sheet.appendRow([name, deviceId, '', '', '', '', '']);
    rowIndex = sheet.getLastRow();
  }

  var current = sheet.getRange(rowIndex, 1, 1, headers.length).getValues()[0];
  var byName = {};
  headers.forEach(function (h, idx) { byName[h] = current[idx]; });
  if (deviceId) byName['기기ID'] = deviceId;
  Object.keys(fields).forEach(function (k) {
    if (fields[k] !== undefined && fields[k] !== null && fields[k] !== '') byName[k] = fields[k];
  });

  var updated = headers.map(function (h) { return byName[h]; });
  sheet.getRange(rowIndex, 1, 1, headers.length).setValues([updated]);
}
