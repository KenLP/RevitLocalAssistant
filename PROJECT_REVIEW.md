# RevitAssistant — Project Review

> Ngày review: 2026-07-17  
> Phạm vi: source chính, `RevitMCPCore` submodule, tests, CI, installer và tài liệu.  
> Phương pháp: đọc mã/tài liệu, static review, build/test Release, dependency advisory scan. Không sửa mã nguồn.

## 1. Kết luận điều hành

Project đã vượt prototype: kiến trúc phân lớp rõ, có local LLM orchestrator, preview/confirm, ExternalEvent, import CSV/XLSX và bộ unit test tương đối tốt.

Tuy nhiên project **chưa production-safe cho public users khi bật quyền ghi model**. Có đường để một tool name ngoài allowlist gọi các command nguy hiểm đã đăng ký trong Core mà không qua preview/confirm. Ngoài ra preview chưa được khóa với document/tập phần tử thực tế lúc commit, undo chưa type-safe, import chưa phải dry-run thật và packaging còn ở mức dev.

Đánh giá hiện tại:

| Hạng mục | Mức hiện tại |
|---|---:|
| MVP kỹ thuật | 65–70% |
| Private alpha | 50–55% |
| Public beta | 35–40% |
| Production readiness | 4.5/10 |

Khuyến nghị phát hành trước mắt: **read-only alpha** hoặc chỉ mở write sau khi đóng toàn bộ P0/P1 bên dưới.

## 2. Bằng chứng kiểm chứng

- Release build R2025: thành công, `0 warning`, `0 error` với `--warnaserror`.
- Release build R2026: thành công, `0 warning`, `0 error` với `--warnaserror`.
- Unit tests: **188 pass, 2 skipped**.
- Hai test bị skip là integration test Ollama thật vì cần Ollama chạy tại localhost.
- NuGet vulnerability scan theo source hiện tại: không phát hiện package có advisory.
- Chưa có automated Revit-in-process E2E test.
- Chưa có test chứng minh document switch, worksharing, rollback thật, import lớn hoặc model LLM thật.

## 3. Findings ưu tiên

### P0 — Tool ngoài allowlist vẫn được thực thi

`OrchestratorChatService` chỉ phân loại một số tên trong `WriteTools`/`ConfirmExecTools`. Mọi tool còn lại rơi vào nhánh read và được dispatch trực tiếp tại:

- `src/RevitAssistant.UI/Services/OrchestratorChatService.cs:733-765`
- `src/RevitAssistant.Addin/UI/RevitBridge.cs:13-34`

Trong khi đó `CommandRegistry` đăng ký nhiều command nguy hiểm, gồm cả delete/create/move/rotate:

- `extern/RevitMCPCore/src/RevitMCP.Core/Commands/CommandRegistry.cs:62-127`

Ba tool được expose nhưng hiện không nằm trong write gate:

- `tag_all_in_view`
- `copy_parameters`
- `configure_schedule`

#### Tác động

Local model hallucinate một function name hợp lệ là có thể bypass preview/confirm. Đây là blocker trước mọi bản public có quyền ghi.

#### Giải pháp

1. Tạo `ToolPolicy` deny-by-default: `name → execution kind → risk → preview strategy`.
2. Validate allowlist tại Orchestrator **và** RevitBridge.
3. Tool lạ trả `tool_not_allowed`, tuyệt đối không dispatch xuống Core.
4. Mọi `ModelWrite` bắt buộc preview/confirm dựa trên policy metadata.
5. Chỉ register các command cần thiết cho Assistant, không expose toàn bộ Core registry.

### P0 — Preview và commit không khóa cùng document/tập phần tử

Khi confirm, tool call được chạy lại trên active document hiện tại:

- `src/RevitAssistant.UI/Services/OrchestratorChatService.cs:318-352`
- `src/RevitAssistant.Addin/UI/RevitBridge.cs:19-33`

Trong thời gian chờ confirm, người dùng có thể đổi project/view hoặc model có thể bị thay đổi. Commit vì vậy có thể tác động tập phần tử khác preview.

#### Giải pháp

- Preview lưu `documentFingerprint`, target IDs, raw before-values và digest.
- Confirm kiểm tra đúng document và dry-run lại.
- Nếu target/before-value digest thay đổi: không commit, yêu cầu preview mới.
- Với workshared model cần thêm stale/version check.

### P1 — Undo không an toàn cho numeric, ElementId và type parameter

Core lưu `before` bằng display string:

- `extern/RevitMCPCore/src/RevitMCP.Core/Commands/UpdateWhereCommand.cs:212-214`

Orchestrator gom chuỗi này rồi gửi vào `set_parameter_batch` với `atomic=false`:

- `src/RevitAssistant.UI/Services/OrchestratorChatService.cs:410-431`

#### Tác động

- Double/Integer/ElementId có thể không parse hoặc sai đơn vị.
- Type parameter có thể undo bằng instance ID sai target.
- Undo partial nhưng trạng thái undo bị xóa.
- `Parameter.Set()` được gọi nhưng return value không được kiểm tra đầy đủ.

#### Giải pháp

- Tạm ẩn Undo cho loại chưa hỗ trợ.
- Lưu typed raw internal value, storage type, units, parameter identifier và đúng write element ID.
- Undo chạy transaction atomic + read-back verify.
- Chỉ xóa undo state sau khi hoàn tất thành công.
- Viết test riêng cho String, Integer, Double, ElementId và Type parameter.

### P1 — Import chưa phải dry-run thật, có thể commit partial hoặc cập nhật nhầm

Dry-run import hiện chủ yếu gọi `find_elements` và dựng lookup:

- `src/RevitAssistant.UI/Services/ImportExecutor.cs:25-91`

Nó chưa validate bằng transaction rollback:

- parameter tồn tại/read-only;
- storage type/units;
- `Set()` có thật sự thành công không;
- duplicate matching key.

Lookup giữ phần tử đầu tiên khi key bị trùng. Core import bắt lỗi từng item và vẫn tiếp tục commit các item khác:

- `extern/RevitMCPCore/src/RevitMCP.Core/Commands/ImportParametersCommand.cs:38-82`

#### Giải pháp

- Dry-run bằng chính command ghi trong transaction rollback.
- Default atomic toàn file; partial mode chỉ khi user chủ động chọn.
- Duplicate key phải block.
- Paginate hoặc block khi vượt giới hạn 5.000 element.
- Truyền units rõ ràng cho numeric values.
- Giới hạn file size, row/column count và XLSX decompression size.
- Import sheets chạy trong một transaction batch.

### P1 — Release/installer chưa production-ready và snapshot chưa tái lập

- `src/RevitAssistant.Compliance/ComplianceEvaluator.cs:31-40` vẫn là stub trả rỗng.
- `src/RevitAssistant.Schema/ModelSchemaExporter.cs:27-37` vẫn trả `{}`; UI có schema sampling riêng nhưng hai hướng chưa hợp nhất.
- Tài liệu đề cập `RevitAssistantSetup.exe`, nhưng repo chỉ có PowerShell dev installer.
- DLL được copy trực tiếp vào thư mục Revit Addins theo năm, dễ va chạm dependency.
- Main branch đang behind remote 4 commit.
- Submodule hiện khác pointer, ahead 4 và có file modified/untracked; snapshot build local không hoàn toàn giống CI checkout.

#### Giải pháp

- Pin submodule ở commit sạch và đồng nhất CI/local.
- Tạo installer R25/R26 thật, có version/upgrade/uninstall/rollback.
- Đặt dependency trong thư mục riêng của add-in.
- Publish artifact, checksum/SBOM và release notes.
- Không dùng build dev auto-deploy như quy trình phát hành.

### P2 — Offline/privacy chưa được enforce

Ollama URL lấy từ environment và chấp nhận URI bất kỳ:

- `src/RevitAssistant.Addin/App.cs:76-85`

Feedback lưu nội dung chat và context vào `%APPDATA%`:

- `src/RevitAssistant.UI/Services/FileFeedbackSink.cs:16-42`

#### Giải pháp

- Mặc định chỉ cho phép loopback.
- Remote endpoint phải opt-in, HTTPS và cảnh báo dữ liệu.
- Cho user xem/xóa diagnostics.
- Redact project path, metadata và parameter nhạy cảm.
- CSV export cần chống Excel formula injection (`=`, `+`, `-`, `@`).

## 4. Điểm mạnh

- Phân lớp Core/UI/LLM khá rõ.
- Dùng `ExternalEvent` đúng hướng cho Revit API.
- Có preview/confirm/cancel và context trimming.
- `update_where` có transaction, atomic mặc định và read-back verify.
- Có hỗ trợ tiếng Việt/English và glossary BIM.
- Có CSV/XLSX import, bảng kết quả và CSV export.
- Logic thuần có độ phủ test tốt cho quy mô hiện tại.

## 5. Definition of Done đề xuất

### DoD 1 — Safe internal alpha

- Unknown tool không bao giờ tới Core.
- Mọi model-write đều preview + confirm.
- Preview khóa đúng document và target digest.
- Undo/import chưa đạt typed/atomic safety thì phải tắt.
- Có regression test cho hallucinated command, delete command và các write tool bị phân loại sai.
- Không còn P0 mở.

### DoD 2 — Free public beta

- Installer một-click cho Revit 2025/2026, uninstall sạch.
- First-run wizard kiểm tra Revit/Ollama/model/RAM/VRAM.
- Có progress, retry, cancel và hướng dẫn lỗi.
- Import atomic, duplicate-safe, có giới hạn tài nguyên.
- Có ít nhất 100–200 golden prompts VI/EN:
  - 100% thao tác nguy hiểm bị block hoặc yêu cầu confirm.
  - ≥95% chọn đúng tool trên benchmark hỗ trợ.
  - 100% write có preview trước commit.
- Revit E2E test cho query, write, rollback, type parameter, read-only, workshared và đổi document.
- Zero P0/P1 mở.

### DoD 3 — Production-quality

- Pilot ít nhất 20 người hoặc 100 phiên làm việc không có model corruption.
- Crash-free session ≥99,5%.
- Task success của use case hỗ trợ ≥95%.
- Preview/commit mismatch luôn bị chặn.
- Warm query phổ biến có p95 khoảng 15–20 giây trên cấu hình mục tiêu.
- Versioned installer, rollback upgrade, checksum/SBOM và dependency audit trong CI.
- Diagnostics export/delete và privacy notice hoàn chỉnh.
- Artifact được ký số nếu muốn trải nghiệm Windows/SmartScreen tốt.

## 6. Thứ tự triển khai khuyến nghị

1. Khóa allowlist và write policy.
2. Ràng buộc preview với document/target digest.
3. Sửa hoặc tắt Undo.
4. Làm import atomic và validate thật.
5. Thêm Revit integration/red-team tests.
6. Hoàn thiện installer, onboarding và diagnostics.
7. Sau cùng mới mở rộng compliance và thêm command.

## 7. Khuyến nghị phát hành

Phiên bản an toàn nhất hiện tại là **read-only alpha**. Không nên quảng bá bulk edit/import như production feature cho đến khi đóng hai P0 và ít nhất ba P1 đầu tiên.

