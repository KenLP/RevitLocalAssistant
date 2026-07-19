# Installer

> **Trạng thái: CHƯA ĐƯỢC KIỂM CHỨNG.** `inno/RevitAssistant.iss` và
> `build-installer.ps1` đã được viết nhưng **chưa từng chạy end-to-end** — máy viết
> chúng không cài Inno Setup. Đừng phát hành cho ai trước khi chạy hết checklist
> bên dưới trên một máy sạch.

## Cách build

```powershell
# Cần Inno Setup 6: https://jrsoftware.org/isdl.php
.\installer\build-installer.ps1 -Version 0.1.0
```

Kết quả: `artifacts\RevitAssistantSetup-0.1.0.exe` + file `.sha256`.

Nếu chưa cài Inno Setup, script vẫn stage payload vào `artifacts\installer\` rồi
thoát với mã 2 — đủ để kiểm tra layout trước.

## Layout khi cài

Khác với deploy lúc dev (đổ phẳng mọi DLL vào thư mục Addins), installer đặt DLL
vào thư mục riêng để không đụng bản ClosedXML/YamlDotNet của add-in khác:

```
%APPDATA%\Autodesk\Revit\Addins\<year>\
    RevitAssistant.addin        <Assembly>RevitAssistant\RevitAssistant.dll</Assembly>
    RevitAssistant\             toàn bộ DLL sản phẩm + phụ thuộc
```

✅ **Layout này đã được kiểm chứng thật** (2026-07-19, Revit 2026): dev deploy giờ
dùng đúng layout trên, add-in load được và trả lời truy vấn bình thường. Đây từng
là rủi ro lớn nhất của installer — Revit có resolve được assembly trong thư mục con
không — và câu trả lời là **có**. Dev deploy và installer giờ dùng **cùng một layout
và cùng một file `.addin`**, nên đường đi này được test mỗi lần build.

## Checklist nghiệm thu (chưa mục nào được chạy)

1. **Build sạch** — chạy `build-installer.ps1` trên máy có Inno Setup 6; ISCC
   không lỗi, sinh ra `.exe` + `.sha256`.
2. **Cài trên máy sạch** — máy chưa từng có RevitAssistant. Wizard chỉ hiện các
   phiên bản Revit thực sự đang cài.
3. ~~**Add-in load được** — mở Revit, tab "AI Assistant" xuất hiện, panel mở được.~~
   ✅ **Đã xác minh 2026-07-19** qua dev deploy dùng cùng layout: add-in load từ
   thư mục con và trả lời truy vấn bình thường. Vẫn nên xác nhận lại sau khi cài
   bằng installer thật (đường copy khác, layout thì giống).
4. **Chặn khi Revit đang chạy** — mở Revit rồi chạy installer: phải báo lỗi và
   dừng, không cài đè.
5. **Nâng cấp** — cài 0.1.0 rồi cài 0.1.1 đè lên: không nhân bản entry trong
   Apps & Features, add-in vẫn load, không còn DLL cũ sót lại.
6. **Gỡ sạch** — uninstall xong: thư mục `RevitAssistant\` và file `.addin` biến
   mất khỏi mọi thư mục Addins; Revit vẫn mở bình thường.
7. **Cài song song** — `RevitMCPAddin` (nếu có) vẫn load; hai add-in dùng ClientId
   khác nhau nên không được xung đột.

## Còn thiếu

- **Chưa ký số.** Windows SmartScreen sẽ cảnh báo khi tải/chạy. Cần code signing
  certificate; thêm `SignTool` vào `[Setup]` khi đã có.
- **Chưa có first-run wizard** kiểm tra Ollama/model/RAM như PLAN.md mô tả.
  `ollama-bootstrap.ps1` hiện phải chạy tay.
- **Chưa gộp vào CI** — chưa có job nào build installer hay publish release.
