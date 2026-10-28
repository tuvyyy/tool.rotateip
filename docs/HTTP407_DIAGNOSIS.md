# Điều tra HTTP 407 — 26/10/2026

Đã sửa lỗi binding PasswordBox làm mất mật khẩu nhập vào khi giá trị khởi đầu rỗng. Không kết luận mật khẩu người dùng nhập là sai.

## Bằng chứng và nguyên nhân

- Đọc cấu hình thật tại `%LOCALAPPDATA%/NetworkProfileManager/manager.db` bằng `SqliteSettingsStore`: DPAPI giải mã thành công, hai lần đọc khớp nhau; có username nhưng password rỗng. Không xuất username, password, Basic token, nội dung phản hồi hay public IP.
- `PasswordBinding.PasswordProperty` có metadata mặc định `""`; khi bind vào view model có password `""`, WPF không gọi callback `Changed`. Callback này là nơi duy nhất đăng ký `PasswordChanged`, nên gõ mật khẩu không cập nhật view model. Lưu cấu hình sau đó vẫn mã hóa chuỗi rỗng, dù PasswordBox có ký tự.
- Regression chạy trước sửa thất bại tại `PasswordBox edit did not reach the view model.` Hai case kiểm tra header vẫn PASS. Bằng chứng: `artifacts/tests/proxy-auth-before.trx`.
- Sau sửa, metadata dùng `null` để binding chuỗi rỗng vẫn kích hoạt callback; cập nhật từ PasswordBox dùng `SetCurrentValue` và `try/finally` để giữ binding cùng trạng thái cập nhật.
- Bỏ câu gợi ý quy lỗi username/password khỏi thông báo CONNECT 407; giữ nguyên mã lỗi để chẩn đoán.

## Đối chiếu trên endpoint Home Proxy đã lưu

Sử dụng cùng host/port và credentials đọc từ DPAPI, thử HTTP và SOCKS5 trên chính endpoint đó; không suy luận giao thức từ port.

| Tuyến | HTTPS CONNECT | HTTP forwarding |
| --- | --- | --- |
| HttpClient/WebProxy trực tiếp, HTTP upstream | upstream 407 | upstream 407 |
| Bridge của app, HTTP upstream | downstream 502, LastError ghi upstream 407 | downstream 502, LastError ghi upstream 407 |
| HttpClient/WebProxy trực tiếp, SOCKS5 upstream | ProxyTunnelError | ProxyTunnelError |
| Bridge của app, SOCKS5 upstream | downstream 502 | downstream 502 |

Bridge chuyển lỗi xác thực upstream thành 502 cho client cục bộ; không chuyển challenge upstream sang browser. Với password hiện lưu rỗng, chưa xác nhận được đăng nhập thành công trên Home Proxy thật. Kết quả này không chứng minh password gốc người dùng nhập là sai.

## Header và vòng đời credentials

- CONNECT và HTTP forwarding đều lấy `Proxy-Authorization: Basic ...` từ cấu hình truyền vào bridge. Test độc lập kiểm tra có đúng một header với dữ liệu fixture dự kiến; không dùng lại helper production để tạo giá trị kỳ vọng.
- Header xác thực do client cục bộ gửi không thay thế credentials upstream. HTTP forwarding loại header đó trước khi thêm header của app; CONNECT tạo yêu cầu upstream riêng.
- Bổ sung đối chiếu thành công giữa `HttpClient/WebProxy` và bridge trên cùng mock upstream, cho cả HTTP và HTTPS. Mock hỗ trợ challenge Basic và tính giá trị kỳ vọng độc lập.
- Store không có cache credentials; `StandardSessionProvider` trả cấu hình đã truyền vào; `UpstreamProxyConnector` không lưu credentials.
- Bridge giữ snapshot cấu hình từ constructor. `BrowserLauncherService` giữ bridge đó suốt phiên browser. Save bị chặn khi browser do app quản lý còn chạy; sau dừng và khởi chạy lại sẽ dùng bridge mới. Gateway test luôn tạo bridge mới, không tái sử dụng listener browser.
- Tại lúc kiểm tra, app đang mở từ `artifacts/publish/NetworkProfileManager.exe`, PID 6236, không sở hữu TCP listener nào. Không có bằng chứng listener cũ gây lỗi 407 đang điều tra.
- Regression đầy đủ dùng PasswordBox thật trên STA dispatcher: nạp password rỗng → bridge cũ nhận 407 → nhập password → lưu cấu hình → đọc lại DPAPI và so khớp với đầu vào → đóng bridge cũ và kiểm tra port đã đóng → tạo view model mới từ store → gateway test thành công với bridge mới. Chỉ dùng credentials fixture.

## Kiểm thử và bản build

- Trước sửa: regression 1 FAIL / 2 PASS, đúng vị trí binding.
- Sau sửa: 5 case regression mới PASS, bao gồm hai case WebProxy so với bridge.
- Lượt cuối tập trung proxy/protocol/storage/DPAPI/configuration/regression: **102 PASS, 0 FAIL**. Kết quả: `artifacts/tests/proxy407-focused.trx`.
- Lượt chạy chung trước đó: 102 PASS / 1 FAIL ở test visual UI, dòng 244 `action.IsVisible`. Lỗi này không liên quan đến xác thực; nguyên nhân chập chờn của UI chưa được điều tra trong phạm vi bản sửa. Chạy lại riêng UI: **1 PASS, 0 FAIL**, kết quả `artifacts/tests/proxy407-desktop-recheck.trx`. Giữ lại kết quả có lỗi ở `artifacts/tests/proxy407-related.trx`.
- `dotnet build NetworkProfileManager.sln -c Release --no-restore`: thành công, **0 warning / 0 error**.
- Harness chẩn đoán cũng build Release thành công, **0 warning / 0 error**.
- Publish `win-x64` self-contained thành công tại `artifacts/proxy407/publish/NetworkProfileManager.exe`. Giữ nguyên toàn bộ thư mục publish khi chạy.
- Không chạy stress suite, không gọi DHCP Renew/Flush DNS, không đổi MAC/IP máy, không gọi API xoay IP, không ghi đè cấu hình thật. Không thay thế binary đang bị app cũ sử dụng.

## Sử dụng bản sửa

Đã cập nhật bản sửa vào `artifacts/publish`, đúng đích của shortcut Desktop `Accre Rotate IP.lnk`, theo yêu cầu tiếp theo của người dùng. Đã kiểm tra SHA-256 của EXE và ba assembly app/infrastructure/core khớp bản Release sửa lỗi trong `artifacts/proxy407/publish`.

Mở lại bằng shortcut **Accre Rotate IP** ngoài Desktop, nhập lại mật khẩu và bấm Lưu cấu hình rồi Kiểm tra gateway. Password đã mất trước đây không thể phục hồi từ DPAPI vì giá trị lưu hiện là chuỗi rỗng. Quy ước cập nhật đúng thư mục shortcut đã được ghi trong `AGENTS.md` cho các lần sửa tiếp theo.

Lệnh chẩn đoán chủ động, chỉ chạy khi cần kiểm tra lại upstream thật:

```powershell
dotnet run --project qa/ReleaseHarness -c Release -- probe-proxy-auth
```

Lệnh này đọc cấu hình đã lưu và thực hiện request IP echo qua HTTP/SOCKS5 cùng endpoint; chỉ in trạng thái và các phép so khớp dạng boolean, không in bí mật. Nó không tự lưu cấu hình hoặc xoay IP.
