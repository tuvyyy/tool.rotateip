# Báo cáo build và kiểm thử

Ngày thực hiện: **25/10/2026**, múi giờ người dùng Asia/Saigon. Workspace: `C:\toolrotateip`.

## Cập nhật giao diện tối giản

Đã cập nhật XAML/WPF và đóng gói lại bản chạy cùng bộ cài sau redesign. Lượt kiểm tra mới nhất: **0 warning, 0 error; 49/49 test PASS, không skip**, khoảng 23 giây. Inno Setup compile thành công, 62,329 giây; bộ cài 47.435.127 byte. Log mới nhất: [`artifacts/ui-redesign-release.log`](artifacts/ui-redesign-release.log).

Header nằm trong vùng nội dung; Tổng quan có hai card song song, status ba hàng gọn, một chip gateway và status bar ngắn. Giải thích chuyển sang tooltip `?`, hỗ trợ hover/click và bàn phím. Test WPF đã render cả Dark/Light, kích thước cửa sổ nhỏ, kiểm tra tooltip mở được, thông báo ngắn tách khỏi lỗi chi tiết và cả bảy trang.

Xem [thiết kế và danh sách tooltip](docs/UI_REDESIGN.md), [ảnh Dark](artifacts/ui-redesign/overview-dark.png) và [ảnh Light](artifacts/ui-redesign/overview-light.png). Phần bên dưới ghi lại phạm vi kiểm thử chức năng V1 ban đầu; các test đó cũng đã chạy lại trong lượt redesign.

## Môi trường thực tế

- Microsoft Windows 11 Home Single Language, build `10.0.26200`, x64.
- .NET SDK `10.0.402` cài cục bộ tại `.tools\dotnet`; test chạy trên .NET 10 Windows.
- WPF, CommunityToolkit.Mvvm 8.4.0; Microsoft.Data.Sqlite 10.0.11 và SQLitePCLRaw.bundle_e_sqlite3 3.0.5; DPAPI CurrentUser.
- Inno Setup compiler 6.7.3, lấy từ gói compiler portable `Tools.InnoSetup`.
- Máy có Chrome, Microsoft Edge, Cốc Cốc và Firefox. Đây là kiểm thử trên Windows Desktop thực, không phải kiểm thử giả định từ Linux.

## Các giai đoạn

| Giai đoạn | Đã triển khai | Kết quả build ở giai đoạn |
|---|---|---|
| 1 | Solution bốn project, WPF/MVVM/DI, SQLite, mã hóa DPAPI, Native WLAN và adapter information | Thành công; cảnh báo SQLite native ban đầu đã được xử lý bằng cập nhật phụ thuộc |
| 2 | HTTP/SOCKS5 xác thực, HTTP CONNECT, local bridge độc lập, IP checker có timeout/cancellation | Thành công |
| 3 | Bốn browser adapters, profile A/B độc lập, phát hiện/chọn executable, Windows Job Object, Start/Stop Both | Thành công, 0 lỗi và 0 cảnh báo |
| 4 | Unit/integration, browser thật, render WPF, sửa lỗi tìm thấy, self-contained publish và Inno Setup | Build Release cuối: 0 lỗi, 0 cảnh báo; 49/49 test thành công; installer biên dịch thành công |

Sticky session không được giả lập ở bất kỳ giai đoạn nào: UI báo **Sticky Session chưa được cấu hình**. Tất cả thao tác mạng phụ thuộc vào cấu hình thật khi sử dụng app; dữ liệu fixture chỉ nằm trong project Tests.

## Kết quả cuối

- `dotnet build NetworkProfileManager.sln -c Release`: **PASS**, 0 warning, 0 error.
- `dotnet test NetworkProfileManager.sln -c Release`: **49 executed / 49 passed / 0 failed / 0 skipped**. Thời gian lượt cuối: khoảng 28 giây.
- Test report: [`artifacts/tests/results.trx`](artifacts/tests/results.trx).
- Publish: **Release / win-x64 / self-contained**, không trim WPF, không yêu cầu runtime cài trên máy đích.
- Installer: **Inno Setup compile thành công**, tên `NetworkProfileManager_Setup.exe`.
- Kiểm tra NuGet `--vulnerable --include-transitive` đối với App: không có gói bị báo lỗ hổng theo nguồn nuget.org tại thời điểm kiểm tra. Đây không phải cam kết ứng dụng không có mọi loại lỗ hổng.
- Log của build/test/publish/installer cuối: [`artifacts/build-release.log`](artifacts/build-release.log).

## Phạm vi test đã chạy

| Yêu cầu | Bằng chứng thực tế |
|---|---|
| Validate proxy hợp lệ/sai, expiry, endpoint/timeout | Unit tests |
| HTTP authentication và sai credentials | Mock HTTP proxy; HTTP 407 được báo rõ |
| SOCKS5 username/password | Mock SOCKS5 với negotiation và tunnel thực qua TCP |
| HTTP CONNECT | Kiểm tra payload hai chiều, không giải mã TLS trong bridge |
| HTTP forwarding | GET qua HTTP/SOCKS và POST có body qua HTTP |
| Hai listener cùng upstream | Request qua hai port độc lập; dừng A không làm hỏng B; IP trùng được chấp nhận |
| IP giống nhau/thay đổi | TLS echo cục bộ, ba request thật với IP cố định hoặc đổi theo lượt |
| Proxy offline/timeout/cancellation | Thất bại có giới hạn thời gian, không fallback trực tiếp |
| Port conflict | Chiếm port trước rồi xác nhận bridge chọn port khác |
| Listener cleanup | Dừng bridge đóng port và tunnel đang mở |
| Credentials an toàn khi lưu | DPAPI round-trip; kiểm tra bytes SQLite không chứa username/password plaintext |
| Profile isolation | Thư mục A/B và browser arguments riêng; không chứa password |
| Hai browser thật cùng loại | Hai tiến trình/profile cho từng Chrome, Edge, Cốc Cốc, Firefox ở chế độ headless; cả A/B gửi request tới mock proxy |
| Kết thúc tiến trình | Chụp danh sách process trong Windows Job Object, đóng Job rồi chờ toàn bộ process đó exit; kiểm tra child process riêng |
| Browser đóng tự nhiên | Lifecycle monitor dừng listener sau khi tiến trình fixture kết thúc |
| Cả bảy trang WPF | Render trên STA thread bằng XAML thật, kiểm tra tên browser, cấu trúc IP grid và network view |
| Dark / Light | Kiểm tra resource Background thay đổi đúng với hai theme |
| Đóng app khi chưa có browser | Regression test WPF chờ sự kiện Closed, không lỗi đóng lồng nhau |

Chứng chỉ TLS của mock server chỉ được chấp nhận qua đối chiếu chứng chỉ trong test fixture. Không cài chứng chỉ CA, không bỏ kiểm tra TLS trong app. Kiểm thử browser dùng thư mục tạm riêng; không sửa profile cá nhân hoặc đóng các browser ngoài Job Object.

Đã mở bản EXE self-contained trên desktop và quan sát Dashboard. Quá trình kiểm tra trực quan phát hiện và sửa nền cửa sổ chưa nhận theme, độ tương phản dropdown và nhãn hiển thị model. Test render WPF xác nhận các bản sửa cùng cả bảy trang, theme sáng/tối và đóng cửa sổ. Khi thao tác desktop có người dùng đang nhập, kiểm tra chuyển sang test render độc lập thay vì tiếp tục điều khiển cửa sổ của người dùng.

Một số lượt kiểm tra trung gian đã thất bại và được sửa trước lượt cuối: chứng chỉ fixture ephemeral không dùng được với Windows Schannel, file tạm bị scanner giữ sau khi process exit, tranh chấp đọc/đóng Job handle, dọn semaphore listener và sự kiện Closing hoàn tất đồng bộ. Thư mục test có thể được giữ lại nếu scanner ngoài test vẫn khóa file; điều này không bỏ qua các assertion bắt buộc về process exit.

## Artifact

- [`NetworkProfileManager.exe`](artifacts/publish/NetworkProfileManager.exe), kèm toàn bộ thư mục publish khoảng 142 MiB.
- [`NetworkProfileManager_Setup.exe`](artifacts/installer/NetworkProfileManager_Setup.exe), khoảng 45 MiB.
- [`NetworkProfileManager.sln`](NetworkProfileManager.sln), source C#/XAML và project files.
- [`Installer/NetworkProfileManager.iss`](Installer/NetworkProfileManager.iss).
- [`scripts/Build.ps1`](scripts/Build.ps1), [`scripts/Bootstrap.ps1`](scripts/Bootstrap.ps1), [`README.md`](README.md).

## Chưa kiểm chứng bằng môi trường của người dùng

- Chưa có rotating proxy thương mại thật: chưa xác nhận quy tắc rotation, hạn chế CONNECT/port, expiry từ nhà cung cấp hoặc sticky session. Standard mode đã kiểm thử đầy đủ bằng proxy cục bộ.
- Chưa thực hiện cài/gỡ bộ cài trên máy người dùng; đã xác nhận compiler tạo được bộ cài. Cấu hình giữ nguyên dữ liệu LocalAppData khi Uninstall.
- Không tự gọi Flush DNS/DHCP Renew, đổi cấu hình Wi-Fi hoặc thay đổi MAC trong quá trình kiểm thử vì sẽ tác động kết nối đang dùng. Các lệnh này chỉ chạy sau xác nhận trong app; cần kiểm tra quyền/driver thực tế khi dùng.
- Không tuyên bố tự động xác minh IP browser bằng kết quả IP checker của app. Browser thật đã được kiểm tra định tuyến bằng request mock; với proxy thương mại cần đối chiếu trang IP echo trong browser.
- Không kiểm thử thủ công mọi tổ hợp khác loại browser, extension và enterprise policy. Đã chạy hai profile cùng loại cho cả bốn browser được hỗ trợ.
- EXE/installer chưa có chữ ký số nhà phát hành. Giao diện hiện là tiếng Việt; chưa có bộ chọn đa ngôn ngữ.
