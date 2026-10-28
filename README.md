# Network Profile Manager V1 — Dual Browser Edition

> QA 25/10/2026: candidate **1.0.1 đang HOLD**, chưa đề xuất phát hành. Lượt đầy đủ: 121/125 PASS, 4 FAIL về browser startup/restart; 49 test cũ vẫn PASS. Xem [FULL_TEST_REPORT](artifacts/reports/FULL_TEST_REPORT.md) và [RELEASE_CHECKLIST](artifacts/reports/RELEASE_CHECKLIST.md). Bản QA self-contained nằm tại `artifacts/release-candidate/publish/`; bộ cài hiện tại chưa ký số.

Ứng dụng desktop C# / .NET 10, WPF, MVVM dành cho Windows 11 x64. Hai profile trình duyệt có thư mục dữ liệu, listener, tiến trình và lịch sử kiểm tra riêng; cùng dùng một tài khoản rotating proxy HTTP hoặc SOCKS5.

Giao diện đã được tối giản: header theo trang, hai card gọn, trợ giúp bằng icon `?`, status bar ngắn và toggle Dark/Light ở sidebar. Xem [thiết kế và danh sách tooltip](docs/UI_REDESIGN.md).

## Chạy ứng dụng

- Bộ cài: `artifacts\installer\NetworkProfileManager_Setup.exe`.
- Bản portable: mở `artifacts\publish\NetworkProfileManager.exe`. **Giữ nguyên toàn bộ thư mục publish**, gồm DLL và runtime đi kèm; không chỉ sao chép riêng EXE.
- Hoặc chạy ` .\scripts\Run.ps1` trong PowerShell tại thư mục dự án.

Bản publish self-contained, không cần cài .NET, Python hoặc quyền Administrator. Bộ cài dành cho tài khoản Windows hiện tại, có shortcut Start Menu, tùy chọn shortcut Desktop và Uninstall. Dữ liệu được giữ lại khi gỡ cài đặt.

## Sử dụng

1. Vào **Cấu hình proxy**, chọn HTTP hoặc SOCKS5; nhập host, port, username/password và ngày hết hạn nếu có. Host không bao gồm giao thức hay tài khoản.
2. Chọn **Lưu cấu hình**, sau đó **Kiểm tra gateway**. Cấu hình credentials được mã hóa bằng Windows DPAPI CurrentUser trước khi ghi SQLite.
3. Trong **Tổng quan** hoặc **Hai trình duyệt**, chọn Chrome, Edge, Cốc Cốc hoặc Firefox cho thẻ A/B. Có thể chọn cùng một loại trình duyệt ở cả hai thẻ. Nếu không tự phát hiện được, dùng **Chọn…** để chỉ định đúng browser `.exe`.
4. Bấm **Khởi chạy cả hai** hoặc khởi chạy từng thẻ. App kiểm tra gateway, xác thực và CONNECT trước khi tạo trình duyệt; mở trang IP echo ngay trong từng profile và kiểm tra IP qua local proxy service.
5. **Dừng cả hai** hủy công việc đang chờ, đóng các tiến trình do app tạo và listener. Có thể dừng từng thẻ. App gửi yêu cầu đóng cửa sổ trước, sau tối đa 3 giây sẽ kết thúc cây tiến trình của profile bằng Windows Job Object. Đóng app cũng dọn tài nguyên; Job Object dọn tiến trình khi app bị crash.
6. **Kiểm tra IP** hỗ trợ tuyến trực tiếp, gateway, A/B, ba lần liên tiếp, sao chép IP đang chọn và xuất CSV. Chọn một dòng để xem lỗi chi tiết.
7. **Thông tin mạng** đọc SSID bằng Native WLAN API, adapter, IPv4, MAC, gateway, DNS và DHCP. Kiểm tra public IP trực tiếp theo yêu cầu. Flush DNS và DHCP Renew yêu cầu xác nhận; nếu Windows từ chối vì quyền, app báo lỗi và không tự nâng quyền.
8. **Cài đặt** cho phép đổi endpoint HTTPS, timeout 3–120 giây, dark/light mode và lưu lựa chọn trình duyệt. Endpoint cần trả plain IP hoặc JSON `{"ip":"..."}`. Endpoint mặc định: `https://api.ipify.org`.

## Ý nghĩa kết quả

- Hai listener không đồng nghĩa hai exit IP khác nhau. IP trùng là thông tin, không tự coi là lỗi.
- Standard Rotation phụ thuộc nhà cung cấp, không cam kết IP cố định hoặc khác nhau. Ba lần kiểm tra là ba request mới, không có giả định đổi IP giữa các lần.
- **Sticky Session chưa được cấu hình**. Interface `IProxySessionProvider` và `StandardSessionProvider` sẵn có để bổ sung adapter theo tài liệu chính thức của nhà cung cấp; không tự chế username/session/API.
- IP hiển thị trên thẻ là IP request của app qua proxy service, **không phải kết luận đã tự động xác minh IP của lưu lượng browser**. Đối chiếu trang IP echo mở trong từng trình duyệt để xác minh thủ công; kết quả có thể khác nếu nhà cung cấp xoay theo kết nối.
- App định tuyến HTTP/HTTPS/TCP của profile do app quản lý. Đây không phải VPN hay tường lửa chặn toàn bộ lưu lượng tiến trình. Chromium được tắt QUIC, DNS trực tiếp và WebRTC UDP không qua proxy; Firefox dùng proxy thủ công, tắt fallback trực tiếp và giới hạn WebRTC. Extension, chính sách doanh nghiệp hoặc cơ chế riêng của phiên bản browser vẫn cần kiểm chứng khi sử dụng thực tế.
- Khi gateway lỗi, app không tạo đường truyền trực tiếp thay thế. App không cấu hình proxy toàn Windows, không sửa profile browser cá nhân, không xóa cookie/cache, không thay MAC bằng registry/driver và không can thiệp fingerprint/CAPTCHA.
- **Địa chỉ phần cứng ngẫu nhiên** mở trang cấu hình Wi-Fi của Windows; tùy chọn cụ thể chỉ hiện khi Windows và driver hỗ trợ. DHCP Renew không bảo đảm đổi public IP.

## Build lại

Yêu cầu Windows 11 x64, .NET SDK 10 và Inno Setup 6.7.3. Có thể chuẩn bị công cụ cục bộ trong `.tools`, không sửa PATH toàn máy:

```powershell
.\scripts\Bootstrap.ps1
.\scripts\Build.ps1
```

Bootstrap tải .NET từ Microsoft và compiler Inno Setup dạng gói NuGet `Tools.InnoSetup` (bản phân phối đóng gói của bên thứ ba, chứa compiler gốc); không chạy bộ cài công cụ ở cấp hệ thống. Có thể dùng SDK và ISCC đã cài trong PATH thay thế.

Build script tạo icon riêng, build Release, chạy test, publish `win-x64` self-contained rồi biên dịch `.iss`. Đóng phiên app đang chạy từ thư mục build/publish trước khi rebuild để tránh DLL bị khóa.

```powershell
# Chỉ build / test
.\.tools\dotnet\dotnet.exe build NetworkProfileManager.sln -c Release
.\.tools\dotnet\dotnet.exe test NetworkProfileManager.sln -c Release

# Không tạo installer
.\scripts\Build.ps1 -SkipInstaller

# Lệnh publish tương đương
.\.tools\dotnet\dotnet.exe publish src\NetworkProfileManager.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o artifacts\publish
```

Publish profile có tại `src\NetworkProfileManager.App\Properties\PublishProfiles\win-x64.pubxml`. Installer ở `Installer\NetworkProfileManager.iss`.

## Kiểm thử

- Unit tests: cấu hình hợp lệ/không hợp lệ, hết hạn, giới hạn endpoint, rotation, không xuất credentials trong chuỗi chẩn đoán, sticky chưa cấu hình.
- Integration tests: mock HTTP/SOCKS5 có xác thực, CONNECT hai chiều, forwarding HTTP GET/POST, hai listener dùng cùng gateway, sai credentials, gateway offline, timeout, cancellation, xung đột port, cleanup socket.
- IP checker được kiểm tra bằng TLS echo cục bộ với chứng chỉ test được tin cậy **chỉ trong test bằng đối chiếu chứng chỉ**; ứng dụng không bỏ kiểm tra chứng chỉ TLS và không cài CA.
- SQLite/DPAPI: round-trip, đọc lại cài đặt/lịch sử/log và kiểm tra bytes DB không chứa username/password plaintext.
- Test `Category=WindowsBrowser` chạy **hai browser thật ở chế độ headless cho từng loại** bằng profile tạm, qua mock proxy cục bộ, kiểm tra cả A và B có request rồi kiểm tra mọi tiến trình thuộc Job đã kết thúc. Cần cài bốn trình duyệt nếu chạy toàn bộ suite. Không dùng proxy thương mại.
- Test `Category=WindowsDesktop` render cả bảy trang WPF trên STA thread với dữ liệu fixture riêng, kiểm tra tên browser trong dropdown, dark/light mode, lỗi sticky và đóng cửa sổ khi chưa có browser chạy. Cửa sổ test ở ngoài vùng màn hình để không chiếm chỗ làm việc. Thư mục tạm chỉ được giữ lại nếu tiến trình ngoài test còn khóa file; bài kiểm tra kết thúc tiến trình vẫn bắt buộc phải thành công.

```powershell
# Chỉ bỏ bài kiểm tra khởi chạy browser thật khi máy build chưa cài browser
.\.tools\dotnet\dotnet.exe test --filter 'Category!=WindowsBrowser&FullyQualifiedName!~BrowserProfilesHaveDistinctPathsAndNoSecretsInCommand'
```

Báo cáo thực tế: [BUILD_REPORT.md](BUILD_REPORT.md). Kết quả máy đọc: `artifacts\tests\results.trx`; log build: `artifacts\build-release.log`.

## Cấu trúc

```text
src/
  NetworkProfileManager.App/            XAML Views, ViewModels, desktop dialogs, DI
  NetworkProfileManager.Core/           Models, validation, interfaces, rotation analysis
  NetworkProfileManager.Infrastructure/
    BrowserAdapters/                   Profile isolation, executable detection, Windows Job Object
    ProxyAdapters/                     HTTP CONNECT, SOCKS5, loopback HTTP bridge
    SessionProviders/                  Standard rotation; sticky extension point
    NetworkServices/                   .NET NetworkInterface + Native WLAN API
    Storage/                           SQLite + DPAPI
    Services/                          Browser lifecycle and IP checking
tests/NetworkProfileManager.Tests/      Unit/integration and real browser tests
Installer/                             Inno Setup source
scripts/                               Bootstrap, build, icon, run
```

UI là tiếng Việt; Views dùng XAML và được tách khỏi ViewModels để có thể chuyển chuỗi sang ResourceDictionary/RESX khi bổ sung ngôn ngữ. V1 chưa cung cấp bộ chọn ngôn ngữ.

## Dữ liệu và bảo mật

`%LOCALAPPDATA%\NetworkProfileManager` chứa SQLite và `Profiles\A|B\<BrowserKind>`. Tên profile không được dùng để xây dựng đường dẫn. Config proxy được mã hóa toàn bộ bằng DPAPI theo user; không chuyển credentials sang máy/tài khoản khác. Password không được truyền trong browser arguments, lưu JSON plaintext hoặc log. Log và lịch sử giữ tối đa 2.000 bản ghi mỗi bảng; UI tải 500 dòng gần nhất. Chỉ bind `127.0.0.1`; port ưu tiên 17881/17882, tự chọn port trống khi bị chiếm. Listener hỗ trợ tối đa 128 kết nối đồng thời/profile, header tối đa 32 KB, timeout handshake cấu hình và idle tunnel 5 phút.

App không gửi telemetry đến server nhà phát triển. Request IP echo sẽ đi tới endpoint bạn cấu hình theo tuyến đã chọn; browser có thể tự gửi request riêng theo tính năng của browser. Không có cam kết IP “sạch” hoặc tránh đánh dấu bởi website.

## Giới hạn kiểm chứng

Chưa có tài khoản proxy thương mại của người dùng nên chưa kiểm tra quy tắc xoay, hạn dùng, hạn chế CONNECT, sticky session hay chính sách riêng của nhà cung cấp. HTTPS-to-upstream proxy và UDP/SOCKS5 UDP không thuộc V1; HTTP và SOCKS5 upstream là giao thức TCP theo cấu hình. Bộ cài chưa được ký số bằng chứng chỉ nhà phát hành. Không tự gọi DHCP Renew/Flush DNS trong kiểm thử để tránh làm gián đoạn mạng đang dùng.
